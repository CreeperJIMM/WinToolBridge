using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinToolBridge.Services.Usb;

namespace WinToolBridge.Services.Storage;

public sealed class PhysicalDiskService(ImageAssetService? images = null, string? assetBaseUrl = null,
    ElevationService? elevation = null, UsbTreeService? usb = null)
{
    private static readonly Guid DiskClass = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");
    private const uint StorageQueryProperty = 0x2D1400;
    private sealed record DiskData(UsbInterface device, StorageIdentity identity, uint? number,
        uint? bus, Dictionary<string, StorageAvailability> availability);

    public Task<StorageDisksResult> GetDisksAsync(CancellationToken token) => Task.Run(() => {
        var disks = new List<DiskData>();
        // This existing SetupAPI enumerator accepts any device-interface class, including disks.
        foreach (var device in UsbDeviceInventory.GetInterfaces(DiskClass, token)) {
            token.ThrowIfCancellationRequested();
            var disk = ReadDisk(device, token);
            if (disk == null) continue;
            var smart = ReadSmartAvailability(disk.bus, device.path, token);
            if (disk.bus == 7 && RealtekNvmeSmartReader.IsCandidate(device, token)) {
                smart = elevation?.IsMainProcessElevated == true || elevation?.IsHelperElevated == true ||
                    (elevation == null && ElevationService.IsElevated())
                    ? new("unavailable", null, "Realtek NVMe SMART is read when disk details are requested.")
                    : RealtekNvmeSmartReader.NeedsElevation().smartAvailability;
            }
            // Exclude only an explicit unsupported query result, not unimplemented protocols or access errors.
            if (smart.status == "notSupported") continue;
            disk.availability["smart"] = smart;
            disks.Add(disk);
        }
        var volumes = disks.Count == 0 ? (paths: new Dictionary<uint, string[]>(), availability: new StorageAvailability("available"))
            : StorageVolumeMap.Read(token);
        var windowsMountPoint = StorageVolumeMap.WindowsMountPoint(token);
        token.ThrowIfCancellationRequested();
        return new StorageDisksResult(disks.Select(disk => {
            disk.availability["mountPoints"] = disk.number.HasValue ? volumes.availability : new("unavailable", null, "Disk number is unavailable.");
            var mounts = Mounts(disk.number, volumes.paths, volumes.availability);
            var iconUrl = images != null && assetBaseUrl != null
                ? StorageDriveIconProvider.GetUrl(StorageDriveIconProvider.SelectRoot(mounts, windowsMountPoint), images, assetBaseUrl, token) : null;
            return new StorageDisk(Id(disk.device), disk.identity.model, disk.identity.busType, disk.identity.sizeBytes,
                mounts, disk.availability, iconUrl, StorageVolumeMap.IsSystemDisk(mounts, windowsMountPoint, volumes.availability));
        }).ToArray(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }, token);

    public Task<StorageDiskDetailsResult> GetDiskDetailsAsync(string diskId, CancellationToken token)
    {
        ValidateId(diskId);
        return Task.Run(async () => {
            // Resolve only against currently enumerated devices; never construct a path from client input.
            var device = Resolve(diskId, token);
            var disk = ReadDisk(device, token) ?? throw new InvalidOperationException("Device is not a physical disk.");
            var availability = disk.availability;
            var identity = disk.identity;
            var capabilities = new StorageCapabilities(null, null, null);
            StorageHealth? health = null;
            StorageUsage? usage = null;
            StorageSmart? smart = null;
            token.ThrowIfCancellationRequested();
            using (var handle = DeviceIoControlQuery.Open(device.path)) {
                if (handle.IsInvalid) {
                    var failure = Failure(new Win32Exception(Marshal.GetLastWin32Error()));
                    availability["identify"] = availability["smart"] = availability["trim"] = failure;
                }
                else {
                    try {
                        var trim = Property(handle, 8, token); // StorageDeviceTrimProperty
                        if (trim.Length < 9) throw new InvalidDataException("Truncated TRIM descriptor.");
                        capabilities = capabilities with { trimEnabled = trim[8] != 0 };
                        availability["trim"] = new("available");
                    }
                    catch (Exception ex) when (IsQueryFailure(ex)) { availability["trim"] = Failure(ex); }
                    if (disk.bus == 17) {
                        try {
                            var data = NvmeData.Identify(Protocol(handle, 1, 1, 4096, token));
                            identity = identity with { model = data.model ?? identity.model, firmware = data.firmware ?? identity.firmware,
                                serialNumber = data.serial ?? identity.serialNumber, nvmeVersion = data.version };
                            capabilities = capabilities with { smartSupported = true, volatileWriteCacheSupported = data.cache };
                            availability["identify"] = new("available");
                        }
                        catch (Exception ex) when (IsQueryFailure(ex)) { availability["identify"] = Failure(ex); }
                        try {
                            (health, usage, smart) = NvmeData.Health(Protocol(handle, 2, 2, 512, token));
                            capabilities = capabilities with { smartSupported = true };
                            availability["smart"] = new("available");
                        }
                        catch (Exception ex) when (IsQueryFailure(ex)) { availability["smart"] = Failure(ex); }
                    }
                    else {
                        availability["identify"] = disk.bus == null
                            ? new("unavailable", null, "The device protocol could not be determined.")
                            : new("notSupported", null,
                                "This version queries NVMe protocol data only; ATA, USB bridge and RAID pass-through are not implemented.");
                        availability["smart"] = ReadSmartAvailability(disk.bus, device.path, token);
                    }
                }
            }
            if (disk.bus == 7 && RealtekNvmeSmartReader.IsCandidate(device, token)) {
                var result = elevation != null ? await elevation.ReadRealtekSmartAsync(diskId, token)
                    : ElevationService.IsElevated() ? await RealtekNvmeSmartReader.ReadAsync(diskId, token)
                    : RealtekNvmeSmartReader.NeedsElevation();
                if (result.identity is { } identified) {
                    identity = identity with { model = identified.model ?? identity.model,
                        firmware = identified.firmware ?? identity.firmware, serialNumber = identified.serialNumber ?? identity.serialNumber,
                        nvmeVersion = identified.nvmeVersion };
                }
                capabilities = capabilities with { smartSupported = result.capabilities.smartSupported,
                    trimEnabled = capabilities.trimEnabled ?? result.capabilities.trimEnabled,
                    volatileWriteCacheSupported = result.capabilities.volatileWriteCacheSupported };
                (health, usage, smart) = (result.health, result.usage, result.smart);
                availability["identify"] = result.identifyAvailability;
                availability["smart"] = result.smartAvailability;
            }
            var volumes = StorageVolumeMap.Read(token);
            availability["mountPoints"] = disk.number.HasValue ? volumes.availability : new("unavailable", null, "Disk number is unavailable.");
            var pcie = StoragePcieProvider.Read(disk.bus, device, token);
            availability["connection"] = pcie.availability;
            StorageUsbConnection? usbConnection = null;
            if (disk.bus == 7) {
                try {
                    usbConnection = usb != null ? await usb.GetDiskConnectionAsync(device.device.windows.instanceId, token) : null;
                    var complete = usbConnection?.speed != null && (usbConnection.speed != "SuperSpeedPlus" ||
                        (usbConnection.rxSpeedMbps.HasValue && usbConnection.txSpeedMbps.HasValue)) &&
                        usbConnection.speedCapabilities != null;
                    availability["usbConnection"] = new(complete ? "available" : usbConnection != null ? "partial" : "unavailable",
                        null, complete ? null : "Some USB connection or device capability data is unavailable.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or System.ComponentModel.Win32Exception or IOException) {
                    availability["usbConnection"] = new("unavailable", null, "The USB connection could not be queried.");
                }
            }
            // Removal during a query must not return an apparently current snapshot of the old device.
            if (UsbDeviceInventory.Refresh(device.device, token) == null)
                throw new InvalidOperationException("Disk was removed while its details were being read.");
            token.ThrowIfCancellationRequested();
            return new StorageDiskDetailsResult(new(diskId, identity, Mounts(disk.number, volumes.paths, volumes.availability),
                pcie.connection with { interfaceName = StorageConnectionProvider.GetInterfaceName(disk.bus, device,
                    smart?.protocol == "NVMe" || identity.nvmeVersion != null, token), usb = usbConnection },
                capabilities, health, usage, smart, availability), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ElevationIconProvider.GetUrl(images, assetBaseUrl, token));
        }, token);
    }

    internal static StorageAvailability ReadSmartAvailability(uint? bus, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bus == null) return new("unavailable", null, "The device protocol could not be determined.");
        if (bus != 17) return new("unavailable", null,
            "SMART support is unknown: ATA, USB bridge and RAID pass-through queries are not implemented.");
        using var handle = DeviceIoControlQuery.Open(path);
        if (handle.IsInvalid) return Failure(new Win32Exception(Marshal.GetLastWin32Error()));
        try {
            _ = NvmeData.Health(Protocol(handle, 2, 2, 512, token));
            return new("available");
        }
        catch (Exception ex) when (IsQueryFailure(ex)) { return Failure(ex); }
    }

    internal static void ValidateId(string? id)
    {
        if (id == null || id.Length != 69 || !id.StartsWith("disk-", StringComparison.Ordinal) ||
            id.Skip(5).Any(c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw new ArgumentException("Invalid diskId.");
    }

    internal static UsbInterface Resolve(string diskId, CancellationToken token)
    {
        ValidateId(diskId);
        token.ThrowIfCancellationRequested();
        return UsbDeviceInventory.GetInterfaces(DiskClass, token).FirstOrDefault(device => Id(device) == diskId)
            ?? throw new InvalidOperationException("Disk is no longer available. Refresh the disk list.");
    }

    private static string Id(UsbInterface device) => "disk-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(device.path.ToUpperInvariant() + "\0" + device.device.windows.instanceId.ToUpperInvariant())));

    private static string[]? Mounts(uint? number, Dictionary<uint, string[]> paths, StorageAvailability state) =>
        number.HasValue && paths.TryGetValue(number.Value, out var mounts) ? mounts :
        number.HasValue && state.status == "available" ? [] : null;

    private static DiskData? ReadDisk(UsbInterface device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var states = new Dictionary<string, StorageAvailability>();
        string? model = device.device.name, firmware = null, serial = null;
        uint? bus = null, number = null;
        long? size = null;
        using var handle = DeviceIoControlQuery.Open(device.path);
        if (handle.IsInvalid) {
            var failure = Failure(new Win32Exception(Marshal.GetLastWin32Error()));
            states["identity"] = states["size"] = states["diskNumber"] = failure;
        }
        else {
            try {
                var bytes = Property(handle, 0, token); // StorageDeviceProperty
                if (bytes.Length < 36) throw new InvalidDataException("Truncated storage device descriptor.");
                bus = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28));
                // Direct-access disks only. Exclude virtual disks and Storage Spaces logical devices by bus type.
                if (bytes[8] != 0 || bus is 14 or 15 or 16) return null;
                model = DescriptorString(bytes, 16) ?? model;
                firmware = DescriptorString(bytes, 20);
                serial = DescriptorString(bytes, 24);
                states["identity"] = new("available");
            }
            catch (Exception ex) when (IsQueryFailure(ex)) { states["identity"] = Failure(ex); }
            try {
                var data = Query(handle, 0x2D1080, new byte[12], 0, token); // IOCTL_STORAGE_GET_DEVICE_NUMBER
                if (data.Length < 12) throw new InvalidDataException("Truncated device number.");
                number = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 7 || number == uint.MaxValue)
                    throw new InvalidDataException("Invalid disk device number.");
                states["diskNumber"] = new("available");
            }
            catch (Exception ex) when (IsQueryFailure(ex)) { states["diskNumber"] = Failure(ex); }
            try {
                var data = Query(handle, 0x700A0, new byte[256], 0, token); // IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, no read/write access required
                if (data.Length < 32) throw new InvalidDataException("Truncated disk geometry.");
                var value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(24));
                if (value < 0 || value > 9007199254740991) throw new InvalidDataException("Disk size cannot be represented safely.");
                size = value;
                states["size"] = new("available");
            }
            catch (Exception ex) when (IsQueryFailure(ex)) { states["size"] = Failure(ex); }
        }
        return new(device, new(model, firmware, serial, BusName(bus), size, null), number, bus, states);
    }

    private static string? BusName(uint? bus) => bus switch {
        null => null, 0 => "Unknown", 1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE1394", 5 => "SSA",
        6 => "FibreChannel", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA",
        12 => "SD", 13 => "MMC", 17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => "Unknown"
    };

    private static string? DescriptorString(byte[] data, int field)
    {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(field));
        if (offset == 0) return null;
        if (offset < 36 || offset >= data.Length) throw new InvalidDataException("Invalid storage string offset.");
        var tail = data.AsSpan((int)offset);
        var end = tail.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("Unterminated storage string.");
        return NvmeData.Text(tail[..end]);
    }

    private static byte[] Property(SafeFileHandle handle, uint property, CancellationToken token)
    {
        // First retrieve the header, then allocate the size supplied by the driver (bounded).
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, property);
        var result = Query(handle, StorageQueryProperty, header, 12, token);
        if (result.Length < 8) throw new InvalidDataException("Truncated storage descriptor header.");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4));
        if (size < 8 || size > 65536) throw new InvalidDataException("Invalid storage descriptor size.");
        var buffer = new byte[Math.Max(12, (int)size)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, property);
        result = Query(handle, StorageQueryProperty, buffer, 12, token);
        if (result.Length < size || result.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4)) != size)
            throw new InvalidDataException("Incomplete storage descriptor.");
        return result.AsSpan(0, (int)size).ToArray();
    }

    private static byte[] Protocol(SafeFileHandle handle, uint dataType, uint request, int length, CancellationToken token)
    {
        var buffer = new byte[48 + length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 50); // StorageDeviceProtocolSpecificProperty
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 3); // ProtocolTypeNvme
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), dataType);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), request);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(28), (uint)length);
        var result = Query(handle, StorageQueryProperty, buffer, buffer.Length, token);
        return NvmeData.ProtocolPayload(result, result.Length, length, dataType);
    }

    private static byte[] Query(SafeFileHandle handle, uint code, byte[] buffer, int inputLength, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var ok = DeviceIoControlQuery.Query(handle, code, buffer, out var returned, out var error, inputLength);
        token.ThrowIfCancellationRequested();
        if (!ok) throw new Win32Exception(error);
        return buffer.AsSpan(0, returned).ToArray();
    }

    private static bool IsQueryFailure(Exception ex) => ex is Win32Exception or InvalidDataException;
    internal static StorageAvailability Failure(Exception ex) => ex is Win32Exception native
        ? new(native.NativeErrorCode switch {
            5 => "accessDenied", 1 or 50 => "notSupported", 2 or 3 or 21 or 55 or 433 or 1167 => "unavailable", _ => "error"
        }, native.NativeErrorCode, native.Message)
        : new("error", null, ex.Message);
}
