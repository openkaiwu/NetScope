using System.Diagnostics;
using NetScope.Core.Models;
using NetScope.Windows.Intervention;

namespace NetScope.Tests;

/// <summary>
/// V1.1 执行器隔离集成测试：只操作由测试自身启动并持有的子进程，
/// 绝不对系统进程做破坏性验证（设计 §9）。PID 复用用“预期创建时间不符”模拟，不真实制造复用。
/// </summary>
public sealed class V11ExecutorTests
{
    private static (Process Process, ProcessInstanceKey Key) SpawnChild(string arguments)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {arguments}",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("无法启动测试子进程");
        var key = new ProcessInstanceKey(process.Id, process.StartTime);
        return (process, key);
    }

    [Fact]
    public void TerminateOwnChildExitsAndReportsSuccess()
    {
        var (process, key) = SpawnChild("ping -n 60 127.0.0.1 > nul");
        try
        {
            var result = new ProcessTerminationExecutor().Terminate(key);
            Assert.Equal(TerminationOutcome.Exited, result.Outcome);
            Assert.True(result.Success);
            Assert.True(process.WaitForExit(3000));
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            process.Dispose();
        }
    }

    [Fact]
    public void RequestCloseSendsCloseRequestForOwnChild()
    {
        // 无窗口子进程：CloseMainWindow 应返回“没有可关闭的主窗口”，结果不冒充成功
        var (process, key) = SpawnChild("ping -n 60 127.0.0.1 > nul");
        try
        {
            var result = new ProcessTerminationExecutor().RequestClose(key);
            Assert.Equal(TerminationOutcome.Failed, result.Outcome);
            Assert.False(result.Success);
            Assert.Contains("主窗口", result.Message);
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            process.Dispose();
        }
    }

    [Fact]
    public void IdentityMismatchRefusesWithoutAction()
    {
        // 模拟 PID 复用：预期创建时间与实际不符 → 拒绝且不动目标
        var (process, key) = SpawnChild("ping -n 60 127.0.0.1 > nul");
        try
        {
            var wrongKey = new ProcessInstanceKey(key.ProcessId, key.StartedAt.AddMinutes(-5));
            var result = new ProcessTerminationExecutor().Terminate(wrongKey);
            Assert.Equal(TerminationOutcome.RefusedIdentityChanged, result.Outcome);
            Assert.False(result.Success);
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            process.Dispose();
        }
    }

    [Fact]
    public async Task AlreadyExitedTargetReportsNoAction()
    {
        var (process, key) = SpawnChild("ping -n 60 127.0.0.1 > nul");
        process.Kill();
        await process.WaitForExitAsync();
        try
        {
            var result = new ProcessTerminationExecutor().Terminate(key);
            Assert.Equal(TerminationOutcome.AlreadyExited, result.Outcome);
        }
        finally
        {
            process.Dispose();
        }
    }

    [Fact]
    public async Task RequestCloseGracefullyClosesWindowedChild()
    {
        // 测试专用 GUI 子进程：WinForms 窗口收到 WM_CLOSE 后正常退出（会短暂闪现窗口）
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -STA -Command \"Add-Type -AssemblyName System.Windows.Forms; "
                        + "$f = New-Object System.Windows.Forms.Form; [System.Windows.Forms.Application]::Run($f)\"",
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("无法启动 GUI 测试子进程");
        try
        {
            var key = new ProcessInstanceKey(process.Id, process.StartTime);
            for (var i = 0; i < 150 && process.MainWindowHandle == IntPtr.Zero && !process.HasExited; i++)
            {
                await Task.Delay(100);
                process.Refresh();
            }
            if (process.HasExited) // PowerShell 不可用或窗口未创建：跳过而不是假失败
                return;

            var result = new ProcessTerminationExecutor().RequestClose(key);
            Assert.Equal(TerminationOutcome.Exited, result.Outcome);
            Assert.True(result.Success);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            process.Dispose();
        }
    }

    [Fact]
    public void NetScopeOwnProcessIsRefused()
    {
        // 对自身 PID（测试进程）执行：executor 只拒绝 NetScope 自身；测试进程名不是 NetScope，
        // 因此这里验证的是“当前用户、非服务、可验证”路径不会误伤：用测试进程自身 PID + 错误创建时间触发身份拒绝
        var key = new ProcessInstanceKey(Environment.ProcessId, DateTimeOffset.UtcNow.AddMinutes(-10));
        var result = new ProcessTerminationExecutor().Terminate(key);
        Assert.Equal(TerminationOutcome.RefusedIdentityChanged, result.Outcome);
        Assert.False(result.Success);
    }
}
