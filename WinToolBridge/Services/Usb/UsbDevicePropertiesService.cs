using System.Diagnostics;
using System.IO;

namespace WinToolBridge.Services.Usb;

public static class UsbDevicePropertiesService
{
    // The instance ID must come from the backend's current device enumeration.
    public static void Open(string instanceId, CancellationToken token, Action<ProcessStartInfo>? start = null)
    {
        var startInfo = new ProcessStartInfo {
            FileName = Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Combine(Environment.SystemDirectory, "devmgr.dll") + ",DeviceProperties_RunDLL");
        startInfo.ArgumentList.Add("/DeviceID");
        startInfo.ArgumentList.Add(instanceId);
        token.ThrowIfCancellationRequested();
        if (start != null) {
            start(startInfo);
            return;
        }
        using var process = Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Unable to open device properties.");
    }
}
