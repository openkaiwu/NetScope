using System.Runtime.InteropServices;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Performance;

/// <summary>系统 CPU / 内存原始读数。网络字节由采集协调器用网卡统计增量填充。</summary>
public sealed class SystemPerformanceProvider : ISystemPerformanceProvider
{
    public ValueTask<SystemPerformanceReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetSystemTimes(out var idle, out var kernel, out var user);
        var memory = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        // 必须按 ref 传：[In,Out] 传值形式在 .NET 里不会回写结构体，读到的全是零
        GlobalMemoryStatusEx(ref memory);
        return ValueTask.FromResult(new SystemPerformanceReading(
            DateTimeOffset.Now, kernel, user, idle,
            memory.ullAvailPhys, memory.ullTotalPhys, 0, 0));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public static implicit operator TimeSpan(FileTime value) =>
            new((long)(((ulong)value.High << 32) | value.Low));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
