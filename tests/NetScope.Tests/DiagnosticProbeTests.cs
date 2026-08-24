using System.Collections.Immutable;
using NetScope.Core.Models;
using NetScope.Windows.Network;

namespace NetScope.Tests;

public sealed class DiagnosticProbeTests
{
    [Fact]
    public async Task IpDhcpProbeReportsApipaOnActiveRoute()
    {
        var adapter = new NetworkAdapterSnapshot("active", "Ethernet", "Adapter", true, false, 1_000_000_000, null, null,
            ["169.254.10.20"], [], [], 0, 0);
        var snapshot = new NetworkSnapshot(DateTimeOffset.UtcNow, true, [adapter], true, false, false, "active");

        var result = await new IpDhcpDiagnosticProbe().RunAsync(snapshot, [], CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Fault, result.Status);
        Assert.Contains("DHCP", result.MostLikelyCause);
    }

    [Fact]
    public async Task AdapterProbeExplainsWeakWifiInsteadOfCallingItHealthy()
    {
        var adapter = new NetworkAdapterSnapshot("wifi", "Wi-Fi", "Wireless", true, true, 300_000_000, 35, "Test Wi-Fi",
            ["192.168.1.10"], ["192.168.1.1"], ["192.168.1.1"], 0, 0, 144_000_000, 144_000_000);
        var snapshot = new NetworkSnapshot(DateTimeOffset.UtcNow, true, [adapter], false, true, true, "wifi");

        var result = await new AdapterDiagnosticProbe().RunAsync(snapshot, [], CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Degraded, result.Status);
        Assert.Contains("Wi-Fi", result.MostLikelyCause);
    }

    [Fact]
    public async Task AdapterProbeDoesNotPresentVirtualLinkRateAsInternetSpeed()
    {
        var adapter = new NetworkAdapterSnapshot("virtual", "vEthernet", "Hyper-V Virtual Ethernet", true, false,
            100_000_000_000, null, null, ["172.20.0.1"], ["172.20.0.254"], ["172.20.0.254"], 0, 0,
            IsVirtual: true, MediaType: "以太网");
        var snapshot = new NetworkSnapshot(DateTimeOffset.UtcNow, true, [adapter], false, true, true, "virtual");

        var result = await new AdapterDiagnosticProbe().RunAsync(snapshot, [], CancellationToken.None);

        Assert.Equal("100 Gbps", result.Metrics["链路速率"]);
        Assert.Contains(result.Evidence, evidence => evidence.Contains("不代表公网带宽", StringComparison.Ordinal));
    }
}
