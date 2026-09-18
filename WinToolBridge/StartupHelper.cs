using Microsoft.Win32;

namespace WinToolBridge;

public static class StartupHelper
{
    private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string APP_NAME = "WinToolBridge";

    public static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, false);
        return key?.GetValue(APP_NAME) != null;
    }

    public static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true);
        if (key == null) return;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        if (enable) {
            key.SetValue(APP_NAME, $"\"{exePath}\" -silent");
        }
        else {
            key.DeleteValue(APP_NAME, false);
        }
    }
}