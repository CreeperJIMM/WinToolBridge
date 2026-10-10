using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Management.Deployment;
using WinToolBridge.Services;

namespace WinToolBridge.Commands.Handlers;

public class SysOpenGpuControlPanelHandler : ICommandHandler
{
    public string Action => "SYS:OPEN_GPU_CONTROL_PANEL";
    public bool RequiresDeduplication => true;

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (parameters is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("vendor", out var vendorValue) ||
            vendorValue.ValueKind != JsonValueKind.String) {
            throw new ArgumentException("Unsupported GPU vendor.");
        }
        var vendor = vendorValue.GetString();
        if (vendor is not ("amd" or "nvidia" or "intel")) {
            throw new ArgumentException("Unsupported GPU vendor.");
        }

        // Both executable paths and package families are fixed by the backend.
        var executable = FindDesktopExecutable(vendor);
        if (executable != null) {
            await StartDesktopAppAsync(executable, token);
            return new { vendor, opened = true };
        }

        if (await TryLaunchPackageAsync(vendor, token)) {
            return new { vendor, opened = true };
        }

        // Newer NVIDIA installations may provide NVIDIA App instead of Control Panel.
        if (vendor == "nvidia") {
            executable = FindExecutable([@"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"]);
            if (executable != null) {
                await StartDesktopAppAsync(executable, token);
                return new { vendor, opened = true };
            }
        }

        return new { vendor, opened = false };
    }

    private static string? FindDesktopExecutable(string vendor) => FindExecutable(vendor switch {
        "amd" => [@"AMD\CNext\CNext\RadeonSoftware.exe"],
        "nvidia" => [@"NVIDIA Corporation\Control Panel Client\nvcplui.exe"],
        "intel" => [@"Intel\Intel Graphics Software\IntelGraphicsSoftware.exe", @"Intel\Intel Arc Control\ArcControl.exe"],
        _ => []
    });

    private static string? FindExecutable(string[] relativePaths)
    {
        foreach (var relativePath in relativePaths) {
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }) {
                var root = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(root)) continue;
                var path = Path.Combine(root, relativePath);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private static Task StartDesktopAppAsync(string executable, CancellationToken token) =>
        ShortcutWindowHelper.StartAsync(new ProcessStartInfo {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        }, token);

    private static async Task<bool> TryLaunchPackageAsync(string vendor, CancellationToken token)
    {
        string[] families = vendor switch {
            "amd" => ["AdvancedMicroDevicesInc-2.AMDRadeonSoftware_0a9344xs7nr4m"],
            "nvidia" => ["NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj"],
            "intel" => ["AppUp.IntelGraphicsExperience_8j3eq9eme6ctt", "AppUp.IntelGraphicsControlPanel_8j3eq9eme6ctt"],
            _ => []
        };

        var manager = new PackageManager();
        foreach (var family in families) {
            token.ThrowIfCancellationRequested();
            try {
                foreach (var package in manager.FindPackagesForUser(string.Empty, family)) {
                    var entries = await package.GetAppListEntriesAsync().AsTask(token);
                    foreach (var entry in entries) {
                        token.ThrowIfCancellationRequested();
                        if (await ShortcutWindowHelper.LaunchPackageAsync(entry, family, token)) return true;
                    }
                }
            }
            catch (COMException ex) {
                Debug.WriteLine($"GPU control app {family}: {ex.Message}");
            }
        }
        return false;
    }
}
