using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Windows.Network;

internal static class ProbeResult
{
    public static DiagnosticStageResult Create(DiagnosticStage stage, DiagnosticStatus status, DateTimeOffset started,
        IDictionary<string, string>? metrics, IEnumerable<string> evidence, string cause, double confidence,
        IEnumerable<string> suggestions, string? error = null, bool timedOut = false, IEnumerable<double>? latencySamples = null,
        IEnumerable<NetworkTimingSample>? timingSamples = null) =>
        new(stage, status, started, DateTimeOffset.Now,
            (metrics ?? new Dictionary<string, string>()).ToImmutableDictionary(), evidence.ToImmutableArray(),
            cause, Math.Clamp(confidence, 0, 1), suggestions.ToImmutableArray(), error, timedOut,
            latencySamples?.ToImmutableArray() ?? [], timingSamples?.ToImmutableArray() ?? []);
}

public sealed class LocalDiagnosticProbe : IDiagnosticProbe
{
    public DiagnosticStage Stage => DiagnosticStage.Local;
    public ValueTask<DiagnosticStageResult> RunAsync(NetworkSnapshot snapshot, IReadOnlyList<DiagnosticTarget> targets, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var result = ProbeResult.Create(Stage, DiagnosticStatus.Healthy, now,
            new Dictionary<string, string> { ["系统"] = Environment.OSVersion.VersionString, ["设备"] = Environment.MachineName },
            ["网络诊断服务可运行", "系统网络 API 可访问"], "本机诊断能力正常", .99,
            ["继续检查网卡和链路状态"]);
        return ValueTask.FromResult(result);
    }
}

public sealed class AdapterDiagnosticProbe : IDiagnosticProbe
{
    public DiagnosticStage Stage => DiagnosticStage.Adapter;
    public ValueTask<DiagnosticStageResult> RunAsync(NetworkSnapshot snapshot, IReadOnlyList<DiagnosticTarget> targets, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var active = snapshot.ActiveAdapter;
        if (active is null)
            return ValueTask.FromResult(ProbeResult.Create(Stage, DiagnosticStatus.Fault, started, null,
                ["没有已连接且可用的物理或虚拟网卡"], "网卡未连接或已禁用", .99,
                ["打开 Windows 网络设置并启用网卡", "检查网线或重新连接 Wi-Fi"]));

        var negotiated = active.TransmitLinkSpeedBitsPerSecond > 0 ? active.TransmitLinkSpeedBitsPerSecond : active.LinkSpeedBitsPerSecond;
        var speed = FormatLinkSpeed(negotiated);
        var weakWifi = active.IsWireless && active.SignalQuality is < 45;
        var status = weakWifi ? DiagnosticStatus.Degraded : DiagnosticStatus.Healthy;
        var metrics = new Dictionary<string, string> { ["网卡"] = active.Name, ["链路速率"] = speed, ["类型"] = active.MediaType };
        if (active.ReceiveLinkSpeedBitsPerSecond > 0) metrics["接收协商"] = FormatLinkSpeed(active.ReceiveLinkSpeedBitsPerSecond);
        if (active.TransmitLinkSpeedBitsPerSecond > 0) metrics["发送协商"] = FormatLinkSpeed(active.TransmitLinkSpeedBitsPerSecond);
        if (active.IsWireless) metrics["Wi-Fi 信号"] = active.SignalQuality is { } signal ? $"{signal}%" : "未知";
        var evidence = new List<string> { $"{active.Name} 状态为 Up", $"协商链路速率 {speed}" };
        if (active.IsVirtual) evidence.Add("当前最佳路由经过虚拟、VPN 或隧道适配器；其链路速率不代表公网带宽");
        if (active.IsWireless && active.SignalQuality is { } quality) evidence.Add($"Wi-Fi 信号质量 {quality}%");
        if (!string.IsNullOrWhiteSpace(active.SsidLabel)) evidence.Add($"当前无线网络：{active.SsidLabel}");
        if (snapshot.HasVpnAdapter) evidence.Add("检测到活动 VPN/Tunnel 类适配器");
        if (snapshot.HasProxyConfigured) evidence.Add("检测到 Windows 代理或自动代理脚本");
        return ValueTask.FromResult(ProbeResult.Create(Stage, status, started, metrics, evidence,
            weakWifi ? "Wi-Fi 信号偏弱，可能限制稳定性和速度" : active.IsVirtual ? "当前路由经过虚拟网络接口" : "网卡链路正常", weakWifi ? .9 : .96,
            weakWifi ? ["靠近接入点或切换到干扰更少的频段", "继续比较网关与公网延迟"] : ["若实际速度明显低于链路速率，请继续检查延迟与丢包"]));
    }

