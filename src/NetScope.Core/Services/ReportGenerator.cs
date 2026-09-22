using System.Globalization;
using System.Text;
using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>
/// 周报 / 月报生成（纯函数）：把时间窗内的事件、分桶采样、端口会话与连接记录
/// 汇总成可阅读、可导出的报告，并与紧邻的上一周期对比。
///
/// 报告只排列已记录的证据，不使用“确定原因”措辞；样本或事件不足时明确写出数据不足，
/// 不生成填充式结论。Markdown 字段是本地导出的完整文本，导出不涉及任何网络上传。
/// </summary>
public static class ReportGenerator
{
    private const int TopOffenders = 5;

    public static PerformanceReport Generate(ReportInput input)
    {
        var window = input.WindowDays;
        var from = input.From;
        var to = input.To;

        var marks = input.Events.Where(e => e.Type == PerformanceEventType.UserMarkedLag).ToArray();
        var previousMarks = input.PreviousPeriodEvents.Count(e => e.Type == PerformanceEventType.UserMarkedLag);
        var events = input.Events.Where(e => e.Type != PerformanceEventType.UserMarkedLag).ToArray();
        var previousEvents = input.PreviousPeriodEvents.Where(e => e.Type != PerformanceEventType.UserMarkedLag).ToArray();

        var ranking = ImpactRankingCalculator.Rank(input.Events, window, to);
        var previousRanking = ImpactRankingCalculator.Rank(input.PreviousPeriodEvents, window, from);
        var comparable = input.PreviousPeriodEvents.Count > 0;

        var sections = new List<ReportSection>
        {
            BuildOverview(input, window, marks, previousMarks, events, previousEvents, comparable),
            BuildOffenders(ranking, previousRanking, comparable),
            BuildEventBreakdown(events, previousEvents, comparable),
            BuildLongTermBehaviors(input.Behaviors),
            BuildPortsAndConnections(input)
        };

        var confidence = ComputeConfidence(input, marks.Length, events.Length);
        var headline = BuildHeadline(window, marks.Length, events.Length, ranking);
        var summary = BuildSummary(input, marks, events, ranking, comparable);
        var notes = BuildNotes(input, comparable);

        var report = new PerformanceReport(input.Period, from, to, input.Now, headline, summary, confidence,
            sections, notes, string.Empty);
        return report with { Markdown = BuildMarkdown(report) };
    }

