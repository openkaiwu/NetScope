using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using NetScope.Core.Services;
using NetScope.Windows.Ipc;

// 用法: NetScope.Repro --server|--client --mode=wrappers|raw|unidir|roundtrip|collector
// roundtrip 模式：在本进程内同时启动真实 CollectorIpcServer 与 CollectorClient，逐段追踪定位 IPC 失败点。
// collector 模式：连接真实后台 Collector（NetScope.Collector.v2 管道），全链路冒烟 V0.2 各操作。
var role = args.Contains("--server") ? "server" : "client";
var mode = args.FirstOrDefault(a => a.StartsWith("--mode="))?.Split('=')[1] ?? "wrappers";

var log = Path.Combine(Path.GetTempPath(), $"netscope-repro-{role}-{mode}.log");
File.WriteAllText(log, $"== {DateTime.Now:HH:mm:ss.fff} {role}/{mode} start =={Environment.NewLine}");
void Trace(string message) => File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
void Fail(string message) { Trace($"ERROR: {message}"); Environment.ExitCode = 1; }

if (mode == "collector")
{
    if (role == "server")
    {
        Console.WriteLine("use --client --mode=collector");
        return;
    }

    await using var ipcClient = new CollectorClient(timeout: TimeSpan.FromSeconds(5));
    Trace("collector: IsAvailableAsync...");
    if (!await ipcClient.IsAvailableAsync()) { Fail("collector not reachable"); Console.WriteLine("collector UNAVAILABLE"); return; }
    Trace("collector: reachable");

    var system = await ipcClient.GetSystemSampleAsync();
    Trace($"collector: system CPU={system?.CpuPercent:0.0}% availMem={system?.AvailableMemoryBytes / 1024 / 1024}MB netDown={system?.NetworkReceivedBytesPerSecond / 1024}KB/s linkUp={system?.NetworkLinkUp} adapter={system?.NetworkAdapterName}");
    Console.WriteLine($"system: CPU {system?.CpuPercent:0.0}%, 可用内存 {system?.AvailableMemoryBytes / 1024 / 1024}MB, 下行 {system?.NetworkReceivedBytesPerSecond / 1024}KB/s, 网卡 {system?.NetworkAdapterName}");

    var processes = await ipcClient.GetProcessSamplesAsync();
    var top = ImpactScoreCalculator.Rank(processes).Take(3).ToList();
    Trace($"collector: processes={processes.Length} top={string.Join(",", top.Select(p => $"{p.Name}({p.CpuPercent:0}%)"))}");
    Console.WriteLine($"processes: {processes.Length} 个采样, Top: {string.Join(", ", top.Select(p => $"{p.Name} CPU {p.CpuPercent:0}%"))}");

    var ports = await ipcClient.GetPortSnapshotAsync();
    Console.WriteLine($"ports: {ports.Length} 条绑定");

    var markAccepted = await ipcClient.MarkLagAsync();
    Trace($"collector: markLag={markAccepted}");
    Console.WriteLine($"markLag: {(markAccepted ? "已接受" : "被拒绝")}");

    await Task.Delay(2500);
    var events = await ipcClient.GetRecentEventsAsync(50);
    Trace($"collector: events={events.Count}");
    Console.WriteLine($"events: {events.Count} 条");
    foreach (var evt in events.Take(5))
        Console.WriteLine($"  [{evt.StartedAt:HH:mm:ss}] {evt.Type} {evt.Status} 可信度{evt.Confidence} {evt.Summary}");

    var history = await ipcClient.QuerySystemHistoryAsync(DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now);
    Console.WriteLine($"systemHistory(5min): {history.Count} 条");
    var processHistory = top.Count > 0
        ? await ipcClient.QueryProcessHistoryAsync(top[0].Process, DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now)
        : [];
    Console.WriteLine($"processHistory(5min, {top.FirstOrDefault().Name}): {processHistory.Count} 条");
    var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetScope", "data", "netscope.db");
    Console.WriteLine($"db: {dbPath} exists={File.Exists(dbPath)} size={(File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0)}B");
    Trace("collector: smoke done");
    return;
}

