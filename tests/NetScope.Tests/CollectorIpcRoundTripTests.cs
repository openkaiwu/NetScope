using System.Collections.Immutable;
using System.IO.Pipes;
using NetScope.Core.Models;
using NetScope.Windows.Ipc;

namespace NetScope.Tests;

/// <summary>
/// IPC 往返测试。每个测试类实例使用独立随机管道名，避免与真实 Collector、
/// 其他测试类或并行运行残留实例共享 "NetScope.Collector.v2" 造成串扰。
/// </summary>
public sealed class CollectorIpcRoundTripTests : IAsyncLifetime
{
    private readonly string _pipeName = $"netscope-test-{Guid.NewGuid():N}";
    private readonly CollectorIpcServer _server;

    public CollectorIpcRoundTripTests()
    {
        _server = new CollectorIpcServer(HandleAsync, pipeName: _pipeName);
    }

    private static async ValueTask<string?> HandleAsync(string op, string? payloadJson, CancellationToken cancellationToken)
    {
        return op switch
        {
            CollectorProtocol.OpPing => "pong",
            CollectorProtocol.OpPorts => CollectorProtocol.Serialize(CollectorDtos.ToDto(
                ImmutableArray.Create(new PortBindingSnapshot(new(PortProtocol.Tcp, IpAddressFamily.IPv4, "0.0.0.0", 8443, 999, "Listen"), DateTimeOffset.Now)))),
            CollectorProtocol.OpSystem => CollectorProtocol.Serialize(new SystemSampleDto(
                DateTimeOffset.Now, 20, 4_000_000_000, 8_000_000_000, 100_000, 50_000)),
            CollectorProtocol.OpProcesses => CollectorProtocol.Serialize(
                new ProcessSampleDto[] { new(777, DateTimeOffset.UnixEpoch, DateTimeOffset.Now, "svc", 5, 1000, 800, 10, 20, 1, 2, true, null) }),
            CollectorProtocol.OpMarkLag => CollectorProtocol.Serialize(new MarkLagDto(DateTimeOffset.Now, true)),
            _ => throw new InvalidOperationException($"未知操作: {op}")
        };
    }

    public Task InitializeAsync()
    {
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var dispose = _server.DisposeAsync().AsTask();
            await Task.WhenAny(dispose, Task.Delay(Timeout.Infinite, timeout.Token));
        }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PingRoundTrip()
    {
        await using var client = NewClient();
        Assert.True(await client.IsAvailableAsync());
    }

    [Fact]
    public async Task PortsRoundTrip()
    {
        await using var client = NewClient();
        var ports = await client.GetPortSnapshotAsync();
        var port = Assert.Single(ports);
        Assert.Equal(8443, port.Port);
        Assert.Equal(999, port.ProcessId);
    }

    [Fact]
    public async Task SystemRoundTrip()
    {
        await using var client = NewClient();
        var sample = await client.GetSystemSampleAsync();
        Assert.NotNull(sample);
        Assert.Equal(20, sample!.CpuPercent, precision: 2);
    }

    [Fact]
    public async Task ProcessesRoundTrip()
    {
        await using var client = NewClient();
        var samples = await client.GetProcessSamplesAsync();
        var sample = Assert.Single(samples);
        Assert.Equal(777, sample.Process.ProcessId);
        Assert.Equal("svc", sample.Name);
    }

    [Fact]
    public async Task MarkLagRoundTrip()
    {
        await using var client = NewClient();
        Assert.True(await client.MarkLagAsync());
    }

    [Fact]
    public async Task VersionMismatchIsRejected()
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000);

        // 错误协议版本
        await PipeFrame.WriteAsync(pipe, CollectorProtocol.Serialize(new IpcEnvelope(
            CollectorProtocol.OpHello, "req-1", true, null,
            CollectorProtocol.Serialize(new HelloRequest(999, "test")))), CancellationToken.None);

        var responseJson = await PipeFrame.ReadAsync(pipe, CancellationToken.None);
        Assert.NotNull(responseJson);
        var response = CollectorProtocol.Deserialize<IpcEnvelope>(responseJson!);
        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.Contains("版本", response.Error);
    }

    [Fact]
    public async Task DisposeStopsListenerWithoutHanging()
    {
        var pipeName = $"netscope-test-{Guid.NewGuid():N}";
        var server = new CollectorIpcServer((_, _, _) => ValueTask.FromResult<string?>("null"), pipeName: pipeName);
        server.Start();
        await Task.Delay(100);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var dispose = server.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(dispose, Task.Delay(Timeout.Infinite, timeout.Token));
        Assert.Same(dispose, finished);
    }

    private CollectorClient NewClient() => new(pipeName: _pipeName, timeout: TimeSpan.FromSeconds(2));
}
