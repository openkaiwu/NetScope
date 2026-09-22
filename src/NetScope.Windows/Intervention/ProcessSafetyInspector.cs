using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using NetScope.Core.Abstractions;
using NetScope.Core.Knowledge;
using NetScope.Core.Models;

namespace NetScope.Windows.Intervention;

/// <summary>
/// 只读进程安全检查（V1.1）：以最小查询权限采集终止评估所需的事实——
/// 关键标志、创建时间、映像路径、所有者 SID、会话、服务归属与主窗口。
/// 只申请 PROCESS_QUERY_LIMITED_INFORMATION；不使用 SeDebugPrivilege，不写任何目标状态。
/// </summary>
public sealed partial class ProcessSafetyInspector(IProcessFileMetadataProvider? metadataProvider = null)
{
    public sealed record InspectionResult(TerminationFacts Facts, string? PathUsed);

    /// <summary>采集目标进程的评估事实。进程已退出返回 null；查询受限时返回带未知项的事实。</summary>
    public InspectionResult? Collect(int processId, string name, int listeningPortCount = 0, int activeConnectionCount = 0)
    {
        if (!TryCollectCore(processId, name, listeningPortCount, activeConnectionCount, out var facts, out var path))
            return null;
        return new(facts, path);
    }

    private bool TryCollectCore(int processId, string name, int listeningPortCount, int activeConnectionCount,
        out TerminationFacts facts, out string? path)
    {
        facts = null!;
        path = null;
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            // 打不开（权限不足或已退出）：区分“已退出”与“查询受限”
            try { using var _ = Process.GetProcessById(processId); }
            catch (ArgumentException) { return false; } // 已退出
            catch (Exception) { }
            facts = BuildFacts(new ProcessInstanceKey(processId, DateTimeOffset.MinValue), name,
                path: null, publisher: null, signature: null,
                critical: false, criticalKnown: false, ownerSid: null, ownerKnown: false,
                session: null, sessionKnown: false, services: [], servicesKnown: false,
                hasMainWindow: QueryHasMainWindow(processId), listeningPortCount, activeConnectionCount);
            return true;
        }

        try
        {
            var creation = NativeMethods.QueryCreationTime(handle);
            var isCritical = NativeMethods.QueryIsCritical(handle, out var criticalKnown);
            path = NativeMethods.QueryImagePath(handle);
            var session = NativeMethods.QuerySessionId(processId, out var sessionKnown);
            var (ownerSid, ownerKnown) = NativeMethods.QueryOwnerSid(handle);
            var (services, servicesKnown) = ServiceIndex.HostedServices(processId);

            string? publisher = null;
            SignatureState? signature = null;
            if (path is { Length: > 0 } && metadataProvider is not null)
            {
                try
                {
                    var metadata = metadataProvider.ResolveAsync(path).AsTask().GetAwaiter().GetResult();
                    publisher = metadata?.CompanyName;
                    signature = metadata?.SignatureState;
                }
                catch (Exception)
                {
                    // 元数据失败不阻断评估：签名保持未知
                }
            }

            facts = BuildFacts(new ProcessInstanceKey(processId, creation ?? DateTimeOffset.MinValue), name,
                path, publisher, signature, isCritical, criticalKnown,
                ownerKnown ? ownerSid : null, ownerKnown,
                sessionKnown ? session : null, sessionKnown,
                services, servicesKnown,
                QueryHasMainWindow(processId), listeningPortCount, activeConnectionCount);
            return true;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static TerminationFacts BuildFacts(ProcessInstanceKey process, string name,
        string? path, string? publisher, SignatureState? signature,
        bool critical, bool criticalKnown, string? ownerSid, bool ownerKnown,
        int? session, bool sessionKnown, IReadOnlyList<string> services, bool servicesKnown,
        bool hasMainWindow, int listeningPortCount, int activeConnectionCount)
    {
        TerminationKnowledge.TryLookup(name, out var knowledge);
        return new(process, name, path, publisher, signature,
            critical, criticalKnown,
            ownerKnown ? ownerSid : null, CurrentUserSid.Value,
            sessionKnown ? session : null, CurrentSessionId.Value,
            services, servicesKnown, hasMainWindow, listeningPortCount, activeConnectionCount,
            IsNetScopeProcess(name, path), knowledge.Policy, knowledge.Impact, knowledge.Alternatives);
    }

    private static bool QueryHasMainWindow(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsNetScopeProcess(string name, string? path) =>
        NormalizeName(name) is "netscope" or "netscope.collector" ||
        (path is not null && NormalizeName(Path.GetFileNameWithoutExtension(path)) is "netscope" or "netscope.collector");

    private static string NormalizeName(string value) => value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? value[..^4]
        : value;

    internal static readonly Lazy<string?> CurrentUserSid = new(() =>
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; }
        catch (Exception) { return null; }
    });

    internal static readonly Lazy<int> CurrentSessionId = new(() => NativeMethods.QuerySessionId(Environment.ProcessId, out _) ?? 0);

    /// <summary>SCM 服务索引：PID → 承载的服务名。按 30 秒缓存，避免每次评估都枚举服务控制管理器。</summary>
    internal static class ServiceIndex
    {
        private static readonly object Gate = new();
        private static Dictionary<int, IReadOnlyList<string>>? _map;
        private static DateTimeOffset _builtAt = DateTimeOffset.MinValue;

        public static (IReadOnlyList<string> Services, bool Known) HostedServices(int processId)
        {
            lock (Gate)
            {
                if (_map is null || DateTimeOffset.UtcNow - _builtAt > TimeSpan.FromSeconds(30))
                {
                    _map = BuildMap();
                    _builtAt = DateTimeOffset.UtcNow;
                }
                if (_map.TryGetValue(processId, out var services)) return (services, true);
                return ([], _map.TryGetValue(-1, out _)); // -1 标记枚举是否成功
            }
        }

