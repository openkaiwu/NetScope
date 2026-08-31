using System.Diagnostics;

namespace NetScope.App.Services;

/// <summary>
/// 按进程 ID 查询可执行文件路径（性能页进程详情使用，仅选中项调用，频率极低）。
/// 结果按 (PID, 启动时间) 缓存，进程退出或无权限时返回 null，由调用方回退到进程名。
/// </summary>
public sealed class ProcessPathLookup
{
    private readonly Dictionary<(int Pid, long Started), string> _cache = new();

    public string? Resolve(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var key = (processId, process.StartTime.ToUniversalTime().Ticks);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }

            if (path is not null && _cache.Count < 512) _cache[key] = path;
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
