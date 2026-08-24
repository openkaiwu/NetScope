using NetScope.Windows.Ports;
using NetScope.Core.Models;
using System.Net;
using System.Net.Sockets;

namespace NetScope.Tests;

public sealed class WindowsPortProviderTests
{
    [Fact]
    public async Task CapturesCurrentProcessTablesWithoutNativeFailure()
    {
        var rows = await new WindowsPortTableProvider().CaptureAsync();
        Assert.False(rows.IsDefault);
        Assert.All(rows, row => Assert.InRange(row.Port, 0, 65535));
    }

    [Fact]
    public async Task LocatesPidForTcp4Tcp6Udp4AndUdp6Bindings()
    {
        var pid = Environment.ProcessId;
        using var tcp4 = new TcpListener(IPAddress.Loopback, 0);
        using var tcp6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        tcp6.Server.DualMode = false;
        tcp4.Start(); tcp6.Start();
        using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udp6 = new UdpClient(AddressFamily.InterNetworkV6);
        udp6.Client.DualMode = false;
        udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));

        var rows = await new WindowsPortTableProvider().CaptureAsync();
        Assert.Contains(rows, x => x.ProcessId == pid && x.Protocol == PortProtocol.Tcp && x.AddressFamily == IpAddressFamily.IPv4 && x.Port == ((IPEndPoint)tcp4.LocalEndpoint).Port);
        Assert.Contains(rows, x => x.ProcessId == pid && x.Protocol == PortProtocol.Tcp && x.AddressFamily == IpAddressFamily.IPv6 && x.Port == ((IPEndPoint)tcp6.LocalEndpoint).Port);
        Assert.Contains(rows, x => x.ProcessId == pid && x.Protocol == PortProtocol.Udp && x.AddressFamily == IpAddressFamily.IPv4 && x.Port == ((IPEndPoint)udp4.Client.LocalEndPoint!).Port);
        Assert.Contains(rows, x => x.ProcessId == pid && x.Protocol == PortProtocol.Udp && x.AddressFamily == IpAddressFamily.IPv6 && x.Port == ((IPEndPoint)udp6.Client.LocalEndPoint!).Port);
    }
}
