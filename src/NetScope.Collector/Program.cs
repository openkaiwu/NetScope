using NetScope.Collector;
using NetScope.Windows.Ipc;
using NetScope.Windows.Logging;

namespace NetScope.Collector;

/// <summary>NetScope.Collector 后台进程入口：单实例运行，启动后常驻采样与 IPC 服务。</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var logger = new RollingFileLogger();
        var primary = false;
        using var mutex = new Mutex(false, CollectorProtocol.LocalMutexName);
        try { primary = mutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { primary = true; }

        if (!primary)
        {
            await logger.WriteAsync("INFO", "Collector 已在运行，本次退出");
            return 0;
        }

        try
        {
            await using var host = new CollectorHost(logger);
            await host.StartAsync();

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

            try { await Task.Delay(Timeout.Infinite, shutdown.Token); }
            catch (OperationCanceledException) { }

            await logger.WriteAsync("INFO", "Collector 已停止");
            return 0;
        }
        catch (Exception ex)
        {
            await logger.WriteAsync("FATAL", $"Collector 启动失败: {ex.Message}");
            return 1;
        }
        finally
        {
            if (primary)
            {
                try { mutex.ReleaseMutex(); }
                catch (ApplicationException) { }
            }
        }
    }
}
