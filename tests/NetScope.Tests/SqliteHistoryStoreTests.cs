using NetScope.Core.Models;
using NetScope.Windows.History;

namespace NetScope.Tests;

/// <summary>SQLite 历史库测试：临时库上的写入/查询往返、保留期清理、降采样与损坏恢复。</summary>
public sealed class SqliteHistoryStoreTests : IAsyncLifetime
{
    private string _directory = null!;
    private SqliteHistoryStore _store = null!;

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "netscope-history-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new SqliteHistoryStore(Path.Combine(_directory, "test.db"));
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [Fact]
    public async Task InitializeCreatesUsableDatabase()
    {
        Assert.True(_store.IsUsable);
        Assert.True(File.Exists(Path.Combine(_directory, "test.db")));
    }

    [Fact]
    public async Task SystemSamplesRoundTripThroughFlush()
    {
        var t0 = DateTimeOffset.Now;
        for (var i = 0; i < 5; i++)
            await _store.AppendSystemSampleAsync(new SystemPerformanceSample(
                t0.AddSeconds(i), 10 + i, 8L << 30, 16L << 30, 1024, 2048, NetworkLinkUp: i != 4, NetworkAdapterName: "以太网"));

        // 未落盘前查询为空（批量写入是异步的）
        Assert.Empty(await _store.QuerySystemAsync(t0.AddMinutes(-1), t0.AddMinutes(1)));

        await _store.FlushNowAsync();
        var result = await _store.QuerySystemAsync(t0.AddMinutes(-1), t0.AddMinutes(1));
        Assert.Equal(5, result.Count);
        Assert.Equal(10, result[0].CpuPercent);
        Assert.Equal(14, result[^1].CpuPercent);
        Assert.True(result[0].NetworkLinkUp);
        Assert.False(result[^1].NetworkLinkUp);
        Assert.Equal("以太网", result[0].NetworkAdapterName);
    }

    [Fact]
    public async Task ProcessSamplesRoundTripPerInstanceKey()
    {
        var started = DateTimeOffset.Now.AddMinutes(-10);
        var keyA = new ProcessInstanceKey(100, started);
        var keyB = new ProcessInstanceKey(100, started.AddMinutes(5)); // 同 PID 复用，不同启动时间
        var t = DateTimeOffset.Now.AddMinutes(-1);

        for (var i = 0; i < 3; i++)
        {
            await _store.AppendProcessSampleAsync(new ProcessPerformanceSample(
                keyA, t.AddSeconds(i), "appA", 10 + i, 1L << 30, 1L << 30, 100, 200, 1, 2, true, null, IsForeground: i == 0));
            await _store.AppendProcessSampleAsync(new ProcessPerformanceSample(
                keyB, t.AddSeconds(i), "appB", 50, 1L << 30, 1L << 30, 0, 0, 0, 0, true, null));
        }
        await _store.FlushNowAsync();

        var a = await _store.QueryProcessAsync(keyA, t.AddMinutes(-1), t.AddMinutes(1));
        var b = await _store.QueryProcessAsync(keyB, t.AddMinutes(-1), t.AddMinutes(1));

        Assert.Equal(3, a.Count);
        Assert.All(a, s => Assert.Equal("appA", s.Name));
        Assert.True(a[0].IsForeground);
        Assert.Equal(3, b.Count);
        Assert.All(b, s => Assert.Equal("appB", s.Name));
    }

    [Fact]
    public async Task EventsRoundTripWithContributors()
    {
        var evt = new PerformanceEvent(
            Guid.NewGuid(), PerformanceEventType.CpuContention, PerformanceEventStatus.Closed,
            DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now.AddMinutes(-4),
            75, "可能存在 CPU 争用", "high 进程贡献显著",
            ["证据一", "系统 CPU 连续 11 秒高于 85%"],
            ["建议一", "建议二"],
            new ProcessInstanceKey(100, DateTimeOffset.Now.AddHours(-1)), "high",
            new[]
            {
                new PerformanceEventContributor(new ProcessInstanceKey(100, DateTimeOffset.Now.AddHours(-1)), "high", 42.5),
                new PerformanceEventContributor(new ProcessInstanceKey(200, DateTimeOffset.Now.AddHours(-1)), "mid", 20.1)
            });

        await _store.AppendEventAsync(evt);
        await _store.FlushNowAsync();

        var result = await _store.QueryEventsAsync(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now);
        var restored = Assert.Single(result);
        Assert.Equal(evt.Id, restored.Id);
        Assert.Equal(PerformanceEventType.CpuContention, restored.Type);
        Assert.Equal(PerformanceEventStatus.Closed, restored.Status);
        Assert.Equal(75, restored.Confidence);
        Assert.Equal(evt.EndedAt, restored.EndedAt);
        Assert.Equal("high", restored.PrimaryProcessName);
        Assert.Equal(2, restored.Contributors!.Count);
        Assert.Equal("high", restored.Contributors[0].ProcessName);
        Assert.Equal(42.5, restored.Contributors[0].ImpactScore, 3);
        Assert.Equal(2, restored.Evidence.Count);
        Assert.Equal(2, restored.Recommendations.Count);
    }

