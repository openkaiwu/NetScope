using NetScope.Core.Models;
using NetScope.Windows.History;

namespace NetScope.Tests;

/// <summary>V1.0 多级降采样验收：&lt;24h 原始粒度、24h–7d 折叠为 30 秒平均、7 天以上折叠为 5 分钟平均。</summary>
public sealed class V10StorageTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netscope-v10-" + Guid.NewGuid().ToString("N"));
    private SqliteHistoryStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new SqliteHistoryStore(Path.Combine(_directory, "history.db"));
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task SamplesOlderThanSevenDaysCollapseToFiveMinuteAverages()
    {
        var at = AlignDown(DateTimeOffset.Now.AddDays(-10), TimeSpan.FromMinutes(5));
        for (var i = 0; i < 10; i++)
            await _store.AppendSystemSampleAsync(Sys(at.AddSeconds(i * 30), 20 + i, 1_000_000_000 + i));
        await _store.FlushNowAsync();
        _store.ConfigureRetention(30);
        await _store.CompactNowAsync();

        var rows = await _store.QuerySystemAsync(at.AddMinutes(-1), at.AddMinutes(6));
        var row = Assert.Single(rows);
        Assert.Equal(24.5, row.CpuPercent, 1);          // 20..29 的平均值，而非抽样
        Assert.Equal(1_000_000_004, row.AvailableMemoryBytes); // 0..9 的平均值
    }

    [Fact]
    public async Task SamplesBetweenOneAndSevenDaysCollapseToThirtySecondAverages()
    {
        var at = AlignDown(DateTimeOffset.Now.AddDays(-3), TimeSpan.FromSeconds(30));
        for (var i = 0; i < 10; i++)
            await _store.AppendSystemSampleAsync(Sys(at.AddSeconds(i * 3), 30 + i));
        await _store.FlushNowAsync();
        _store.ConfigureRetention(30);
        await _store.CompactNowAsync();

        var rows = await _store.QuerySystemAsync(at.AddMinutes(-1), at.AddMinutes(6));
        var row = Assert.Single(rows);
        Assert.Equal(34.5, row.CpuPercent, 1);
    }

    [Fact]
    public async Task SamplesWithinTwentyFourHoursStayRaw()
    {
        var at = DateTimeOffset.Now.AddHours(-2);
        for (var i = 0; i < 6; i++)
            await _store.AppendSystemSampleAsync(Sys(at.AddSeconds(i * 3), 50 + i));
        await _store.FlushNowAsync();
        await _store.CompactNowAsync();

        var rows = await _store.QuerySystemAsync(at.AddMinutes(-1), at.AddMinutes(6));
        Assert.Equal(6, rows.Count);
    }

    [Fact]
    public async Task RepeatedCompactionDoesNotChangeAverages()
    {
        var at = AlignDown(DateTimeOffset.Now.AddDays(-8), TimeSpan.FromMinutes(5));
        for (var i = 0; i < 10; i++)
            await _store.AppendSystemSampleAsync(Sys(at.AddSeconds(i * 30), 20 + i));
        await _store.FlushNowAsync();
        _store.ConfigureRetention(30);
        await _store.CompactNowAsync();
        var first = Assert.Single(await _store.QuerySystemAsync(at.AddMinutes(-1), at.AddMinutes(6)));

        await _store.CompactNowAsync();
        var second = Assert.Single(await _store.QuerySystemAsync(at.AddMinutes(-1), at.AddMinutes(6)));
        Assert.Equal(first.CpuPercent, second.CpuPercent, 5);
        Assert.Equal(first.AvailableMemoryBytes, second.AvailableMemoryBytes);
    }

    [Fact]
    public async Task FiveMinuteTierKeepsProcessInstancesSeparate()
    {
        var at = AlignDown(DateTimeOffset.Now.AddDays(-9), TimeSpan.FromMinutes(5));
        var first = new ProcessInstanceKey(10, at);
        var second = new ProcessInstanceKey(11, at);
        for (var i = 0; i < 8; i++)
        {
            await _store.AppendProcessSampleAsync(Sample(first, at.AddSeconds(i * 30), "app.exe", 10, 100));
            await _store.AppendProcessSampleAsync(Sample(second, at.AddSeconds(i * 30), "app.exe", 20, 200));
        }
        await _store.FlushNowAsync();
        _store.ConfigureRetention(30);
        await _store.CompactNowAsync();

        var firstRows = await _store.QueryProcessAsync(first, at.AddMinutes(-1), at.AddMinutes(6));
        var secondRows = await _store.QueryProcessAsync(second, at.AddMinutes(-1), at.AddMinutes(6));
        Assert.Equal(10, Assert.Single(firstRows).CpuPercent, 1);
        Assert.Equal(20, Assert.Single(secondRows).CpuPercent, 1);
        Assert.Equal(100L * 1024 * 1024, firstRows[0].PrivateBytes);
    }

    [Fact]
    public async Task RetentionStillDeletesBeforeTieringApplies()
    {
        var at = DateTimeOffset.Now.AddDays(-8);
        await _store.AppendSystemSampleAsync(Sys(at, 50));
        await _store.FlushNowAsync();
        _store.ConfigureRetention(7);
        await _store.CompactNowAsync();
        Assert.Empty(await _store.QuerySystemAsync(at.AddMinutes(-1), at.AddMinutes(1)));
    }

    [Fact]
    public async Task SystemSeriesBucketsLongWindowsAndAveragesValues()
    {
        var at = DateTimeOffset.Now.AddDays(-2);
        for (var i = 0; i < 30; i++)
            await _store.AppendSystemSampleAsync(Sys(at.AddMinutes(i), 10 + i % 10));
        await _store.FlushNowAsync();

        var series = await _store.QuerySystemSeriesAsync(at, at.AddMinutes(30), maxPoints: 5);
        Assert.InRange(series.Count, 1, 5);
        for (var i = 1; i < series.Count; i++)
            Assert.True(series[i - 1].Timestamp < series[i].Timestamp);
        Assert.All(series, point => Assert.InRange(point.CpuPercent, 9.9, 19.1));
        Assert.All(series, point => Assert.Null(point.Disk));
    }

    [Fact]
    public async Task CoverageCountsSystemAndProcessSamplesInWindow()
    {
        var at = DateTimeOffset.Now.AddHours(-1);
        for (var i = 0; i < 5; i++) await _store.AppendSystemSampleAsync(Sys(at.AddSeconds(i), 30));
        var key = new ProcessInstanceKey(42, at);
        for (var i = 0; i < 3; i++) await _store.AppendProcessSampleAsync(Sample(key, at.AddSeconds(i), "app.exe", 5, 50));
        await _store.FlushNowAsync();

        var coverage = await _store.QueryCoverageAsync(at.AddMinutes(-1), at.AddMinutes(1));
        Assert.Equal(5, coverage.SystemSampleCount);
        Assert.Equal(3, coverage.ProcessSampleCount);
        var outside = await _store.QueryCoverageAsync(at.AddDays(-10), at.AddDays(-9));
        Assert.Equal(0, outside.SystemSampleCount);
    }

    private static SystemPerformanceSample Sys(DateTimeOffset at, double cpu, long available = 8_000_000_000) =>
        new(at, cpu, available, 16_000_000_000, 1024, 2048, true, "以太网");

    private static ProcessPerformanceSample Sample(ProcessInstanceKey key, DateTimeOffset at, string name, double cpu, long memoryMb) =>
        new(key, at, name, cpu, memoryMb * 1024 * 1024, memoryMb * 1024 * 1024, 100, 200, 0, 0);

    /// <summary>把时间对齐到单位边界，保证测试样本确定性地落在同一个降采样桶内。</summary>
    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan unit)
    {
        var ticks = value.UtcTicks - value.UtcTicks % unit.Ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime();
    }
}
