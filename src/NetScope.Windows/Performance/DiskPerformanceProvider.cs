using System.Runtime.InteropServices;
using NetScope.Core.Models;

namespace NetScope.Windows.Performance;

/// <summary>英文计数器路径由 PDH 映射到本地语言。失败返回 null，不以零伪装成功。</summary>
public sealed class DiskPerformanceProvider : IDisposable
{
    private IntPtr _query;
    private readonly IntPtr[] _counters = new IntPtr[4];
    private bool _primed;
    private DateTimeOffset _retryAfter;

    public DiskPerformanceSample? Read()
    {
        if (_query == IntPtr.Zero)
        {
            if (DateTimeOffset.UtcNow < _retryAfter) return null;
            _retryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return null;
            string[] names = ["Avg. Disk sec/Read", "Avg. Disk sec/Write", "Avg. Disk Queue Length", "% Idle Time"];
            for (var i = 0; i < names.Length; i++)
                if (PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\" + names[i], IntPtr.Zero, out _counters[i]) != 0)
                { Dispose(); return null; }
        }
        if (PdhCollectQueryData(_query) != 0) { Dispose(); return null; }
        if (!_primed) { _primed = true; return null; }
        var values = new double[4];
        for (var i = 0; i < values.Length; i++)
        {
            if (PdhGetFormattedCounterValue(_counters[i], 0x200, out _, out var value) != 0 ||
                value.Status > 1 || !double.IsFinite(value.Value) || value.Value < 0) return null;
            values[i] = value.Value;
        }
        return new(values[0] * 1000, values[1] * 1000, values[2], Math.Clamp(100 - values[3], 0, 100));
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _primed = false;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
