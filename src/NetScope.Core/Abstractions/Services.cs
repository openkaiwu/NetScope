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

/// <summary>
/// 可执行文件静态身份提供方：公司/产品/描述/版本 + 本地签名验证结果。
/// 结果按“路径+大小+修改时间”缓存（内存 + 磁盘），文件未变时不重复读取或验证签名。
/// </summary>
public interface IProcessFileMetadataProvider
{
    ValueTask<ProcessFileMetadata?> ResolveAsync(string filePath, CancellationToken cancellationToken = default);
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
