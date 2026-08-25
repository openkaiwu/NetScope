using System.Collections.Immutable;
using System.IO.Pipes;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Ipc;

/// <summary>
/// App 侧 Collector 命名管道客户端。每次请求建立短连接并先做版本握手；
/// 连接失败、超时或版本不匹配时返回空结果，由调用方决定降级。
/// </summary>
public sealed class CollectorClient : ICollectorClient
{
    private readonly string _pipeName;
    private readonly string _clientVersion;
    private readonly TimeSpan _timeout;

    public CollectorClient(string? pipeName = null, string? clientVersion = null, TimeSpan? timeout = null)
    {
        _pipeName = pipeName ?? CollectorProtocol.PipeName;
        _clientVersion = clientVersion ?? CollectorProtocol.ServerVersion;
        _timeout = timeout ?? TimeSpan.FromSeconds(1.5);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(CollectorProtocol.OpPing, null, cancellationToken);
        return payload is not null;
    }

    public async ValueTask<ImmutableArray<PortBindingSnapshot>> GetPortSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(CollectorProtocol.OpPorts, null, cancellationToken);
        if (payload is null) return [];
        try
        {
            var dto = CollectorProtocol.Deserialize<PortsSnapshotDto>(payload);
            return dto is null ? [] : CollectorDtos.ToModels(dto);
        }
        catch { return []; }
    }

    public async ValueTask<SystemPerformanceSample?> GetSystemSampleAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(CollectorProtocol.OpSystem, null, cancellationToken);
        if (payload is null) return null;
        try
        {
            var dto = CollectorProtocol.Deserialize<SystemSampleDto>(payload);
            return dto is null ? null : CollectorDtos.ToModel(dto);
        }
        catch { return null; }
    }

    public async ValueTask<ImmutableArray<ProcessPerformanceSample>> GetProcessSamplesAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(CollectorProtocol.OpProcesses, null, cancellationToken);
        if (payload is null) return [];
        try
        {
            var dtos = CollectorProtocol.Deserialize<ImmutableArray<ProcessSampleDto>>(payload);
            if (dtos.IsDefault) return [];
            var builder = ImmutableArray.CreateBuilder<ProcessPerformanceSample>(dtos.Length);
            foreach (var dto in dtos) builder.Add(CollectorDtos.ToModel(dto));
            return builder.MoveToImmutable();
        }
        catch { return []; }
    }

    public async ValueTask<bool> MarkLagAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(CollectorProtocol.OpMarkLag, null, cancellationToken);
        if (payload is null) return false;
        try
        {
            var dto = CollectorProtocol.Deserialize<MarkLagDto>(payload);
            return dto is { Accepted: true };
        }
        catch { return false; }
    }

    public async ValueTask<IReadOnlyList<PerformanceEvent>> GetRecentEventsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(CollectorProtocol.OpEvents,
            CollectorProtocol.Serialize(new EventsRequest(Math.Clamp(limit, 1, 500))), cancellationToken);
        if (payload is null) return [];
        try
        {
            var dtos = CollectorProtocol.Deserialize<PerformanceEventDto[]>(payload);
            return dtos is null ? [] : dtos.Select(CollectorDtos.ToModel).ToList();
        }
        catch { return []; }
    }

    public async ValueTask<IReadOnlyList<SystemPerformanceSample>> QuerySystemHistoryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(CollectorProtocol.OpSystemHistory,
            CollectorProtocol.Serialize(new HistoryRequest(from, to)), cancellationToken);
        if (payload is null) return [];
        try
        {
            var dtos = CollectorProtocol.Deserialize<SystemSampleDto[]>(payload);
            if (dtos is null || dtos.Length == 0) return [];
            return dtos.Select(CollectorDtos.ToModel).ToList();
        }
        catch { return []; }
    }

    public async ValueTask<IReadOnlyList<ProcessPerformanceSample>> QueryProcessHistoryAsync(ProcessInstanceKey process, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(CollectorProtocol.OpProcessHistory,
            CollectorProtocol.Serialize(new ProcessHistoryRequest(process.ProcessId, process.StartedAt, from, to)), cancellationToken);
        if (payload is null) return [];
        try
        {
            var dtos = CollectorProtocol.Deserialize<ProcessSampleDto[]>(payload);
            if (dtos is null || dtos.Length == 0) return [];
            return dtos.Select(CollectorDtos.ToModel).ToList();
        }
        catch { return []; }
    }

    private async ValueTask<string?> RequestAsync(string op, string? payloadJson, CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_timeout);
        try
        {
            await client.ConnectAsync(linked.Token);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException) { return null; }

        var requestId = Guid.NewGuid().ToString("N");

        try
        {
            await PipeFrame.WriteAsync(client, CollectorProtocol.Serialize(new IpcEnvelope(
                CollectorProtocol.OpHello, requestId, true, null,
                CollectorProtocol.Serialize(new HelloRequest(CollectorProtocol.ProtocolVersion, _clientVersion)))), linked.Token);

            var helloJson = await PipeFrame.ReadAsync(client, linked.Token);
            if (helloJson is null) return null;
            var hello = CollectorProtocol.Deserialize<IpcEnvelope>(helloJson);
            if (hello is null || !hello.Ok) return null;

            await PipeFrame.WriteAsync(client, CollectorProtocol.Serialize(new IpcEnvelope(op, requestId, true, null, payloadJson)), linked.Token);
            var responseJson = await PipeFrame.ReadAsync(client, linked.Token);
            if (responseJson is null) return null;
            var response = CollectorProtocol.Deserialize<IpcEnvelope>(responseJson);
            return response is { Ok: true } ? response.Json : null;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            return null;
        }
    }
}
