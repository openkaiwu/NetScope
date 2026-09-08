using NetScope.Core.Models;
using NetScope.Core.Services;
using NetScope.Windows.Ipc;

namespace NetScope.Tests;

public sealed class V04AnalysisTests
{
    [Fact]
    public async Task PhysicalDiskCountersProduceFiniteSamplesOnWindows()
    {
        using var provider = new NetScope.Windows.Performance.DiskPerformanceProvider();
        Assert.Null(provider.Read());
        await Task.Delay(1100);
        var sample = provider.Read();
        Assert.NotNull(sample);
        Assert.InRange(sample.ActivePercent, 0, 100);
        Assert.True(double.IsFinite(sample.ReadLatencyMs) && sample.ReadLatencyMs >= 0);
        Assert.True(double.IsFinite(sample.WriteLatencyMs) && sample.WriteLatencyMs >= 0);
        Assert.True(double.IsFinite(sample.QueueLength) && sample.QueueLength >= 0);
    }
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private static ProcessPerformanceSample Proc(int second, double cpu, int pid = 10) =>
        new(new(pid, Start), Start.AddSeconds(second), "rgb.exe", cpu, 1000, 1000, 0, 0, 0, 0);
    private static SystemPerformanceSample System(int second = 0) => new(Start.AddSeconds(second), 10, 8L << 30, 16L << 30, 0, 0);

    [Fact]
    public void LowBaselineSpikeTriggersBelowAbsoluteThresholdAndCloses()
    {
        var detector = new RelativeAnomalyDetector();
        for (var i = 0; i < 35; i++) Assert.Empty(detector.Evaluate([Proc(i, 0.2)], Start.AddSeconds(i)));
        var events = new List<PerformanceEvent>();
        for (var i = 35; i <= 40; i++) events.AddRange(detector.Evaluate([Proc(i, 15)], Start.AddSeconds(i)));
        var evt = Assert.Single(events);
        Assert.Equal(PerformanceEventType.RelativeCpuAnomaly, evt.Type);
        Assert.Equal(Start.AddSeconds(35), evt.StartedAt);
        var closed = Assert.Single(detector.Evaluate([Proc(41, 0.2)], Start.AddSeconds(41)));
        Assert.Equal(evt.Id, closed.Id);
        Assert.Equal(PerformanceEventStatus.Closed, closed.Status);
        for (var i = 42; i < 60; i++) Assert.Empty(detector.Evaluate([Proc(i, 15)], Start.AddSeconds(i)));
    }

    [Fact]
    public void StableHighLoadIsNotRelativeAnomaly()
    {
        var detector = new RelativeAnomalyDetector();
        for (var i = 0; i < 150; i++) Assert.Empty(detector.Evaluate([Proc(i, 30)], Start.AddSeconds(i)));
    }

    [Fact]
    public void ShortSpikeDoesNotTriggerAndPidReuseStartsNewBaseline()
    {
        var detector = new RelativeAnomalyDetector();
        for (var i = 0; i < 35; i++) detector.Evaluate([Proc(i, 0.2)], Start.AddSeconds(i));
        for (var i = 35; i < 38; i++) Assert.Empty(detector.Evaluate([Proc(i, 15)], Start.AddSeconds(i)));
        Assert.Empty(detector.Evaluate([Proc(38, 0.2)], Start.AddSeconds(38)));
        for (var i = 39; i < 50; i++) Assert.Empty(detector.Evaluate([Proc(i, 15) with { Process = new(10, Start.AddSeconds(39)) }], Start.AddSeconds(i)));
    }

    [Fact]
    public void BurstSamplesCannotAccelerateWarmupAndGapsResetBaseline()
    {
        var detector = new RelativeAnomalyDetector();
        for (var i = 0; i < 60; i++) Assert.Empty(detector.Evaluate([Proc(0, i < 30 ? 0.2 : 15)], Start.AddMilliseconds(i * 100)));
        for (var i = 100; i < 120; i++) Assert.Empty(detector.Evaluate([Proc(i, 15)], Start.AddSeconds(i)));
    }

    [Fact]
    public async Task LowThroughputDiskStallProducesEvidenceAndCloses()
    {
        var engine = new PerformanceEventEngine(new() { IoSustainSeconds = 2 });
        var events = new List<PerformanceEvent>();
        for (var i = 0; i <= 2; i++)
            events.AddRange(await engine.EvaluateAsync(System(i) with { Disk = new(80, 60, 4, 99) }, [], Start.AddSeconds(i)));
        var evt = Assert.Single(events);
        Assert.Equal(PerformanceEventType.DiskIoPressure, evt.Type);
        Assert.Contains(evt.Evidence, e => e.Contains("80.0 ms"));
        var closed = Assert.Single(await engine.EvaluateAsync(System(3) with { Disk = new(1, 1, 0, 2) }, [], Start.AddSeconds(3)));
        Assert.Equal(evt.Id, closed.Id);
        Assert.Equal(PerformanceEventStatus.Closed, closed.Status);
    }

    [Fact]
    public void ResponsivenessFallsWithPressureAndExplainsMissingMetrics()
    {
        var healthy = ResponsivenessCalculator.Compute(System() with { Disk = new(0, 0, 0, 0) }, []);
        var pressured = ResponsivenessCalculator.Compute(System() with { CpuPercent = 100, AvailableMemoryBytes = 0, Disk = new(100, 100, 10, 100) }, []);
        Assert.Equal(100, healthy.Score);
        Assert.InRange(pressured.Score, 0, 20);
        Assert.Contains(ResponsivenessCalculator.Compute(System(), []).Evidence, e => e.Contains("不完整"));
        var marked = ResponsivenessCalculator.Compute(System(), [], Start);
        var expired = ResponsivenessCalculator.Compute(System(31), [], Start);
        Assert.Equal(expired.Score - 5, marked.Score);
    }

    [Fact]
    public void AnalysisSurvivesIpcRoundTripAndOldPayloadDefaultsToUnknown()
    {
        var sample = System() with { Disk = new(3, 4, 2, 70), Responsiveness = new(88, "较流畅", ["evidence"]) };
        var roundtrip = CollectorDtos.ToModel(CollectorProtocol.Deserialize<SystemSampleDto>(CollectorProtocol.Serialize(CollectorDtos.ToDto(sample)))!);
        Assert.Equal(sample.Disk, roundtrip.Disk);
        Assert.Equal(88, roundtrip.Responsiveness!.Score);
        var old = CollectorProtocol.Deserialize<SystemSampleDto>("{\"timestamp\":\"2026-09-01T00:00:00Z\",\"cpuPercent\":10}")!;
        Assert.Null(old.Disk);
        Assert.Null(old.Responsiveness);
    }
}
