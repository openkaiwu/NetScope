using System.Diagnostics;
using System.Runtime.InteropServices;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Performance;

/// <summary>只读取 Collector 当前进程累计计数；CPU 换算为整机百分比。</summary>
public sealed class CollectorSelfMonitor : ICollectorSelfMonitor, IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private DateTimeOffset? _lastAt;
    private TimeSpan _lastCpu;
    private ulong _lastRead;
    private ulong _lastWrite;

    public CollectorUsageReading Read()
    {
        _process.Refresh();
        var now = DateTimeOffset.Now;
        var cpu = _process.TotalProcessorTime;
        var ioAvailable = GetProcessIoCounters(_process.Handle, out var io);
        if (!ioAvailable)
        {
            io.ReadTransferCount = _lastRead;
            io.WriteTransferCount = _lastWrite;
        }
        var elapsed = _lastAt is { } at ? (now - at).TotalSeconds : 0;
        var cpuPercent = elapsed > 0 ? Math.Clamp((cpu - _lastCpu).TotalSeconds / elapsed * 100 / Environment.ProcessorCount, 0, 100) : 0;
        var readDelta = io.ReadTransferCount >= _lastRead ? io.ReadTransferCount - _lastRead : 0;
        var writeDelta = io.WriteTransferCount >= _lastWrite ? io.WriteTransferCount - _lastWrite : 0;
        var read = elapsed > 0 ? (long)Math.Min(long.MaxValue, readDelta / elapsed) : 0;
        var write = elapsed > 0 ? (long)Math.Min(long.MaxValue, writeDelta / elapsed) : 0;
        _lastAt = now; _lastCpu = cpu; _lastRead = io.ReadTransferCount; _lastWrite = io.WriteTransferCount;
        return new(now, cpuPercent, _process.WorkingSet64, read, write);
    }

    public void Dispose() => _process.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
