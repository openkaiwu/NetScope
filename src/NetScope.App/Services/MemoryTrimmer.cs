using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetScope.App.Services;

internal static class MemoryTrimmer
{
    public static void Trim()
    {
        GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        _ = EmptyWorkingSet(process.Handle);
    }

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
