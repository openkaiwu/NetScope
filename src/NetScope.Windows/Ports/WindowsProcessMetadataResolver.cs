using System.Collections.Concurrent;
using System.Diagnostics;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Ports;

public sealed class WindowsProcessMetadataResolver : IProcessMetadataResolver
{
    private readonly ConcurrentDictionary<(int Pid, long Started), ProcessIdentity> _cache = new();

    public ValueTask<ProcessIdentity> ResolveAsync(int processId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.GetProcessById(processId);
            var started = process.StartTime.ToUniversalTime();
            var key = (processId, started.Ticks);
            if (_cache.TryGetValue(key, out var cached)) return ValueTask.FromResult(cached);

            string? path = null;
            var accessible = true;
            string? message = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                accessible = false;
                message = "路径需要更高权限";
            }

            var value = new ProcessIdentity(processId, started, process.ProcessName, path, accessible, process.HasExited, message);
            _cache[key] = value;
            if (_cache.Count > 2048)
                foreach (var stale in _cache.Keys.Where(x => x.Pid != processId).Take(256)) _cache.TryRemove(stale, out _);
            return ValueTask.FromResult(value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ValueTask.FromResult(ProcessIdentity.Unknown(processId, "进程已退出或访问受限"));
        }
    }
}
