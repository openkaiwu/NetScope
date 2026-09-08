using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class V05BehaviorAnalysisTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DetectsStableLongTermMemoryGrowth()
    {
        var points = Enumerable.Range(0, 61).Select(i => Point("leaky.exe", i, 1, 100 + i * 8, 0)).ToArray();
        var finding = Assert.Single(new ProcessBehaviorAnalyzer().Analyze(points), x => x.Kind == ProcessBehaviorKind.MemoryGrowth);
        Assert.Equal("leaky.exe", finding.ProcessName);
        Assert.InRange(finding.Confidence, 70, 92);
        Assert.Contains(finding.Evidence, x => x.Contains("R²=1.00", StringComparison.Ordinal));
    }

    [Fact]
    public void FlatAndSawtoothMemoryDoNotProduceLeakFinding()
    {
        var flat = Enumerable.Range(0, 121).Select(i => Point("flat.exe", i, 1, 300, 0));
        var saw = Enumerable.Range(0, 121).Select(i => Point("cache.exe", i, 1, 200 + (i % 10) * 20, 0));
        var results = new ProcessBehaviorAnalyzer().Analyze(flat.Concat(saw));
        Assert.DoesNotContain(results, x => x.Kind == ProcessBehaviorKind.MemoryGrowth);
    }

    [Fact]
    public void ProcessRestartsAreNotJoinedIntoOneFalseGrowthLine()
    {
        var options = new ProcessBehaviorAnalyzerOptions
        {
            MinimumMemoryWindow = TimeSpan.FromMinutes(10),
            MinimumMemoryPoints = 10,
            MinimumMemoryGrowthMb = 100,
            MinimumMemorySlopeMbPerMinute = 2
        };
        var firstStart = Start;
        var secondStart = Start.AddMinutes(20);
        var first = Enumerable.Range(0, 7).Select(i => Point("worker.exe", i, 1, 100 + i * 40, 0, 42, firstStart));
        var second = Enumerable.Range(0, 7).Select(i => Point("worker.exe", 20 + i, 1, 100 + i * 40, 0, 77, secondStart));
        Assert.DoesNotContain(new ProcessBehaviorAnalyzer(options).Analyze(first.Concat(second)), x => x.Kind == ProcessBehaviorKind.MemoryGrowth);
    }

    [Fact]
    public void DetectsRegularPeriodicCpuBursts()
    {
        var points = Enumerable.Range(0, 181)
            .Select(i => Point("updater.exe", i, i % 20 == 5 ? 35 : 1, 100, 0)).ToArray();
        var finding = Assert.Single(new ProcessBehaviorAnalyzer().Analyze(points), x => x.Kind == ProcessBehaviorKind.PeriodicActivity);
        Assert.Contains("20 分钟", finding.Summary, StringComparison.Ordinal);
        Assert.True(finding.Confidence >= 75);
    }

    [Fact]
    public void IrregularBurstsAndContinuousLoadAreNotCalledPeriodic()
    {
        var irregularMinutes = new HashSet<int> { 5, 16, 43, 91, 130, 179 };
        var irregular = Enumerable.Range(0, 181).Select(i => Point("random.exe", i, irregularMinutes.Contains(i) ? 40 : 1, 100, 0));
        var continuous = Enumerable.Range(0, 181).Select(i => Point("busy.exe", i, 40, 100, 0));
        var findings = new ProcessBehaviorAnalyzer().Analyze(irregular.Concat(continuous));
        Assert.DoesNotContain(findings, x => x.Kind == ProcessBehaviorKind.PeriodicActivity);
    }

    [Fact]
    public void InvalidSamplesAreIgnoredWithoutBreakingOtherProcesses()
    {
        var valid = Enumerable.Range(0, 61).Select(i => Point("valid.exe", i, 1, 100 + i * 8, 0));
        var invalid = new[]
        {
            new ProcessHistoryPoint("", Start, double.NaN, -1, -1),
            new ProcessHistoryPoint("bad.exe", Start, double.PositiveInfinity, 0, 0)
        };
        Assert.Contains(new ProcessBehaviorAnalyzer().Analyze(valid.Concat(invalid)), x => x.ProcessName == "valid.exe");
    }

    [Fact]
    public void ThirtyDaySimulationRemainsBoundedAndFindsExpectedSignals()
    {
        var points = new List<ProcessHistoryPoint>(30 * 24 * 12 * 3);
        for (var i = 0; i < 30 * 24 * 12; i++)
        {
            var minute = i * 5;
            points.Add(Point("stable.exe", minute, 2, 300, 0));
            points.Add(Point("growing.exe", minute, 1, 100 + minute * 0.12, 0));
            points.Add(Point("sync.exe", minute, i % 6 == 0 ? 25 : 1, 120, 0));
        }
        var findings = new ProcessBehaviorAnalyzer().Analyze(points);
        Assert.Contains(findings, x => x.Kind == ProcessBehaviorKind.MemoryGrowth && x.ProcessName == "growing.exe");
        Assert.Contains(findings, x => x.Kind == ProcessBehaviorKind.PeriodicActivity && x.ProcessName == "sync.exe");
        Assert.DoesNotContain(findings, x => x.ProcessName == "stable.exe");
        Assert.True(findings.Count <= 6);
    }

    private static ProcessHistoryPoint Point(string name, int minute, double cpu, double memoryMb, long io,
        int pid = 1, DateTimeOffset? startedAt = null) =>
        new(name, Start.AddMinutes(minute), cpu, (long)(memoryMb * 1024 * 1024), io, 1, pid, startedAt ?? Start);
}
