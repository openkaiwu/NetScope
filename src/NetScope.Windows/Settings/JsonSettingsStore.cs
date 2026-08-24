using System.Text.Json;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.General) { WriteIndented = true };

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetScope", "settings.json");
    }

    public async ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            await using var stream = File.OpenRead(_path);
            return (await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken) ?? new AppSettings()).Normalize();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new AppSettings(); }
    }

    public async ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            await JsonSerializer.SerializeAsync(stream, settings.Normalize(), _options, cancellationToken);
        File.Move(temporary, _path, true);
    }
}
