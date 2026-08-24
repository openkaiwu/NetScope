using System.Text.RegularExpressions;

namespace NetScope.Windows.Logging;

public sealed partial class RollingFileLogger
{
    private const long MaxBytes = 1024 * 1024;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RollingFileLogger()
    {
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetScope", "logs");
    }

    public async ValueTask WriteAsync(string level, string message)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_directory);
            var active = Path.Combine(_directory, "netscope.log");
            if (File.Exists(active) && new FileInfo(active).Length >= MaxBytes) Rotate(active);
            var safe = SensitiveValueRegex().Replace(message, "[已脱敏]");
            await File.AppendAllTextAsync(active, $"{DateTimeOffset.Now:O} [{level}] {safe}{Environment.NewLine}");
        }
        finally { _gate.Release(); }
    }

    private static void Rotate(string active)
    {
        var directory = Path.GetDirectoryName(active)!;
        var old2 = Path.Combine(directory, "netscope.2.log");
        var old1 = Path.Combine(directory, "netscope.1.log");
        if (File.Exists(old2)) File.Delete(old2);
        if (File.Exists(old1)) File.Move(old1, old2);
        File.Move(active, old1);
    }

    [GeneratedRegex(@"(?i)(https?://\S+|(?:[0-9A-F]{2}[:-]){5}[0-9A-F]{2}|\b(?:\d{1,3}\.){3}\d{1,3}\b|ssid\s*[:=]\s*\S+)")]
    private static partial Regex SensitiveValueRegex();
}
