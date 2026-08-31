using System.Collections.Immutable;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Ipc;

/// <summary>无 Collector 模式（--no-collector）下的空客户端：所有请求返回空，UI 显示未连接。</summary>
public sealed class NullCollectorClient : ICollectorClient
{
    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask<ImmutableArray<PortBindingSnapshot>> GetPortSnapshotAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ImmutableArray<PortBindingSnapshot>.Empty);
    public ValueTask<SystemPerformanceSample?> GetSystemSampleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<SystemPerformanceSample?>(null);
    public ValueTask<ImmutableArray<ProcessPerformanceSample>> GetProcessSamplesAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ImmutableArray<ProcessPerformanceSample>.Empty);
    public ValueTask<bool> MarkLagAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask<IReadOnlyList<PerformanceEvent>> GetRecentEventsAsync(int limit = 100, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<PerformanceEvent>>([]);
    public ValueTask<IReadOnlyList<SystemPerformanceSample>> QuerySystemHistoryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<SystemPerformanceSample>>([]);
    public ValueTask<IReadOnlyList<ProcessPerformanceSample>> QueryProcessHistoryAsync(ProcessInstanceKey process, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ProcessPerformanceSample>>([]);
    public ValueTask<IReadOnlyList<PortUsageSummary>> QueryPortUsageAsync(int port, PortProtocol protocol, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<PortUsageSummary>>([]);
    public ValueTask<ProcessEventsSummary> QueryProcessEventsAsync(string processName, int days = 7, int limit = 10, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ProcessEventsSummary(0, []));
    public ValueTask<IReadOnlyList<ImpactRankEntry>> GetImpactRankingAsync(int days = 7, int limit = 10, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ImpactRankEntry>>([]);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
