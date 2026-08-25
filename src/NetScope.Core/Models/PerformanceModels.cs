using System.Collections.Immutable;

namespace NetScope.Core.Models;

/// <summary>进程实例唯一身份：PID + 启动时间。仅靠 PID 会在 Windows 复用 PID 后把不同进程的历史混在一起。</summary>
public readonly record struct ProcessInstanceKey(int ProcessId, DateTimeOffset StartedAt)
{
    public override string ToString() => $"{ProcessId}@{StartedAt:yyyyMMddHHmmss}";
}

/// <summary>进程累计计数原始读数（采集提供方输出，供差量换算速率）。</summary>
public sealed record ProcessPerformanceReading(
    ProcessInstanceKey Process,
    DateTimeOffset Timestamp,
    string Name,
    TimeSpan TotalCpuTime,
    long WorkingSetBytes,
    long PrivateBytes,
    long ReadBytes,
    long WriteBytes,
    long ReadOperations,
    long WriteOperations,
    bool IsAccessible,
    string? StatusMessage = null);

/// <summary>进程采样：CPU 百分比与每秒字节/操作次数，由相邻两次原始读数换算。IsForeground 表示采样瞬间处于前台。</summary>
public sealed record ProcessPerformanceSample(
    ProcessInstanceKey Process,
    DateTimeOffset Timestamp,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    long ReadBytesPerSecond,
    long WriteBytesPerSecond,
    long ReadOperationsPerSecond,
    long WriteOperationsPerSecond,
    bool IsAccessible = true,
    string? StatusMessage = null,
    bool IsForeground = false);

/// <summary>系统累计原始读数。KernelCpuTime 含 IdleCpuTime；网络字节由网卡统计读数填充。</summary>
public sealed record SystemPerformanceReading(
    DateTimeOffset Timestamp,
    TimeSpan KernelCpuTime,
    TimeSpan UserCpuTime,
    TimeSpan IdleCpuTime,
    ulong AvailableMemoryBytes,
    ulong TotalMemoryBytes,
    long NetworkReceivedBytes,
    long NetworkSentBytes);

/// <summary>系统采样：CPU 百分比、可用/总内存、每秒网络收发字节。NetworkLinkUp 来自网卡被动状态，供网络退化规则使用。</summary>
public sealed record SystemPerformanceSample(
    DateTimeOffset Timestamp,
    double CpuPercent,
    long AvailableMemoryBytes,
    long TotalMemoryBytes,
    long NetworkReceivedBytesPerSecond,
    long NetworkSentBytesPerSecond,
    bool NetworkLinkUp = true,
    string NetworkAdapterName = "");

public enum PerformanceEventType
{
    CpuContention,
    MemoryPressure,
    DiskIoPressure,
    NetworkDegradation,
    UserMarkedLag
}

public enum PerformanceEventStatus
{
    Capturing,
    Confirmed,
    Closed
}

public sealed record PerformanceEventContributor(ProcessInstanceKey Process, string ProcessName, double ImpactScore);

/// <summary>一次性能异常或用户标记事件。所有结论都必须附带证据与可信度，且使用“可能/疑似”语义。</summary>
public sealed record PerformanceEvent(
    Guid Id,
    PerformanceEventType Type,
    PerformanceEventStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int Confidence,
    string Summary,
    string MostLikelyCause,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Recommendations,
    ProcessInstanceKey? PrimaryProcess,
    string? PrimaryProcessName = null,
    IReadOnlyList<PerformanceEventContributor>? Contributors = null);

/// <summary>进程中心排序依据：影响分及其构成，仅作证据排序，不代表“确定原因”。</summary>
public sealed record ProcessImpact(
    ProcessInstanceKey Process,
    string Name,
    double Score,
    double CpuPercent,
    long WorkingSetBytes,
    long ReadBytesPerSecond,
    long WriteBytesPerSecond,
    DateTimeOffset LastSeenAt);
