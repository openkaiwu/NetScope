namespace NetScope.Core.Models;

public enum SamplingMode { Normal, Reduced, Minimal }

public sealed record SamplingProfile(SamplingMode Mode, int PerformanceIntervalMilliseconds,
    int PortIntervalMilliseconds, int ProcessHistoryTopN);

public sealed record CollectorHealthSnapshot(DateTimeOffset Timestamp, double CpuPercent,
    long WorkingSetBytes, long ReadBytesPerSecond, long WriteBytesPerSecond,
    double CycleDurationMilliseconds, SamplingProfile Profile, IReadOnlyList<string> Reasons);

public sealed record CollectorUsageReading(DateTimeOffset Timestamp, double CpuPercent,
    long WorkingSetBytes, long ReadBytesPerSecond, long WriteBytesPerSecond);

public enum ProcessBehaviorKind { MemoryGrowth, PeriodicActivity }

public sealed record ProcessHistoryPoint(string ProcessName, DateTimeOffset Timestamp,
    double CpuPercent, long PrivateBytes, long IoBytesPerSecond, int SampleCount = 1,
    int ProcessId = 0, DateTimeOffset? ProcessStartedAt = null);

public sealed record ProcessBehaviorFinding(ProcessBehaviorKind Kind, string ProcessName,
    DateTimeOffset From, DateTimeOffset To, int Confidence, string Title, string Summary,
    IReadOnlyList<string> Evidence);

public enum InsightKind
{
    LagSummary,
    FrequentPerformanceEvent,
    MemoryGrowth,
    PeriodicActivity,
    PortActivity,
    ConnectionActivity
}

public sealed record InsightQuery(int Days = 30, string ProcessName = "",
    InsightKind? Kind = null, int Limit = 50);

public sealed record InsightItem(Guid Id, InsightKind Kind, string Title, string Summary,
    int Confidence, DateTimeOffset From, DateTimeOffset To, string? ProcessName,
    IReadOnlyList<string> Evidence, IReadOnlyList<Guid> SourceEventIds);

public sealed record PortActivitySummary(int Port, PortProtocol Protocol, string ProcessName,
    int SessionCount, double TotalSeconds, DateTimeOffset LastSeenAt);