        private static Dictionary<int, IReadOnlyList<string>> BuildMap()
        {
            var map = new Dictionary<int, List<string>>();
            var success = false;
            var manager = IntPtr.Zero;
            try
            {
                manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerEnumerateService);
                if (manager != IntPtr.Zero)
                {
                    success = EnumerateInto(manager, map);
                }
            }
            catch (Exception)
            {
                // 枚举失败：标记未知
            }
            finally
            {
                if (manager != IntPtr.Zero) NativeMethods.CloseServiceHandle(manager);
            }
            if (success) map[-1] = []; // 成功标志
            return map.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
        }

        private static bool EnumerateInto(IntPtr manager, Dictionary<int, List<string>> map)
        {
            const int infoLevel = 0;          // SC_ENUM_PROCESS_INFO
            const uint serviceType = 0x30;    // SERVICE_WIN32
            const uint serviceState = 3;     // SERVICE_STATE_ALL
            var size = 64 * 1024;
            var resume = 0u;
            while (true)
            {
                var buffer = new byte[size];
                if (!NativeMethods.EnumServicesStatusEx(manager, infoLevel, serviceType, serviceState,
                        buffer, (uint)buffer.Length, out var needed, out var returned, ref resume, null))
                {
                    if (Marshal.GetLastWin32Error() != 234 /* ERROR_MORE_DATA */)
                        return false;
                    if (needed == 0) return false;
                    size = (int)needed;
                    continue;
                }

                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var basePointer = handle.AddrOfPinnedObject();
                    for (var index = 0; index < returned; index++)
                    {
                        var entry = Marshal.PtrToStructure<NativeMethods.EnumServiceStatusProcess>(basePointer + index * NativeMethods.EnumServiceStatusProcess.Size);
                        var pid = (int)entry.Status.ProcessId;
                        if (pid <= 0) continue;
                        var serviceName = Marshal.PtrToStringUni(entry.ServiceName);
                        if (string.IsNullOrEmpty(serviceName)) continue;
                        if (!map.TryGetValue(pid, out var list))
                        {
                            list = [];
                            map[pid] = list;
                        }
                        list.Add(serviceName);
                    }
                }
                finally
                {
                    handle.Free();
                }
                if (resume == 0) return true;
            }
        }
    }

    internal static partial class NativeMethods
    {
        internal const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const int TokenOwnerClass = 4;
        internal const uint ScManagerEnumerateService = 0x0004;

        [LibraryImport("kernel32", SetLastError = true)]
        internal static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr handle);

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsProcessCritical(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] out bool critical);

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, System.Text.StringBuilder name, ref uint size);

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [LibraryImport("advapi32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

        [LibraryImport("advapi32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, uint length, out uint returnLength);

        [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

        [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumServicesStatusEx(IntPtr manager, int informationLevel, uint serviceType, uint serviceState,
            byte[] services, uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

        [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [LibraryImport("advapi32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseServiceHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatusProcess
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
            public uint ProcessId;
            public uint ServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EnumServiceStatusProcess
        {
            // x64：两个指针 + 36 字节状态结构，按 8 字节对齐后共 56 字节
            internal const int Size = 56;
            public IntPtr ServiceName;
            public IntPtr DisplayName;
            public ServiceStatusProcess Status;
        }

        internal static DateTimeOffset? QueryCreationTime(IntPtr handle)
        {
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) return null;
            return DateTime.FromFileTimeUtc(creation);
        }

        /// <summary>关键标志查询：调用失败绝不降级为“非关键”，由 criticalKnown=false 标记未知。</summary>
        internal static bool QueryIsCritical(IntPtr handle, out bool criticalKnown)
        {
            if (IsProcessCritical(handle, out var critical))
            {
                criticalKnown = true;
                return critical;
            }
            criticalKnown = false;
            return false;
        }

        internal static string? QueryImagePath(IntPtr handle)
        {
            var builder = new System.Text.StringBuilder(1024);
            uint size = (uint)builder.Capacity;
            return QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString() : null;
        }

        internal static int? QuerySessionId(int processId, out bool known)
        {
            if (ProcessIdToSessionId((uint)processId, out var session))
            {
                known = true;
                return (int)session;
            }
            known = false;
            return null;
        }

        /// <summary>读取进程令牌所有者 SID（TokenOwner）。失败返回 Known=false。</summary>
        internal static (string? Sid, bool Known) QueryOwnerSid(IntPtr processHandle)
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var token))
                return (null, false);
            try
            {
                GetTokenInformation(token, TokenOwnerClass, IntPtr.Zero, 0, out var needed);
                if (needed == 0) return (null, false);
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!GetTokenInformation(token, TokenOwnerClass, buffer, needed, out _))
                        return (null, false);
                    // TOKEN_OWNER = { 指向 SID 的指针 }；SID 本体也在缓冲区内
                    var sid = Marshal.ReadIntPtr(buffer);
                    if (sid == IntPtr.Zero) return (null, false);
                    if (!ConvertSidToStringSid(sid, out var stringSidPointer) || stringSidPointer == IntPtr.Zero)
                        return (null, false);
                    try
                    {
                        return (Marshal.PtrToStringUni(stringSidPointer), true);
                    }
                    finally
                    {
                        LocalFree(stringSidPointer);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }

        [LibraryImport("kernel32")]
        private static partial void LocalFree(IntPtr handle);
    }
}
