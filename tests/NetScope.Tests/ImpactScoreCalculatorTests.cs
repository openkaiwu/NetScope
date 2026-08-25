using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

/// <summary>影响分计算测试：分项上限、前台与用户标记加权、总体夹紧与排序语义。</summary>
public sealed class ImpactScoreCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ProcessPerformanceSample Sample(
        double cpu = 0, long workingSet = 0, long readBps = 0, long writeBps = 0,
        bool foreground = false, DateTimeOffset? t = null, bool accessible = true) =>
        new(new ProcessInstanceKey(1, T0), t ?? T0, "proc", cpu, workingSet, 0, readBps, writeBps, 0, 0, accessible, null, foreground);

    [Fact]
    public void InaccessibleProcessScoresZero()
    {
        // 无法访问的进程没有证据可用，不得参与归因排序
        Assert.Equal(0, ImpactScoreCalculator.Compute(Sample(cpu: 90, accessible: false)));
    }

    [Fact]
    public void IdleProcessScoresZero()
    {
        Assert.Equal(0, ImpactScoreCalculator.Compute(Sample()));
    }

    [Fact]
    public void CpuContributionCapsAtForty()
    {
        // 50% 单核占用记满 40 分；100% 不再增加
        var half = ImpactScoreCalculator.Compute(Sample(cpu: 50));
        var full = ImpactScoreCalculator.Compute(Sample(cpu: 100));
        Assert.Equal(40, half, 3);
        Assert.Equal(40, full, 3);
    }

    [Fact]
    public void CpuContributionScalesLinearlyBelowCap()
    {
        var quarter = ImpactScoreCalculator.Compute(Sample(cpu: 12.5));
        Assert.Equal(10, quarter, 3); // 12.5/50 * 40 = 10
    }

    [Fact]
    public void MemoryContributionCapsAtTwentyFive()
    {
        var fourGb = ImpactScoreCalculator.Compute(Sample(workingSet: 4L << 30));
        var eightGb = ImpactScoreCalculator.Compute(Sample(workingSet: 8L << 30));
        Assert.Equal(25, fourGb, 3);
        Assert.Equal(25, eightGb, 3);
    }

    [Fact]
    public void IoContributionCapsAtTwentyFive()
    {
        var fiftyMb = ImpactScoreCalculator.Compute(Sample(readBps: 50L << 20));
        var huge = ImpactScoreCalculator.Compute(Sample(readBps: 1L << 30, writeBps: 1L << 30));
        Assert.Equal(25, fiftyMb, 3);
        Assert.Equal(25, huge, 3);
    }

    [Fact]
    public void IoContributionSumsReadAndWrite()
    {
        // 读 25MB/s + 写 25MB/s = 50MB/s -> 满分 25
        var score = ImpactScoreCalculator.Compute(Sample(readBps: 25L << 20, writeBps: 25L << 20));
        Assert.Equal(25, score, 3);
    }

    [Fact]
    public void ForegroundAddsTenPoints()
    {
        var background = ImpactScoreCalculator.Compute(Sample(cpu: 50));
        var foreground = ImpactScoreCalculator.Compute(Sample(cpu: 50, foreground: true));
        Assert.Equal(10, foreground - background, 3);
    }

    [Fact]
    public void UserMarkProximityAddsUpToFifteenWithinSixtySeconds()
    {
        var baseScore = ImpactScoreCalculator.Compute(Sample(cpu: 50)); // 40
        var atMark = ImpactScoreCalculator.Compute(Sample(cpu: 50), T0);
        var halfMinute = ImpactScoreCalculator.Compute(Sample(cpu: 50), T0.AddSeconds(30));
        var oneMinute = ImpactScoreCalculator.Compute(Sample(cpu: 50), T0.AddSeconds(60));
        var beyond = ImpactScoreCalculator.Compute(Sample(cpu: 50), T0.AddSeconds(61));

        Assert.Equal(15, atMark - baseScore, 3);
        Assert.Equal(7.5, halfMinute - baseScore, 3);
        Assert.Equal(0, oneMinute - baseScore, 3);
        Assert.Equal(0, beyond - baseScore, 3);
    }

    [Fact]
    public void MarkProximityCountsBothDirections()
    {
        // 采样时间在标记之后也计入邻近度
        var before = ImpactScoreCalculator.Compute(Sample(cpu: 50), T0.AddSeconds(-30));
        Assert.Equal(7.5, before - 40, 3);
    }

    [Fact]
    public void TotalScoreClampsToHundred()
    {
        var extreme = ImpactScoreCalculator.Compute(Sample(cpu: 100, workingSet: 8L << 30, readBps: 1L << 30, foreground: true), T0);
        Assert.Equal(100, extreme, 3);
    }

    [Fact]
    public void RankOrdersByScoreDescending()
    {
        var low = Sample(cpu: 5) with { Process = new ProcessInstanceKey(1, T0), Name = "low" };
        var mid = Sample(cpu: 30, workingSet: 2L << 30) with { Process = new ProcessInstanceKey(2, T0), Name = "mid" };
        var high = Sample(cpu: 60, workingSet: 3L << 30, foreground: true) with { Process = new ProcessInstanceKey(3, T0), Name = "high" };

        var ranked = ImpactScoreCalculator.Rank([low, mid, high]).ToList();

        Assert.Equal("high", ranked[0].Name);
        Assert.Equal("mid", ranked[1].Name);
        Assert.Equal("low", ranked[2].Name);
    }
}
