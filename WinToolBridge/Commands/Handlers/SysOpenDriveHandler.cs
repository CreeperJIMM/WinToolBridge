using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace WinToolBridge.Commands.Handlers;

public class SysOpenDriveHandler : ICommandHandler
{
    public string Action => "SYS:OPEN_DRIVE";

    public Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (parameters is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("name", out var nameValue) ||
            nameValue.ValueKind != JsonValueKind.String) {
            throw new ArgumentException("Invalid drive name.");
        }

        // Match an enumerated root exactly; never pass arbitrary paths or Explorer switches.
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
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to open File Explorer.");

        return Task.FromResult<object?>(new { opened = drive.Name });
    }
}
