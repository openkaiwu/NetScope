using System.Text.Json;
using NetScope.Core.Models;

namespace NetScope.Windows.Metadata;

/// <summary>
/// 可执行文件元数据缓存（JSON 单文件落盘 %LocalAppData%\NetScope\cache\process-metadata.json）。
/// 键 = 文件路径小写 + 修改时间刻度 + 字节大小：文件没变就直接命中，免去重复的
/// FileVersionInfo 读取与签名验证（后者是昂贵操作）。磁盘文件损坏时整体丢弃重建。
/// </summary>
public sealed class ProcessMetadataDiskCache
{
    private sealed record CachedRecord(string Path, long Size, long WriteTimeUtcTicks, string? Company, string? Product, string? Description, string? Version, string? VerifyState);

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, CachedRecord>? _loaded;

    public ProcessMetadataDiskCache(string? filePath = null)
    {
        _path = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetScope", "cache", "process-metadata.json");
    }

    public string CacheFilePath => _path;

    /// <summary>查找缓存记录；未命中返回 null。内存映像未加载时从磁盘读取一次。</summary>
    public async ValueTask<ProcessFileMetadata?> FindAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var key = Key(filePath);
        if (key is null) return null;

        Dictionary<string, CachedRecord> map;
        await _gate.WaitAsync(cancellationToken);
        try { map = _loaded ??= await LoadAsync(cancellationToken); }
        finally { _gate.Release(); }

        return map.TryGetValue(key, out var hit) ? ToMetadata(hit) : null;
    }

    /// <summary>写入一条记录并标记内存映像为“脏”，由 <see cref="FlushAsync"/> 落盘。</summary>
    public async ValueTask StoreAsync(ProcessFileMetadata metadata, CancellationToken cancellationToken = default)
    {
        var key = Key(metadata.FilePath);
        if (key is null) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var map = _loaded ??= await LoadAsync(cancellationToken);
            map[key] = new CachedRecord(
                metadata.FilePath, metadata.FileSize, metadata.LastWriteTimeUtcTicks,
                metadata.CompanyName, metadata.ProductName, metadata.FileDescription, metadata.FileVersion,
                metadata.SignatureState.ToString());
        }
        finally { _gate.Release(); }
    }

    /// <summary>把累积的记录原子写盘（临时文件 + 覆盖移动），失败静默（缓存丢失只影响速度不影响功能）。</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded is null || _loaded.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var serialized = _loaded.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(serialized), cancellationToken);
            File.Move(temporary, _path, true);
        }
        catch (Exception)
        {
            // 缓存落盘失败不影响功能，下次启动重新积累
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<Dictionary<string, CachedRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path)) return [];
            await using var stream = File.OpenRead(_path);
            var records = await JsonSerializer.DeserializeAsync<CachedRecord[]>(stream, cancellationToken: cancellationToken);
            var map = new Dictionary<string, CachedRecord>(StringComparer.OrdinalIgnoreCase);
            if (records is not null)
                foreach (var record in records)
                {
                    // 键必须按记录里保存的大小/时间重建：若按当前文件重算，
                    // 文件在两次启动之间被替换过反而会错误命中旧记录
                    if (string.IsNullOrWhiteSpace(record.Path)) continue;
                    map[RecordKey(record)] = record;
                }
            return map;
        }
        catch (Exception)
        {
            // 缓存文件损坏或格式不兼容：整体丢弃，按需重建
            return [];
        }
    }

    private static string? Key(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length == 0) return null;
        return filePath.ToLowerInvariant() + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
    }

    /// <summary>按记录自身保存的大小/时间构造键（与 <see cref="Key"/> 相同格式）。</summary>
    private static string RecordKey(CachedRecord record) =>
        record.Path.ToLowerInvariant() + "|" + record.WriteTimeUtcTicks + "|" + record.Size;

    private static ProcessFileMetadata ToMetadata(CachedRecord record) => new(
        record.Path, record.Size, record.WriteTimeUtcTicks,
        record.Company, record.Product, record.Description, record.Version,
        Enum.TryParse<SignatureState>(record.VerifyState, out var state) ? state : SignatureState.Unknown);
}
