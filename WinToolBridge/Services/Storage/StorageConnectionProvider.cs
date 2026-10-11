using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WinToolBridge.Services.Usb;

namespace WinToolBridge.Services.Storage;

internal static class StorageConnectionProvider
{
    internal static string? GetInterfaceName(uint? bus, UsbInterface device, bool nvme, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bus == 17) return "NVM Express";
        if (bus != 7) return null;
        var uasp = false;
        var node = device.device.devInst;
        // Inspect only this disk's PnP ancestry, not unrelated USB devices.
        for (var depth = 0; depth < 16; depth++) {
            token.ThrowIfCancellationRequested();
            var id = new StringBuilder(512);
            if (CM_Get_Device_ID(node, id, id.Capacity, 0) != 0) break;
            try {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id);
                if (string.Equals(key?.GetValue("Service") as string, "UASPStor", StringComparison.OrdinalIgnoreCase)) {
                    uasp = true;
                    break;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            if (id.ToString().StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) break;
            if (CM_Get_Parent(out node, node, 0) != 0) break;
        }
        token.ThrowIfCancellationRequested();
        var transport = uasp ? "UASP" : "USB";
        return nvme ? $"{transport} (NVM Express)" : transport;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern uint CM_Get_Device_ID(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
}
