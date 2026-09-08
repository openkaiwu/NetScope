using NetScope.Core.Models;

namespace NetScope.Core.Services;

public static class ResponsivenessCalculator
{
    public static ResponsivenessAssessment Compute(SystemPerformanceSample system,
        IEnumerable<ProcessPerformanceSample> processes, DateTimeOffset? lastUserMark = null)
    {
        static double Unit(double x) => double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0;
        var cpu = Unit((system.CpuPercent - 50) / 50);
        var memory = system.TotalMemoryBytes > 0
            ? Unit((0.2 - (double)system.AvailableMemoryBytes / system.TotalMemoryBytes) / 0.2) : 0;
        var disk = system.Disk is { } d
            ? Math.Max(Unit(Math.Max(d.ReadLatencyMs, d.WriteLatencyMs) / 100),
                Unit(d.ActivePercent / 100) * Unit(d.QueueLength / 4)) : 0;
        // 进程 CPU 以单逻辑核=100% 计，必须换算为整机口径。
        var foreground = processes.Where(p => p.IsAccessible && p.IsForeground)
            .Select(p => Unit(p.CpuPercent / (100 * Environment.ProcessorCount))).DefaultIfEmpty().Max();
        var marked = lastUserMark is { } mark && system.Timestamp >= mark && system.Timestamp - mark <= TimeSpan.FromSeconds(30);
        var score = (int)Math.Round(Math.Clamp(100 - 35 * cpu - 25 * memory - 30 * disk - 5 * foreground - (marked ? 5 : 0), 0, 100));
        return new(score, score >= 80 ? "较流畅" : score >= 60 ? "可能迟缓" : "压力较高",
            [$"CPU 压力扣分 {35 * cpu:0.0}；内存压力扣分 {25 * memory:0.0}",
             system.Disk is null ? "磁盘指标未采集，评分证据不完整" : $"磁盘压力扣分 {30 * disk:0.0}",
             $"前台负载扣分 {5 * foreground:0.0}；最近用户卡顿标记扣分 {(marked ? 5 : 0)}",
             "资源压力估计，不代表实测输入延迟或确定故障"]);
    }
}
