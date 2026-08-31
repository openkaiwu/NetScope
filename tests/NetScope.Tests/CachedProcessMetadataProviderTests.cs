using NetScope.Core.Models;
using NetScope.Windows.Metadata;

namespace NetScope.Tests;

/// <summary>签名验证与提供方缓存的实机冒烟测试（Windows 本机运行，结果不假定特定文件签名状态）。</summary>
public sealed class CachedProcessMetadataProviderTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netscope-provider-test-" + Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(_root, "metadata.json");
    private string CopyPath => Path.Combine(_root, "copy-of-notepad.exe");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
        return Task.CompletedTask;
    }

    [Fact]
    public void Verify_ReturnsKnownState_ForSignedSystemBinary()
    {
        var notepad = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\notepad.exe");
        if (!File.Exists(notepad))
        {
            // ARM64 或精简系统的兜底：跳过而非失败
            return;
        }
        var state = AuthenticodeVerifier.Verify(notepad);
        Assert.True(state is SignatureState.Valid or SignatureState.Unknown or SignatureState.Invalid);
    }

    [Fact]
    public void Verify_ReturnsMissing_ForUnsignedStubFile()
    {
        File.WriteAllText(Path.Combine(_root, "stub.exe"), "MZ stub without signature");
        var state = AuthenticodeVerifier.Verify(Path.Combine(_root, "stub.exe"));
        Assert.Equal(SignatureState.Missing, state);
    }

    [Fact]
    public async Task ResolveAsync_PopulatesMetadata_AndSecondCallHitsMemory()
    {
        var notepad = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\notepad.exe");
        if (!File.Exists(notepad)) return;
        File.Copy(notepad, CopyPath, overwrite: true);

        var provider = new CachedProcessMetadataProvider(new ProcessMetadataDiskCache(CachePath));
        var first = await provider.ResolveAsync(CopyPath);
        Assert.NotNull(first);
        Assert.True(first!.FileSize > 0);
        // 复制的系统文件：微软签名链在多数机器上可本地验证通过
        Assert.Equal(SignatureState.Valid, first.SignatureState);

        var second = await provider.ResolveAsync(CopyPath);
        Assert.Same(first, second);
        await provider.FlushAsync();
    }

    [Fact]
    public async Task ResolveAsync_PersistsAcrossProviderInstances_ViaDiskCache()
    {
        var notepad = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\notepad.exe");
        if (!File.Exists(notepad)) return;
        File.Copy(notepad, CopyPath, overwrite: true);

        var first = new CachedProcessMetadataProvider(new ProcessMetadataDiskCache(CachePath));
        var metadata = await first.ResolveAsync(CopyPath);
        await first.FlushAsync();
        Assert.NotNull(metadata);

        var second = new CachedProcessMetadataProvider(new ProcessMetadataDiskCache(CachePath));
        var reloaded = await second.ResolveAsync(CopyPath);
        Assert.NotNull(reloaded);
        Assert.Equal(metadata!.CompanyName, reloaded!.CompanyName);
        Assert.Equal(metadata.SignatureState, reloaded.SignatureState);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_ForMissingOrEmptyPath()
    {
        var provider = new CachedProcessMetadataProvider(new ProcessMetadataDiskCache(CachePath));
        Assert.Null(await provider.ResolveAsync(""));
        Assert.Null(await provider.ResolveAsync(Path.Combine(_root, "no-such-file.exe")));
    }
}
