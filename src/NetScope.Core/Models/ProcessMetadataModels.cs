namespace NetScope.Core.Models;

/// <summary>可执行文件签名验证状态（本地 WinVerifyTrust 结果，不含联网吊销检查）。</summary>
public enum SignatureState
{
    Unknown = 0,
    Valid = 1,
    Missing = 2,
    Invalid = 3
}

/// <summary>
/// 可执行文件静态身份信息（来自文件版本资源与签名验证）。
/// FileSize + LastWriteTimeUtcTicks 与缓存键一致：文件被替换后缓存自动失效。
/// </summary>
public sealed record ProcessFileMetadata(
    string FilePath,
    long FileSize,
    long LastWriteTimeUtcTicks,
    string? CompanyName,
    string? ProductName,
    string? FileDescription,
    string? FileVersion,
    SignatureState SignatureState)
{
    public bool IsEmpty => string.IsNullOrEmpty(CompanyName) && string.IsNullOrEmpty(ProductName) && string.IsNullOrEmpty(FileDescription);
}
