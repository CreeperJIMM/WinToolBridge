using System.ComponentModel;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WinToolBridge.Services.Usb;

internal sealed record UsbPnpDevice(string name, string? driverKey, UsbWindowsInfo windows, uint devInst, uint? parentDevInst, Guid? containerId = null);
internal sealed record UsbInterface(string path, UsbPnpDevice device);

internal static class UsbDeviceInventory
{
    internal static readonly Guid ControllerClass = new("3ABF6F2D-71C4-462A-8A92-1E6861E6AF27");
    internal static readonly Guid HubClass = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");
    internal static readonly Guid DeviceClass = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint size; public Guid classGuid; public uint devInst; public IntPtr reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceInfo { public uint size; public Guid classGuid; public uint flags; public IntPtr reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid format; public uint id; }

    private sealed class DeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DeviceInfoSet() : base(true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern DeviceInfoSet SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(DeviceInfoSet set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern DeviceInfoSet SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parent);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfo(DeviceInfoSet set, string instanceId, IntPtr parent, uint flags, ref DeviceInfo info);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(DeviceInfoSet set, IntPtr info, ref Guid guid, uint index, ref InterfaceInfo data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(DeviceInfoSet set, ref InterfaceInfo data, IntPtr detail, uint size, out uint required, ref DeviceInfo info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(DeviceInfoSet set, ref DeviceInfo info, uint property, out uint type, byte[]? buffer, uint size, out uint required);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceProperty(DeviceInfoSet set, ref DeviceInfo info, ref PropertyKey key,
        out uint type, byte[]? buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(DeviceInfoSet set, ref DeviceInfo info, StringBuilder id, uint size, out uint required);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNode(out uint devInst, string instanceId, uint flags);

    private static DeviceInfoSet Open(Guid? guid)
    {
        var pointer = IntPtr.Zero;
        try {
            if (guid.HasValue) {
                pointer = Marshal.AllocHGlobal(16);
                Marshal.StructureToPtr(guid.Value, pointer, false);
            }
            var set = SetupDiGetClassDevs(pointer, null, IntPtr.Zero, guid.HasValue ? 0x12u : 0x6u);
            if (set.IsInvalid) {
                var error = Marshal.GetLastWin32Error();
                set.Dispose();
                throw new Win32Exception(error, "USB device enumeration failed.");
            }
            return set;
        }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer); }
    }

    public static List<UsbInterface> GetInterfaces(Guid guid, CancellationToken token)
    {
        using var set = Open(guid);
        var result = new List<UsbInterface>();
        for (uint index = 0; ; index++) {
            token.ThrowIfCancellationRequested();
            var data = new InterfaceInfo { size = (uint)Marshal.SizeOf<InterfaceInfo>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) break;
                throw new Win32Exception(error, "USB interface enumeration failed.");
            }
            var info = new DeviceInfo { size = (uint)Marshal.SizeOf<DeviceInfo>() };
            SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, ref info);
            if (required < 6 || required > 65536) continue;
            var buffer = Marshal.AllocHGlobal((int)required);
            try {
                // Unicode SP_DEVICE_INTERFACE_DETAIL_DATA has cbSize 8 on x64/ARM64, 6 on x86.
                // DevicePath starts at byte 4 on all architectures.
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, ref info)) continue;
                var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4), ((int)required - 4) / 2)?.Split('\0')[0];
                if (!string.IsNullOrEmpty(path)) result.Add(new(path, ReadDevice(set, ref info)));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }

    public static List<UsbPnpDevice> GetPresentDevices(CancellationToken token)
    {
        using var set = Open(null);
        var result = new List<UsbPnpDevice>();
        for (uint index = 0; ; index++) {
            token.ThrowIfCancellationRequested();
            var info = new DeviceInfo { size = (uint)Marshal.SizeOf<DeviceInfo>() };
            if (!SetupDiEnumDeviceInfo(set, index, ref info)) {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) break;
                throw new Win32Exception(error, "USB PnP enumeration failed.");
            }
            result.Add(ReadDevice(set, ref info));
        }
        return result;
    }

    private static string? Property(DeviceInfoSet set, ref DeviceInfo info, uint property)
    {
        SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, null, 0, out var required);
        if (required == 0 || required > 65536) return null;
        var bytes = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out var type, bytes, required, out var length) ||
            type is not (1 or 7) || length > bytes.Length || (length & 1) != 0) return null;
        return Encoding.Unicode.GetString(bytes, 0, (int)length).TrimEnd('\0');
    }

    private static UsbPnpDevice ReadDevice(DeviceInfoSet set, ref DeviceInfo info)
    {
        var description = Property(set, ref info, 0);
        // Bus-reported description is already stored by PnP; do not query every device's
        // string descriptors just to replace the driver's generic composite-device label.
        var friendlyName = Property(set, ref info, 12);
        var busDescription = BusReportedDescription(set, ref info);
        var service = Property(set, ref info, 4);
        var id = new StringBuilder(512);
        if (!SetupDiGetDeviceInstanceId(set, ref info, id, (uint)id.Capacity, out _)) id.Clear();
        var name = DisplayName(friendlyName, description, busDescription, service, id.ToString());
        var driverKey = Property(set, ref info, 9);
        string? provider = null, version = null, date = null, inf = null;
        if (driverKey != null) {
            try {
                using var driver = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
                provider = driver?.GetValue("ProviderName") as string;
                version = driver?.GetValue("DriverVersion") as string;
                date = driver?.GetValue("DriverDate") as string;
                inf = driver?.GetValue("InfPath") as string;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        var hasStatus = CM_Get_DevNode_Status(out var status, out var problem, info.devInst, 0) == 0;
        var windows = new UsbWindowsInfo(id.ToString(), description, Property(set, ref info, 11),
            Property(set, ref info, 7), service, Property(set, ref info, 13),
            Property(set, ref info, 1)?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [],
            hasStatus ? status : null, hasStatus ? problem : null, provider, version, date, inf, PowerData(set, ref info), busDescription);
        var hasParent = CM_Get_Parent(out var parent, info.devInst, 0) == 0;
        return new(name, driverKey, windows, info.devInst, hasParent ? parent : null, ContainerId(set, ref info));
    }

    private static string DisplayName(string? friendlyName, string? description, string? busDescription, string? service, string instanceId)
    {
        if (!string.IsNullOrWhiteSpace(friendlyName)) return friendlyName;
        description = string.IsNullOrWhiteSpace(description) ? null : description;
        busDescription = string.IsNullOrWhiteSpace(busDescription) ? null : busDescription;
        // Generic composite/storage USB parents retain the product name. Functions
        // (including Bluetooth interfaces) prefer the driver's device description.
        var genericUsbParent = instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) &&
            !instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(service, "usbccgp", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(service, "USBSTOR", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(service, "UASPStor", StringComparison.OrdinalIgnoreCase));
        return (genericUsbParent ? busDescription : description) ?? description ?? busDescription ?? "USB device";
    }

    internal static UsbPnpDevice? Refresh(UsbPnpDevice cached, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new DeviceInfo { size = (uint)Marshal.SizeOf<DeviceInfo>() };
        // A recreated devnode needs a new snapshot, including its parent mappings.
        if (CM_Locate_DevNode(out var present, cached.windows.instanceId, 0) != 0 || present != cached.devInst ||
            !SetupDiOpenDeviceInfo(set, cached.windows.instanceId, IntPtr.Zero, 0, ref info) || info.devInst != present ||
            CM_Get_DevNode_Status(out var status, out var problem, info.devInst, 0) != 0) return null;
        token.ThrowIfCancellationRequested();
        var hasParent = CM_Get_Parent(out var parent, info.devInst, 0) == 0;
        return cached with { devInst = info.devInst, parentDevInst = hasParent ? parent : null,
            windows = cached.windows with { statusFlags = status, problemCode = problem, power = PowerData(set, ref info) } };
    }

    private static Guid? ContainerId(DeviceInfoSet set, ref DeviceInfo info)
    {
        var key = new PropertyKey { format = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), id = 2 };
        var bytes = new byte[16];
        if (!SetupDiGetDeviceProperty(set, ref info, ref key, out var type, bytes, 16, out var length, 0) ||
            type != 0x0D || length != 16) return null; // DEVPROP_TYPE_GUID
        var id = new Guid(bytes);
        return id == Guid.Empty ? null : id;
    }

    private static string? BusReportedDescription(DeviceInfoSet set, ref DeviceInfo info)
    {
        var key = new PropertyKey { format = new("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), id = 4 };
        SetupDiGetDeviceProperty(set, ref info, ref key, out _, null, 0, out var required, 0);
        if (required < 2 || required > 65536 || (required & 1) != 0) return null;
        var bytes = new byte[required];
        if (!SetupDiGetDeviceProperty(set, ref info, ref key, out var type, bytes, required, out var length, 0) ||
            type != 0x12 || length > bytes.Length || (length & 1) != 0) return null; // DEVPROP_TYPE_STRING
        var value = Encoding.Unicode.GetString(bytes, 0, (int)length).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static UsbPowerInfo? PowerData(DeviceInfoSet set, ref DeviceInfo info)
    {
        // SPDRP_DEVICE_POWER_DATA is CM_POWER_DATA: 56 bytes, DWORD fields on all targets.
        var bytes = new byte[56];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, 30, out var type, bytes, 56, out var length) ||
            type != 3 || length != 56) return null;
        return DecodePowerData(bytes);
    }

    private static UsbPowerInfo? DecodePowerData(byte[] bytes)
    {
        if (bytes.Length < 56 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 56) return null;
        var state = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var capabilities = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        var supported = new List<string>();
        var wake = new List<string>();
        for (var index = 0; index < 4; index++) {
            if ((capabilities & (1u << index)) != 0) supported.Add($"D{index}");
            if ((capabilities & (0x10u << index)) != 0) wake.Add($"D{index}");
        }
        return new(state is >= 1 and <= 4 ? $"D{state - 1}" : null, supported.ToArray(), wake.ToArray());
    }
}