if (mode == "roundtrip")
{
    if (role == "server")
    {
        Trace("roundtrip/server: not used; client mode hosts both sides");
        Console.WriteLine("use --client --mode=roundtrip");
        return;
    }

    var serverName = $"netscope-repro-{Guid.NewGuid():N}";
    Trace($"roundtrip: starting CollectorIpcServer on {serverName}");
    var server = new CollectorIpcServer((op, payload, ct) =>
    {
        Trace($"roundtrip: server handling op={op} payload={(payload is null ? "<null>" : payload[..Math.Min(40, payload.Length)])}");
        return ValueTask.FromResult<string?>(op == CollectorProtocol.OpPing ? "pong" : "{\"ok\":true}");
    }, pipeName: serverName);
    server.Start();
    Trace("roundtrip: server started, waiting 300ms for listener");
    await Task.Delay(300);

    // 用真实 CollectorClient，指向临时管道名，隔离对生产管道名的依赖
    await using var ipcClient = new CollectorClient(pipeName: serverName, timeout: TimeSpan.FromSeconds(3));
    var sw = Stopwatch.StartNew();
    Trace("roundtrip: calling IsAvailableAsync");
    var ok = await ipcClient.IsAvailableAsync();
    Trace($"roundtrip: IsAvailableAsync={ok} in {sw.ElapsedMilliseconds}ms");

    if (!ok) Fail("ping round trip failed");
    else Trace("roundtrip: SUCCESS");

    await server.DisposeAsync();
    Console.WriteLine(ok ? "roundtrip OK" : "roundtrip FAILED");
    return;
}

if (role == "server")
{
    Trace("server: creating pipe...");
    using var server = new NamedPipeServerStream(CollectorProtocol.PipeName, PipeDirection.InOut, 8,
        PipeTransmissionMode.Byte);
    Trace("server: waiting for connection...");
    await server.WaitForConnectionAsync(CancellationToken.None);
    Trace("server: connected!");

    if (mode == "unidir")
    {
        Trace("server: creating reader (unidir)...");
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        var line = await reader.ReadLineAsync();
        Trace($"server: read '{line}'");
    }
    else if (mode == "raw")
    {
        Trace("server: raw reading frame...");
        var lenBuf = new byte[4];
        await server.ReadExactlyAsync(lenBuf);
        var len = BitConverter.ToInt32(lenBuf);
        var body = new byte[len];
        await server.ReadExactlyAsync(body);
        var line = Encoding.UTF8.GetString(body);
        Trace($"server: read '{line}'");
        var payload = CollectorProtocol.Serialize(new HelloResponse(CollectorProtocol.ProtocolVersion, true, "srv"));
        var response = CollectorProtocol.Serialize(new IpcEnvelope(CollectorProtocol.OpHello, "req-1", true, null, payload));
        var respBytes = Encoding.UTF8.GetBytes(response);
        await server.WriteAsync(BitConverter.GetBytes(respBytes.Length));
        await server.WriteAsync(respBytes);
        Trace("server: raw response written");
    }
    else
    {
        Trace("server: creating reader...");
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        Trace("server: creating writer...");
        using var writer = new StreamWriter(server, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
        Trace("server: reading line...");
        var line = await reader.ReadLineAsync();
        Trace($"server: read '{line}'");
    }
    Console.WriteLine("server done");
    return;
}

// client
Trace("client: creating pipe...");
using var client = new NamedPipeClientStream(".", CollectorProtocol.PipeName, PipeDirection.InOut);
Trace("client: connecting...");
using var connectCts = new CancellationTokenSource(3000);
await client.ConnectAsync(connectCts.Token);
Trace("client: connected");

if (mode == "unidir")
{
    Trace("client: creating writer (unidir)...");
    using var writer = new StreamWriter(client, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
    var envelope = CollectorProtocol.Serialize(new IpcEnvelope(CollectorProtocol.OpHello, "req-1", true, null,
        CollectorProtocol.Serialize(new HelloRequest(CollectorProtocol.ProtocolVersion, "test"))));
    await writer.WriteLineAsync(envelope);
    Trace("client: hello written");
}
else if (mode == "raw")
{
    Trace("client: raw writing frame...");
    var envelope = CollectorProtocol.Serialize(new IpcEnvelope(CollectorProtocol.OpHello, "req-1", true, null,
        CollectorProtocol.Serialize(new HelloRequest(CollectorProtocol.ProtocolVersion, "test"))));
    var body = Encoding.UTF8.GetBytes(envelope);
    await client.WriteAsync(BitConverter.GetBytes(body.Length));
    await client.WriteAsync(body);
    Trace("client: raw frame written, reading...");
    var lenBuf = new byte[4];
    await client.ReadExactlyAsync(lenBuf);
    var len = BitConverter.ToInt32(lenBuf);
    var resp = new byte[len];
    await client.ReadExactlyAsync(resp);
    Trace($"client: got '{Encoding.UTF8.GetString(resp)}'");
}
else
{
    Trace("client: creating reader...");
    using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
    Trace("client: creating writer...");
    using var writer = new StreamWriter(client, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
    Trace("client: writing hello...");
    var envelope = CollectorProtocol.Serialize(new IpcEnvelope(CollectorProtocol.OpHello, "req-1", true, null,
        CollectorProtocol.Serialize(new HelloRequest(CollectorProtocol.ProtocolVersion, "test"))));
    await writer.WriteLineAsync(envelope);
    Trace("client: hello written");
}
Console.WriteLine("client done");
