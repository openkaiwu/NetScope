using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

/// <summary>V1.0 归因链验收：五阶段齐全、措辞带不确定语义、无候选时诚实降级、可重放且确定性。</summary>
public sealed class V10AttributionChainTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly ProcessInstanceKey MsEdge = new(28440, At.AddHours(-1));
    private static readonly ProcessInstanceKey DevEnv = new(3916, At.AddHours(-1));

    private static PerformanceEvent CpuEvent(
        IReadOnlyList<PerformanceEventContributor>? contributors = null,
        ProcessInstanceKey? primary = null, string? primaryName = "msedge.exe") => new(
        Guid.NewGuid(), PerformanceEventType.CpuContention, PerformanceEventStatus.Closed,
        At.AddMinutes(-6), At.AddMinutes(-6).AddSeconds(12), 80,
        "系统 CPU 连续 12 秒超过 85%，疑似资源争用",
        "msedge.exe 在事件期间 CPU 与内存均显著抬升",
        ["系统 CPU 峰值 94%，持续 12 秒", "msedge.exe 平均 CPU 42%，明显高于基线"],
        ["检查浏览器后台标签与扩展数量"],
        primary ?? MsEdge, primaryName, contributors ?? DefaultContributors());

    private static List<PerformanceEventContributor> DefaultContributors() =>
    [
        new(MsEdge, "msedge.exe", 70),
        new(DevEnv, "devenv.exe", 20),
        new(new ProcessInstanceKey(11484, At.AddHours(-1)), "NetScope.Collector.exe", 10)
    ];

    [Fact]
    public void Build_ProducesFiveStagesInOrder()
    {
        var chain = AttributionChainBuilder.Build(CpuEvent(), new(true, 42, 0, At.AddDays(-30)));
        Assert.Equal(5, chain.Steps.Count);
        Assert.Equal(
            [AttributionStage.Event, AttributionStage.Candidates, AttributionStage.Evidence, AttributionStage.Hypothesis, AttributionStage.Confidence],
            chain.Steps.Select(s => s.Stage).ToArray());
        Assert.True(chain.RawSamplesAvailable);
        Assert.Contains("系统 42 条", chain.ReplayNote);
    }

    [Fact]
    public void Build_TopContributorDrivesHypothesisWithHedgedWording()
    {
        var chain = AttributionChainBuilder.Build(CpuEvent(), new(true, 42, 0, At.AddDays(-30)));
        var hypothesis = chain.Steps.Single(s => s.Stage == AttributionStage.Hypothesis);
        Assert.Contains("msedge.exe", hypothesis.Detail);
        Assert.Contains("疑似", hypothesis.Detail);
        Assert.Contains(hypothesis.Items, item => item.Contains("不是确定性定责"));
        var candidates = chain.Steps.Single(s => s.Stage == AttributionStage.Candidates);
        Assert.Contains("msedge.exe", candidates.Items[0]);
        Assert.Contains("影响分 70", candidates.Items[0]);
    }

    [Fact]
    public void Build_NoCandidatesFallsBackToEnvironmentHypothesis()
    {
        var evt = new PerformanceEvent(
            Guid.NewGuid(), PerformanceEventType.NetworkDegradation, PerformanceEventStatus.Closed,
            At.AddMinutes(-6), At.AddMinutes(-5), 70,
            "网络链路可能退化", "活动网卡持续处于断开或不可用状态",
            ["活动网卡处于断开状态"], ["检查网线"], null, null, []);
        var chain = AttributionChainBuilder.Build(evt, AttributionReplayContext.Unavailable);

        var candidates = chain.Steps.Single(s => s.Stage == AttributionStage.Candidates);
        Assert.Contains("无法给出候选进程排序", candidates.Detail);
        var hypothesis = chain.Steps.Single(s => s.Stage == AttributionStage.Hypothesis);
        Assert.Contains("系统或环境层面", hypothesis.Detail);
        var confidence = chain.Steps.Single(s => s.Stage == AttributionStage.Confidence);
        Assert.InRange(confidence.Confidence!.Value, 30, 60); // 无进程证据时压低上限
    }

    [Fact]
    public void Build_PrimaryWithoutContributorsGivesDirectionalHintOnly()
    {
        var chain = AttributionChainBuilder.Build(CpuEvent(contributors: [], primaryName: "svchost.exe"));
        var hypothesis = chain.Steps.Single(s => s.Stage == AttributionStage.Hypothesis);
        Assert.Contains("svchost.exe", hypothesis.Detail);
        Assert.Contains("方向性线索", hypothesis.Detail);
    }

    [Fact]
    public void Build_ReplayAvailabilityAffectsEvidenceAndConfidence()
    {
        var available = AttributionChainBuilder.Build(CpuEvent(), new(true, 42, 12, At.AddDays(-30)));
        var unavailable = AttributionChainBuilder.Build(CpuEvent(), AttributionReplayContext.Unavailable);

        Assert.Contains(available.Steps.Single(s => s.Stage == AttributionStage.Evidence).Items, item => item.Contains("可回放"));
        Assert.Contains(unavailable.Steps.Single(s => s.Stage == AttributionStage.Evidence).Items, item => item.Contains("不可回放"));
        var availableConfidence = available.Steps.Single(s => s.Stage == AttributionStage.Confidence).Confidence!.Value;
        var unavailableConfidence = unavailable.Steps.Single(s => s.Stage == AttributionStage.Confidence).Confidence!.Value;
        Assert.True(availableConfidence > unavailableConfidence);
    }

    [Fact]
    public void Build_IsDeterministicForReplay()
    {
        var evt = CpuEvent();
        var first = AttributionChainBuilder.Build(evt, new(true, 42, 12, At.AddDays(-30)));
        var second = AttributionChainBuilder.Build(evt, new(true, 42, 12, At.AddDays(-30)));
        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal(first.Headline, second.Headline);
        Assert.Equal(first.Steps.Select(s => (s.Stage, s.Title, s.Detail, string.Join("|", s.Items), s.Confidence)).ToArray(),
            second.Steps.Select(s => (s.Stage, s.Title, s.Detail, string.Join("|", s.Items), s.Confidence)).ToArray());
    }

    [Fact]
    public void Build_ConfidenceStaysWithinBoundedRange()
    {
        var low = CpuEvent() with { Confidence = 10 };
        var high = CpuEvent() with { Confidence = 100 };
        Assert.InRange(AttributionChainBuilder.Build(low, AttributionReplayContext.Unavailable)
            .Steps.Single(s => s.Stage == AttributionStage.Confidence).Confidence!.Value, 30, 95);
        Assert.InRange(AttributionChainBuilder.Build(high, new(true, 99, 99, At.AddDays(-30)))
            .Steps.Single(s => s.Stage == AttributionStage.Confidence).Confidence!.Value, 30, 95);
    }
}
