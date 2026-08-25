using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class PerformanceMathTests
{
    [Fact]
    public void ProcessCpuPercentIsDeltaOverElapsed()
    {
        // 1 个核心上，1 秒内消耗 0.5 秒 CPU → 50%
        var result = PerformanceMath.ComputeProcessCpuPercent(TimeSpan.Zero, TimeSpan.FromSeconds(0.5), 1.0);
        Assert.InRange(result, 49, 51);
    }

    [Fact]
    public void SystemCpuPercentSubtractsIdleFromTotal()
    {
        // 1 秒窗口内总 CPU 1 秒、其中 idle 0.7 秒 → 30%
        var result = PerformanceMath.ComputeSystemCpuPercent(
            TimeSpan.Zero, TimeSpan.FromSeconds(0.6),
            TimeSpan.Zero, TimeSpan.FromSeconds(0.4),
            TimeSpan.Zero, TimeSpan.FromSeconds(0.7));
        Assert.InRange(result, 29, 31);
    }

    [Fact]
    public void RateIsZeroWhenClockStandsStill()
    {
        Assert.Equal(0, PerformanceMath.ComputeRate(1000, 2000, 0));
        Assert.Equal(0, PerformanceMath.ComputeRate(1000, 2000, -1));
    }

    [Fact]
    public void IoRateIsPerSecond()
    {
        // 2 秒内读取 2000 字节 → 1000 B/s
        Assert.Equal(1000, PerformanceMath.ComputeRate(100, 2100, 2.0));
    }

    [Fact]
    public void ProcessSampleMappingPreservesIdentityAndRate()
    {
        var prev = new ProcessPerformanceReading(
            new(123, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch.AddSeconds(1), "app",
            TimeSpan.Zero, 1000, 800, 500, 300, 10, 20, true);
        var curr = new ProcessPerformanceReading(
            new(123, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch.AddSeconds(2), "app",
            TimeSpan.FromSeconds(0.5), 1200, 900, 1500, 700, 15, 30, true);

        var sample = PerformanceMath.ToSample(prev, curr, 1.0);

        Assert.Equal(new ProcessInstanceKey(123, DateTimeOffset.UnixEpoch), sample.Process);
        Assert.Equal(1200, sample.WorkingSetBytes);
        Assert.Equal(1000, sample.ReadBytesPerSecond);   // (1500-500)/1s
        Assert.Equal(400, sample.WriteBytesPerSecond);   // (700-300)/1s
        Assert.InRange(sample.CpuPercent, 49, 51);
    }
}
