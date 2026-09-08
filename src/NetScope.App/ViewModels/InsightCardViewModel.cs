using NetScope.Core.Models;

namespace NetScope.App.ViewModels;

public sealed class InsightCardViewModel(InsightItem item)
{
    public InsightItem Item { get; } = item;
    public string KindText => Item.Kind switch
    {
        InsightKind.LagSummary => "卡顿统计",
        InsightKind.FrequentPerformanceEvent => "重复事件",
        InsightKind.MemoryGrowth => "内存趋势",
        InsightKind.PeriodicActivity => "周期行为",
        InsightKind.PortActivity => "端口活动",
        InsightKind.ConnectionActivity => "连接活动",
        _ => "洞察"
    };
    public string Title => Item.Title;
    public string Summary => Item.Summary;
    public string ConfidenceText => $"可信度 {Item.Confidence}";
    public string ProcessText => string.IsNullOrEmpty(Item.ProcessName) ? "系统" : Item.ProcessName;
    public string TimeText => $"{Item.From:MM-dd HH:mm} → {Item.To:MM-dd HH:mm}";
    public IReadOnlyList<string> Evidence => Item.Evidence;
    public string SourceText => Item.SourceEventIds.Count > 0
        ? $"关联原始事件 {Item.SourceEventIds.Count} 条，可在事件时间线回查"
        : "依据历史分桶样本或端口/连接记录生成";
}
