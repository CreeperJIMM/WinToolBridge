using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinToolBridge.Commands.Handlers;

public class SysOpenShortcutHandler : ICommandHandler
{
    public string Action => "SYS:OPEN_SHORTCUT";

    #region Win32 API 視窗置頂輔助

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private const int SW_RESTORE = 9;
    private const int ASFW_ANY = -1;

    #endregion

    private static readonly Dictionary<string, (string FileName, string Arguments)> Shortcuts = new() {
        // 裝置印表機
        ["classic_devices"] = ("explorer.exe", "shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}"),

        // 裝置管理員
        ["device_manager"] = ("devmgmt.msc", ""),

        // 網路連線
        ["network_adapters"] = ("ncpa.cpl", ""),

        // 聲音設定
        ["sound_settings"] = ("mmsys.cpl", ""),

        // 程式和功能
        ["programs"] = ("appwiz.cpl", ""),

        // 電源選項
        ["power_options"] = ("powercfg.cpl", ""),

    };

    public Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        var target = parameters?.TryGetProperty("target", out var t) == true ? t.GetString() : null;

        if (string.IsNullOrEmpty(target) || !Shortcuts.TryGetValue(target, out var cmd)) {
            throw new ArgumentException("無效的捷徑目標");
        }

        AllowSetForegroundWindow(ASFW_ANY);

        var psi = new ProcessStartInfo {
            FileName = cmd.FileName,
            Arguments = cmd.Arguments,
            UseShellExecute = true
        };

        var proc = Process.Start(psi);

        if (proc != null) {
            _ = Task.Run(async () => {
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(100);
                    proc.Refresh();
                    if (proc.MainWindowHandle != IntPtr.Zero) {
                        ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(proc.MainWindowHandle);
                        break;
                    }
                }
            });
        }

        return Task.FromResult<object?>(new { opened = target });
    }
}