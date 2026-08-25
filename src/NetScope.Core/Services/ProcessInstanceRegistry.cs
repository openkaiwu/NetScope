using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>
/// 以 (PID, 启动时间) 为键跟踪已知进程实例。Windows 复用 PID 时会得到不同的启动时间，
/// 因此同一 PID 在新实例出现时会被判定为“新进程”，旧实例关联随之解除。
/// </summary>
public sealed class ProcessInstanceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<ProcessInstanceKey, string> _instances = new();
    private readonly Dictionary<int, ProcessInstanceKey> _currentByPid = new();

    public int Count
    {
        get { lock (_gate) return _instances.Count; }
    }

    public ProcessInstanceKey Register(int processId, DateTimeOffset startedAt, string name)
    {
        lock (_gate)
        {
            var key = new ProcessInstanceKey(processId, startedAt);
            if (_instances.ContainsKey(key)) return key;

            // 同一 PID 换了新实例（启动时间不同）→ 解除旧实例
            if (_currentByPid.TryGetValue(processId, out var previous) && previous != key)
                _instances.Remove(previous);

            _instances[key] = name;
            _currentByPid[processId] = key;
            return key;
        }
    }

    public bool TryGetName(ProcessInstanceKey key, out string name)
    {
        lock (_gate) return _instances.TryGetValue(key, out name!);
    }

    public ProcessInstanceKey? TryGetCurrentKey(int processId)
    {
        lock (_gate) return _currentByPid.TryGetValue(processId, out var key) ? key : null;
    }

    public void Remove(ProcessInstanceKey key)
    {
        lock (_gate)
        {
            if (_instances.Remove(key) && _currentByPid.TryGetValue(key.ProcessId, out var current) && current == key)
                _currentByPid.Remove(key.ProcessId);
        }
    }

    public void RemoveExited(IEnumerable<ProcessInstanceKey> liveKeys)
    {
        lock (_gate)
        {
            var live = new HashSet<ProcessInstanceKey>(liveKeys);
            foreach (var key in _instances.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _instances.Remove(key);
                if (_currentByPid.TryGetValue(key.ProcessId, out var current) && current == key)
                    _currentByPid.Remove(key.ProcessId);
            }
        }
    }
}