    private static string FormatLinkSpeed(long bitsPerSecond) => bitsPerSecond switch
    {
        <= 0 => "未知",
        >= 1_000_000_000 => $"{bitsPerSecond / 1_000_000_000d:0.#} Gbps",
        _ => $"{bitsPerSecond / 1_000_000d:0.#} Mbps"
    };
}

public sealed class IpDhcpDiagnosticProbe : IDiagnosticProbe
{
    public DiagnosticStage Stage => DiagnosticStage.IpDhcp;
    public ValueTask<DiagnosticStageResult> RunAsync(NetworkSnapshot snapshot, IReadOnlyList<DiagnosticTarget> targets, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var active = snapshot.ActiveAdapter;
        if (active is null)
            return ValueTask.FromResult(ProbeResult.Create(Stage, DiagnosticStatus.NotTested, started, null,
                ["无活动网卡，跳过 IP/DHCP"], "未测试", 1, ["先恢复网卡连接"]));
        if (snapshot.HasApipaAddress)
            return ValueTask.FromResult(ProbeResult.Create(Stage, DiagnosticStatus.Fault, started,
                new Dictionary<string, string> { ["地址"] = "169.254.x.x (APIPA)" },
                ["检测到 APIPA 自动私有地址", "设备未从 DHCP 获得正常地址"], "DHCP 分配可能失败", .98,
                ["重新连接网络并执行 DHCP 续租", "检查路由器 DHCP 服务是否开启"]));

        var status = snapshot.HasDefaultGateway && snapshot.HasDnsServers ? DiagnosticStatus.Healthy : DiagnosticStatus.Degraded;
        return ValueTask.FromResult(ProbeResult.Create(Stage, status, started,
            new Dictionary<string, string> { ["IP"] = string.Join(" / ", active.Addresses.Take(2)), ["网关"] = active.Gateways.FirstOrDefault() ?? "未配置", ["DNS"] = $"{active.DnsServers.Length} 个" },
            [snapshot.HasDefaultGateway ? "已配置默认网关" : "未发现默认网关", snapshot.HasDnsServers ? "已配置 DNS 服务器" : "未发现 DNS 服务器"],
            status == DiagnosticStatus.Healthy ? "IP 与 DHCP 配置正常" : "网络配置可能不完整", status == DiagnosticStatus.Healthy ? .96 : .9,
            status == DiagnosticStatus.Healthy ? ["继续验证网关可达性"] : ["检查静态 IP 或 DHCP 配置"]));
    }
}

public sealed class GatewayDiagnosticProbe : IDiagnosticProbe
{
    public DiagnosticStage Stage => DiagnosticStage.Gateway;

