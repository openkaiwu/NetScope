using NetScope.Core.Models;

namespace NetScope.Core.Services;

public sealed record ProcessBehaviorAnalyzerOptions
{
    public TimeSpan MinimumMemoryWindow { get; init; } = TimeSpan.FromMinutes(30);
    public int MinimumMemoryPoints { get; init; } = 12;
    public double MinimumMemoryGrowthMb { get; init; } = 256;
    public double MinimumMemorySlopeMbPerMinute { get; init; } = 4;
    public double MinimumMemoryR2 { get; init; } = 0.72;
    public double MinimumPositiveStepRatio { get; init; } = 0.65;
    public int MinimumPeriodicEpisodes { get; init; } = 4;
    public TimeSpan MinimumPeriod { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaximumPeriod { get; init; } = TimeSpan.FromHours(2);
    public double MaximumPeriodCoefficientOfVariation { get; init; } = 0.25;
}

public sealed class ProcessBehaviorAnalyzer(ProcessBehaviorAnalyzerOptions? options = null)
{
    private readonly ProcessBehaviorAnalyzerOptions _options = options ?? new();

    public IReadOnlyList<ProcessBehaviorFinding> Analyze(IEnumerable<ProcessHistoryPoint> points)
    {
        var output = new List<ProcessBehaviorFinding>();
        foreach (var group in points.Where(Valid).GroupBy(Identity))
        {
            var samples = group.OrderBy(p => p.Timestamp).ToArray();
            if (samples.Length == 0) continue;
            var name = samples[^1].ProcessName;
            if (AnalyzeMemory(name, samples) is { } memory) output.Add(memory);
            if (AnalyzePeriod(name, samples) is { } periodic) output.Add(periodic);
        }
        return output.GroupBy(x => (x.Kind, Name: Normalize(x.ProcessName)))
            .Select(g => g.OrderByDescending(x => x.Confidence).ThenByDescending(x => x.To).First())
            .OrderByDescending(x => x.Confidence).ThenBy(x => x.ProcessName).ToArray();
    }

    private ProcessBehaviorFinding? AnalyzeMemory(string name, ProcessHistoryPoint[] p)
    {
        if (p.Length < _options.MinimumMemoryPoints || p[^1].Timestamp - p[0].Timestamp < _options.MinimumMemoryWindow) return null;
        var origin = p[0].Timestamp;
        var x = p.Select(v => (v.Timestamp - origin).TotalMinutes).ToArray();
        var y = p.Select(v => v.PrivateBytes / 1024.0 / 1024).ToArray();
        var xMean = x.Average(); var yMean = y.Average();
        var denominator = x.Sum(v => (v - xMean) * (v - xMean));
        if (denominator <= 0) return null;
        var slope = x.Zip(y).Sum(v => (v.First - xMean) * (v.Second - yMean)) / denominator;
        var predicted = x.Select(v => yMean + slope * (v - xMean)).ToArray();
        var total = y.Sum(v => (v - yMean) * (v - yMean));
        var residual = y.Zip(predicted).Sum(v => (v.First - v.Second) * (v.First - v.Second));
        var r2 = total <= 0 ? 0 : Math.Clamp(1 - residual / total, 0, 1);
        var edge = Math.Max(2, p.Length / 10);
        var growth = Median(y[^edge..].Order().ToArray()) - Median(y[..edge].Order().ToArray());
        var positiveRatio = y.Skip(1).Zip(y).Count(v => v.First >= v.Second) / (double)(y.Length - 1);
        var observedMinutes = Math.Max(1, (p[^1].Timestamp - p[0].Timestamp).TotalMinutes);
        // 长窗口允许更低的每分钟斜率，但总增长仍必须超过门槛；否则 30 天缓慢泄漏会被短窗口速率门槛永久漏掉。
        var minimumSlope = Math.Min(_options.MinimumMemorySlopeMbPerMinute,
            _options.MinimumMemoryGrowthMb / observedMinutes);
        if (growth < _options.MinimumMemoryGrowthMb || slope < minimumSlope ||
            r2 < _options.MinimumMemoryR2 || positiveRatio < _options.MinimumPositiveStepRatio) return null;
        var confidence = (int)Math.Round(Math.Clamp(55 + Math.Min(20, growth / 64) + r2 * 15, 0, 92));
        return new(ProcessBehaviorKind.MemoryGrowth, name, p[0].Timestamp, p[^1].Timestamp, confidence,
            $"{name} 内存呈持续增长趋势", $"观察期内私有内存约增加 {growth:0} MB，可能存在泄漏或持续缓存",
            [$"线性趋势 {slope:0.0} MB/分钟，拟合度 R²={r2:0.00}",
             $"非下降采样占比 {positiveRatio:P0}，样本 {p.Length} 条，观察 {(p[^1].Timestamp - p[0].Timestamp).TotalMinutes:0} 分钟",
             "趋势不能单独证明内存泄漏；应用缓存、工作负载增加和多个同名实例也可能造成增长"],
            p[^1].ProcessId, p[^1].ProcessStartedAt);
    }

