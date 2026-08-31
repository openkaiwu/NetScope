namespace NetScope.Core.Models;

/// <summary>
/// 端口占用会话：一次连续的“同一进程占用同一端口”区间。
/// 由 Collector 在端口快照差分中生成（连续两次出现视为开始，消失或换主视为结束）。
/// </summary>
public sealed record PortSessionRecord(
    int Port,
    PortProtocol Protocol,
    string ProcessName,
    int ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt)
{
    public double DurationSeconds => Math.Max(0, (EndedAt - StartedAt).TotalSeconds);
}

/// <summary>端口在时间窗内的占用聚合：按“端口 + 协议 + 进程名”分组，回答“8080 过去 7 天被谁占用过几次”。</summary>
public sealed record PortUsageSummary(
    int Port,
    PortProtocol Protocol,
    string ProcessName,
    int SessionCount,
    double TotalSeconds,
    DateTimeOffset LastSeenAt);

/// <summary>7 天影响排行条目：按事件参与、累计时长与卡顿重合合成的聚合评分，只用于排序展示。</summary>
public sealed record ImpactRankEntry(
    string ProcessName,
    int EventCount,
    double TotalSeconds,
    int LagRelatedCount,
    int Score);

/// <summary>某进程名在时间窗内关联的性能事件汇总。</summary>
public sealed record ProcessEventsSummary(int TotalCount, IReadOnlyList<PerformanceEvent> Events);
