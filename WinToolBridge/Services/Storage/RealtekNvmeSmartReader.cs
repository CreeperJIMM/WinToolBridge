using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinToolBridge.Services.Usb;

namespace WinToolBridge.Services.Storage;

public sealed record RealtekSmartResult(StorageIdentity? identity, StorageCapabilities capabilities,
    StorageHealth? health, StorageUsage? usage, StorageSmart? smart,
    StorageAvailability identifyAvailability, StorageAvailability smartAvailability);

/// <summary>Fixed data-in queries for known Realtek RTL9210/9211/9220 USB-NVMe bridges.</summary>
internal static class RealtekNvmeSmartReader
{
    internal static bool IsCandidate(UsbInterface device, CancellationToken token)
    {
        var node = device.device.devInst;
        for (var depth = 0; depth < 16; depth++) {
            token.ThrowIfCancellationRequested();
            var id = new StringBuilder(512);
            if (CM_Get_Device_ID(node, id, id.Capacity, 0) != 0) return false;
            if (id.ToString().StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
                return MatchesUsbId(id.ToString());
            if (CM_Get_Parent(out node, node, 0) != 0) return false;
        }
        return false;
    }

    internal static bool MatchesUsbId(string id)
    {
        var hardwareId = id.Split('\\');
        if (hardwareId.Length < 2 || !hardwareId[0].Equals("USB", StringComparison.OrdinalIgnoreCase)) return false;
        var fields = hardwareId[1].Split('&');
        // Known sntrealtek IDs from smartmontools/drivedb.h. These are query
        // candidates only: SATA and RAID modes may reject the NVMe reads.
        // Do not send mode-switch commands to make an unsupported mode work.
        if (fields.Contains("VID_0BDA", StringComparer.OrdinalIgnoreCase))
            return fields.Contains("PID_9210", StringComparer.OrdinalIgnoreCase) ||
                fields.Contains("PID_9211", StringComparer.OrdinalIgnoreCase) ||
                fields.Contains("PID_9220", StringComparer.OrdinalIgnoreCase);
        if (fields.Contains("VID_2EB9", StringComparer.OrdinalIgnoreCase))
            return fields.Contains("PID_9211", StringComparer.OrdinalIgnoreCase); // Sabrent RTL9211
        return fields.Contains("VID_1DE1", StringComparer.OrdinalIgnoreCase) &&
            fields.Contains("PID_E101", StringComparer.OrdinalIgnoreCase); // Actions AM8180 (RTL9210)
    }

    internal static RealtekSmartResult NeedsElevation() => Failure(new StorageAvailability("accessDenied", 5,
        "Administrator privileges are required to query this Realtek USB-NVMe bridge.", requiresElevation: true));

    internal static RealtekSmartResult Failure(StorageAvailability state) =>
        new(null, new(null, null, null), null, null, null, state, state);

    internal static Task<RealtekSmartResult> ReadAsync(string diskId, CancellationToken token)
    {
        PhysicalDiskService.ValidateId(diskId);
        return Task.Run(() => {
            var device = PhysicalDiskService.Resolve(diskId, token);
            if (!IsCandidate(device, token)) return Failure(new("notSupported", null, "Not a supported Realtek USB-NVMe bridge."));
            token.ThrowIfCancellationRequested();
            // SCSI pass-through requires a synchronous handle and read/write access,
            // even though every command below is data-in only.
            using var handle = CreateFile(device.path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return Failure(PhysicalDiskService.Failure(new Win32Exception(Marshal.GetLastWin32Error())));
            StorageIdentity? identity = null;
            var capabilities = new StorageCapabilities(null, null, null);
            StorageHealth? health = null;
            StorageUsage? usage = null;
            StorageSmart? smart = null;
            StorageAvailability identifyState, smartState;
            try {
                var value = NvmeData.Identify(Query(handle, true, token));
                identity = new(value.model, value.firmware, value.serial, "USB", null, value.version);
                capabilities = new(true, value.trim, value.cache);
                identifyState = new("available");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException) {
                identifyState = PhysicalDiskService.Failure(ex);
            }
            try {
                (health, usage, smart) = NvmeData.Health(Query(handle, false, token));
                capabilities = capabilities with { smartSupported = true };
                smartState = new("available");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException) {
                smartState = PhysicalDiskService.Failure(ex);
            }
            token.ThrowIfCancellationRequested();
            if (UsbDeviceInventory.Refresh(device.device, token) == null)
                return Failure(new("unavailable", null, "Disk was removed during the Realtek query."));
            return new RealtekSmartResult(identity, capabilities, health, usage, smart, identifyState, smartState);
        }, token);
    }

    private static byte[] Query(SafeFileHandle handle, bool identify, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var packet = Request(identify);
        var ok = DeviceIoControl(handle, 0x4D004, packet, packet.Length, packet, packet.Length, out var returned, IntPtr.Zero);
        var error = ok ? 0 : Marshal.GetLastWin32Error();
        token.ThrowIfCancellationRequested();
        if (!ok) throw new Win32Exception(error);
        return Payload(packet, returned, identify);
    }

    internal static byte[] Request(bool identify)
    {
        var header = Marshal.SizeOf<ScsiPassThrough>();
        if (header != (IntPtr.Size == 8 ? 56 : 44)) throw new InvalidDataException("Unexpected SCSI header layout.");
        var length = identify ? 4096 : 512;
        var cdb = new byte[16];
        cdb[0] = 0xE4;
        cdb[1] = (byte)(length & 255);
        cdb[2] = (byte)(length >> 8);
        cdb[3] = identify ? (byte)0x06 : (byte)0x02;
        cdb[4] = identify ? (byte)1 : (byte)0;
        var request = new ScsiPassThrough {
            length = (ushort)header, cdbLength = 16, senseLength = 32, dataIn = 1,
            transferLength = (uint)length, timeout = 2,
            dataOffset = (UIntPtr)(uint)(header + 36), senseOffset = (uint)(header + 4), cdb = cdb
        };
        var buffer = new byte[header + 36 + length];
        var native = Marshal.AllocHGlobal(header);
        try {
            Marshal.Copy(buffer, 0, native, header);
            Marshal.StructureToPtr(request, native, false);
            Marshal.Copy(native, buffer, 0, header);
        }
        finally { Marshal.FreeHGlobal(native); }
        return buffer;
    }

    internal static byte[] Payload(byte[] buffer, int returned, bool identify)
    {
        var header = Marshal.SizeOf<ScsiPassThrough>();
        var length = identify ? 4096 : 512;
        var offset = header + 36;
        if (returned < header || returned > buffer.Length || buffer[2] != 0)
            throw new InvalidDataException("Truncated response or unsuccessful SCSI status.");
        var lengthOffset = (int)Marshal.OffsetOf<ScsiPassThrough>(nameof(ScsiPassThrough.transferLength));
        var dataOffset = (int)Marshal.OffsetOf<ScsiPassThrough>(nameof(ScsiPassThrough.dataOffset));
        var actualOffset = IntPtr.Size == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(dataOffset))
            : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(dataOffset));
        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(lengthOffset)) != length ||
            actualOffset != (ulong)offset || returned < offset + length)
            throw new InvalidDataException("Invalid Realtek data offset or transfer length.");
        var payload = buffer.AsSpan(offset, length).ToArray();
        if (payload.All(value => value == 0)) throw new InvalidDataException("Empty Realtek payload.");
        return payload;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThrough
    {
        public ushort length;
        public byte status, path, target, lun, cdbLength, senseLength, dataIn;
        public uint transferLength, timeout;
        public UIntPtr dataOffset;
        public uint senseOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] cdb;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern uint CM_Get_Device_ID(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, [In] byte[] input, int inputLength,
        [Out] byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