    public async ValueTask<DiagnosticStageResult> RunAsync(NetworkSnapshot snapshot, IReadOnlyList<DiagnosticTarget> targets, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var gateway = snapshot.ActiveAdapter?.Gateways.FirstOrDefault();
        if (gateway is null)
            return ProbeResult.Create(Stage, DiagnosticStatus.NotTested, started, null, ["没有可测试的默认网关"], "未配置默认网关", .98, ["检查 IP 配置"]);

        const int sampleCount = 10;
        var attempts = await Task.WhenAll(Enumerable.Range(0, sampleCount)
            .Select(index => PingOnceAsync(gateway, index, cancellationToken)));
        var times = attempts.Where(x => x is not null).Select(x => x!.Value).ToArray();
        var failures = sampleCount - times.Length;

        if (times.Length == 0)
            return ProbeResult.Create(Stage, DiagnosticStatus.Degraded, started,
                new Dictionary<string, string> { ["ICMP"] = $"{sampleCount}/{sampleCount} 未响应", ["网关"] = "本地默认网关" },
                ["默认网关没有响应 ICMP", "ICMP 被屏蔽时仍可能正常联网"], "网关可能不可达或屏蔽 ICMP", .62,
                ["结合 DNS 与 HTTPS 结果判断，不要仅凭 Ping 判定断网", "检查路由器或接入点状态"]);

        var avg = times.Average();
        var p95 = NetworkPerformanceMath.Percentile(times, .95);
        var jitter = NetworkPerformanceMath.AverageJitter(times);
        var loss = failures / (double)sampleCount * 100;
        var status = loss > 0 || p95 > 80 || jitter > 30 ? DiagnosticStatus.Degraded : DiagnosticStatus.Healthy;
        var captured = DateTimeOffset.Now;
        var timingSamples = times.Select((value, index) => new NetworkTimingSample(
            NetworkTimingKind.GatewayIcmp, $"网关 #{index + 1}", value, captured.AddMilliseconds(index * 60)));
        return ProbeResult.Create(Stage, status, started,
            new Dictionary<string, string>
            {
                ["平均延迟"] = NetworkPerformanceMath.FormatLatency(avg), ["P95"] = NetworkPerformanceMath.FormatLatency(p95),
                ["抖动"] = NetworkPerformanceMath.FormatLatency(jitter), ["丢包"] = $"{loss:0}%", ["样本"] = sampleCount.ToString()
            },
            [$"网关响应 {times.Length}/{sampleCount} 次", $"平均 {NetworkPerformanceMath.FormatLatency(avg)} · P95 {NetworkPerformanceMath.FormatLatency(p95)} · 抖动 {NetworkPerformanceMath.FormatLatency(jitter)}"],
            status == DiagnosticStatus.Healthy ? "本地网关可达" : "本地链路可能存在延迟或丢包", status == DiagnosticStatus.Healthy ? .94 : .82,
            status == DiagnosticStatus.Healthy ? ["继续检查 DNS"] : ["靠近 Wi-Fi 路由器或检查网线", "排除无线干扰"],
            latencySamples: times, timingSamples: timingSamples);
    }

    private static async Task<double?> PingOnceAsync(string gateway, int index, CancellationToken cancellationToken)
    {
        if (index > 0) await Task.Delay(TimeSpan.FromMilliseconds(index * 60), cancellationToken);
        using var ping = new Ping();
        var timer = Stopwatch.StartNew();
        try
        {
            var reply = await ping.SendPingAsync(gateway, TimeSpan.FromMilliseconds(700), cancellationToken: cancellationToken);
            return reply.Status == IPStatus.Success ? timer.Elapsed.TotalMilliseconds : null;
        }
        catch (PingException) { return null; }
    }
}

public sealed class DnsDiagnosticProbe : IDiagnosticProbe
{
    public DiagnosticStage Stage => DiagnosticStage.Dns;
    public async ValueTask<DiagnosticStageResult> RunAsync(NetworkSnapshot snapshot, IReadOnlyList<DiagnosticTarget> targets, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        if (!snapshot.HasDnsServers)
            return ProbeResult.Create(Stage, DiagnosticStatus.Fault, started, null, ["没有配置 DNS 服务器"], "DNS 配置缺失", .99,
                ["恢复自动 DNS 或配置可信 DNS 服务器"]);

        var attempts = await Task.WhenAll(targets.Take(3).Select(target => ResolveAsync(target, cancellationToken)));
        var successful = attempts.Where(x => x.Success).ToArray();
        var samples = successful.Select(x => x.ElapsedMs).ToArray();
        var failures = attempts.Where(x => !x.Success).Select(x => x.Name).ToArray();

        if (samples.Length == 0)
            return ProbeResult.Create(Stage, DiagnosticStatus.Fault, started,
                new Dictionary<string, string> { ["成功"] = "0", ["失败"] = failures.Length.ToString() },
                ["多个独立域名均解析失败"], "DNS 解析可能故障", .97,
                ["尝试切换 DNS 服务器", "检查 VPN 或安全软件的 DNS 接管"]);

        var avg = samples.Average();
        var status = failures.Length > 0 || avg > 180 ? DiagnosticStatus.Degraded : DiagnosticStatus.Healthy;
        var timingSamples = successful.Select(x => new NetworkTimingSample(NetworkTimingKind.DnsLookup, $"DNS · {x.Name}", x.ElapsedMs, DateTimeOffset.Now));
        return ProbeResult.Create(Stage, status, started,
            new Dictionary<string, string> { ["平均耗时"] = $"{avg:0} ms", ["成功"] = samples.Length.ToString(), ["失败"] = failures.Length.ToString() },
            [$"{samples.Length} 个独立域名解析成功", failures.Length == 0 ? "没有解析失败" : $"{failures.Length} 个目标解析失败"],
            status == DiagnosticStatus.Healthy ? "DNS 响应正常" : "DNS 响应偏慢或部分失败", status == DiagnosticStatus.Healthy ? .94 : .88,
            status == DiagnosticStatus.Healthy ? ["继续验证 HTTPS"] : ["尝试切换到更稳定的 DNS", "检查 VPN 或代理设置"],
            latencySamples: samples, timingSamples: timingSamples);
    }

