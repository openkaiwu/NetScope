using System.Diagnostics;
using System.Runtime.InteropServices;
using NetScope.Core.Models;

namespace NetScope.Windows.Intervention;

/// <summary>
/// 受控进程终止执行器（V1.1）。只存在于前台 App 进程，不经 Collector IPC 暴露。
/// 执行瞬间重新打开句柄并核对：PID + 创建时间 + 关键标志 + 会话/所有者/服务归属，
/// 任一不符即拒绝，防止 PID 复用误杀。请求关闭等待最多 10 秒；强制结束等待最多 5 秒。
/// 不使用 SeDebugPrivilege、不提权、不结束进程树、不结束服务。
/// </summary>
public sealed partial class ProcessTerminationExecutor(ProcessSafetyInspector? inspector = null)
{
    private static readonly TimeSpan CreationTimeTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CloseWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TerminateWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly ProcessSafetyInspector _inspector = inspector ?? new();

    /// <summary>请求关闭：向主窗口发送正常关闭请求，等待应用退出（最多 10 秒）。</summary>
    public TerminationResult RequestClose(ProcessInstanceKey expected) =>
        Run(expected, graceful: true);

    /// <summary>强制结束：最小权限 TerminateProcess，仅对评估放行的目标开放（由调用方保证），执行器仍做全部硬性复核。</summary>
    public TerminationResult Terminate(ProcessInstanceKey expected) =>
        Run(expected, graceful: false);

    private TerminationResult Run(ProcessInstanceKey expected, bool graceful)
    {
        // 执行前重新采集事实：与确认对话框展示时的数据可能是不同瞬间
        var inspection = _inspector.Collect(expected.ProcessId, ProcessNameOf(expected));
        if (inspection is null)
            return new(TerminationOutcome.AlreadyExited, "目标进程已退出，无需操作。");

        var facts = inspection.Facts;
        if (facts.Process.StartedAt == DateTimeOffset.MinValue)
            return new(TerminationOutcome.RefusedUnknownIdentity, "拒绝执行：无法读取进程创建时间，无法核对身份。");
        if (Math.Abs((facts.Process.StartedAt - expected.StartedAt).TotalSeconds) > CreationTimeTolerance.TotalSeconds)
            return new(TerminationOutcome.RefusedIdentityChanged,
                "拒绝执行：PID 相同但创建时间已变化（PID 已被复用），未执行任何操作。");
        if (facts.IsNetScopeProcess)
            return new(TerminationOutcome.RefusedProtection, "拒绝执行：目标是 NetScope 自身。");
        if (facts.IsCritical && facts.CriticalFlagKnown)
            return new(TerminationOutcome.RefusedProtection, "拒绝执行：Windows 标记的关键进程。");
        if (!facts.CriticalFlagKnown || string.IsNullOrEmpty(facts.OwnerSid) || facts.SessionId is null || !facts.ServicesKnown)
            return new(TerminationOutcome.RefusedUnknownIdentity, "拒绝执行：关键证据缺失，无法在执行瞬间验证身份。");
        if (facts.SessionId == 0)
            return new(TerminationOutcome.RefusedProtection, "拒绝执行：目标位于系统会话（Session 0）。");
        if (!facts.OwnerIsCurrentUser)
            return new(TerminationOutcome.RefusedProtection, "拒绝执行：目标属于其他用户。");
        if (facts.HostedServices.Count > 0)
            return new(TerminationOutcome.RefusedProtection,
                $"拒绝执行：目标承载 Windows 服务（{string.Join("、", facts.HostedServices.Take(3))}）。");

        return graceful ? RequestCloseCore(expected) : TerminateCore(expected);
    }

