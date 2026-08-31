using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>
/// 7 天影响排行（纯函数）：把时间窗内的性能事件按进程聚合，回答“过去 7 天谁最可能拖慢电脑”。
/// 评分只是证据排序，不是因果结论。公式（各分量封顶后加权）：
///   频率 45%（15 次封顶）+ 累计时长 30%（2 小时封顶）+ 与用户卡顿标记重合 25%（4 次封顶）。
/// 事件的累计时长按贡献者的影响分占比分摊；主关联进程必然是贡献者之一。
/// </summary>
public static class ImpactRankingCalculator
{
    private const int FrequencyCap = 15;
    private const double DurationCapSeconds = 2 * 60 * 60;
    private const int LagCap = 4;
    private const double FrequencyWeight = 0.45, DurationWeight = 0.30, LagWeight = 0.25;
    private const double MaxEventSeconds = 600; // 单事件时长计入上限，防止挂起事件拉爆时长
    private static readonly TimeSpan MarkProximity = TimeSpan.FromSeconds(90);

    public static IReadOnlyList<ImpactRankEntry> Rank(IReadOnlyList<PerformanceEvent> events, int days = 7, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var windowStart = at.AddDays(-Math.Max(1, days));
        var markTimes = events
            .Where(e => e.Type == PerformanceEventType.UserMarkedLag)
            .Select(e => e.StartedAt)
            .ToList();

        var stats = new Dictionary<string, Stat>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in events)
        {
            if (evt.Type == PerformanceEventType.UserMarkedLag) continue;
            if (evt.StartedAt < windowStart || evt.StartedAt > at) continue;

            var duration = Math.Clamp(((evt.EndedAt ?? evt.StartedAt.AddSeconds(30)) - evt.StartedAt).TotalSeconds, 1, MaxEventSeconds);
            var lagRelated = markTimes.Any(m => Math.Abs((evt.StartedAt - m).TotalSeconds) <= MarkProximity.TotalSeconds);

            IReadOnlyList<PerformanceEventContributor> contributors;
            if (evt.Contributors is { Count: > 0 })
                contributors = evt.Contributors;
            else if (evt.PrimaryProcessName is { Length: > 0 } primaryName)
                contributors = [new PerformanceEventContributor(evt.PrimaryProcess ?? default, primaryName, 0)];
            else
                continue;
            if (contributors.Count == 0) continue;

            var totalImpact = contributors.Sum(c => Math.Max(0, c.ImpactScore));
            foreach (var contributor in contributors)
            {
                if (string.IsNullOrWhiteSpace(contributor.ProcessName)) continue;
                var share = totalImpact > 0 ? Math.Max(0, contributor.ImpactScore) / totalImpact : 1.0 / contributors.Count;
                var stat = stats.TryGetValue(contributor.ProcessName, out var existing) ? existing : default;
                stats[contributor.ProcessName] = new Stat(
                    stat.Count + 1,
                    stat.Seconds + duration * share,
                    stat.Lag + (lagRelated ? 1 : 0));
            }
        }

        return stats
            .Select(pair =>
            {
                var (count, seconds, lag) = pair.Value;
                var score = (int)Math.Round(100 * (
                    FrequencyWeight * Math.Min(1, (double)count / FrequencyCap) +
                    DurationWeight * Math.Min(1, seconds / DurationCapSeconds) +
                    LagWeight * Math.Min(1, (double)lag / LagCap)));
                return new ImpactRankEntry(pair.Key, count, seconds, lag, Math.Clamp(score, 0, 100));
            })
            .OrderByDescending(e => e.Score)
            .ThenByDescending(e => e.LagRelatedCount)
            .ThenByDescending(e => e.TotalSeconds)
            .ThenBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private readonly record struct Stat(int Count, double Seconds, int Lag);
}
