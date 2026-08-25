using System.Collections.Immutable;
using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

/// <summary>事件规则引擎的模拟时间测试：验证持续阈值、关闭、冷却防风暴与贡献进程归因。</summary>
public sealed class PerformanceEventEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static SystemPerformanceSample System(
        double cpu = 10, long availBytes = 8L << 30, long totalBytes = 16L << 30,
        bool linkUp = true, DateTimeOffset? t = null) =>
        new(t ?? T0, cpu, availBytes, totalBytes, 0, 0, linkUp);

    private static ProcessPerformanceSample Proc(
        int pid, string name, double cpu, long workingSet = 100L << 20,
        long readBps = 0, long writeBps = 0, DateTimeOffset? t = null, bool foreground = false) =>
        // 实例键的启动时间固定，样本时间跟随时间线，模拟同一进程的连续采样
        new(new ProcessInstanceKey(pid, T0.AddMinutes(-10)), t ?? T0, name, cpu, workingSet, workingSet,
            readBps, writeBps, 0, 0, true, null, foreground);

    private static ImmutableArray<ProcessPerformanceSample> Procs(params ProcessPerformanceSample[] samples) =>
        samples.ToImmutableArray();

    private static async Task<List<PerformanceEvent>> RunAsync(
        PerformanceEventEngine engine, IEnumerable<(SystemPerformanceSample system, ImmutableArray<ProcessPerformanceSample> processes, DateTimeOffset now)> timeline)
    {
        var output = new List<PerformanceEvent>();
        foreach (var (system, processes, now) in timeline)
            output.AddRange(await engine.EvaluateAsync(system, processes, now));
        return output;
    }

    [Fact]
    public async Task CpuContentionRequiresSustainedThreshold()
    {
        var engine = new PerformanceEventEngine();
        var timeline = Enumerable.Range(0, 11).Select(i =>
            (System(cpu: 90, t: T0.AddSeconds(i)), Procs(Proc(100, "heavy", 60, t: T0.AddSeconds(i))), T0.AddSeconds(i)));

        var events = await RunAsync(engine, timeline);

        // 第 10 秒才满足 10 秒持续条件，此前不应有事件
        var created = Assert.Single(events, e => e.Status == PerformanceEventStatus.Capturing);
        Assert.Equal(PerformanceEventType.CpuContention, created.Type);
        Assert.Equal(T0, created.StartedAt);
        Assert.InRange(created.Confidence, 40, 90);
    }

    [Fact]
    public async Task CpuSpikeBelowSustainSecondsCreatesNoEvent()
    {
        var engine = new PerformanceEventEngine();
        var timeline = Enumerable.Range(0, 9).Select(i =>
            (System(cpu: 95, t: T0.AddSeconds(i)), Procs(), T0.AddSeconds(i)));
        // 第 9 秒恢复
        timeline = timeline.Append((System(cpu: 10, t: T0.AddSeconds(9)), Procs(), T0.AddSeconds(9)));

        var events = await RunAsync(engine, timeline);
        Assert.Empty(events);
    }

    [Fact]
    public async Task ConditionFlapResetsSustainWindow()
    {
        var engine = new PerformanceEventEngine();
        var moments = new List<(SystemPerformanceSample, ImmutableArray<ProcessPerformanceSample>, DateTimeOffset)>();
        // 高 5 秒 -> 低 1 秒 -> 高 5 秒：两段都不足 10 秒持续
        for (var i = 0; i < 5; i++) moments.Add((System(cpu: 90, t: T0.AddSeconds(i)), Procs(), T0.AddSeconds(i)));
        moments.Add((System(cpu: 10, t: T0.AddSeconds(5)), Procs(), T0.AddSeconds(5)));
        for (var i = 6; i < 11; i++) moments.Add((System(cpu: 90, t: T0.AddSeconds(i)), Procs(), T0.AddSeconds(i)));

        var events = await RunAsync(engine, moments);
        Assert.Empty(events);
    }

    [Fact]
    public async Task CpuEventClosesWhenConditionClears()
    {
        var engine = new PerformanceEventEngine();
        var moments = new List<(SystemPerformanceSample, ImmutableArray<ProcessPerformanceSample>, DateTimeOffset)>();
        for (var i = 0; i <= 10; i++) moments.Add((System(cpu: 90, t: T0.AddSeconds(i)), Procs(Proc(100, "heavy", 60, t: T0.AddSeconds(i))), T0.AddSeconds(i)));
        for (var i = 11; i <= 13; i++) moments.Add((System(cpu: 10, t: T0.AddSeconds(i)), Procs(), T0.AddSeconds(i)));

        var events = await RunAsync(engine, moments);
        var created = events.Single(e => e.Status == PerformanceEventStatus.Capturing);
        var closed = events.Single(e => e.Status == PerformanceEventStatus.Closed);

        Assert.Equal(created.Id, closed.Id);
        Assert.NotNull(closed.EndedAt);
        // 条件消失的第一拍即关闭
        Assert.Equal(T0.AddSeconds(11), closed.EndedAt);
        // 关闭事件的证据以持续时间开头
        Assert.StartsWith("系统 CPU 连续", closed.Evidence[0]);
        Assert.Contains("11 秒", closed.Evidence[0]);
    }

    [Fact]
    public async Task CooldownSuppressesEventStormAfterClose()
    {
        var engine = new PerformanceEventEngine(new PerformanceEventEngineOptions { CooldownSeconds = 120 });
        var moments = new List<(SystemPerformanceSample, ImmutableArray<ProcessPerformanceSample>, DateTimeOffset)>();
        // 第一轮：10 秒高压 -> 2 秒恢复 -> 立刻再来 20 秒高压（仍在 120 秒冷却内）
        for (var i = 0; i <= 10; i++) moments.Add((System(cpu: 90, t: T0.AddSeconds(i)), Procs(), T0.AddSeconds(i)));
        moments.Add((System(cpu: 10, t: T0.AddSeconds(11)), Procs(), T0.AddSeconds(11)));
        moments.Add((System(cpu: 10, t: T0.AddSeconds(12)), Procs(), T0.AddSeconds(12)));
        for (var i = 13; i <= 35; i++) moments.Add((System(cpu: 90, t: T0.AddSeconds(i)), Procs(), T0.AddSeconds(i)));

        var events = await RunAsync(engine, moments);
        Assert.Single(events, e => e.Status == PerformanceEventStatus.Capturing);

        // 冷却结束后（第 11 秒关闭 + 120 秒 = 第 131 秒），新的持续高压应再次触发
        for (var i = 140; i <= 160; i++) moments.Add((System(cpu: 90, t: T0.AddSeconds(i)), Procs(), T0.AddSeconds(i)));
        var eventsAfterCooldown = await RunAsync(engine, moments.Skip(moments.Count - 21));
        Assert.Single(eventsAfterCooldown, e => e.Status == PerformanceEventStatus.Capturing);
    }

    [Fact]
    public async Task MemoryPressureTriggersOnLowAvailable()
    {
        var engine = new PerformanceEventEngine();
        // 可用 0.5GB < 16GB * 10%
        var timeline = Enumerable.Range(0, 11).Select(i =>
            (System(availBytes: 512L << 20, t: T0.AddSeconds(i)),
             Procs(Proc(200, "leaky", 5, workingSet: 6L << 30, t: T0.AddSeconds(i))), T0.AddSeconds(i)));

        var events = await RunAsync(engine, timeline);
        var created = events.Single(e => e.Status == PerformanceEventStatus.Capturing);
        Assert.Equal(PerformanceEventType.MemoryPressure, created.Type);
        Assert.Equal(200, created.PrimaryProcess?.ProcessId);
        Assert.Equal("leaky", created.PrimaryProcessName);
    }

    [Fact]
    public async Task IoPressureTriggersOnHighAggregateIo()
    {
        var engine = new PerformanceEventEngine();
        // 单进程 100MB/s > 80MB/s 阈值
        var timeline = Enumerable.Range(0, 11).Select(i =>
            (System(cpu: 5, t: T0.AddSeconds(i)),
             Procs(Proc(300, "copier", 5, readBps: 100L << 20, t: T0.AddSeconds(i))), T0.AddSeconds(i)));

        var events = await RunAsync(engine, timeline);
        var created = events.Single(e => e.Status == PerformanceEventStatus.Capturing);
        Assert.Equal(PerformanceEventType.DiskIoPressure, created.Type);
        Assert.Contains("疑似", created.Summary);
        // I/O 结论必须声明计数不等同于磁盘延迟
        Assert.Contains(created.Recommendations, r => r.Contains("不等同于磁盘延迟"));
    }

    [Fact]
    public async Task NetworkDegradationTriggersOnLinkDown()
    {
        var engine = new PerformanceEventEngine();
        var timeline = Enumerable.Range(0, 11).Select(i =>
            (System(linkUp: false, t: T0.AddSeconds(i)), Procs(), T0.AddSeconds(i)));

        var events = await RunAsync(engine, timeline);
        var created = events.Single(e => e.Status == PerformanceEventStatus.Capturing);
        Assert.Equal(PerformanceEventType.NetworkDegradation, created.Type);
        Assert.Contains(created.Evidence, e => e.Contains("被动状态"));
        Assert.Null(created.PrimaryProcess);
    }

    [Fact]
    public async Task EventCarriesContributorsRankedByImpact()
    {
        var engine = new PerformanceEventEngine();
        var timeline = Enumerable.Range(0, 11).Select(i => (
            System(cpu: 95, t: T0.AddSeconds(i)),
            Procs(
                Proc(100, "low", 5, t: T0.AddSeconds(i)),
                Proc(101, "mid", 30, workingSet: 2L << 30, t: T0.AddSeconds(i)),
                Proc(102, "high", 60, workingSet: 3L << 30, t: T0.AddSeconds(i))),
            T0.AddSeconds(i)));

        var events = await RunAsync(engine, timeline);
        var created = events.Single(e => e.Status == PerformanceEventStatus.Capturing);

        Assert.NotNull(created.Contributors);
        Assert.Equal(3, created.Contributors.Count);
        // 按影响分降序：high 进程必须在首位
        Assert.Equal("high", created.Contributors[0].ProcessName);
        Assert.True(created.Contributors[0].ImpactScore >= created.Contributors[^1].ImpactScore);
        // 证据必须包含 Top 进程行
        Assert.Contains(created.Evidence, e => e.Contains("Top 进程"));
    }

    [Fact]
    public async Task UserMarkProximityReordersContributors()
    {
        var engine = new PerformanceEventEngine();
        engine.NoteUserMark(T0);
        var timeline = Enumerable.Range(0, 11).Select(i => (
            System(cpu: 95, t: T0.AddSeconds(i)),
            Procs(
                // nearMark：采样时间就在标记附近 -> +15 邻近分
                Proc(100, "nearMark", 20, t: T0.AddSeconds(i)),
                // farMark：CPU 更高但采样时间远离标记（60 秒外），无邻近加分
                Proc(101, "farMark", 35, t: T0.AddSeconds(i).AddMinutes(5))),
            T0.AddSeconds(i)));

        var events = await RunAsync(engine, timeline);
        var created = events.Single(e => e.Status == PerformanceEventStatus.Capturing);

        // nearMark: 20*40/50=16 + 15 = 31；farMark: 35*40/50=28 -> nearMark 应排第一
        Assert.Equal("nearMark", created.Contributors![0].ProcessName);
    }

    [Fact]
    public async Task InaccessibleProcessesAreExcludedFromAttribution()
    {
        var engine = new PerformanceEventEngine();
        var restricted = Proc(400, "restricted", 99, t: T0) with { IsAccessible = false };
        var timeline = Enumerable.Range(0, 11).Select(i =>
            (System(cpu: 95, t: T0.AddSeconds(i)), Procs(restricted), T0.AddSeconds(i)));

        var events = await RunAsync(engine, timeline);
        var created = events.Single(e => e.Status == PerformanceEventStatus.Capturing);
        // 无法访问的进程不参与归因，也不作为主关联进程
        Assert.Null(created.PrimaryProcess);
        Assert.DoesNotContain(created.Contributors ?? [], c => c.ProcessName == "restricted");
    }
}
