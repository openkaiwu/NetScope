using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NetScope.App.Services;

public static class WindowBackdrop
{
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        var backdrop = 2;
        _ = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
