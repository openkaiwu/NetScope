using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Performance;

/// <summary>
/// 通过 CreateToolhelp32Snapshot + OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)
/// 读取进程 CPU / 内存 / I/O 累计值。受限、瞬时退出进程返回不可访问读数，不中断整轮采样。
/// </summary>
public sealed class ProcessPerformanceProvider : IProcessPerformanceProvider
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaxPathChars = 260;

    public async ValueTask<ImmutableArray<ProcessPerformanceReading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = ImmutableArray.CreateBuilder<ProcessPerformanceReading>();

        var snapshot = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero) return results.ToImmutable();
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return results.ToImmutable();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pid = (int)entry.th32ProcessID;
                if (pid is 0 or 4) continue; // 空闲与系统进程不参与归因
                results.Add(TryReadProcess(pid, entry.szExeFile));
            } while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return results.ToImmutable();
    }

    private static ProcessPerformanceReading TryReadProcess(int pid, string exeName)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return new ProcessPerformanceReading(
                new ProcessInstanceKey(pid, DateTimeOffset.MinValue), DateTimeOffset.Now, SanitizeName(exeName),
                TimeSpan.Zero, 0, 0, 0, 0, 0, 0, false, "访问受限");
        try
        {
            if (!GetProcessTimes(handle, out var creation, out _, out var kernel, out var user))
                return new ProcessPerformanceReading(
                    new ProcessInstanceKey(pid, DateTimeOffset.MinValue), DateTimeOffset.Now, SanitizeName(exeName),
                    TimeSpan.Zero, 0, 0, 0, 0, 0, 0, false, "无法读取进程时间");

            var memory = new ProcessMemoryCounters { cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            if (!GetProcessMemoryInfo(handle, ref memory, memory.cb)) memory = default;

            var io = default(IoCounters);
            if (!GetProcessIoCounters(handle, out io)) io = default;

            return new ProcessPerformanceReading(
                new ProcessInstanceKey(pid, creation.ToDateTimeOffset()), DateTimeOffset.Now, SanitizeName(exeName),
                kernel, (long)memory.WorkingSetSize, (long)memory.PrivateUsage,
                (long)io.ReadTransferCount, (long)io.WriteTransferCount,
                (long)io.ReadOperationCount, (long)io.WriteOperationCount, true);
        }
        catch (Win32Exception)
        {
            return new ProcessPerformanceReading(
                new ProcessInstanceKey(pid, DateTimeOffset.MinValue), DateTimeOffset.Now, SanitizeName(exeName),
                TimeSpan.Zero, 0, 0, 0, 0, 0, 0, false, "访问受限");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string SanitizeName(string exeName)
    {
        var name = Path.GetFileNameWithoutExtension(exeName);
        return string.IsNullOrWhiteSpace(name) ? "未知进程" : name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPathChars)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public DateTimeOffset ToDateTimeOffset() =>
            DateTimeOffset.FromFileTime((long)(((ulong)High << 32) | Low));

        public static implicit operator TimeSpan(FileTime value) =>
            new((long)(((ulong)value.High << 32) | value.Low));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out FileTime creationTime, out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