    private static ReportSection BuildOverview(ReportInput input, int window, PerformanceEvent[] marks,
        int previousMarks, PerformanceEvent[] events, PerformanceEvent[] previousEvents, bool comparable)
    {
        var metrics = new List<ReportMetric>
        {
            new("用户标记卡顿", $"{marks.Length} 次", Delta(marks.Length, previousMarks, comparable, "次")),
            new("自动性能事件", $"{events.Length} 次", Delta(events.Length, previousEvents.Length, comparable, "次")),
            new("事件密度", window > 0 ? $"{(marks.Length + events.Length) / (double)window:0.0} 次/天" : "—"),
            new("数据覆盖", $"系统采样 {input.SystemSampleCount} 条 · 进程采样 {input.ProcessSampleCount} 条",
                Note: input.SystemSampleCount == 0 ? "本周期没有可用采样，报告结论仅基于事件记录" : "")
        };

        var bullets = new List<string>();
        if (marks.Length == 0 && events.Length == 0)
        {
            bullets.Add("本周期没有记录到任何卡顿标记或性能事件");
            bullets.Add("这既可能是系统确实平稳，也可能是历史记录曾关闭或后台采集未运行");
        }
        else
        {
            bullets.Add($"标记与事件合计 {marks.Length + events.Length} 次，平均 {(marks.Length + events.Length) / (double)window:0.0} 次/天");
            if (marks.Length > 0)
            {
                var related = marks.SelectMany(m => m.Contributors ?? [])
                    .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count()).FirstOrDefault();
                bullets.Add(related is null
                    ? "标记时刻没有可关联的进程证据"
                    : $"其中 {related.Count()} 次标记包含 {related.Key} 的相关证据（相关性，不是确定因果）");
            }
        }
        return new ReportSection("概览", $"统计窗口 {FormatFrom(input)} 起的 {window} 天", metrics, bullets);
    }

    private static ReportSection BuildOffenders(IReadOnlyList<ImpactRankEntry> ranking,
        IReadOnlyList<ImpactRankEntry> previous, bool comparable)
    {
        if (ranking.Count == 0)
            return new ReportSection("最可能拖慢电脑的软件", "本周期没有足够的事件证据生成排序",
                [], ["需要开启性能历史并积累事件（含“刚才卡了”标记）后才能排序"]);

        var metrics = ranking.Take(TopOffenders).Select(entry =>
        {
            var before = previous.FirstOrDefault(p => string.Equals(p.ProcessName, entry.ProcessName, StringComparison.OrdinalIgnoreCase));
            var delta = !comparable ? "上期无可比数据"
                : before is null ? "本期新进入榜单"
                : $"影响分 {entry.Score - before.Score:+#;-#;0}，事件 {entry.EventCount - before.EventCount:+#;-#;0} 次";
            return new ReportMetric(entry.ProcessName,
                $"影响分 {entry.Score} · 事件 {entry.EventCount} 次 · 累计 {FormatDuration(entry.TotalSeconds)}",
                delta,
                entry.LagRelatedCount > 0 ? $"与您的卡顿标记重合 {entry.LagRelatedCount} 次" : "无卡顿标记重合");
        }).ToList();

        var bullets = new List<string>
        {
            "排序由频率 45%、累计时长 30%、卡顿标记重合 25% 合成，只代表证据强度，不是定责",
            $"本期共 {ranking.Count} 个进程进入统计，展示前 {Math.Min(TopOffenders, ranking.Count)} 个"
        };
        if (comparable && previous.Count > 0)
        {
            var entered = ranking.Take(TopOffenders).Where(r => previous.All(p =>
                !string.Equals(p.ProcessName, r.ProcessName, StringComparison.OrdinalIgnoreCase))).ToArray();
            var left = previous.Take(TopOffenders).Where(p => ranking.All(r =>
                !string.Equals(r.ProcessName, p.ProcessName, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (entered.Length > 0) bullets.Add($"本期新进入前 {TopOffenders}：{string.Join("、", entered.Select(x => x.ProcessName))}");
            if (left.Length > 0) bullets.Add($"上期在榜、本期离开：{string.Join("、", left.Select(x => x.ProcessName))}");
        }
        return new ReportSection("最可能拖慢电脑的软件", "按证据聚合排序", metrics, bullets);
    }

    private static ReportSection BuildEventBreakdown(PerformanceEvent[] events, PerformanceEvent[] previous, bool comparable)
    {
        if (events.Length == 0)
            return new ReportSection("事件构成", "本周期没有自动性能事件", [], []);

        var metrics = events.GroupBy(e => e.Type)
            .OrderByDescending(g => g.Count())
            .Select(group =>
            {
                var before = previous.Count(e => e.Type == group.Key);
                return new ReportMetric(AttributionChainBuilder.TypeName(group.Key),
                    $"{group.Count()} 次",
                    Delta(group.Count(), before, comparable, "次"),
                    $"平均可信度 {group.Average(e => e.Confidence):0}");
            }).ToList();
        var bullets = new List<string>
        {
            $"持续时间最长的单次事件：{FormatDuration(events.Max(e => ((e.EndedAt ?? e.StartedAt.AddSeconds(30)) - e.StartedAt).TotalSeconds))}"
        };
        return new ReportSection("事件构成", $"共 {events.Length} 次自动事件", metrics, bullets);
    }

    private static ReportSection BuildLongTermBehaviors(IReadOnlyList<ProcessBehaviorFinding> behaviors)
    {
        if (behaviors.Count == 0)
            return new ReportSection("长期行为", "本周期没有识别到内存增长或周期性活动趋势",
                [], ["需要更长的连续采样窗口才能给出趋势结论"]);

        var metrics = behaviors.Take(TopOffenders).Select(finding => new ReportMetric(
            finding.ProcessName,
            finding.Kind == ProcessBehaviorKind.MemoryGrowth ? "内存持续增长" : "周期性后台活动",
            $"可信度 {finding.Confidence}",
            finding.Summary)).ToList();
        var bullets = behaviors.Select(f => $"· {f.Title}（{f.From:MM-dd HH:mm} 至 {f.To:MM-dd HH:mm}）").ToList();
        bullets.Add("趋势与周期只是相关性证据；缓存扩张、工作负载变化、更新与索引都可能形成同样形态");
        return new ReportSection("长期行为", $"识别到 {behaviors.Count} 项趋势或周期结论", metrics, bullets);
    }

    private static ReportSection BuildPortsAndConnections(ReportInput input)
    {
        var portGroups = input.Ports.GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Name = g.Key, Sessions = g.Sum(x => x.SessionCount), Seconds = g.Sum(x => x.TotalSeconds), Top = g.OrderByDescending(x => x.SessionCount).First() })
            .OrderByDescending(x => x.Sessions).Take(TopOffenders).ToArray();
        var connectionGroups = input.Connections.GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Name = g.Key, Remotes = g.Select(x => (x.RemoteAddress, x.RemotePort)).Distinct().Count() })
            .OrderByDescending(x => x.Remotes).Take(TopOffenders).ToArray();

        if (portGroups.Length == 0 && connectionGroups.Length == 0)
            return new ReportSection("端口与连接", "本周期没有端口会话或连接记录", [], []);

        var metrics = new List<ReportMetric>();
        foreach (var group in portGroups)
            metrics.Add(new ReportMetric($"{group.Name} · 端口", $"{group.Sessions} 个占用会话",
                $"累计 {FormatDuration(group.Seconds)}", $"最常见 {group.Top.Protocol}/{group.Top.Port}"));
        foreach (var group in connectionGroups)
            metrics.Add(new ReportMetric($"{group.Name} · 连接", $"{group.Remotes} 个不同远端",
                Note: "只统计端点与观察时间，不解析流量内容"));

        return new ReportSection("端口与连接", "观察到的监听与出站活动（不代表风险）", metrics,
            ["端口占用频繁不等于异常；连接为轮询观察，短连接可能漏记"]);
    }

    private static int ComputeConfidence(ReportInput input, int marks, int events)
    {
        var confidence = 45;
        // 期望覆盖 ≈ 原始 1 天（5 秒粒度）+ 其余天数按 30 秒层估算；达到一半即视为覆盖充分
        var expected = 17_280 + (input.WindowDays - 1) * 2_880;
        if (input.SystemSampleCount >= expected / 2) confidence += 20;
        else if (input.SystemSampleCount > 0) confidence += 10;
        if (input.ProcessSampleCount > 0) confidence += 10;
        if (marks + events >= 5) confidence += 15;
        else if (marks + events > 0) confidence += 8;
        if (input.PreviousPeriodEvents.Count > 0) confidence += 5;
        return Math.Clamp(confidence, 30, 95);
    }

    private static string BuildHeadline(int window, int marks, int events, IReadOnlyList<ImpactRankEntry> ranking)
    {
        var period = window == 7 ? "本周" : "本月";
        if (marks == 0 && events == 0) return $"{period}没有记录到卡顿标记或性能事件";
        var top = ranking.FirstOrDefault();
        var head = $"{period}记录 {marks} 次卡顿标记、{events} 次自动事件";
        return top is null ? head : $"{head}，{top.ProcessName} 的证据最集中";
    }

    private static string BuildSummary(ReportInput input, PerformanceEvent[] marks, PerformanceEvent[] events,
        IReadOnlyList<ImpactRankEntry> ranking, bool comparable)
    {
        if (marks.Length == 0 && events.Length == 0)
            return input.SystemSampleCount > 0
                ? "窗口内没有触发任何规则，也没有用户标记；采样正常，可认为这段时间系统活动平稳。"
                : "窗口内没有事件，也没有可用采样；请确认历史记录与后台采集已开启。";

        var parts = new List<string>();
        var top = ranking.FirstOrDefault();
        if (top is not null)
            parts.Add($"证据最集中的是 {top.ProcessName}（影响分 {top.Score}，事件 {top.EventCount} 次，其中与卡顿标记重合 {top.LagRelatedCount} 次）");
        if (marks.Length > 0) parts.Add($"用户主动标记 {marks.Length} 次");
        if (input.Behaviors.Count > 0) parts.Add($"识别到 {input.Behaviors.Count} 项长期趋势或周期行为");
        if (!comparable) parts.Add("上一周期没有可比数据（历史保留期不足两个周期）");
        return string.Join("；", parts) + "。";
    }

    private static IReadOnlyList<string> BuildNotes(ReportInput input, bool comparable)
    {
        var notes = new List<string>
        {
            "报告只使用本机已落库的证据，排序与趋势均为相关性证据，不是确定因果结论",
            $"数据保留设置为 {input.RetentionDays} 天；超出保留期的原始采样无法回放"
        };
        if (!comparable)
            notes.Add(input.Period == ReportPeriod.Month
                ? "月报的上期对比需要 60 天历史，当前保留期不足，已省略环比"
                : "周报的上期对比需要 14 天历史，当前保留期不足，已省略环比");
        if (input.SystemSampleCount == 0)
            notes.Add("本周期没有系统采样：报告只反映事件记录，曲线与趋势不可用");
        return notes;
    }

    private static string BuildMarkdown(PerformanceReport report)
    {
        var text = new StringBuilder();
        var period = report.Period == ReportPeriod.Week ? "周报" : "月报";
        text.AppendLine($"# NetScope {period} · {report.From:yyyy-MM-dd} 至 {report.To:yyyy-MM-dd}");
        text.AppendLine();
        text.AppendLine($"**{report.Headline}**");
        text.AppendLine();
        text.AppendLine(report.Summary);
        text.AppendLine();
        text.AppendLine($"- 生成时间：{report.GeneratedAt:yyyy-MM-dd HH:mm:ss}（本机）");
        text.AppendLine($"- 综合可信度：{report.Confidence}");
        text.AppendLine();

        foreach (var section in report.Sections)
        {
            text.AppendLine($"## {section.Title}");
            text.AppendLine();
            text.AppendLine(section.Summary);
            text.AppendLine();
            if (section.Metrics.Count > 0)
            {
                text.AppendLine("| 项目 | 数值 | 环比 | 说明 |");
                text.AppendLine("| --- | --- | --- | --- |");
                foreach (var metric in section.Metrics)
                    text.AppendLine($"| {Escape(metric.Label)} | {Escape(metric.Value)} | {Escape(metric.Delta)} | {Escape(metric.Note)} |");
                text.AppendLine();
            }
            foreach (var bullet in section.Bullets)
                text.AppendLine($"- {bullet}");
            if (section.Bullets.Count > 0) text.AppendLine();
        }

        if (report.Notes.Count > 0)
        {
            text.AppendLine("## 数据说明");
            text.AppendLine();
            foreach (var note in report.Notes) text.AppendLine($"- {note}");
            text.AppendLine();
        }
        text.AppendLine("---");
        text.AppendLine();
        text.AppendLine("由 NetScope 在本机生成，内容不包含遥测，也未上传到任何服务。");
        return text.ToString();
    }

    private static string Escape(string value) => string.IsNullOrEmpty(value) ? "—" : value.Replace("|", "\\|");

    private static string Delta(int current, int previous, bool comparable, string unit) =>
        !comparable ? "上期无可比数据"
        : current == previous ? $"与上期持平（{previous} {unit}）"
        : current > previous ? $"较上期 +{current - previous} {unit}"
        : $"较上期 -{previous - current} {unit}";

    private static string FormatDuration(double seconds) => seconds switch
    {
        < 90 => $"{seconds:0} 秒",
        < 3600 => $"{seconds / 60:0} 分钟",
        _ => $"{seconds / 3600:0.0} 小时"
    };

    private static string FormatFrom(ReportInput input) => input.From.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
