namespace NetScope.Core.Models;

/// <summary>报告周期：周报为最近 7 天，月报为最近 30 天，均与紧邻的上一周期对比。</summary>
public enum ReportPeriod
{
    Week,
    Month
}

public sealed record ReportRequest(ReportPeriod Period = ReportPeriod.Week);

/// <summary>报告中的一个指标行。Delta 为空表示没有可比的上期数据（例如超出保留期）。</summary>
public sealed record ReportMetric(string Label, string Value, string Delta = "", string Note = "");

public sealed record ReportSection(
    string Title,
    string Summary,
    IReadOnlyList<ReportMetric> Metrics,
    IReadOnlyList<string> Bullets);

/// <summary>
/// 周报/月报。全部结论来自本机已落库的事件、分桶采样、端口会话与连接记录；
/// Markdown 为可导出的纯文本版本，导出动作只在本地写文件，不上传任何内容。
/// </summary>
public sealed record PerformanceReport(
    ReportPeriod Period,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset GeneratedAt,
    string Headline,
    string Summary,
    int Confidence,
    IReadOnlyList<ReportSection> Sections,
    IReadOnlyList<string> Notes,
    string Markdown);

/// <summary>
/// 报告生成输入：一次查询到的全部证据。样本计数用于说明结论的数据覆盖度，
/// 计数为 0 时报告明确写出“样本不足”，不生成填充式结论。
/// </summary>
public sealed record ReportInput
{
    public required ReportPeriod Period { get; init; }
    public required DateTimeOffset Now { get; init; }
    public IReadOnlyList<PerformanceEvent> Events { get; init; } = [];
    public IReadOnlyList<PerformanceEvent> PreviousPeriodEvents { get; init; } = [];
    public IReadOnlyList<ProcessBehaviorFinding> Behaviors { get; init; } = [];
    public IReadOnlyList<PortActivitySummary> Ports { get; init; } = [];
    public IReadOnlyList<TcpConnectionRecord> Connections { get; init; } = [];
    public int SystemSampleCount { get; init; }
    public int ProcessSampleCount { get; init; }
    public int RetentionDays { get; init; } = 30;

    public DateTimeOffset From => Now.AddDays(-WindowDays);
    public DateTimeOffset To => Now;
    public int WindowDays => Period == ReportPeriod.Week ? 7 : 30;
    public DateTimeOffset PreviousFrom => From.AddDays(-WindowDays);
}