    private ProcessBehaviorFinding? AnalyzePeriod(string name, ProcessHistoryPoint[] p)
    {
        if (p.Length < _options.MinimumPeriodicEpisodes * 3) return null;
        var cpuValues = p.Select(x => x.CpuPercent).Order().ToArray();
        var ioValues = p.Select(x => (double)x.IoBytesPerSecond).Order().ToArray();
        var cpuThreshold = Math.Max(5, Median(cpuValues) + 5);
        var ioThreshold = Math.Max(1024 * 1024, Median(ioValues) * 3 + 256 * 1024);
        var steps = p.Skip(1).Zip(p).Select(v => (v.First.Timestamp - v.Second.Timestamp).TotalSeconds).Where(v => v > 0).Order().ToArray();
        if (steps.Length == 0) return null;
        var joinGap = TimeSpan.FromSeconds(Math.Max(2, Median(steps) * 2.5));
        var starts = new List<DateTimeOffset>();
        DateTimeOffset? lastActive = null;
        foreach (var sample in p)
        {
            if (sample.CpuPercent < cpuThreshold && sample.IoBytesPerSecond < ioThreshold) continue;
            if (lastActive is null || sample.Timestamp - lastActive > joinGap) starts.Add(sample.Timestamp);
            lastActive = sample.Timestamp;
        }
        if (starts.Count < _options.MinimumPeriodicEpisodes) return null;
        var intervals = starts.Skip(1).Zip(starts).Select(v => (v.First - v.Second).TotalSeconds).ToArray();
        var mean = intervals.Average();
        if (mean < _options.MinimumPeriod.TotalSeconds || mean > _options.MaximumPeriod.TotalSeconds) return null;
        var sd = Math.Sqrt(intervals.Average(v => (v - mean) * (v - mean)));
        var cv = sd / mean;
        if (cv > _options.MaximumPeriodCoefficientOfVariation) return null;
        var confidence = (int)Math.Round(Math.Clamp(60 + Math.Min(20, starts.Count * 2) + (1 - cv) * 10, 0, 93));
        return new(ProcessBehaviorKind.PeriodicActivity, name, p[0].Timestamp, p[^1].Timestamp, confidence,
            $"{name} 出现周期性后台活动", $"检测到 {starts.Count} 次活动，平均约每 {FormatPeriod(mean)} 一次",
            [$"周期离散系数 {cv:0.00}（越低越规律），活动阈值 CPU {cpuThreshold:0.0}% 或 I/O {ioThreshold / 1024 / 1024:0.0} MB/s",
             $"活动开始：{string.Join("、", starts.Take(6).Select(x => x.ToString("MM-dd HH:mm")))}",
             "周期相关性不等于有害行为；更新、同步、索引和遥测都可能形成规律活动"],
            p[^1].ProcessId, p[^1].ProcessStartedAt);
    }

    private static bool Valid(ProcessHistoryPoint p) => !string.IsNullOrWhiteSpace(p.ProcessName) &&
        double.IsFinite(p.CpuPercent) && p.CpuPercent >= 0 && p.PrivateBytes >= 0 && p.IoBytesPerSecond >= 0;
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    private static (string Name, int ProcessId, long StartedAt) Identity(ProcessHistoryPoint value) =>
        (Normalize(value.ProcessName), value.ProcessId, value.ProcessStartedAt?.UtcTicks ?? 0);
    private static double Median(double[] values) => values.Length == 0 ? 0 : (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    private static string FormatPeriod(double seconds) => seconds < 3600 ? $"{seconds / 60:0} 分钟" : $"{seconds / 3600:0.0} 小时";
}
