using NetScope.Core.Models;
using NetScope.Windows.Metadata;

namespace NetScope.Tests;

public sealed class ProcessMetadataDiskCacheTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netscope-metadata-test-" + Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_root, "process-metadata.json");
    private string ExePath => Path.Combine(_root, "app.exe");

    /// <summary>按真实文件属性构造元数据（与 CachedProcessMetadataProvider 的生产路径一致，缓存键才能对上）。</summary>
    private ProcessFileMetadata BuildMetadata(string company = "Contoso", SignatureState signature = SignatureState.Valid)
    {
        var info = new FileInfo(ExePath);
        return new ProcessFileMetadata(ExePath, info.Length, info.LastWriteTimeUtc.Ticks, company, "Widget", "Widget Host", "1.2.3.4", signature);
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(ExePath, "stub executable content");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StoreAndFlush_RoundTripsAcrossInstances()
    {
        var original = BuildMetadata();
        var cache = new ProcessMetadataDiskCache(CachePath);
        await cache.StoreAsync(original);
        await cache.FlushAsync();

        // 新实例模拟下次启动：只能从磁盘恢复
        var reloaded = new ProcessMetadataDiskCache(CachePath);
        var found = await reloaded.FindAsync(ExePath);
        Assert.NotNull(found);
        Assert.Equal(original.CompanyName, found.CompanyName);
        Assert.Equal(original.ProductName, found.ProductName);
        Assert.Equal(original.FileDescription, found.FileDescription);
        Assert.Equal(original.FileVersion, found.FileVersion);
        Assert.Equal(original.SignatureState, found.SignatureState);
        Assert.Equal(original.FileSize, found.FileSize);
    }

    [Fact]
    public async Task Find_ReturnsNull_ForChangedFile()
    {
        var cache = new ProcessMetadataDiskCache(CachePath);
        await cache.StoreAsync(BuildMetadata(signature: SignatureState.Missing));
        await cache.FlushAsync();

        // 文件内容变化 -> 大小/修改时间变化 -> 缓存键失效，需重新验证
        File.AppendAllText(ExePath, "more bytes");
        var reloaded = new ProcessMetadataDiskCache(CachePath);
        Assert.Null(await reloaded.FindAsync(ExePath));
    }

    [Fact]
    public async Task Find_ReturnsNull_ForMissingFile()
    {
        var cache = new ProcessMetadataDiskCache(CachePath);
        var missing = Path.Combine(_root, "does-not-exist.exe");
        Assert.Null(await cache.FindAsync(missing));
        Assert.Null(await cache.FindAsync(""));
    }

    [Fact]
    public async Task Load_CorruptCacheFile_IsDiscardedSilently()
    {
        await File.WriteAllTextAsync(CachePath, "not json at all {{{");
        var cache = new ProcessMetadataDiskCache(CachePath);
        var found = await cache.FindAsync(ExePath);
        Assert.Null(found);

        // 损坏后仍可正常写入并在下次读取命中
        var fresh = BuildMetadata(company: "A");
        await cache.StoreAsync(fresh);
        await cache.FlushAsync();
        var reloaded = new ProcessMetadataDiskCache(CachePath);
        var hit = await reloaded.FindAsync(ExePath);
        Assert.NotNull(hit);
        Assert.Equal("A", hit.CompanyName);
    }

    [Fact]
    public async Task Store_SamePathKeepsLatestValue()
    {
        var cache = new ProcessMetadataDiskCache(CachePath);
        await cache.StoreAsync(BuildMetadata(company: "Old"));
        await cache.StoreAsync(BuildMetadata(company: "New"));
        var found = await cache.FindAsync(ExePath);
        Assert.NotNull(found);
        Assert.Equal("New", found.CompanyName);
    }
}
