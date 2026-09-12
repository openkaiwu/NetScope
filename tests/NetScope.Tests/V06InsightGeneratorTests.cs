using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class V06InsightGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LagSummaryCountsMarksAndLinksRawEvents()
    {
        var events = Enumerable.Range(0, 3).Select(i => Event(PerformanceEventType.UserMarkedLag, Now.AddDays(-i), "app.exe")).ToArray();
        var item = Assert.Single(InsightGenerator.Generate(new(), Now, events, [], [], []));
        Assert.Equal(InsightKind.LagSummary, item.Kind);
        Assert.Equal(3, item.SourceEventIds.Count);
        Assert.Contains("3 次", item.Title, StringComparison.Ordinal);
        Assert.Contains("app.exe", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedEventsNeedAtLeastTwoOccurrencesWithinWindow()
    {
        var events = new[]
        {
            Event(PerformanceEventType.CpuContention, Now.AddDays(-2), "browser.exe"),
            Event(PerformanceEventType.CpuContention, Now.AddDays(-1), "browser.exe"),
            Event(PerformanceEventType.MemoryPressure, Now.AddDays(-40), "old.exe"),
            Event(PerformanceEventType.DiskIoPressure, Now.AddHours(-1), "single.exe")
        };
        var results = InsightGenerator.Generate(new(30), Now, events, [], [], []);
        var repeated = Assert.Single(results);
        Assert.Equal(InsightKind.FrequentPerformanceEvent, repeated.Kind);
        Assert.Equal(2, repeated.SourceEventIds.Count);
        Assert.DoesNotContain("  ", repeated.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void BehaviorPortAndConnectionEvidenceBecomeInsightsAtThresholds()
    {
        var behavior = new ProcessBehaviorFinding(ProcessBehaviorKind.MemoryGrowth, "app.exe", Now.AddHours(-2), Now, 88,
            "内存增长", "增长摘要", ["原始趋势证据"], 42, Now.AddHours(-8));
        var ports = new[] { new PortActivitySummary(8080, PortProtocol.Tcp, "app.exe", 3, 300, Now) };
        var connections = Enumerable.Range(1, 5).Select(i => new TcpConnectionRecord(Guid.NewGuid(), 1, Now.AddDays(-1), "app.exe",
            IpAddressFamily.IPv4, "127.0.0.1", 5000 + i, $"192.0.2.{i}", 443, "Closed", Now.AddHours(-i), Now)).ToArray();
        var results = InsightGenerator.Generate(new(), Now, [], [behavior], ports, connections);
        var memory = Assert.Single(results, x => x.Kind == InsightKind.MemoryGrowth && x.Evidence.Contains("原始趋势证据"));
        Assert.Equal(42, memory.ProcessId);
        Assert.Equal(Now.AddHours(-8), memory.ProcessStartedAt);
        var port = Assert.Single(results, x => x.Kind == InsightKind.PortActivity);
        Assert.Equal(8080, port.Port);
        Assert.Equal(PortProtocol.Tcp, port.Protocol);
        Assert.Contains(results, x => x.Kind == InsightKind.ConnectionActivity);
    }

    [Fact]
    public void BelowThresholdPortAndConnectionActivityStaySilent()
    {
        var ports = new[] { new PortActivitySummary(80, PortProtocol.Tcp, "quiet.exe", 2, 10, Now) };
        var connections = Enumerable.Range(1, 4).Select(i => new TcpConnectionRecord(Guid.NewGuid(), 1, Now, "quiet.exe",
            IpAddressFamily.IPv4, "127.0.0.1", 6000 + i, $"198.51.100.{i}", 443, "Closed", Now, Now)).ToArray();
        Assert.Empty(InsightGenerator.Generate(new(), Now, [], [], ports, connections));
    }

    [Fact]
    public void FiltersByProcessKindAndLimit()
    {
        var behaviors = new[]
        {
            new ProcessBehaviorFinding(ProcessBehaviorKind.MemoryGrowth, "Alpha.exe", Now.AddDays(-1), Now, 80, "Alpha 内存", "", ["e"]),
            new ProcessBehaviorFinding(ProcessBehaviorKind.MemoryGrowth, "Beta.exe", Now.AddDays(-1), Now, 90, "Beta 内存", "", ["e"]),
            new ProcessBehaviorFinding(ProcessBehaviorKind.PeriodicActivity, "Alpha.exe", Now.AddDays(-1), Now, 70, "Alpha 周期", "", ["e"])
        };
        var results = InsightGenerator.Generate(new(30, "alpha", InsightKind.MemoryGrowth, 1), Now, [], behaviors, [], []);
        var only = Assert.Single(results);
        Assert.Equal("Alpha.exe", only.ProcessName);
        Assert.Equal(InsightKind.MemoryGrowth, only.Kind);
    }

    [Fact]
    public void EmptyHistoryProducesNoClaims()
    {
        Assert.Empty(InsightGenerator.Generate(new(), Now, [], [], [], []));
    }

    private static PerformanceEvent Event(PerformanceEventType type, DateTimeOffset at, string process) =>
        new(Guid.NewGuid(), type, PerformanceEventStatus.Closed, at, at.AddSeconds(10), 80,
            "摘要", "可能相关", ["证据"], ["建议"], new ProcessInstanceKey(1, at.AddHours(-1)), process,
            [new PerformanceEventContributor(new ProcessInstanceKey(1, at.AddHours(-1)), process, 50)]);
}
