namespace NetScope.Core.Models;

/// <summary>归因链的五个阶段：事件 → 候选进程 → 证据 → 根因假设 → 置信度。</summary>
public enum AttributionStage
{
    Event,
    Candidates,
    Evidence,
    Hypothesis,
    Confidence
}

/// <summary>归因链的一步。Replayable 表示这一步依赖的原始采样仍在保留期内、可以回放原始数据。</summary>
public sealed record AttributionChainStep(
    AttributionStage Stage,
    string Title,
    string Detail,
    IReadOnlyList<string> Items,
    bool Replayable = true,
    int? Confidence = null);

/// <summary>
/// 回放上下文：说明原始样本是否仍在保留期内，以及窗口内实际读到多少条采样。
/// 由调用方在查询历史后填写；缺失时链条按“原始采样不可回放”降级，但仍完整可读。
/// </summary>
public sealed record AttributionReplayContext(
    bool RawSamplesAvailable,
    int SystemSampleCount = 0,
    int ProcessSampleCount = 0,
    DateTimeOffset? RetentionCutoff = null)
{
    public static AttributionReplayContext Unavailable { get; } = new(false);
}

/// <summary>
/// 一次事件的完整归因链。链条只由已落库的事件（含贡献者、证据、可信度）与保留期内的采样推导，
/// 不依赖生成时的内存状态，因此同一事件在任何时候都能重放出一致结论。
/// 所有结论都是相关性证据，使用“可能/疑似”语义，不构成确定性定责。
/// </summary>
public sealed record AttributionChain(
    Guid EventId,
    PerformanceEventType EventType,
    string Headline,
    IReadOnlyList<AttributionChainStep> Steps,
    bool RawSamplesAvailable,
    string ReplayNote);
