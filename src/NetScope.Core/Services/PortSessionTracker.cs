using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>
/// 端口占用会话追踪器（纯状态机）：对连续端口快照做差分，生成“同一进程连续占用同一端口”的会话。
/// 规则：
/// - 同一 (端口, 协议, PID) 连续出现 2 次快照才开启会话（过滤 UDP 瞬态绑定与扫描噪声）；
/// - 快照中消失或换主即结束会话；
/// - 进程名在会话开启时解析一次并缓存；快照只统计 TCP Listen 与 UDP 绑定。
/// 输出已结束的会话，由调用方写入历史存储。
/// </summary>
public sealed class PortSessionTracker
{
    private const int OpenThreshold = 2;

    private sealed record OpenSession(PortSessionRecord Record);
    private sealed record Candidate(int Port, PortProtocol Protocol, int ProcessId, string ProcessName, DateTimeOffset FirstSeen);

    private readonly Dictionary<string, OpenSession> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Candidate> _candidates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>喂入一次端口快照，返回本次因此结束的会话。</summary>
    public IReadOnlyList<PortSessionRecord> Feed(
        IReadOnlyList<PortBindingSnapshot> bindings,
        DateTimeOffset observedAt,
        Func<int, string?>? resolveProcessName = null)
    {
        List<PortSessionRecord>? closed = null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            if (binding.Protocol == PortProtocol.Tcp && !binding.State.Equals("Listen", StringComparison.OrdinalIgnoreCase))
                continue; // 只统计监听占用，瞬时连接不算会话
            if (binding.Port is < 0 or > 65535) continue;

            var key = Key(binding.Port, binding.Protocol, binding.ProcessId);
            seen.Add(key);

            if (_open.TryGetValue(key, out var open)) continue;

            if (_candidates.TryGetValue(key, out var candidate))
            {
                _candidates.Remove(key);
                var name = candidate.ProcessName;
                _open[key] = new OpenSession(new PortSessionRecord(
                    binding.Port, binding.Protocol, name, binding.ProcessId, candidate.FirstSeen, observedAt));
            }
            else
            {
                var name = resolveProcessName?.Invoke(binding.ProcessId) ?? $"PID {binding.ProcessId}";
                _candidates[key] = new Candidate(binding.Port, binding.Protocol, binding.ProcessId, name, observedAt);
            }
        }

        // 消失或换主：结束会话；换主场景下新 PID 会在后续快照重新走候选期
        foreach (var key in _open.Keys.ToList())
        {
            if (seen.Contains(key)) continue;
            var session = _open[key].Record;
            _open.Remove(key);
            closed ??= [];
            closed.Add(session with { EndedAt = observedAt });
        }

        // 候选未达阈值即消失：静默丢弃
        foreach (var key in _candidates.Keys.ToList())
        {
            if (!seen.Contains(key)) _candidates.Remove(key);
        }

        return closed ?? [];
    }

    /// <summary>当前开启中的会话（Collector 关闭时调用方可用它们收尾，避免长会话丢失）。</summary>
    public IReadOnlyList<PortSessionRecord> CloseAll(DateTimeOffset endedAt)
    {
        if (_open.Count == 0) return [];
        var result = _open.Values.Select(s => s.Record with { EndedAt = endedAt }).ToList();
        _open.Clear();
        _candidates.Clear();
        return result;
    }

    private static string Key(int port, PortProtocol protocol, int processId) =>
        $"{protocol}:{port}:{processId}";
}
