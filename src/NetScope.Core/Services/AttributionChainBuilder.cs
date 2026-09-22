using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>
/// 归因链构建（纯函数）：把一条已落库的性能事件展开为
/// 事件 → 候选进程 → 证据 → 根因假设 → 置信度 五步链条。
///
/// 输入只有事件本身与回放上下文，因此链条是可重放的：同一事件在保留期内或保留期外
/// 都会得到同样的结论文字，只有“原始采样是否可回放”这一项会变化。
/// 所有假设都标注为相关性证据，不构成确定性定责。
/// </summary>
public static class AttributionChainBuilder
{
    private const int MaxCandidates = 5;

    public static AttributionChain Build(PerformanceEvent evt, AttributionReplayContext? replay = null)
    {
        var context = replay ?? AttributionReplayContext.Unavailable;
        var candidates = (evt.Contributors ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.ProcessName))
            .OrderByDescending(c => c.ImpactScore)
            .ThenBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .ToArray();
        var totalImpact = candidates.Sum(c => Math.Max(0, c.ImpactScore));
        var top = candidates.Length > 0 ? candidates[0] : null;
        var topShare = top is null ? 0 : totalImpact > 0 ? Math.Max(0, top.ImpactScore) / totalImpact : 1.0 / candidates.Length;

        var steps = new List<AttributionChainStep>
        {
            BuildEventStep(evt),
            BuildCandidateStep(evt, candidates, totalImpact, context),
            BuildEvidenceStep(evt, context),
            BuildHypothesisStep(evt, top, topShare, candidates.Length, context)
        };
        var confidence = ComputeConfidence(evt, top, topShare, candidates.Length, context);
        steps.Add(BuildConfidenceStep(evt, confidence, top, candidates.Length, context));

