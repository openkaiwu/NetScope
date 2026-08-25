using System.Runtime.InteropServices;

namespace NetScope.Windows.Performance;

/// <summary>
/// 通过 GetForegroundWindow + GetWindowThreadProcessId 读取当前前台进程 PID。
/// 用于影响分的前台权重；查询失败时返回 null，不中断采样。
/// </summary>
public sealed class ForegroundProcessProvider
{
    public int? GetForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        return GetWindowThreadProcessId(window, out var pid) != 0 && pid != 0 ? (int)pid : null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
