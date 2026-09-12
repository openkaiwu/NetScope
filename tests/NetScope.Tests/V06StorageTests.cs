using NetScope.Core.Models;
using NetScope.Windows.History;

namespace NetScope.Tests;

public sealed class V06StorageTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netscope-v06-" + Guid.NewGuid().ToString("N"));
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
    public async Task CollectorHealthRoundTripsAndPreservesReasonsAndProfile()
    {
        var now = DateTimeOffset.Now;
        var health = new CollectorHealthSnapshot(now, 1.25, 70_000_000, 100, 200, 42,
            new(SamplingMode.Reduced, 2000, 5000, 15), ["CPU 超预算"]);
        await _store.AppendCollectorHealthAsync(health);
        await _store.FlushNowAsync();
        var restored = Assert.Single(await _store.QueryCollectorHealthAsync(now.AddSeconds(-1), now.AddSeconds(1)));
        Assert.Equal(1.25, restored.CpuPercent, 2);
        Assert.Equal(SamplingMode.Reduced, restored.Profile.Mode);
        Assert.Equal(15, restored.Profile.ProcessHistoryTopN);
        Assert.Equal("CPU 超预算", Assert.Single(restored.Reasons));
    }

    [Fact]
    public async Task ProcessSeriesAggregatesBucketsButKeepsRestartedInstancesSeparate()
    {
        var at = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var first = new ProcessInstanceKey(10, at.AddHours(-1));
        var second = new ProcessInstanceKey(11, at);
        await _store.AppendProcessSampleAsync(Sample(first, at.AddSeconds(1), "Worker.exe", 10, 100));
        await _store.AppendProcessSampleAsync(Sample(first, at.AddSeconds(2), "worker.exe", 20, 200));
        await _store.AppendProcessSampleAsync(Sample(second, at.AddSeconds(3), "worker.exe", 30, 300));
        await _store.FlushNowAsync();
        var rows = await _store.QueryProcessSeriesAsync(at.AddMinutes(-1), at.AddMinutes(1));
        Assert.Equal(2, rows.Count);
        var firstRow = Assert.Single(rows, x => x.ProcessId == 10);
        Assert.Equal(2, firstRow.SampleCount);
        Assert.Equal(15, firstRow.CpuPercent, 2);
        Assert.Equal(150L * 1024 * 1024, firstRow.PrivateBytes);
        Assert.NotNull(firstRow.ProcessStartedAt);
        Assert.Equal(first.StartedAt, firstRow.ProcessStartedAt.Value, TimeSpan.FromSeconds(1));
        Assert.Single(rows, x => x.ProcessId == 11);
    }

    [Fact]
    public async Task PortActivityClipsDurationToRequestedWindow()
    {
        var now = DateTimeOffset.Now;
        await _store.AppendPortSessionAsync(new(8080, PortProtocol.Tcp, "server.exe", 10,
            now.AddHours(-2), now.AddHours(2)));
        await _store.FlushNowAsync();
        var summary = Assert.Single(await _store.QueryPortActivityAsync(now.AddMinutes(-5), now.AddMinutes(5)));
        Assert.Equal(600, summary.TotalSeconds, precision: 1);
    }

    [Fact]
    public async Task ProcessChartHistoryIsBoundedAndKeepsRequestedInstance()
    {
        var at = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var key = new ProcessInstanceKey(42, at.AddHours(-1));
        for (var i = 0; i < 1_200; i++)
            await _store.AppendProcessSampleAsync(Sample(key, at.AddSeconds(i), "chart.exe", i % 100, 100 + i));
        await _store.FlushNowAsync();

        var rows = await _store.QueryProcessAsync(key, at, at.AddSeconds(1_199));

        Assert.InRange(rows.Count, 1, 900);
        Assert.All(rows, row => Assert.Equal(key, row.Process));
        Assert.True(rows[0].Timestamp < rows[^1].Timestamp);
        Assert.True(rows[^1].PrivateBytes > rows[0].PrivateBytes);
    }

    [Fact]
    public async Task RetentionDeletesExpiredCollectorHealth()
    {
        var old = DateTimeOffset.Now.AddDays(-3);
        var fresh = DateTimeOffset.Now.AddMinutes(-1);
        var profile = new SamplingProfile(SamplingMode.Normal, 1000, 2000, 25);
        await _store.AppendCollectorHealthAsync(new(old, 0, 1, 0, 0, 1, profile, []));
        await _store.AppendCollectorHealthAsync(new(fresh, 0, 1, 0, 0, 1, profile, []));
        await _store.FlushNowAsync();
        _store.ConfigureRetention(1);
        await _store.CompactNowAsync();
        var rows = await _store.QueryCollectorHealthAsync(old.AddHours(-1), fresh.AddHours(1));
        Assert.Single(rows);
        Assert.Equal(fresh, rows[0].Timestamp, TimeSpan.FromSeconds(1));
    }

    private static ProcessPerformanceSample Sample(ProcessInstanceKey key, DateTimeOffset at, string name, double cpu, long memoryMb) =>
        new(key, at, name, cpu, memoryMb * 1024 * 1024, memoryMb * 1024 * 1024, 100, 200, 0, 0);
}
