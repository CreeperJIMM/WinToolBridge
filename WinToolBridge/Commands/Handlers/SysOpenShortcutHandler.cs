using System.Diagnostics;
using System.Text.Json;
using WinToolBridge.Services;

namespace WinToolBridge.Commands.Handlers;

public class SysOpenShortcutHandler : ICommandHandler
{
    public string Action => "SYS:OPEN_SHORTCUT";
    public bool RequiresDeduplication => true;

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

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        var target = parameters?.TryGetProperty("target", out var t) == true ? t.GetString() : null;

        if (string.IsNullOrEmpty(target) || !Shortcuts.TryGetValue(target, out var cmd)) {
            throw new ArgumentException("無效的捷徑目標");
        }

        token.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo {
            FileName = cmd.FileName,
            Arguments = cmd.Arguments,
            UseShellExecute = true
        };

        await ShortcutWindowHelper.StartAsync(psi, token, ["explorer", "control", "rundll32", "mmc"]);

        return new { opened = target };
    }
}
