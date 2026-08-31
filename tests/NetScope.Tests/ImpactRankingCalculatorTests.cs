using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class ImpactRankingCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.FromHours(8));

    private static PerformanceEvent Event(
        string primary, double primaryImpact, DateTimeOffset start, double? seconds = 60,
        PerformanceEventType type = PerformanceEventType.CpuContention, string[]? contributors = null)
    {
        var list = contributors is null
            ? new[] { new PerformanceEventContributor(new ProcessInstanceKey(1, start), primary, primaryImpact) }
            : contributors.Select((name, i) => new PerformanceEventContributor(new ProcessInstanceKey(10 + i, start), name, primaryImpact)).ToArray();
        return new PerformanceEvent(
            Guid.NewGuid(), type, PerformanceEventStatus.Closed, start,
            seconds is { } s ? start.AddSeconds(s) : null, 60, "s", "c",
            [], [], new ProcessInstanceKey(1, start), primary, list);
    }

    [Fact]
    public void Rank_EmptyEvents_ReturnsEmpty()
    {
        Assert.Empty(ImpactRankingCalculator.Rank([], 7, Now));
    }

    [Fact]
    public void Rank_ExcludesEventsOutsideWindow()
    {
        var events = new[] { Event("a.exe", 50, Now.AddDays(-8), 60) };
        Assert.Empty(ImpactRankingCalculator.Rank(events, 7, Now));
    }

    [Fact]
    public void Rank_FrequentLongProcess_OutranksRareOne()
    {
        var frequent = Enumerable.Range(0, 10)
            .Select(i => Event("busy.exe", 60, Now.AddDays(-1).AddMinutes(-i * 10), 300))
            .ToList();
        var rare = new[] { Event("quiet.exe", 60, Now.AddDays(-2), 30) };
        var ranking = ImpactRankingCalculator.Rank(frequent.Concat(rare).ToList(), 7, Now);

        Assert.Equal("busy.exe", ranking[0].ProcessName);
        Assert.Equal("quiet.exe", ranking[1].ProcessName);
        Assert.Equal(10, ranking[0].EventCount);
        Assert.True(ranking[0].Score > ranking[1].Score);
    }

    [Fact]
    public void Rank_LagProximity_IncreasesLagCount()
    {
        var mark = Event("mark", 0, Now.AddHours(-1), 5, PerformanceEventType.UserMarkedLag);
        var near = Event("a.exe", 50, Now.AddHours(-1).AddSeconds(-30), 60); // 标记前 30 秒
        var far = Event("a.exe", 50, Now.AddDays(-3), 60);
        var ranking = ImpactRankingCalculator.Rank([mark, near, far], 7, Now);

        Assert.Single(ranking);
        Assert.Equal(1, ranking[0].LagRelatedCount);
    }

    [Fact]
    public void Rank_UserMarkedLagEvents_AreNotCountedAsResourceEvents()
    {
        var mark = Event("a.exe", 0, Now.AddHours(-1), 5, PerformanceEventType.UserMarkedLag);
        var ranking = ImpactRankingCalculator.Rank([mark], 7, Now);
        Assert.Empty(ranking);
    }

    [Fact]
    public void Rank_DurationSharedByImpactShare_AmongContributors()
    {
        var start = Now.AddHours(-2);
        var evt = new PerformanceEvent(
            Guid.NewGuid(), PerformanceEventType.CpuContention, PerformanceEventStatus.Closed,
            start, start.AddSeconds(120), 60, "s", "c", [], [],
            new ProcessInstanceKey(1, start), "primary.exe",
            new[]
            {
                new PerformanceEventContributor(new ProcessInstanceKey(10, start), "primary.exe", 75),
                new PerformanceEventContributor(new ProcessInstanceKey(11, start), "side.exe", 25)
            });
        var ranking = ImpactRankingCalculator.Rank([evt], 7, Now);

        var primary = ranking.Single(r => r.ProcessName == "primary.exe");
        var side = ranking.Single(r => r.ProcessName == "side.exe");
        Assert.Equal(120 * 0.75, primary.TotalSeconds, 1);
        Assert.Equal(120 * 0.25, side.TotalSeconds, 1);
        Assert.Equal(1, primary.EventCount);
        Assert.Equal(1, side.EventCount);
    }

    [Fact]
    public void Rank_ScoreIsClampedToHundred_AndOrdered()
    {
        var events = Enumerable.Range(0, 30)
            .Select(i => Event("x.exe", 100, Now.AddMinutes(-i * 30), 600))
            .ToList();
        events.Add(new PerformanceEvent(
            Guid.NewGuid(), PerformanceEventType.UserMarkedLag, PerformanceEventStatus.Confirmed,
            Now.AddMinutes(-10), Now.AddMinutes(-9), 100, "s", "c", [], [],
            null, "mark", Array.Empty<PerformanceEventContributor>()));
        // 30 分钟内的事件与标记重合（标记在 10 分钟前，事件在其前 30 秒，处于 90 秒窗口内）
        events.Add(Event("x.exe", 100, Now.AddMinutes(-10).AddSeconds(-30), 600));

        var ranking = ImpactRankingCalculator.Rank(events, 7, Now);
        Assert.True(ranking[0].Score is > 0 and <= 100);
        Assert.Equal(1, ranking[0].LagRelatedCount);
        Assert.Equal("x.exe", ranking[0].ProcessName);
    }
}
