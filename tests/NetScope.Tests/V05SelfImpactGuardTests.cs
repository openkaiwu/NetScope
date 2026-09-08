using NetScope.Core.Models;
using NetScope.Core.Services;
using NetScope.Windows.Performance;

namespace NetScope.Tests;

public sealed class V05SelfImpactGuardTests
{
    private static SelfImpactGuard Guard() => new(new SelfImpactGuardOptions
    {
        CpuHighPercent = 10,
        CpuSeverePercent = 20,
        MemoryHighBytes = 100,
        MemorySevereBytes = 200,
        WriteHighBytesPerSecond = 100,
        WriteSevereBytesPerSecond = 200,
        CycleHighMilliseconds = 100,
        CycleSevereMilliseconds = 200,
        BreachSamples = 3,
        RecoverySamples = 2
    });

    [Fact]
    public void SustainedHighLoadDegradesOneLevelAtATime()
    {
        var guard = Guard();
        Assert.Equal(SamplingMode.Normal, guard.Evaluate(11, 0, 0, 0).Mode);
        guard.Evaluate(11, 0, 0, 0);
        Assert.Equal(SamplingMode.Reduced, guard.Evaluate(11, 0, 0, 0).Mode);
        Assert.Equal(SamplingMode.Reduced, guard.Evaluate(11, 0, 0, 0).Mode);
        guard.Evaluate(11, 0, 0, 0);
        Assert.Equal(SamplingMode.Minimal, guard.Evaluate(11, 0, 0, 0).Mode);
    }

    [Fact]
    public void SevereLoadGoesDirectlyToMinimalAfterSustainedBreach()
    {
        var guard = Guard();
        guard.Evaluate(21, 0, 0, 0);
        guard.Evaluate(21, 0, 0, 0);
        var profile = guard.Evaluate(21, 0, 0, 0);
        Assert.Equal(SamplingMode.Minimal, profile.Mode);
        Assert.Equal(5000, profile.PerformanceIntervalMilliseconds);
        Assert.Equal(10000, profile.PortIntervalMilliseconds);
        Assert.Equal(8, profile.ProcessHistoryTopN);
    }

    [Fact]
    public void RecoveryUsesHysteresisAndRestoresOneLevelAtATime()
    {
        var guard = Guard();
        for (var i = 0; i < 3; i++) guard.Evaluate(21, 0, 0, 0);
        Assert.Equal(SamplingMode.Minimal, guard.Mode);
        guard.Evaluate(0, 0, 0, 0);
        Assert.Equal(SamplingMode.Minimal, guard.Mode);
        guard.Evaluate(0, 0, 0, 0);
        Assert.Equal(SamplingMode.Reduced, guard.Mode);
        guard.Evaluate(0, 0, 0, 0);
        guard.Evaluate(0, 0, 0, 0);
        Assert.Equal(SamplingMode.Normal, guard.Mode);
    }

    [Theory]
    [InlineData(0, 101, 0, 0, "内存")]
    [InlineData(0, 0, 101, 0, "磁盘写入")]
    [InlineData(0, 0, 0, 101, "采样周期")]
    [InlineData(11, 0, 0, 0, "CPU")]
    public void EveryBudgetDimensionProducesEvidence(double cpu, long memory, long write, double cycle, string expected)
    {
        var guard = Guard();
        guard.Evaluate(cpu, memory, write, cycle);
        Assert.Contains(guard.LastReasons, reason => reason.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ExactBudgetBoundaryDoesNotTrigger()
    {
        var guard = Guard();
        for (var i = 0; i < 100; i++) guard.Evaluate(10, 100, 100, 100);
        Assert.Equal(SamplingMode.Normal, guard.Mode);
        Assert.Empty(guard.LastReasons);
    }

    [Fact]
    public async Task RealCollectorSelfMonitorReturnsFiniteNonnegativeReadings()
    {
        using var monitor = new CollectorSelfMonitor();
        _ = monitor.Read();
        await Task.Delay(25);
        var reading = monitor.Read();
        Assert.True(double.IsFinite(reading.CpuPercent));
        Assert.InRange(reading.CpuPercent, 0, 100);
        Assert.True(reading.WorkingSetBytes > 0);
        Assert.True(reading.ReadBytesPerSecond >= 0);
        Assert.True(reading.WriteBytesPerSecond >= 0);
    }
}
