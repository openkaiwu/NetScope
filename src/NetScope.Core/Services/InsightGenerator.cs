using NetScope.Core.Models;

namespace NetScope.Core.Services;

public static class InsightGenerator
{
    public static IReadOnlyList<InsightItem> Generate(InsightQuery query, DateTimeOffset now,
        IEnumerable<PerformanceEvent> events, IEnumerable<ProcessBehaviorFinding> behaviors,
        IEnumerable<PortActivitySummary> ports, IEnumerable<TcpConnectionRecord> connections)
    {
        var from = now.AddDays(-Math.Clamp(query.Days, 1, 30));
        var filteredEvents = events.Where(e => e.StartedAt >= from && e.StartedAt <= now).ToArray();
        var output = new List<InsightItem>();
        var marks = filteredEvents.Where(e => e.Type == PerformanceEventType.UserMarkedLag).ToArray();
        if (marks.Length > 0)
        {
            var related = marks.SelectMany(e => e.Contributors ?? []).GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).FirstOrDefault();
            output.Add(Item(InsightKind.LagSummary, $"{query.Days} 天内标记卡顿 {marks.Length} 次",
                related is null ? "尚无足够证据关联到具体进程" : $"其中 {related.Count()} 次包含 {related.Key} 的相关证据",
                85, from, now, related?.Key,
                [$"统计窗口 {from:g} 至 {now:g}", $"用户标记事件 {marks.Length} 条", "关联表示时间和资源证据重合，不代表确定因果"], marks.Select(x => x.Id)));
        }
        foreach (var group in filteredEvents.Where(e => e.Type != PerformanceEventType.UserMarkedLag)
                     .GroupBy(e => (e.Type, Name: e.PrimaryProcessName ?? "系统")))
        {
            if (group.Count() < 2) continue;
            var ordered = group.OrderBy(e => e.StartedAt).ToArray();
            output.Add(Item(InsightKind.FrequentPerformanceEvent, $"{group.Key.Name} 反复出现{TypeName(group.Key.Type)}",
                $"{query.Days} 天内记录 {ordered.Length} 次，最近一次 {ordered[^1].StartedAt:g}",
                Math.Clamp((int)ordered.Average(x => x.Confidence), 40, 95), ordered[0].StartedAt, ordered[^1].StartedAt,
                group.Key.Name == "系统" ? null : group.Key.Name,
                [$"事件类型：{TypeName(group.Key.Type)}", $"平均可信度 {ordered.Average(x => x.Confidence):0}", "打开事件时间线可查看每次原始证据"], ordered.Select(x => x.Id)));
        }
        foreach (var finding in behaviors)
            output.Add(Item(finding.Kind == ProcessBehaviorKind.MemoryGrowth ? InsightKind.MemoryGrowth : InsightKind.PeriodicActivity,
                finding.Title, finding.Summary, finding.Confidence, finding.From, finding.To, finding.ProcessName, finding.Evidence, []));
        foreach (var group in ports.Where(p => p.LastSeenAt >= from).GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            var count = group.Sum(x => x.SessionCount);
            if (count < 3) continue;
            var top = group.OrderByDescending(x => x.SessionCount).First();
            output.Add(Item(InsightKind.PortActivity, $"{group.Key} 的监听端口活动较多", $"记录 {count} 个占用会话，最常见 {top.Protocol}/{top.Port}",
                Math.Clamp(45 + count, 45, 80), from, group.Max(x => x.LastSeenAt), group.Key,
                [$"端口会话 {count} 个，涉及 {group.Select(x => (x.Port, x.Protocol)).Distinct().Count()} 个端口", "端口出现频繁不代表风险或异常"], []));
        }
        foreach (var group in connections.Where(c => c.LastSeenAt >= from).GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            var uniqueRemote = group.Select(x => (x.RemoteAddress, x.RemotePort)).Distinct().Count();
            if (uniqueRemote < 5) continue;
            output.Add(Item(InsightKind.ConnectionActivity, $"{group.Key} 连接了多个远端", $"观察到 {uniqueRemote} 个不同远端地址/端口组合",
                55, group.Min(x => x.FirstSeenAt), group.Max(x => x.LastSeenAt), group.Key,
                [$"连接记录 {group.Count()} 条，不同远端 {uniqueRemote} 个", "仅统计端点和观察时间，不解析流量内容；短连接可能漏记"], []));
        }
        IEnumerable<InsightItem> result = output;
        if (!string.IsNullOrWhiteSpace(query.ProcessName)) result = result.Where(x => x.ProcessName?.Contains(query.ProcessName, StringComparison.OrdinalIgnoreCase) == true || x.Title.Contains(query.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (query.Kind is { } kind) result = result.Where(x => x.Kind == kind);
        return result.OrderByDescending(x => x.Confidence).ThenByDescending(x => x.To).Take(Math.Clamp(query.Limit, 1, 200)).ToArray();
    }

    private static InsightItem Item(InsightKind kind, string title, string summary, int confidence,
        DateTimeOffset from, DateTimeOffset to, string? process, IEnumerable<string> evidence, IEnumerable<Guid> ids) =>
        new(Guid.NewGuid(), kind, title, summary, confidence, from, to, process, evidence.ToArray(), ids.ToArray());

    private static string TypeName(PerformanceEventType type) => type switch
    {
        PerformanceEventType.CpuContention => "CPU 争用",
        PerformanceEventType.MemoryPressure => "内存压力",
        PerformanceEventType.DiskIoPressure => "磁盘压力",
        PerformanceEventType.NetworkDegradation => "网络退化",
        PerformanceEventType.RelativeCpuAnomaly => "相对 CPU 异常",
        _ => "性能事件"
    };
}
