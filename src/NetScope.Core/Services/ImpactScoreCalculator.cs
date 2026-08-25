using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>
/// 进程影响分：仅用于进程中心排序与事件归因排序，是多种证据的加权汇总，
/// 不代表“该进程导致了卡顿”。CPU/内存/I/O 为客观数值，前台与用户标记邻近度为加权。
/// </summary>
public static class ImpactScoreCalculator
{
    /// <summary>计算单个进程的影响分（0–100）。</summary>
    /// <param name="sample">进程采样。</param>
    /// <param name="recentUserMarkAt">最近一次用户标记时间；与采样时间邻近（±60 秒内）时加权。</param>
    public static double Compute(ProcessPerformanceSample sample, DateTimeOffset? recentUserMarkAt = null)
    {
        if (!sample.IsAccessible) return 0;

        // CPU：50% 单核占用记满 40 分
        var cpu = Math.Min(40.0, sample.CpuPercent * 40.0 / 50.0);
        // 内存：工作集 4GB 记满 25 分
        var memory = Math.Min(25.0, sample.WorkingSetBytes / 1024.0 / 1024.0 / 1024.0 * 25.0 / 4.0);
        // I/O：读写合计 50MB/s 记满 25 分
        var ioBytesPerSecond = sample.ReadBytesPerSecond + sample.WriteBytesPerSecond;
        var io = Math.Min(25.0, ioBytesPerSecond / (1024.0 * 1024.0) * 25.0 / 50.0);
        // 前台进程：+10
        var foreground = sample.IsForeground ? 10.0 : 0;
        // 用户标记邻近：±60 秒内最高 +15，随距离线性衰减
        var markProximity = 0.0;
        if (recentUserMarkAt is { } marked)
        {
            var distance = Math.Abs((sample.Timestamp - marked).TotalSeconds);
            if (distance <= 60) markProximity = 15.0 * (1 - distance / 60.0);
        }

        return Math.Clamp(cpu + memory + io + foreground + markProximity, 0, 100);
    }

    /// <summary>按影响分降序排序进程采样。</summary>
    public static IOrderedEnumerable<ProcessPerformanceSample> Rank(
        IEnumerable<ProcessPerformanceSample> samples, DateTimeOffset? recentUserMarkAt = null) =>
        samples.OrderByDescending(s => Compute(s, recentUserMarkAt));
}
