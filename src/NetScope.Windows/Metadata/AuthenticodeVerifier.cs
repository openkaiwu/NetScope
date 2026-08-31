using System.Runtime.InteropServices;
using System.Text;
using NetScope.Core.Models;

namespace NetScope.Windows.Metadata;

/// <summary>
/// 可执行文件签名验证（WinVerifyTrust），与资源管理器“数字签名”属性页一致的两级策略：
/// 1) 嵌入式签名（GENERIC_VERIFY_V2，文件内的 PKCS#7）；失败时
/// 2) 目录签名（DriverActionVerify + CryptCATAdmin，Windows 系统文件的目录方式）。
/// 仅做本地信任链验证，不做联网吊销检查；结果由调用方按“路径+大小+修改时间”缓存。
/// </summary>
public static partial class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("{00AAC56B-CD44-11D0-8CC2-00C04FC295EE}");
    private static readonly Guid DriverActionVerify = new("{F750E6C3-38EE-11D1-85E5-00C04FC295EE}");

    private const int WTD_UI_NONE = 2;
    private const int WTD_REVOKE_NONE = 0;
    private const int WTD_CHOICE_FILE = 1;
    private const int WTD_CHOICE_CATALOG = 2;
    private const int WTD_STATEACTION_VERIFY = 1;
    private const int WTD_STATEACTION_CLOSE = 2;
    private const int WTD_REVOCATION_CHECK_NONE = 0x10;
    private const int WTD_CACHE_ONLY_URL_RETRIEVAL = 0x40;
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int ERROR_SUCCESS = 0;

    /// <summary>验证文件签名：先嵌入式，再目录；0/无签名/无效分别映射到枚举。</summary>
    public static SignatureState Verify(string filePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath)) return SignatureState.Unknown;
        try
        {
            var embedded = VerifyEmbedded(filePath);
            if (embedded == ERROR_SUCCESS) return SignatureState.Valid;
            if (embedded == TRUST_E_NOSIGNATURE || embedded == unchecked((int)0x800B0003))
            {
                // 无嵌入式签名：查询 Windows 目录（catalog）签名
                var catalog = VerifyCatalog(filePath);
                if (catalog == ERROR_SUCCESS) return SignatureState.Valid;
                if (catalog == TRUST_E_NOSIGNATURE) return SignatureState.Missing;
                return SignatureState.Invalid;
            }
            return SignatureState.Invalid;
        }
        catch (Exception)
        {
            return SignatureState.Unknown;
        }
    }

    private static int VerifyEmbedded(string filePath)
    {
        var info = new WintrustFileInfo
        {
            cbStruct = Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = Marshal.StringToHGlobalUni(filePath)
        };
        var data = new WintrustData
        {
            cbStruct = Marshal.SizeOf<WintrustData>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = WTD_CHOICE_FILE,
            pUnion = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>()),
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
        };
        try
        {
            Marshal.StructureToPtr(info, data.pUnion, false);
            return WinVerifyTrust(0, in GenericVerifyV2, ref data);
        }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            try { WinVerifyTrust(0, in GenericVerifyV2, ref data); } catch (Exception) { }
            Marshal.FreeHGlobal(data.pUnion);
            Marshal.FreeHGlobal(info.pcwszFilePath);
        }
    }

    /// <summary>目录（catalog）签名验证：Windows 系统文件的可执行内容不入文件签名而入安全目录。</summary>
    private static int VerifyCatalog(string filePath)
    {
        using var handle = File.OpenHandle(filePath, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read | FileShare.Delete);
        if (!CryptCATAdminAcquireContext(out var catAdmin, in DriverActionVerify, dwFlags: 0)) return TRUST_E_NOSIGNATURE;
        try
        {
            var hashLength = 0;
            CryptCATAdminCalcHashFromFileHandle(handle, ref hashLength, null, 0);
            if (hashLength <= 0) return TRUST_E_NOSIGNATURE;
            var hash = new byte[hashLength];
            if (!CryptCATAdminCalcHashFromFileHandle(handle, ref hashLength, hash, 0)) return TRUST_E_NOSIGNATURE;

            var catalogContext = CryptCATAdminEnumCatalogFromHash(catAdmin, hash, hashLength, 0, IntPtr.Zero);
            if (catalogContext == IntPtr.Zero) return TRUST_E_NOSIGNATURE;
            try
            {
                var catalogInfo = new CatalogInfo { cbStruct = Marshal.SizeOf<CatalogInfo>() };
                if (!CryptCATCatalogInfoFromContext(catalogContext, ref catalogInfo, 0)) return TRUST_E_NOSIGNATURE;

                // 成员标签 = 文件哈希的十六进制大写串，目录内按标签定位成员
                var tag = new StringBuilder(hashLength * 2);
                foreach (var b in hash) tag.Append(b.ToString("X2"));

                var trustCatalogInfo = new WintrustCatalogInfo
                {
                    cbStruct = Marshal.SizeOf<WintrustCatalogInfo>(),
                    pcwszCatalogFilePath = Marshal.StringToHGlobalUni(catalogInfo.wszCatalogFile),
                    pcwszMemberFilePath = Marshal.StringToHGlobalUni(filePath),
                    pcwszMemberTag = Marshal.StringToHGlobalUni(tag.ToString()),
                    hMemberFile = IntPtr.Zero,
                    pbCalculatedFileHash = Marshal.AllocHGlobal(hashLength),
                    cbCalculatedFileHash = hashLength
                };
                Marshal.Copy(hash, 0, trustCatalogInfo.pbCalculatedFileHash, hashLength);

                var data = new WintrustData
                {
                    cbStruct = Marshal.SizeOf<WintrustData>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_CATALOG,
                    pUnion = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustCatalogInfo>()),
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
                };
                try
                {
                    Marshal.StructureToPtr(trustCatalogInfo, data.pUnion, false);
                    return WinVerifyTrust(0, in DriverActionVerify, ref data);
                }
                finally
                {
                    data.dwStateAction = WTD_STATEACTION_CLOSE;
                    try { WinVerifyTrust(0, in DriverActionVerify, ref data); } catch (Exception) { }
                    Marshal.FreeHGlobal(data.pUnion);
                    Marshal.FreeHGlobal(trustCatalogInfo.pcwszCatalogFilePath);
                    Marshal.FreeHGlobal(trustCatalogInfo.pcwszMemberFilePath);
                    Marshal.FreeHGlobal(trustCatalogInfo.pcwszMemberTag);
                    Marshal.FreeHGlobal(trustCatalogInfo.pbCalculatedFileHash);
                }
            }
            finally
            {
                CryptCATAdminReleaseCatalogContext(catAdmin, catalogContext, 0);
            }
        }
        finally
        {
            CryptCATAdminReleaseContext(catAdmin, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public int cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPCallbackData;
        public int dwUIChoice;
        public int fdwRevocationChecks;
        public int dwUnionChoice;
        public IntPtr pUnion;
        public int dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public int dwProvFlags;
        public int dwUIContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public int cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustCatalogInfo
    {
        public int cbStruct;
        public int dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public int cbCalculatedFileHash;
        public IntPtr pcCTLContext;
    }

    [DllImport("wintrust.dll")]
    private static extern int WinVerifyTrust(int sessionId, in Guid actionId, ref WintrustData data);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern bool CryptCATAdminAcquireContext(out IntPtr phCatAdmin, in Guid pPolicySubId, int dwFlags);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, ref int pcbHash, byte[]? pbHash, int dwFlags);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, int cbHash, int dwFlags, IntPtr phPrev);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, int dwFlags);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, int dwFlags);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CatalogInfo psInfo, int dwFlags);
}
