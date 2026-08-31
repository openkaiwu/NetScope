using System.Collections.Concurrent;
using System.Diagnostics;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Metadata;

/// <summary>
/// 可执行文件元数据提供方：FileVersionInfo + 本地 Authenticode 验证。
/// 两级缓存：内存（本会话）与磁盘（跨会话 JSON，键含文件大小与修改时间，文件替换自动失效）。
/// 签名验证是文件级昂贵操作，仅对缓存未命中的文件执行一次。
/// </summary>
public sealed class CachedProcessMetadataProvider : IProcessFileMetadataProvider, IDisposable
{
    private readonly ProcessMetadataDiskCache _disk;
    private readonly ConcurrentDictionary<string, Task<ProcessFileMetadata?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProcessFileMetadata> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private int _pendingDiskWrites;
    private bool _disposed;

    public CachedProcessMetadataProvider(ProcessMetadataDiskCache? diskCache = null)
    {
        _disk = diskCache ?? new ProcessMetadataDiskCache();
    }

    /// <summary>实际使用的磁盘缓存文件路径（暴露给测试与诊断）。</summary>
    public string DiskCachePath => _disk.CacheFilePath;

    public async ValueTask<ProcessFileMetadata?> ResolveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var key = filePath.ToLowerInvariant();
        if (_memory.TryGetValue(key, out var hit)) return hit;

        // 同一文件的并发请求合并为一次加载（懒加载详情面板与端口页可能同时命中）
        var load = _inFlight.GetOrAdd(key, _ => Task.Run(() => LoadAsync(filePath, cancellationToken), CancellationToken.None));
        try { return await load; }
        finally { _inFlight.TryRemove(key, out _); }
    }

    private async Task<ProcessFileMetadata?> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var cached = await _disk.FindAsync(filePath, cancellationToken);
            if (cached is not null)
            {
                _memory[filePath.ToLowerInvariant()] = cached;
                return cached;
            }

            var info = new FileInfo(filePath);
            if (!info.Exists) return null;

            string? company = null, product = null, description = null, version = null;
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(filePath);
                company = NullIfEmpty(fvi.CompanyName);
                product = NullIfEmpty(fvi.ProductName);
                description = NullIfEmpty(fvi.FileDescription);
                version = NullIfEmpty(fvi.FileVersion);
            }
            catch (Exception)
            {
                // 版本资源读取失败（访问受限等）：签名验证照常，版本字段留空
            }

            var signature = AuthenticodeVerifier.Verify(filePath);
            var metadata = new ProcessFileMetadata(
                filePath, info.Length, info.LastWriteTimeUtc.Ticks, company, product, description, version, signature);

            _memory[filePath.ToLowerInvariant()] = metadata;
            await _disk.StoreAsync(metadata, cancellationToken);
            if (Interlocked.Increment(ref _pendingDiskWrites) >= 8)
            {
                Interlocked.Exchange(ref _pendingDiskWrites, 0);
                _ = FlushAsync(cancellationToken);
            }
            return metadata;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>把内存中新增的记录落盘；退出前调用（App 关闭钩子），失败静默。</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        await _flushLock.WaitAsync(cancellationToken);
        try { await _disk.FlushAsync(cancellationToken); }
        finally { _flushLock.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = FlushAsync(CancellationToken.None);
        _flushLock.Dispose();
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
