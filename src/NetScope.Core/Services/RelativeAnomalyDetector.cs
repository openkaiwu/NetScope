using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>按进程实例维护 CPU 基线。时间门控防止突发采样改变学习速度；异常期间冻结基线。</summary>
public sealed class RelativeAnomalyDetector
{
    private readonly Dictionary<ProcessInstanceKey, Baseline> _baselines = new();
    public IReadOnlyList<PerformanceEvent> Evaluate(IEnumerable<ProcessPerformanceSample> samples, DateTimeOffset now)
    {
        var output = new List<PerformanceEvent>();
        var seen = new HashSet<ProcessInstanceKey>();
        foreach (var p in samples.Where(p => p.IsAccessible && double.IsFinite(p.CpuPercent) && p.CpuPercent >= 0))
        {
            seen.Add(p.Process);
            if (!_baselines.TryGetValue(p.Process, out var b))
            {
                if (_baselines.Count >= 4096) continue;
                _baselines[p.Process] = b = new();
            }
            if (b.LastAt is { } previous && now - previous < TimeSpan.FromSeconds(1)) continue;
            if (b.LastAt is { } last && now - last > TimeSpan.FromSeconds(5))
            {
                if (b.Active is { } interrupted) output.Add(interrupted with { Status = PerformanceEventStatus.Closed, EndedAt = last, Evidence = [.. interrupted.Evidence, "采样中断，结束时间为最后观察时刻"] });
                _baselines[p.Process] = b = new();
            }
            b.LastAt = now;
            var ordered = b.Values.Order().ToArray();
            var median = Median(ordered);
            var mad = Median(ordered.Select(x => Math.Abs(x - median)).Order().ToArray());
            var threshold = Math.Max(b.Ewma + 5, median + Math.Max(5, 6 * 1.4826 * mad));
            var anomaly = b.Values.Count >= 30 && p.CpuPercent >= threshold && p.CpuPercent >= Math.Max(5, b.Ewma * 3);
            if (anomaly)
            {
                b.Since ??= now;
                if (b.Active is null && now >= b.CooldownUntil && now - b.Since >= TimeSpan.FromSeconds(5))
                {
                    b.Active = new(Guid.NewGuid(), PerformanceEventType.RelativeCpuAnomaly, PerformanceEventStatus.Capturing,
                        b.Since.Value, null, 65, $"{p.Name} CPU 相对基线异常升高",
                        "进程活动可能异常增强；并不代表它已造成系统卡顿",
                        [$"CPU 当前 {p.CpuPercent:0.0}%（单核口径），EWMA 基线 {b.Ewma:0.0}%",
                         $"历史中位数 {median:0.0}%，MAD {mad:0.0}，触发阈值 {threshold:0.0}%",
                         "基线至少 30 秒，异常持续至少 5 秒；基线按 PID 与启动时间隔离"],
                        ["查看该进程的历史曲线，确认是否正在更新、扫描或同步"], p.Process, p.Name,
                        [new(p.Process, p.Name, Math.Clamp(p.CpuPercent - b.Ewma, 0, 100))]);
                    output.Add(b.Active);
                }
            }
            else
            {
                if (b.Active is { } active)
                {
                    output.Add(active with { Status = PerformanceEventStatus.Closed, EndedAt = now });
                    b.Active = null;
                    b.CooldownUntil = now.AddMinutes(2);
                }
                b.Since = null;
                b.Ewma = b.Values.Count == 0 ? p.CpuPercent : b.Ewma * 0.95 + p.CpuPercent * 0.05;
                b.Values.Enqueue(p.CpuPercent);
                while (b.Values.Count > 120) b.Values.Dequeue();
            }
        }
        foreach (var key in _baselines.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            if (_baselines[key].Active is { } active)
                output.Add(active with { Status = PerformanceEventStatus.Closed, EndedAt = now, Evidence = [.. active.Evidence, "进程退出或采样不可用"] });
            _baselines.Remove(key);
        }
        return output;
    }
    private static double Median(double[] values) => values.Length == 0 ? 0 : (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    private sealed class Baseline
    {
        public Queue<double> Values { get; } = new();
        public double Ewma;
        public DateTimeOffset? LastAt, Since;
        public DateTimeOffset CooldownUntil;
        public PerformanceEvent? Active;
    }
}
