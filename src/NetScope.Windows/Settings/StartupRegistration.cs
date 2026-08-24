using Microsoft.Win32;

namespace NetScope.Windows.Settings;

public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue("NetScope") is string;
        }
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue("NetScope", $"\"{executablePath}\" --background", RegistryValueKind.String);
        else key.DeleteValue("NetScope", false);
    }
}
