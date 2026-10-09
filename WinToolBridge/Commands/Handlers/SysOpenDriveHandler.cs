using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WinToolBridge.Services;

namespace WinToolBridge.Commands.Handlers;

public class SysOpenDriveHandler : ICommandHandler
{
    public string Action => "SYS:OPEN_DRIVE";

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (parameters is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("name", out var nameValue) ||
            nameValue.ValueKind != JsonValueKind.String) {
            throw new ArgumentException("Invalid drive name.");
        }

        var name = nameValue.GetString();
        var drive = DriveInfo.GetDrives().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

        if (drive == null ||
            drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram) ||
            !drive.IsReady) {
            throw new ArgumentException("The local drive is unavailable.");
        }

        var startInfo = new ProcessStartInfo {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(drive.Name);
        token.ThrowIfCancellationRequested();
        await ShortcutWindowHelper.StartAsync(startInfo, token, ["explorer"]);

        return new { opened = drive.Name };
    }
}
