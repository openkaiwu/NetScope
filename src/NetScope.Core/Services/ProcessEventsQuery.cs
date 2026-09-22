using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>
/// 按进程名汇总时间窗内的关联事件（跨 PID 实例按名字聚合），并统计与用户卡顿标记的重合次数。
/// Collector 的 processEvents 操作与端到端验收测试共用同一口径，保证界面数字与测试一致。
/// </summary>
public static class ProcessEventsQuery
{
    /// <summary>与影响排行一致的卡顿重合口径：事件开始时间与某次标记相差 90 秒以内。</summary>
    public static readonly TimeSpan LagOverlapWindow = TimeSpan.FromSeconds(90);

    public static bool Matches(PerformanceEvent evt, string processName) =>
        string.Equals(evt.PrimaryProcessName, processName, StringComparison.OrdinalIgnoreCase) ||
        evt.Contributors?.Any(c => string.Equals(c.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) == true;

    public static ProcessEventsSummary Summarize(IEnumerable<PerformanceEvent> events, string processName,
        int days, int limit, DateTimeOffset now)
    {
        var windowDays = Math.Clamp(days, 1, 30);
        var from = now.AddDays(-windowDays);
        var all = events as IReadOnlyList<PerformanceEvent> ?? events.ToList();
        var markTimes = all
            .Where(e => e.Type == PerformanceEventType.UserMarkedLag)
            .Select(e => e.StartedAt)
            .ToList();
        var matching = all
            .Where(e => e.StartedAt >= from && e.StartedAt <= now && Matches(e, processName))
            .OrderByDescending(e => e.StartedAt)
            .ToList();
        var lagRelated = matching.Count(m =>
            m.Type != PerformanceEventType.UserMarkedLag &&
            markTimes.Any(t => Math.Abs((m.StartedAt - t).TotalSeconds) <= LagOverlapWindow.TotalSeconds));
        return new ProcessEventsSummary(matching.Count, matching.Take(Math.Clamp(limit, 1, 50)).ToList(), lagRelated, windowDays);
    }
}
