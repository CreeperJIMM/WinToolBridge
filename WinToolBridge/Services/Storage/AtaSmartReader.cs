using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinToolBridge.Services.Usb;

namespace WinToolBridge.Services.Storage;

/// <summary>Fixed, synchronous data-in ATA queries for directly attached ATA/SATA disks.</summary>
internal static class AtaSmartReader
{
    internal static bool IsCandidate(uint? bus) => bus is 3 or 11;
    internal static StorageProtocolResult NeedsElevation() => StorageProtocolResult.Failure(new("accessDenied", 5,
        "Administrator privileges are required for ATA SMART pass-through.", requiresElevation: true));

    internal static Task<StorageProtocolResult> ReadAsync(string diskId, CancellationToken token)
    {
        PhysicalDiskService.ValidateId(diskId);
        return Task.Run(() => {
            var device = PhysicalDiskService.Resolve(diskId, token);
            token.ThrowIfCancellationRequested();
            using var handle = CreateFile(device.path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) {
                var error = Marshal.GetLastWin32Error();
                return error == 5 && !ElevationService.IsElevated() ? NeedsElevation()
                    : StorageProtocolResult.Failure(PhysicalDiskService.Failure(new Win32Exception(error)));
            }
            // Recheck the enumerated device's bus before sending any ATA command.
            var descriptor = new byte[4096];
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0);
            if (!DeviceIoControl(handle, 0x2D1400, descriptor, 12, descriptor, descriptor.Length, out var returned, IntPtr.Zero))
                return StorageProtocolResult.Failure(PhysicalDiskService.Failure(new Win32Exception(Marshal.GetLastWin32Error())));
            token.ThrowIfCancellationRequested();
            if (returned < 36 || descriptor[8] != 0 || !IsCandidate(BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(28))))
                return StorageProtocolResult.Failure(new("notSupported", null, "ATA SMART is limited to direct ATA/SATA disks."));

            string? descriptorModel = null;
            var modelOffset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(16));
            if (returned <= descriptor.Length && modelOffset >= 36 && modelOffset < returned) {
                var end = Array.IndexOf(descriptor, (byte)0, (int)modelOffset, returned - (int)modelOffset);
                if (end >= 0) descriptorModel = NvmeData.Text(descriptor.AsSpan((int)modelOffset, end - (int)modelOffset));
            }
            StorageIdentity? identity = null;
            StorageConnection? connection = null;
            var capabilities = new StorageCapabilities(null, null, null);
            StorageAvailability identifyState;
            try {
                var value = AtaData.Identify(Query(handle, 0xEC, 0, true, token).data);
                identity = AtaData.Apply(new(null, null, null, BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(28)) == 3 ? "ATA" : "SATA", null, null), value);
                capabilities = value.capabilities;
                connection = new(null, null, null, null, currentSataSpeedGbps: value.currentSpeed, maximumSataSpeedGbps: value.maximumSpeed);
                identifyState = new("available");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException) { identifyState = PhysicalDiskService.Failure(ex); }
            StorageHealth? health = null;
            StorageUsage? usage = null;
            StorageSmart? smart = null;
            StorageAvailability smartState;
            if (capabilities.smartSupported == false || capabilities.smartEnabled == false) {
                smartState = new("notSupported", null, capabilities.smartSupported == false
                    ? "ATA Identify reports no SMART support." : "SMART is disabled; the bridge does not enable it.");
            }
            else try {
                var data = Query(handle, 0xB0, 0xD0, true, token).data;
                if (!AtaData.ValidSmart(data)) throw new InvalidDataException("Invalid ATA SMART data or checksum.");
                byte[]? thresholds = null;
                string? partialReason = null;
                try {
                    thresholds = Query(handle, 0xB0, 0xD1, true, token).data;
                    if (!AtaData.ValidSmart(thresholds)) throw new InvalidDataException("Invalid ATA SMART thresholds.");
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidDataException) {
                    thresholds = null; partialReason = "SMART attributes were read, but thresholds are unavailable.";
                }
                bool? deviceHealthy = null;
                try {
                    var taskFile = Query(handle, 0xB0, 0xDA, false, token).taskFile;
                    deviceHealthy = (taskFile[3], taskFile[4]) switch { (0x4F, 0xC2) => true, (0xF4, 0x2C) => false, _ => null };
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidDataException) { }
                if (deviceHealthy == null) partialReason ??= "SMART attributes were read, but the device health status is unavailable.";
                (health, usage, smart) = AtaData.Health(data, thresholds, identity?.model ?? descriptorModel, deviceHealthy);
                capabilities = capabilities with { smartSupported = true };
                smartState = new(partialReason == null ? "available" : "partial", null, partialReason);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException) { smartState = PhysicalDiskService.Failure(ex); }
            token.ThrowIfCancellationRequested();
            if (UsbDeviceInventory.Refresh(device.device, token) == null)
                return StorageProtocolResult.Failure(new("unavailable", null, "Disk was removed during the ATA query."));
            return new StorageProtocolResult(identity, capabilities, health, usage, smart, identifyState, smartState, connection);
        }, token);
    }

    internal static byte[] Request(byte command, byte feature, bool dataIn)
    {
        if (!((command == 0xEC && feature == 0 && dataIn) ||
            (command == 0xB0 && ((feature is 0xD0 or 0xD1 && dataIn) || (feature == 0xDA && !dataIn)))))
            throw new ArgumentException("Unsupported ATA query.");
        var header = Marshal.SizeOf<AtaPassThrough>();
        if (header != (IntPtr.Size == 8 ? 48 : 40)) throw new InvalidDataException("Unexpected ATA header layout.");
        var registers = new byte[8];
        registers[0] = feature; registers[1] = dataIn ? (byte)1 : (byte)0;
        registers[5] = 0xA0; registers[6] = command;
        if (command == 0xB0) { registers[3] = 0x4F; registers[4] = 0xC2; }
        var request = new AtaPassThrough {
            length = (ushort)header, flags = (ushort)(dataIn ? 3 : 1), transferLength = dataIn ? 512u : 0,
            timeout = 3, dataOffset = dataIn ? (UIntPtr)(uint)header : UIntPtr.Zero,
            previousTaskFile = new byte[8], currentTaskFile = registers
        };
        var buffer = new byte[header + (dataIn ? 512 : 0)];
        var native = Marshal.AllocHGlobal(header);
        try {
            Marshal.Copy(buffer, 0, native, header);
            Marshal.StructureToPtr(request, native, false);
            Marshal.Copy(native, buffer, 0, header);
        }
        finally { Marshal.FreeHGlobal(native); }
        return buffer;
    }

    internal static (byte[] data, byte[] taskFile) Payload(byte[] buffer, int returned, bool dataIn)
    {
        var header = Marshal.SizeOf<AtaPassThrough>();
        if (returned < header || returned > buffer.Length || BinaryPrimitives.ReadUInt16LittleEndian(buffer) != header)
            throw new InvalidDataException("Invalid ATA response header.");
        var taskOffset = (int)Marshal.OffsetOf<AtaPassThrough>(nameof(AtaPassThrough.currentTaskFile));
        var registers = buffer.AsSpan(taskOffset, 8).ToArray();
        if ((registers[6] & 0xA9) != 0) throw new InvalidDataException("ATA device returned an error or incomplete status.");
        if (!dataIn) return ([], registers);
        var offsetField = (int)Marshal.OffsetOf<AtaPassThrough>(nameof(AtaPassThrough.dataOffset));
        var offset = IntPtr.Size == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offsetField))
            : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offsetField));
        if (offset != (ulong)header || BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)) != 512 || returned < header + 512)
            throw new InvalidDataException("Invalid ATA data offset or transfer length.");
        return (buffer.AsSpan(header, 512).ToArray(), registers);
    }

    private static (byte[] data, byte[] taskFile) Query(SafeFileHandle handle, byte command, byte feature, bool dataIn, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var buffer = Request(command, feature, dataIn);
        var header = Marshal.SizeOf<AtaPassThrough>();
        var ok = DeviceIoControl(handle, 0x4D02C, buffer, header, buffer, buffer.Length, out var returned, IntPtr.Zero);
        var error = ok ? 0 : Marshal.GetLastWin32Error();
        token.ThrowIfCancellationRequested();
        if (!ok) throw new Win32Exception(error);
        return Payload(buffer, returned, dataIn);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AtaPassThrough
    {
        public ushort length, flags;
        public byte path, target, lun, reservedByte;
        public uint transferLength, timeout, reserved;
        public UIntPtr dataOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] previousTaskFile;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] currentTaskFile;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, [In] byte[] input, int inputLength,
        [Out] byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
