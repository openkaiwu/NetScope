using System.Collections.Immutable;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.App.Services;

/// <summary>
/// 端口表提供方：优先从后台 Collector 获取持续采集的端口快照；
/// Collector 不可用时回退到本地直接采集，保证端口页始终可用。
/// </summary>
public sealed class CollectorPortTableProvider : IPortTableProvider
{
    private readonly ICollectorClient _client;
    private readonly IPortTableProvider _fallback;

    public CollectorPortTableProvider(ICollectorClient client, IPortTableProvider fallback)
    {
        _client = client;
        _fallback = fallback;
    }

    public async ValueTask<ImmutableArray<PortBindingSnapshot>> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!await _client.IsAvailableAsync(cancellationToken))
            return await _fallback.CaptureAsync(cancellationToken);
        return await _client.GetPortSnapshotAsync(cancellationToken);
    }
}
