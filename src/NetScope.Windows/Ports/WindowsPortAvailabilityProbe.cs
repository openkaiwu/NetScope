using System.Net;
using System.Net.Sockets;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Ports;

public sealed class WindowsPortAvailabilityProbe : IPortAvailabilityProbe
{
    public ValueTask<PortAvailabilityResult> ProbeAsync(int port, PortProtocol protocol, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var v4 = CanBind(port, protocol, AddressFamily.InterNetwork);
        var v6 = Socket.OSSupportsIPv6 && CanBind(port, protocol, AddressFamily.InterNetworkV6);
        var available = v4 && (!Socket.OSSupportsIPv6 || v6);
        return ValueTask.FromResult(new PortAvailabilityResult(port, protocol, v4, v6, available,
            available ? "IPv4/IPv6 独占绑定验证通过（未启动监听）" : "至少一个地址族无法独占绑定"));
    }

    private static bool CanBind(int port, PortProtocol protocol, AddressFamily family)
    {
        try
        {
            using var socket = new Socket(family, protocol == PortProtocol.Tcp ? SocketType.Stream : SocketType.Dgram,
                protocol == PortProtocol.Tcp ? ProtocolType.Tcp : ProtocolType.Udp) { ExclusiveAddressUse = true };
            socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, port));
            return true;
        }
        catch (SocketException) { return false; }
    }
}
