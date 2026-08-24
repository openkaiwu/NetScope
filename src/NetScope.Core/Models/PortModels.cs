using System.Collections.Immutable;

namespace NetScope.Core.Models;

public enum PortProtocol { Tcp, Udp }
public enum IpAddressFamily { IPv4, IPv6 }
public enum PortChangeKind { Added, Removed, StateChanged }
public enum PortRangeKind { Candidate, Dynamic, Excluded, Registered, HighRisk, Occupied, Recommended }

public sealed record PortBindingKey(
    PortProtocol Protocol,
    IpAddressFamily AddressFamily,
    string LocalAddress,
    int Port,
    int ProcessId,
    string State)
{
    public string Identity => $"{Protocol}|{AddressFamily}|{LocalAddress}|{Port}|{ProcessId}";
}

public sealed record ProcessIdentity(
    int ProcessId,
    DateTimeOffset? StartTime,
    string Name,
    string? Path,
    bool IsAccessible,
    bool HasExited,
    string? StatusMessage = null)
{
    public static ProcessIdentity Unknown(int pid, string message) =>
        new(pid, null, "受限或已退出", null, false, true, message);
}

public sealed record PortBindingSnapshot(
    PortBindingKey Key,
    DateTimeOffset ObservedAt,
    ProcessIdentity? Process = null,
    PortCatalogEntry? CatalogEntry = null)
{
    public PortProtocol Protocol => Key.Protocol;
    public IpAddressFamily AddressFamily => Key.AddressFamily;
    public string LocalAddress => Key.LocalAddress;
    public int Port => Key.Port;
    public int ProcessId => Key.ProcessId;
    public string State => Key.State;
}

public sealed record PortChange(
    PortChangeKind Kind,
    PortBindingSnapshot? Before,
    PortBindingSnapshot? After,
    DateTimeOffset ChangedAt);

public sealed record PortDiff(
    ImmutableArray<PortChange> Changes,
    ImmutableArray<PortBindingSnapshot> Current)
{
    public static PortDiff Empty { get; } = new([], []);
}

public sealed record PortCatalogEntry(
    int PortStart,
    int PortEnd,
    PortProtocol? Protocol,
    string Service,
    string ChineseDescription,
    string Category,
    bool IsRegistered,
    bool IsHighRisk = false)
{
    public bool Contains(int port, PortProtocol protocol) =>
        port >= PortStart && port <= PortEnd && (Protocol is null || Protocol == protocol);
}

public sealed record PortAvailabilityResult(
    int Port,
    PortProtocol Protocol,
    bool IPv4Available,
    bool IPv6Available,
    bool IsRecommended,
    string Reason,
    bool UsedDefaultRange = false)
{
    public string Badge => IsRecommended ? "当前推荐" : "不可推荐";
}

public sealed record PortRange(
    int Start,
    int End,
    PortProtocol Protocol,
    PortRangeKind Kind,
    string Source,
    string Description)
{
    public bool Contains(int port) => port >= Start && port <= End;
    public string Display => Start == End ? Start.ToString() : $"{Start}–{End}";
}

public sealed record SystemPortRangeSnapshot(
    ImmutableArray<PortRange> Ranges,
    bool UsedDefaultDynamicRange,
    DateTimeOffset CapturedAt)
{
    public static SystemPortRangeSnapshot Default { get; } = new(
        [
            new(49_152, 65_535, PortProtocol.Tcp, PortRangeKind.Dynamic, "Windows 默认", "TCP 默认动态客户端端口范围"),
            new(49_152, 65_535, PortProtocol.Udp, PortRangeKind.Dynamic, "Windows 默认", "UDP 默认动态客户端端口范围")
        ], true, DateTimeOffset.MinValue);
}
