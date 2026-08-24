using System.Collections.Immutable;
using NetScope.Core.Models;

namespace NetScope.Core.Abstractions;

public interface IPortTableProvider
{
    ValueTask<ImmutableArray<PortBindingSnapshot>> CaptureAsync(CancellationToken cancellationToken = default);
}

public interface IProcessMetadataResolver
{
    ValueTask<ProcessIdentity> ResolveAsync(int processId, CancellationToken cancellationToken = default);
}

public interface IPortCatalog
{
    PortCatalogEntry? Find(int port, PortProtocol protocol);
    IReadOnlyList<PortCatalogEntry> Search(string query, int limit = 100);
    bool IsAssigned(int port, PortProtocol protocol);
}

public interface IPortAvailabilityProbe
{
    ValueTask<PortAvailabilityResult> ProbeAsync(int port, PortProtocol protocol, CancellationToken cancellationToken = default);
}

public interface IPortSystemRangeProvider
{
    ValueTask<SystemPortRangeSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}

public interface INetworkSnapshotProvider
{
    ValueTask<NetworkSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}

public interface IDiagnosticProbe
{
    DiagnosticStage Stage { get; }
    ValueTask<DiagnosticStageResult> RunAsync(NetworkSnapshot snapshot, IReadOnlyList<DiagnosticTarget> targets, CancellationToken cancellationToken);
}

public interface IDiagnosticEngine
{
    ValueTask<DiagnosticRun> RunAsync(IReadOnlyList<DiagnosticTarget> targets, TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask<DiagnosticRun> RunWithProgressAsync(IReadOnlyList<DiagnosticTarget> targets, TimeSpan timeout,
        IProgress<DiagnosticStageResult> progress, CancellationToken cancellationToken = default);
}

public interface INetworkPerformanceTester
{
    ValueTask<NetworkPerformanceResult> RunAsync(
        NetworkPerformanceTestOptions options,
        IProgress<NetworkPerformanceProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
