using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>连接四元组 + 地址族 + 进程实例；状态变化更新同一会话。调用方只传完整成功快照。</summary>
public sealed class TcpConnectionTracker
{
    private readonly Dictionary<(int, DateTimeOffset?, IpAddressFamily, string, int, string, int), TcpConnectionRecord> _active = new();
    public IReadOnlyList<TcpConnectionRecord> Active => _active.Values.ToArray();

    public IReadOnlyList<TcpConnectionRecord> Feed(IEnumerable<PortBindingSnapshot> bindings, DateTimeOffset now,
        Func<int, ProcessIdentity?> resolver)
    {
        var changed = new List<TcpConnectionRecord>();
        var seen = new HashSet<(int, DateTimeOffset?, IpAddressFamily, string, int, string, int)>();
        var identities = new Dictionary<int, ProcessIdentity?>();
        foreach (var b in bindings.Where(b => b.Protocol == PortProtocol.Tcp && b.State != "Listen" && b.RemotePort > 0 && b.RemoteAddress is not null))
        {
            if (!identities.TryGetValue(b.ProcessId, out var identity))
                identities[b.ProcessId] = identity = resolver(b.ProcessId);
            var key = (b.ProcessId, identity?.StartTime, b.AddressFamily, b.LocalAddress, b.Port, b.RemoteAddress!, b.RemotePort);
            seen.Add(key);
            if (_active.TryGetValue(key, out var existing))
            {
                var updated = existing with { State = b.State, LastSeenAt = now };
                _active[key] = updated;
                if (existing.State != b.State) changed.Add(updated);
            }
            else if (_active.Count < 10000)
            {
                var record = new TcpConnectionRecord(Guid.NewGuid(), b.ProcessId, identity?.StartTime,
                    identity?.Name ?? $"PID {b.ProcessId}", b.AddressFamily, b.LocalAddress, b.Port,
                    b.RemoteAddress!, b.RemotePort, b.State, now, now);
                _active[key] = record;
                changed.Add(record);
            }
        }
        foreach (var key in _active.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            // 结束时间是最后观察时刻，实际关闭发生在两次轮询之间。
            changed.Add(_active[key] with { EndedAt = _active[key].LastSeenAt, EndReason = "后续快照中消失" });
            _active.Remove(key);
        }
        return changed;
    }

    public IReadOnlyList<TcpConnectionRecord> Stop(string reason)
    {
        var result = _active.Values.Select(c => c with { EndedAt = c.LastSeenAt, EndReason = reason }).ToArray();
        _active.Clear();
        return result;
    }
}
