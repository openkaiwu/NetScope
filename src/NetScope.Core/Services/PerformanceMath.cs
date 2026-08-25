using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>由相邻两次原始读数换算速率与百分比，纯计算便于单元测试。</summary>
public static class PerformanceMath
{
    public static double ComputeProcessCpuPercent(TimeSpan previous, TimeSpan current, double elapsedSeconds)
        => elapsedSeconds > 0
            ? Math.Clamp((current - previous).TotalSeconds / elapsedSeconds * 100.0, 0, 100.0 * Environment.ProcessorCount)
            : 0;

    /// <summary>GetSystemTimes 的 kernel 时间包含 idle，繁忙 = total - idle。</summary>
    public static double ComputeSystemCpuPercent(
        TimeSpan previousKernel, TimeSpan currentKernel,
        TimeSpan previousUser, TimeSpan currentUser,
        TimeSpan previousIdle, TimeSpan currentIdle)
    {
        var total = (currentKernel - previousKernel) + (currentUser - previousUser);
        if (total <= TimeSpan.Zero) return 0;
        var idle = currentIdle - previousIdle;
        return Math.Clamp((total - idle).TotalSeconds / total.TotalSeconds * 100.0, 0, 100);
    }

    public static long ComputeRate(long previous, long current, double elapsedSeconds)
        => elapsedSeconds > 0 ? (long)Math.Max(0, (current - previous) / elapsedSeconds) : 0;

    public static ProcessPerformanceSample ToSample(ProcessPerformanceReading previous, ProcessPerformanceReading current, double elapsedSeconds) =>
        new(current.Process, current.Timestamp, current.Name,
            ComputeProcessCpuPercent(previous.TotalCpuTime, current.TotalCpuTime, elapsedSeconds),
            current.WorkingSetBytes, current.PrivateBytes,
            ComputeRate(previous.ReadBytes, current.ReadBytes, elapsedSeconds),
            ComputeRate(previous.WriteBytes, current.WriteBytes, elapsedSeconds),
            ComputeRate(previous.ReadOperations, current.ReadOperations, elapsedSeconds),
            ComputeRate(previous.WriteOperations, current.WriteOperations, elapsedSeconds),
            current.IsAccessible, current.StatusMessage);

    public static SystemPerformanceSample ToSample(SystemPerformanceReading previous, SystemPerformanceReading current, double elapsedSeconds) =>
        new(current.Timestamp,
            ComputeSystemCpuPercent(previous.KernelCpuTime, current.KernelCpuTime, previous.UserCpuTime, current.UserCpuTime, previous.IdleCpuTime, current.IdleCpuTime),
            (long)current.AvailableMemoryBytes, (long)current.TotalMemoryBytes,
            ComputeRate(previous.NetworkReceivedBytes, current.NetworkReceivedBytes, elapsedSeconds),
            ComputeRate(previous.NetworkSentBytes, current.NetworkSentBytes, elapsedSeconds));
}