    private static async Task<(string Name, bool Success, double ElapsedMs)> ResolveAsync(DiagnosticTarget target, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var perTarget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            perTarget.CancelAfter(TimeSpan.FromSeconds(2));
            _ = await Dns.GetHostAddressesAsync(target.Host, perTarget.Token);
            return (target.Name, true, timer.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (target.Name, false, timer.Elapsed.TotalMilliseconds); }
        catch (SocketException) { return (target.Name, false, timer.Elapsed.TotalMilliseconds); }
    }
}

public sealed class InternetDiagnosticProbe : IDiagnosticProbe
{
    public DiagnosticStage Stage => DiagnosticStage.Internet;

    public async ValueTask<DiagnosticStageResult> RunAsync(NetworkSnapshot snapshot, IReadOnlyList<DiagnosticTarget> targets, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var attempts = await Task.WhenAll(targets.Take(3).Select(t => TestTlsAsync(t, cancellationToken)));
        var successes = attempts.Where(x => x.Success).ToArray();
        var ncsi = await CheckNcsiAsync(cancellationToken);
        var metrics = new Dictionary<string, string>
        {
            ["TLS 成功"] = $"{successes.Length}/{attempts.Length}",
            ["NCSI"] = ncsi.IsConfirmed ? "可用" : ncsi.IsPortalSuspected ? "疑似认证门户" : "未确认",
            ["平均 TCP"] = successes.Length == 0 ? "—" : $"{successes.Average(x => x.TcpMs):0} ms",
            ["平均 TLS"] = successes.Length == 0 ? "—" : $"{successes.Average(x => x.TlsMs):0} ms"
        };
        var timingSamples = successes.SelectMany(x => new[]
        {
            new NetworkTimingSample(NetworkTimingKind.TcpConnect, $"TCP · {x.Target.Name}", x.TcpMs, DateTimeOffset.Now),
            new NetworkTimingSample(NetworkTimingKind.TlsHandshake, $"TLS · {x.Target.Name}", x.TlsMs, DateTimeOffset.Now)
        });

        if (successes.Length == 0 && !ncsi.IsConfirmed)
            return ProbeResult.Create(Stage, DiagnosticStatus.Fault, started, metrics,
                ["多个独立站点的 TCP 443/TLS 均失败", ncsi.Evidence], ncsi.IsPortalSuspected ? "可能需要完成网络认证" : "公网连接可能中断", .96,
                ncsi.IsPortalSuspected ? ["打开浏览器完成 Wi-Fi/校园网认证", "认证后重新运行诊断"] : ["检查上级路由、宽带或认证门户", "暂时停用代理/VPN 后重试"],
                timingSamples: timingSamples);

        var status = successes.Length == attempts.Length && ncsi.IsConfirmed ? DiagnosticStatus.Healthy : DiagnosticStatus.Degraded;
        return ProbeResult.Create(Stage, status, started, metrics,
            [$"{successes.Length} 个目标完成 TCP 443 与 TLS 握手", ncsi.Evidence],
            status == DiagnosticStatus.Healthy ? "互联网连接正常" : ncsi.IsPortalSuspected ? "公网可达但可能存在认证门户" : "公网连接可用但存在局部退化", status == DiagnosticStatus.Healthy ? .97 : .82,
            status == DiagnosticStatus.Healthy ? ["网络主链路可用"] : ["检查代理、VPN 或认证门户", "单目标失败不代表整个互联网故障"],
            latencySamples: successes.Select(x => x.TotalMs), timingSamples: timingSamples);
    }

