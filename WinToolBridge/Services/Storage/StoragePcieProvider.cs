using System.Runtime.InteropServices;
using System.Text;
using WinToolBridge.Services.Usb;

namespace WinToolBridge.Services.Storage;

/// <summary>Reads the NVMe endpoint's PCIe link properties, never an upstream port's link.</summary>
internal static class StoragePcieProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid format; public uint id; }
    private static readonly Guid PciProperties = new("3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62");

    internal static (StorageConnection connection, StorageAvailability availability) Read(
        uint? bus, UsbInterface device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var empty = new StorageConnection(null, null, null, null);
        if (bus != 17) return (empty, new("notSupported", null, "PCIe link queries apply to direct NVMe disks only."));
        var node = device.device.devInst;
        for (var depth = 0; depth < 16; depth++) {
            token.ThrowIfCancellationRequested();
            var id = new StringBuilder(512);
            if (CM_Get_Device_ID(node, id, id.Capacity, 0) != 0) break;
            if (id.ToString().StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) break;
            if (id.ToString().StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) {
                // Stop at the first PCI ancestor. A RAID/VMD controller or bridge
                // must not contribute a misleading link for the underlying disk.
                if (Property(node, 3, token) != 1 || Property(node, 4, token) != 8 || Property(node, 5, token) != 2)
                    return (empty, new("unavailable", null, "The disk could not be mapped to a PCI NVMe endpoint."));
                var connection = new StorageConnection(Generation(Property(node, 9, token)),
                    Lanes(Property(node, 10, token)), Generation(Property(node, 11, token)), Lanes(Property(node, 12, token)));
                var count = new[] { connection.currentPcieGeneration, connection.currentLaneCount,
                    connection.maximumPcieGeneration, connection.maximumLaneCount }.Count(value => value.HasValue);
                return (connection, new(count == 4 ? "available" : count > 0 ? "partial" : "unavailable", null,
                    count == 4 ? null : "Some PCIe link properties are unavailable or have an unknown encoding."));
            }
            if (CM_Get_Parent(out node, node, 0) != 0) break;
        }
        return (empty, new("unavailable", null, "A direct PCI NVMe endpoint was not found in the disk's ancestry."));
    }

    internal static int? Generation(uint? value) => value is >= 1 and <= 6 ? (int)value.Value : null;
    internal static int? Lanes(uint? value) => value is 1 or 2 or 4 or 8 or 12 or 16 or 32 ? (int)value.Value : null;

    private static uint? Property(uint node, uint id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = new PropertyKey { format = PciProperties, id = id };
        uint size = 4;
        var result = CM_Get_DevNode_Property(node, ref key, out var type, out var value, ref size, 0);
        token.ThrowIfCancellationRequested();
        // DEVPROP_TYPE_UINT32. Unknown/missing values remain null, including zero link values.
        return result == 0 && type == 7 && size == 4 ? value : null;
    }

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint node, ref PropertyKey key, out uint type,
        out uint buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern uint CM_Get_Device_ID(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
}
