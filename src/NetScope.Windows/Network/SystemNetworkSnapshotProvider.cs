using System.Collections.Immutable;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Network;

public sealed class SystemNetworkSnapshotProvider : INetworkSnapshotProvider
{
    private readonly NativeWifiSignalProvider _wifi = new();
    public ValueTask<NetworkSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapters = ImmutableArray.CreateBuilder<NetworkAdapterSnapshot>();
        var interfaceIndexes = new Dictionary<int, string>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = adapter.GetIPProperties();
            var addresses = properties.UnicastAddresses
                .Where(x => x.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(x => x.Address.ToString()).ToImmutableArray();
            var gateways = properties.GatewayAddresses.Select(x => x.Address.ToString()).Where(x => x is not "0.0.0.0" and not "::").ToImmutableArray();
            var dns = properties.DnsAddresses.Select(x => x.ToString()).ToImmutableArray();
            var isUp = adapter.OperationalStatus == OperationalStatus.Up;
            var isWireless = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            var isVirtual = IsVirtualLike(adapter);
            var ipv4Stats = TryStatistics(adapter);
            var ipv4 = TryIpv4(properties);
            if (ipv4 is not null) interfaceIndexes[ipv4.Index] = adapter.Id;

            var wifi = isWireless && isUp ? _wifi.TryGetConnectionInfo(adapter.Id) : null;
            adapters.Add(new(adapter.Id, adapter.Name, adapter.Description, isUp, isWireless, adapter.Speed,
                wifi?.SignalQuality, wifi?.Ssid, addresses, gateways, dns, ipv4Stats?.BytesReceived ?? 0, ipv4Stats?.BytesSent ?? 0,
                wifi?.ReceiveRateBitsPerSecond ?? 0, wifi?.TransmitRateBitsPerSecond ?? 0, isVirtual, MediaType(adapter.NetworkInterfaceType)));
        }

        var all = adapters.ToImmutable();
        var activeId = TryGetBestInterfaceIndex() is { } bestIndex && interfaceIndexes.TryGetValue(bestIndex, out var routedId)
            ? routedId
            : all.FirstOrDefault(x => x.IsUp && x.Gateways.Length > 0)?.Id ?? all.FirstOrDefault(x => x.IsUp)?.Id;
        var active = all.FirstOrDefault(x => x.Id == activeId);
        var hasApipa = active?.Addresses.Any(IsApipa) == true;
        var hasGateway = active?.Gateways.Length > 0;
        var hasDns = active?.DnsServers.Length > 0;
        var hasVpn = all.Any(x => x.IsUp && IsVpnLike(x));
        return ValueTask.FromResult(new NetworkSnapshot(DateTimeOffset.Now, NetworkInterface.GetIsNetworkAvailable(),
            all, hasApipa, hasGateway, hasDns, activeId, hasVpn, IsProxyConfigured()));
    }

    private static IPv4InterfaceStatistics? TryStatistics(NetworkInterface adapter)
    {
        try { return adapter.GetIPv4Statistics(); }
        catch (NetworkInformationException) { return null; }
    }

    private static IPv4InterfaceProperties? TryIpv4(IPInterfaceProperties properties)
    {
        try { return properties.GetIPv4Properties(); }
        catch (NetworkInformationException) { return null; }
    }

    private static int? TryGetBestInterfaceIndex()
    {
        var bytes = System.Net.IPAddress.Parse("1.1.1.1").GetAddressBytes();
        var destination = BitConverter.ToUInt32(bytes, 0);
        return GetBestInterface(destination, out var index) == 0 ? (int)index : null;
    }

    private static bool IsVpnLike(NetworkAdapterSnapshot adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}";
        return text.Contains("VPN", StringComparison.OrdinalIgnoreCase) || text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("TAP", StringComparison.OrdinalIgnoreCase) || text.Contains("TUN", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVirtualLike(NetworkInterface adapter)
    {
        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback) return true;
        var text = $"{adapter.Name} {adapter.Description}";
        string[] markers = ["Virtual", "Hyper-V", "VMware", "VirtualBox", "WSL", "TAP", "TUN", "VPN", "WireGuard"];
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string MediaType(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => "以太网",
        NetworkInterfaceType.Ppp => "PPP/VPN",
        NetworkInterfaceType.Tunnel => "隧道",
        _ => type.ToString()
    };

    private static bool IsProxyConfigured()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false);
            return Convert.ToInt32(key?.GetValue("ProxyEnable", 0)) != 0 || !string.IsNullOrWhiteSpace(key?.GetValue("AutoConfigURL") as string);
        }
        catch { return false; }
    }

    private static bool IsApipa(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
        ip.GetAddressBytes() is [169, 254, _, _];

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);
}
