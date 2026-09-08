namespace NetScope.Core.Models;

public sealed record TcpConnectionRecord(Guid Id, int ProcessId, DateTimeOffset? ProcessStartedAt,
    string ProcessName, IpAddressFamily AddressFamily, string LocalAddress, int LocalPort,
    string RemoteAddress, int RemotePort, string State, DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt, DateTimeOffset? EndedAt = null, string? EndReason = null);

public sealed record ConnectionQuery(DateTimeOffset From, DateTimeOffset To, string Search = "", int Limit = 300);