    private static TerminationResult RequestCloseCore(ProcessInstanceKey expected)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(expected.ProcessId);
        }
        catch (ArgumentException)
        {
            return new(TerminationOutcome.AlreadyExited, "目标进程已退出，无需操作。");
        }

        using (process)
        {
            if (Math.Abs((process.StartTime - expected.StartedAt).TotalSeconds) > CreationTimeTolerance.TotalSeconds)
                return new(TerminationOutcome.RefusedIdentityChanged,
                    "拒绝执行：PID 相同但创建时间已变化（PID 已被复用），未执行任何操作。");

            if (!process.CloseMainWindow())
                return new(TerminationOutcome.Failed,
                    "没有可关闭的主窗口：应用可能没有界面或正在拒绝关闭。可尝试应用内退出，或使用强制结束（如评估允许）。");

            try
            {
                if (process.WaitForExit((int)CloseWaitTimeout.TotalMilliseconds))
                    return new(TerminationOutcome.Exited, "目标已正常退出（已等待退出确认）。");
            }
            catch (Exception ex)
            {
                return new(TerminationOutcome.Failed, $"等待退出时出错：{ex.Message}");
            }
            return new(TerminationOutcome.CloseRequested,
                $"关闭请求已发送，应用在 {CloseWaitTimeout.TotalSeconds:0} 秒内未退出（可能在保存或等待确认）。请求不等于关闭成功。");
        }
    }

    private static TerminationResult TerminateCore(ProcessInstanceKey expected)
    {
        const uint processTerminate = 0x0001;
        const uint synchronize = 0x00100000;
        const uint processQueryLimited = 0x1000;

        // 已终止但句柄未被释放的进程：TERMINATE 权限请求会被拒绝，先确认实际状态
        try
        {
            using var check = Process.GetProcessById(expected.ProcessId);
            if (check.HasExited)
                return new(TerminationOutcome.AlreadyExited, "目标进程已退出，无需操作。");
        }
        catch (ArgumentException)
        {
            return new(TerminationOutcome.AlreadyExited, "目标进程已退出，无需操作。");
        }

        var handle = Native.OpenProcess(processTerminate | synchronize | processQueryLimited, false, expected.ProcessId);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 87 /* ERROR_INVALID_PARAMETER */ || error == 0)
                return new(TerminationOutcome.AlreadyExited, "目标进程已退出，无需操作。");
            return new(TerminationOutcome.AccessDenied,
                $"无法打开进程（Win32 错误 {error}）：权限不足。本功能不提权、不重试。", error);
        }

        try
        {
            // 拿到终止句柄后再次核对创建时间与关键标志（设计 §6.2）
            var creation = Native.QueryCreationTime(handle);
            if (creation is null || Math.Abs((creation.Value - expected.StartedAt).TotalSeconds) > CreationTimeTolerance.TotalSeconds)
                return new(TerminationOutcome.RefusedIdentityChanged,
                    "拒绝执行：取得句柄后创建时间与预期不符（PID 已被复用），未执行任何操作。");
            if (Native.QueryIsCritical(handle, out var criticalKnown) && criticalKnown)
                return new(TerminationOutcome.RefusedProtection, "拒绝执行：取得句柄后确认目标是关键进程。");
            if (!criticalKnown)
                return new(TerminationOutcome.RefusedUnknownIdentity, "拒绝执行：取得句柄后无法确认关键标志。");

            if (!Native.TerminateProcess(handle, 1))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 5)
                    return new(TerminationOutcome.AccessDenied, "强制结束被拒绝：权限不足（Win32 错误 5）。", error);
                return new(TerminationOutcome.Failed, $"强制结束失败（Win32 错误 {error}）。", error);
            }

            var wait = Native.WaitForSingleObject(handle, (int)TerminateWaitTimeout.TotalMilliseconds);
            if (wait == 0) // WAIT_OBJECT_0
                return new(TerminationOutcome.Exited, "目标已被强制结束（跳过应用清理）并确认退出。");
            return new(TerminationOutcome.Failed,
                $"TerminateProcess 已调用，但在 {TerminateWaitTimeout.TotalSeconds:0} 秒内未确认退出（句柄等待超时）。");
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static string ProcessNameOf(ProcessInstanceKey expected) => $"PID {expected.ProcessId}";

    internal static partial class Native
    {
        [LibraryImport("kernel32", SetLastError = true)]
        internal static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr handle);

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsProcessCritical(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] out bool critical);

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateProcess(IntPtr handle, uint exitCode);

        [LibraryImport("kernel32", SetLastError = true)]
        internal static partial int WaitForSingleObject(IntPtr handle, int milliseconds);

        internal static DateTimeOffset? QueryCreationTime(IntPtr handle)
        {
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) return null;
            return DateTime.FromFileTimeUtc(creation);
        }

        internal static bool QueryIsCritical(IntPtr handle, out bool criticalKnown)
        {
            if (IsProcessCritical(handle, out var critical))
            {
                criticalKnown = true;
                return critical;
            }
            criticalKnown = false;
            return false;
        }
    }
}
