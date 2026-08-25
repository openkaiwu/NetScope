using System.Diagnostics;
using NetScope.Core.Abstractions;

namespace NetScope.App.Services;

/// <summary>确保后台 Collector 已运行：先探测管道，未就绪则启动同目录的 NetScope.Collector.exe。</summary>
public static class CollectorLauncher
{
    public static async ValueTask<bool> EnsureRunningAsync(ICollectorClient client, CancellationToken cancellationToken = default)
    {
        if (await client.IsAvailableAsync(cancellationToken)) return true;

        var exe = Path.Combine(AppContext.BaseDirectory, "NetScope.Collector.exe");
        if (!File.Exists(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo(exe, "--background")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