    [Fact]
    public async Task EventUpsertOnSameIdReplacesState()
    {
        var id = Guid.NewGuid();
        var capturing = new PerformanceEvent(id, PerformanceEventType.MemoryPressure,
            PerformanceEventStatus.Capturing, DateTimeOffset.Now, null, 60,
            "可能存在内存压力", "可用内存低", ["证据"], ["建议"], null, null, []);
        var closed = capturing with { Status = PerformanceEventStatus.Closed, EndedAt = DateTimeOffset.Now, Confidence = 80 };

        await _store.AppendEventAsync(capturing);
        await _store.FlushNowAsync();
        await _store.AppendEventAsync(closed);
        await _store.FlushNowAsync();

        var result = await _store.QueryEventsAsync(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now);
        var restored = Assert.Single(result);
        Assert.Equal(PerformanceEventStatus.Closed, restored.Status);
        Assert.NotNull(restored.EndedAt);
        Assert.Equal(80, restored.Confidence);
    }

    [Fact]
    public async Task RetentionCleanupDeletesExpiredData()
    {
        var old = DateTimeOffset.Now.AddDays(-10);
        var fresh = DateTimeOffset.Now.AddMinutes(-1);
        await _store.AppendSystemSampleAsync(new SystemPerformanceSample(old, 5, 1, 2, 0, 0));
        await _store.AppendSystemSampleAsync(new SystemPerformanceSample(fresh, 5, 1, 2, 0, 0));
        await _store.FlushNowAsync();

        _store.ConfigureRetention(7);
        await _store.CompactNowAsync();

        var result = await _store.QuerySystemAsync(old.AddHours(-1), fresh.AddHours(1));
        var restored = Assert.Single(result);
        Assert.Equal(fresh, restored.Timestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DownsamplingCompressesOldSamplesTo30SecondBuckets()
    {
        // 对齐到 30 秒桶边界，保证样本 0..89 恰好落在 3 个桶内
        var t = DateTimeOffset.Now.AddDays(-2);
        t = t.AddTicks(-(t.Ticks % TimeSpan.FromSeconds(30).Ticks));
        // 90 条 1 秒间隔的旧采样（24 小时以上），应压缩为 3 个 30 秒桶
        for (var i = 0; i < 90; i++)
            await _store.AppendSystemSampleAsync(new SystemPerformanceSample(t.AddSeconds(i), i, 1, 2, 0, 0));
        await _store.FlushNowAsync();

        await _store.CompactNowAsync();

        var result = await _store.QuerySystemAsync(t.AddMinutes(-1), t.AddMinutes(5));
        Assert.Equal(3, result.Count);
        // 每个桶保留第一条
        Assert.Equal(0, result[0].CpuPercent);
        Assert.Equal(30, result[1].CpuPercent);
        Assert.Equal(60, result[2].CpuPercent);
    }

    [Fact]
    public async Task RetentionIsClampedToOneToThirtyDays()
    {
        _store.ConfigureRetention(0);
        _store.ConfigureRetention(100);
        // 极值被夹紧后不影响可用性；语义由 CompactNowAsync 生效，这里只验证不抛异常
        Assert.True(_store.IsUsable);
        await _store.CompactNowAsync();
        Assert.True(_store.IsUsable);
    }

    [Fact]
    public async Task CorruptedDatabaseIsRecreatedAndStoreStaysUsable()
    {
        await _store.AppendSystemSampleAsync(new SystemPerformanceSample(DateTimeOffset.Now, 5, 1, 2, 0, 0));
        await _store.FlushNowAsync();

        // 关闭连接后把数据库文件写坏，模拟磁盘损坏
        await _store.DisposeAsync();
        await File.WriteAllTextAsync(Path.Combine(_directory, "test.db"), "this is not a sqlite database");

        var recovered = new SqliteHistoryStore(Path.Combine(_directory, "test.db"));
        await recovered.InitializeAsync();
        try
        {
            // 损坏文件被备份为 .corrupt-*，重建后可继续写入
            Assert.True(recovered.IsUsable);
            Assert.True(Directory.EnumerateFiles(_directory, "test.db.corrupt-*").Any());
            await recovered.AppendSystemSampleAsync(new SystemPerformanceSample(DateTimeOffset.Now, 5, 1, 2, 0, 0));
            await recovered.FlushNowAsync();
            var result = await recovered.QuerySystemAsync(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now);
            Assert.Single(result);
        }
        finally
        {
            await recovered.DisposeAsync();
        }
    }
}
