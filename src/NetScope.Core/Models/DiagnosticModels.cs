using System.Collections.Immutable;

namespace NetScope.Core.Models;

public enum DiagnosticStage
{
    Local,
    Adapter,
    IpDhcp,
    Gateway,
    Dns,
    Internet,
    Target
}

public enum DiagnosticStatus { Healthy, Degraded, Fault, NotTested }

public enum NetworkTimingKind
{
    GatewayIcmp,
    DnsLookup,
    TcpConnect,
    TlsHandshake,
    TargetTcp,
    HttpLatency
}

public sealed record NetworkTimingSample(
    NetworkTimingKind Kind,
    string Label,
    double Milliseconds,
    DateTimeOffset CapturedAt,
    bool Succeeded = true);

public sealed record NetworkAdapterSnapshot(
    string Id,
    string Name,
    string Description,
    bool IsUp,
    bool IsWireless,
    long LinkSpeedBitsPerSecond,
    int? SignalQuality,
    string? SsidLabel,
    ImmutableArray<string> Addresses,
    ImmutableArray<string> Gateways,
    ImmutableArray<string> DnsServers,
    long BytesReceived,
    long BytesSent,
    long ReceiveLinkSpeedBitsPerSecond = 0,
    long TransmitLinkSpeedBitsPerSecond = 0,
    bool IsVirtual = false,
    string MediaType = "未知");

public sealed record NetworkSnapshot(
    DateTimeOffset CapturedAt,
    bool IsNetworkAvailable,
    ImmutableArray<NetworkAdapterSnapshot> Adapters,
    bool HasApipaAddress,
    bool HasDefaultGateway,
    bool HasDnsServers,
    string? ActiveAdapterId,
    bool HasVpnAdapter = false,
    bool HasProxyConfigured = false)
{
    public NetworkAdapterSnapshot? ActiveAdapter =>
        Adapters.FirstOrDefault(a => a.Id == ActiveAdapterId) ?? Adapters.FirstOrDefault(a => a.IsUp);
}

public sealed record DiagnosticTarget(string Name, string Host, int Port = 443);

public sealed record DiagnosticStageResult(
    DiagnosticStage Stage,
    DiagnosticStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    ImmutableDictionary<string, string> Metrics,
    ImmutableArray<string> Evidence,
    string MostLikelyCause,
    double Confidence,
    ImmutableArray<string> Suggestions,
    string? Error = null,
    bool TimedOut = false,
    ImmutableArray<double> LatencySamples = default,
    ImmutableArray<NetworkTimingSample> TimingSamples = default)
{
    public TimeSpan Duration => EndedAt - StartedAt;
    public ImmutableArray<double> NetworkLatencySamples => LatencySamples.IsDefault ? [] : LatencySamples;
    public ImmutableArray<NetworkTimingSample> NetworkTimings => TimingSamples.IsDefault ? [] : TimingSamples;
}

public sealed record DiagnosticRun(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    ImmutableArray<DiagnosticStageResult> Stages,
    string Summary,
    DiagnosticStatus OverallStatus,
    double Confidence,
    bool WasCancelled = false)
{
    public TimeSpan Duration => EndedAt - StartedAt;
}

public enum PerformanceTestPhase
{
    Preparing,
    IdleLatency,
    Download,
    Upload,
    Finalizing,
    Completed
}

public sealed record NetworkPerformanceTestOptions(
    Uri DownloadEndpoint,
    Uri UploadEndpoint,
    int IdleLatencySamples = 8,
    long DownloadWarmupBytes = 1_000_000,
    long DownloadMinimumBytes = 5_000_000,
    long DownloadMaximumBytes = 50_000_000,
    long UploadWarmupBytes = 256_000,
    long UploadMinimumBytes = 1_000_000,
    long UploadMaximumBytes = 10_000_000)
{
    public static NetworkPerformanceTestOptions CloudflareDefault { get; } = new(
        new Uri("https://speed.cloudflare.com/__down"),
        new Uri("https://speed.cloudflare.com/__up"));

    public long MaximumTransferBytes => DownloadWarmupBytes + DownloadMaximumBytes + UploadWarmupBytes + UploadMaximumBytes;
}

public sealed record NetworkPerformanceProgress(
    PerformanceTestPhase Phase,
    double Progress,
    string Message,
    long BytesTransferred = 0,
    double? MegabitsPerSecond = null);

public sealed record NetworkPerformanceResult(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    bool Succeeded,
    double IdleLatencyMilliseconds,
    double IdleJitterMilliseconds,
    double DownloadMegabitsPerSecond,
    double UploadMegabitsPerSecond,
    double DownloadLoadedLatencyMilliseconds,
    double UploadLoadedLatencyMilliseconds,
    double BufferbloatDeltaMilliseconds,
    string BufferbloatGrade,
    string Summary,
    long DownloadBytes,
    long UploadBytes,
    ImmutableArray<double> IdleLatencySamples,
    ImmutableArray<double> DownloadLoadedLatencySamples,
    ImmutableArray<double> UploadLoadedLatencySamples,
    string? Error = null,
    bool WasCancelled = false)
{
    public TimeSpan Duration => EndedAt - StartedAt;
}
