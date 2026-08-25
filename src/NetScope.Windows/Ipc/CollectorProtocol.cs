using System.Collections.Immutable;
using System.Text.Json;
using NetScope.Core.Models;

namespace NetScope.Windows.Ipc;

/// <summary>Collector 与 App 之间命名管道协议：常量、线上 DTO 与 Core 模型映射。</summary>
public static class CollectorProtocol
{
    public const string PipeName = "NetScope.Collector.v2";
    public const string LocalMutexName = @"Local\NetScope.Collector.v2";
    public const int ProtocolVersion = 1;
    public const string ServerVersion = "0.2.0";
    public const int MaxMessageBytes = 2 * 1024 * 1024;

    public const string OpHello = "hello";
    public const string OpPing = "ping";
    public const string OpPorts = "ports";
    public const string OpSystem = "system";
    public const string OpProcesses = "processes";
    public const string OpMarkLag = "markLag";
    public const string OpEvents = "events";
    public const string OpSystemHistory = "systemHistory";
    public const string OpProcessHistory = "processHistory";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 24,
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);
}

/// <summary>管道消息信封：op + requestId 关联请求与响应，json 为负载序列化字符串。</summary>
public sealed record IpcEnvelope(string Op, string RequestId, bool Ok, string? Error, string? Json);

public sealed record HelloRequest(int ProtocolVersion, string ClientVersion);
public sealed record HelloResponse(int ProtocolVersion, bool Accepted, string ServerVersion);

public sealed record PortBindingDto(int Protocol, int AddressFamily, string LocalAddress, int Port, int ProcessId, string State);
public sealed record PortsSnapshotDto(DateTimeOffset CapturedAt, ImmutableArray<PortBindingDto> Bindings);

public sealed record SystemSampleDto(
    DateTimeOffset Timestamp,
    double CpuPercent,
    long AvailableMemoryBytes,
    long TotalMemoryBytes,
    long NetworkReceivedBytesPerSecond,
    long NetworkSentBytesPerSecond,
    bool NetworkLinkUp = true,
    string NetworkAdapterName = "");

public sealed record ProcessSampleDto(
    int ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset Timestamp,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    long ReadBytesPerSecond,
    long WriteBytesPerSecond,
    long ReadOperationsPerSecond,
    long WriteOperationsPerSecond,
    bool IsAccessible,
    string? StatusMessage,
    bool IsForeground = false);

public sealed record MarkLagDto(DateTimeOffset MarkedAt, bool Accepted);

public sealed record EventContributorDto(int ProcessId, DateTimeOffset StartedAt, string ProcessName, double ImpactScore);

public sealed record PerformanceEventDto(
    string Id,
    int Type,
    int Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int Confidence,
    string Summary,
    string MostLikelyCause,
    string[] Evidence,
    string[] Recommendations,
    int? PrimaryProcessId,
    DateTimeOffset? PrimaryStartedAt,
    string? PrimaryProcessName,
    EventContributorDto[] Contributors);

public sealed record EventsRequest(int Limit);

public sealed record HistoryRequest(DateTimeOffset From, DateTimeOffset To);

public sealed record ProcessHistoryRequest(int ProcessId, DateTimeOffset StartedAt, DateTimeOffset From, DateTimeOffset To);

public static class CollectorDtos
{
    public static PortsSnapshotDto ToDto(ImmutableArray<PortBindingSnapshot> bindings) =>
        new(DateTimeOffset.Now, bindings.Select(b => new PortBindingDto(
            (int)b.Protocol, (int)b.AddressFamily, b.LocalAddress, b.Port, b.ProcessId, b.State)).ToImmutableArray());

    public static ImmutableArray<PortBindingSnapshot> ToModels(PortsSnapshotDto dto)
    {
        if (dto.Bindings.IsDefault) return [];
        var builder = ImmutableArray.CreateBuilder<PortBindingSnapshot>(dto.Bindings.Length);
        foreach (var b in dto.Bindings)
            builder.Add(new PortBindingSnapshot(
                new((PortProtocol)b.Protocol, (IpAddressFamily)b.AddressFamily, b.LocalAddress, b.Port, b.ProcessId, b.State),
                dto.CapturedAt));
        return builder.MoveToImmutable();
    }

    public static SystemSampleDto ToDto(SystemPerformanceSample sample) =>
        new(sample.Timestamp, sample.CpuPercent, sample.AvailableMemoryBytes, sample.TotalMemoryBytes,
            sample.NetworkReceivedBytesPerSecond, sample.NetworkSentBytesPerSecond,
            sample.NetworkLinkUp, sample.NetworkAdapterName);

    public static SystemPerformanceSample ToModel(SystemSampleDto dto) =>
        new(dto.Timestamp, dto.CpuPercent, dto.AvailableMemoryBytes, dto.TotalMemoryBytes,
            dto.NetworkReceivedBytesPerSecond, dto.NetworkSentBytesPerSecond,
            dto.NetworkLinkUp, dto.NetworkAdapterName);

    public static ProcessSampleDto ToDto(ProcessPerformanceSample sample) =>
        new(sample.Process.ProcessId, sample.Process.StartedAt, sample.Timestamp, sample.Name, sample.CpuPercent,
            sample.WorkingSetBytes, sample.PrivateBytes, sample.ReadBytesPerSecond, sample.WriteBytesPerSecond,
            sample.ReadOperationsPerSecond, sample.WriteOperationsPerSecond, sample.IsAccessible, sample.StatusMessage,
            sample.IsForeground);

    public static ProcessPerformanceSample ToModel(ProcessSampleDto dto) =>
        new(new(dto.ProcessId, dto.StartedAt), dto.Timestamp, dto.Name, dto.CpuPercent, dto.WorkingSetBytes, dto.PrivateBytes,
            dto.ReadBytesPerSecond, dto.WriteBytesPerSecond, dto.ReadOperationsPerSecond, dto.WriteOperationsPerSecond,
            dto.IsAccessible, dto.StatusMessage, dto.IsForeground);

    public static PerformanceEventDto ToDto(PerformanceEvent evt) => new(
        evt.Id.ToString(), (int)evt.Type, (int)evt.Status, evt.StartedAt, evt.EndedAt, evt.Confidence,
        evt.Summary, evt.MostLikelyCause,
        evt.Evidence.ToArray(), evt.Recommendations.ToArray(),
        evt.PrimaryProcess?.ProcessId, evt.PrimaryProcess?.StartedAt, evt.PrimaryProcessName,
        (evt.Contributors ?? []).Select(c => new EventContributorDto(c.Process.ProcessId, c.Process.StartedAt, c.ProcessName, c.ImpactScore)).ToArray());

    public static PerformanceEvent ToModel(PerformanceEventDto dto) => new(
        Guid.Parse(dto.Id), (PerformanceEventType)dto.Type, (PerformanceEventStatus)dto.Status,
        dto.StartedAt, dto.EndedAt, dto.Confidence, dto.Summary, dto.MostLikelyCause,
        dto.Evidence, dto.Recommendations,
        dto.PrimaryProcessId is { } pid ? new ProcessInstanceKey(pid, dto.PrimaryStartedAt ?? DateTimeOffset.MinValue) : null,
        dto.PrimaryProcessName,
        dto.Contributors.Select(c => new PerformanceEventContributor(new ProcessInstanceKey(c.ProcessId, c.StartedAt), c.ProcessName, c.ImpactScore)).ToList());
}