    private static async Task<(DiagnosticTarget Target, bool Success, double TcpMs, double TlsMs, double TotalMs)> TestTlsAsync(DiagnosticTarget target, CancellationToken cancellationToken)
    {
        var totalTimer = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var tcp = new TcpClient();
            var tcpTimer = Stopwatch.StartNew();
            await tcp.ConnectAsync(target.Host, target.Port, timeout.Token);
            var tcpMs = tcpTimer.Elapsed.TotalMilliseconds;
            using var tls = new SslStream(tcp.GetStream(), false);
            var tlsTimer = Stopwatch.StartNew();
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = target.Host }, timeout.Token);
            return (target, true, tcpMs, tlsTimer.Elapsed.TotalMilliseconds, totalTimer.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (target, false, 0, 0, totalTimer.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            return (target, false, 0, 0, totalTimer.Elapsed.TotalMilliseconds);
        }
    }

    private static async Task<NcsiResult> CheckNcsiAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using var response = await client.GetAsync("http://www.msftconnecttest.com/connecttest.txt", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
                return new(false, true, $"NCSI 被重定向到认证页面（HTTP {(int)response.StatusCode}）");
            var content = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.IsSuccessStatusCode && content.Trim().Equals("Microsoft Connect Test", StringComparison.Ordinal))
                return new(true, false, "NCSI 内容检查通过");
            if (response.IsSuccessStatusCode)
                return new(false, true, "NCSI 返回了非预期内容，可能被认证门户接管");
            return new(false, false, $"NCSI 返回 HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(false, false, "NCSI 未确认，但仍会结合多目标 TLS 判断"); }
    }

    private sealed record NcsiResult(bool IsConfirmed, bool IsPortalSuspected, string Evidence);
}

public sealed class TargetDiagnosticProbe : IDiagnosticProbe
{
    public DiagnosticStage Stage => DiagnosticStage.Target;
    public async ValueTask<DiagnosticStageResult> RunAsync(NetworkSnapshot snapshot, IReadOnlyList<DiagnosticTarget> targets, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        if (targets.Count == 0)
            return ProbeResult.Create(Stage, DiagnosticStatus.NotTested, started, null, ["没有配置目标"], "未测试自定义目标", 1, ["在设置中添加目标"]);
        var attempts = new List<(DiagnosticTarget Target, bool Success, double ElapsedMs, string? Error)>();
        foreach (var target in targets.Take(8))
        {
            var timer = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(target.Host, target.Port, timeout.Token);
                attempts.Add((target, true, timer.Elapsed.TotalMilliseconds, null));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                attempts.Add((target, false, timer.Elapsed.TotalMilliseconds, "目标检测超时"));
            }
            catch (SocketException ex)
            {
                attempts.Add((target, false, timer.Elapsed.TotalMilliseconds, ex.Message));
            }
        }
        var succeeded = attempts.Where(x => x.Success).ToArray();
        var failed = attempts.Where(x => !x.Success).ToArray();
        var status = failed.Length == 0 ? DiagnosticStatus.Healthy : DiagnosticStatus.Degraded;
        var timingSamples = succeeded.Select(x => new NetworkTimingSample(NetworkTimingKind.TargetTcp,
            $"目标 · {x.Target.Name}", x.ElapsedMs, DateTimeOffset.Now));
        return ProbeResult.Create(Stage, status, started,
            new Dictionary<string, string> { ["成功"] = succeeded.Length.ToString(), ["失败"] = failed.Length.ToString(), ["目标数"] = attempts.Count.ToString() },
            attempts.Select(x => $"{x.Target.Name} · TCP {x.Target.Port} · {(x.Success ? $"{x.ElapsedMs:0} ms" : "失败")}"),
            status == DiagnosticStatus.Healthy ? "所有配置目标均可达" : failed.Length == attempts.Count ? "配置目标均不可达，但需结合互联网阶段判断" : "部分目标可能异常",
            status == DiagnosticStatus.Healthy ? .94 : .82,
            status == DiagnosticStatus.Healthy ? ["目标链路工作正常"] : ["检查失败目标自身状态", "不要把单站点失败判定为整个互联网断开"],
            failed.FirstOrDefault().Error, false, succeeded.Select(x => x.ElapsedMs), timingSamples);
    }
}