        return new AttributionChain(
            evt.Id, evt.Type,
            $"{TypeName(evt.Type)} · {evt.StartedAt:MM-dd HH:mm:ss}",
            steps,
            context.RawSamplesAvailable,
            context.RawSamplesAvailable
                ? $"原始采样可回放：窗口内系统 {context.SystemSampleCount} 条、进程 {context.ProcessSampleCount} 条"
                : "原始采样已超出保留期或历史记录曾关闭，本链条只依据事件落库时的证据清单重建");
    }

    private static AttributionChainStep BuildEventStep(PerformanceEvent evt)
    {
        var duration = evt.EndedAt is { } ended
            ? $"{Math.Max(1, (int)(ended - evt.StartedAt).TotalSeconds)} 秒"
            : "进行中";
        return new AttributionChainStep(AttributionStage.Event, "事件",
            evt.Summary,
            [
                $"类型：{TypeName(evt.Type)}",
                $"时间：{evt.StartedAt:yyyy-MM-dd HH:mm:ss} 起，持续 {duration}",
                $"状态：{StatusName(evt.Status)}，事件编号 {evt.Id:N}",
                $"最可能的原因（事件当时推断）：{evt.MostLikelyCause}"
            ],
            Confidence: evt.Confidence);
    }

    private static AttributionChainStep BuildCandidateStep(PerformanceEvent evt,
        IReadOnlyList<PerformanceEventContributor> candidates, double totalImpact, AttributionReplayContext context)
    {
        if (candidates.Count == 0)
        {
            var items = new List<string> { "事件落库时没有记录到任何进程贡献者" };
            if (evt.PrimaryProcessName is { Length: > 0 } primary)
                items.Add($"仅有主关联进程 {primary}（PID {evt.PrimaryProcess?.ProcessId}），没有可比较的权重");
            items.Add("常见于网卡链路状态类事件，或事件发生时进程采样不可用");
            return new AttributionChainStep(AttributionStage.Candidates, "候选进程",
                "无法给出候选进程排序", items, context.RawSamplesAvailable);
        }

        var items2 = candidates.Select(c =>
        {
            var share = totalImpact > 0 ? Math.Max(0, c.ImpactScore) / totalImpact : 1.0 / candidates.Count;
            return $"{c.ProcessName}（PID {c.Process.ProcessId}）· 影响分 {c.ImpactScore:0} · 占候选权重 {share:P0}";
        }).ToList();
        items2.Add("影响分是 CPU、内存、I/O、前台与用户标记邻近度的加权汇总，只用于排序证据强度");
        return new AttributionChainStep(AttributionStage.Candidates, "候选进程",
            $"事件期间记录到 {candidates.Count} 个候选进程",
            items2, context.RawSamplesAvailable);
    }

    private static AttributionChainStep BuildEvidenceStep(PerformanceEvent evt, AttributionReplayContext context)
    {
        var items = new List<string>(evt.Evidence);
        if (context.RawSamplesAvailable)
        {
            items.Add(context.ProcessSampleCount > 0
                ? $"原始窗口采样：系统 {context.SystemSampleCount} 条、进程 {context.ProcessSampleCount} 条，可回放曲线"
                : $"原始窗口采样：系统 {context.SystemSampleCount} 条，进程曲线可点击“回放原始证据”加载");
        }
        else
        {
            items.Add(context.RetentionCutoff is { } cutoff
                ? $"原始采样早于保留期起点 {cutoff:yyyy-MM-dd HH:mm}，已不可回放"
                : "原始采样已不可回放（历史记录曾关闭或已超出保留期）");
        }
        if (evt.Recommendations.Count > 0)
            items.Add($"建议：{string.Join("；", evt.Recommendations)}");
        return new AttributionChainStep(AttributionStage.Evidence, "证据",
            $"共 {evt.Evidence.Count} 条事件证据", items, context.RawSamplesAvailable);
    }

    private static AttributionChainStep BuildHypothesisStep(PerformanceEvent evt,
        PerformanceEventContributor? top, double topShare, int candidateCount, AttributionReplayContext context)
    {
        string detail;
        var items = new List<string>();

        if (top is null)
        {
            if (evt.PrimaryProcessName is { Length: > 0 } primary)
            {
                detail = $"疑似与 {primary} 的资源占用相关；事件只记录了主关联进程，缺少可比较的贡献权重，只能作为方向性线索";
                items.Add("主关联进程没有量化权重，无法与同时段其他进程比较");
                items.Add("可回看该进程在事件前后的曲线进一步核对");
            }
            else
            {
                detail = "本次事件没有可归属的进程证据，假设停留在系统或环境层面（例如网卡链路状态、磁盘设备层压力）";
                items.Add("无法给出进程级根因假设");
                items.Add("可回看事件时间线的系统曲线，或检查链路与磁盘状态");
            }
        }
        else if (candidateCount == 1 && evt.PrimaryProcessName is null)
        {
            detail = $"疑似与 {top.ProcessName} 的资源占用相关，但只有一个候选进程，缺少可对比的权重";
            items.Add("单候选不能排除同名多实例或未采样进程的贡献");
        }
        else
        {
            detail = $"最可能：{top.ProcessName} 的资源占用与本次事件在时间与指标上重合，疑似主要贡献者（占候选权重 {topShare:P0}）";
            items.Add("该结论是时间与指标上的相关性证据，不是确定性定责");
            items.Add("应用缓存回收、系统更新、索引或未采样的进程也可能造成同等负载");
            if (context.RawSamplesAvailable)
                items.Add("可用“回放原始证据”核对事件前后该进程的 CPU、内存与 I/O 曲线");
        }

        return new AttributionChainStep(AttributionStage.Hypothesis, "根因假设", detail, items,
            context.RawSamplesAvailable);
    }

    private static AttributionChainStep BuildConfidenceStep(PerformanceEvent evt, int confidence,
        PerformanceEventContributor? top, int candidateCount, AttributionReplayContext context)
    {
        var items = new List<string> { $"事件记录可信度 {evt.Confidence} 为基线" };
        if (candidateCount == 0) items.Add("缺少进程候选：上限被压到 60，避免过度归因");
        else if (top is not null && top.ImpactScore >= 50) items.Add($"首要候选影响分 {top.ImpactScore:0}，证据较强：+5");
        items.Add(context.RawSamplesAvailable
            ? "原始采样仍在保留期内，可回放核对：+5"
            : "原始采样不可回放，只能依赖事件当时的证据清单：-5");
        return new AttributionChainStep(AttributionStage.Confidence, "置信度",
            $"综合可信度 {confidence}", items, context.RawSamplesAvailable, confidence);
    }

    /// <summary>综合可信度：事件基线分加上证据强度修正，夹在 30–95，且无进程候选时不高于 60。</summary>
    private static int ComputeConfidence(PerformanceEvent evt, PerformanceEventContributor? top,
        double topShare, int candidateCount, AttributionReplayContext context)
    {
        var confidence = evt.Confidence;
        if (top is not null && top.ImpactScore >= 50) confidence += 5;
        if (top is not null && topShare >= 0.6 && candidateCount > 1) confidence += 3;
        confidence += context.RawSamplesAvailable ? 5 : -5;
        if (candidateCount == 0) confidence = Math.Min(confidence, 60);
        return Math.Clamp(confidence, 30, 95);
    }

    internal static string TypeName(PerformanceEventType type) => type switch
    {
        PerformanceEventType.CpuContention => "CPU 争用",
        PerformanceEventType.MemoryPressure => "内存压力",
        PerformanceEventType.DiskIoPressure => "磁盘压力",
        PerformanceEventType.NetworkDegradation => "网络退化",
        PerformanceEventType.RelativeCpuAnomaly => "相对 CPU 异常",
        PerformanceEventType.UserMarkedLag => "用户标记卡顿",
        _ => "性能事件"
    };

    private static string StatusName(PerformanceEventStatus status) => status switch
    {
        PerformanceEventStatus.Capturing => "捕获中",
        PerformanceEventStatus.Confirmed => "已确认",
        PerformanceEventStatus.Closed => "已结束",
        _ => "已结束"
    };
}
