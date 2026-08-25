using System.IO.Pipes;
using NetScope.Windows.Logging;

namespace NetScope.Windows.Ipc;

/// <summary>Collector 侧命名管道服务端。每个连接先完成 hello 版本握手，再处理请求/响应。</summary>
public sealed class CollectorIpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<string, string?, CancellationToken, ValueTask<string?>> _handler;
    private readonly RollingFileLogger? _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _pendingGate = new();
    private NamedPipeServerStream? _pendingListener;
    private Task? _loop;
    private bool _started;

    public CollectorIpcServer(Func<string, string?, CancellationToken, ValueTask<string?>> handler, RollingFileLogger? logger = null, string? pipeName = null)
    {
        _pipeName = pipeName ?? CollectorProtocol.PipeName;
        _handler = handler;
        _logger = logger;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _loop = Task.Run(() => RunLoopAsync(_lifetime.Token));
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                lock (_pendingGate) _pendingListener = server;
                await server.WaitForConnectionAsync(cancellationToken);
                lock (_pendingGate) _pendingListener = null;
                _ = HandleConnectionAsync(server, cancellationToken);
                server = null; // 已移交连接处理，不再由本循环负责
            }
            catch (OperationCanceledException)
            {
                await DisposeListenerAsync(server);
                break;
            }
            catch (ObjectDisposedException)
            {
                // DisposeAsync 强制关闭了挂起的监听实例，属正常退出路径
                await DisposeListenerAsync(server);
                break;
            }
            catch (Exception ex)
            {
                await DisposeListenerAsync(server);
                await LogErrorAsync("监听循环", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ContinueWith(_ => { });
            }
        }
    }

    private static async ValueTask DisposeListenerAsync(NamedPipeServerStream? server)
    {
        if (server is null) return;
        try { await server.DisposeAsync(); }
        catch (Exception) { }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var json = await PipeFrame.ReadAsync(server, cancellationToken);
                if (json is null) return;

                IpcEnvelope? envelope;
                try { envelope = CollectorProtocol.Deserialize<IpcEnvelope>(json); }
                catch { return; }
                if (envelope is null) continue;

                if (envelope.Op == CollectorProtocol.OpHello)
                {
                    HelloRequest? hello;
                    try { hello = CollectorProtocol.Deserialize<HelloRequest>(envelope.Json ?? "{}"); }
                    catch { hello = null; }
                    var accepted = hello is not null && hello.ProtocolVersion == CollectorProtocol.ProtocolVersion;
                    var response = new IpcEnvelope(CollectorProtocol.OpHello, envelope.RequestId, accepted,
                        accepted ? null : "Collector 协议版本不匹配",
                        CollectorProtocol.Serialize(new HelloResponse(CollectorProtocol.ProtocolVersion, accepted, CollectorProtocol.ServerVersion)));
                    await PipeFrame.WriteAsync(server, CollectorProtocol.Serialize(response), cancellationToken);
                    if (!accepted) return;
                    continue;
                }

                string? payload;
                string? error = null;
                try
                {
                    payload = await _handler(envelope.Op, envelope.Json, cancellationToken) ?? "null";
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    error = ex.Message;
                    payload = "null";
                    await LogErrorAsync($"操作 {envelope.Op}", ex);
                }

                await PipeFrame.WriteAsync(server, CollectorProtocol.Serialize(
                    new IpcEnvelope(envelope.Op, envelope.RequestId, error is null, error, payload)), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            await LogErrorAsync("连接处理", ex);
        }
        finally
        {
            try { await server.DisposeAsync(); }
            catch (Exception) { }
        }
    }

    private async ValueTask LogErrorAsync(string context, Exception exception)
    {
        if (_logger is not null)
            await _logger.WriteAsync("ERROR", $"IPC {context}: {exception.Message}");
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();

        // WaitForConnectionAsync 在 Windows 上不响应取消令牌；
        // 强制关闭挂起的监听实例使等待立即以 ObjectDisposedException 结束。
        NamedPipeServerStream? pending;
        lock (_pendingGate) pending = _pendingListener;
        if (pending is not null)
        {
            try { pending.Dispose(); }
            catch (Exception) { }
        }

        if (_loop is not null)
        {
            try { await _loop; }
            catch (Exception) { }
        }
        _lifetime.Dispose();
    }
}
