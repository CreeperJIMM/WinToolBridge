using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace WinToolBridge.Services.Usb;

internal static class UsbStorageInfoProvider
{
    private static readonly Guid DiskClass = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");
    private static readonly Guid VolumeClass = new("53F5630D-B6BF-11D0-94F2-00A0C91EFB8B");
    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, uint length);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClusterSize(string name, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
    [DllImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolume(StringBuilder name, uint length);
    [DllImport("kernel32.dll", EntryPoint = "FindNextVolumeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolume(IntPtr search, StringBuilder name, uint length);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr search);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePaths(string name, [Out] char[] paths, uint length, out uint required);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string name, StringBuilder label, uint labelLength,
        out uint serial, out uint maximumComponentLength, out uint flags, StringBuilder fileSystem, uint fileSystemLength);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpace(string name, out ulong available, out ulong total, out ulong free);

    internal static UsbDiskInfo[] Read(UsbPnpDevice selected, List<UsbPnpDevice> devices, CancellationToken token, List<string> warnings)
    {
        var parents = devices.ToDictionary(device => device.devInst, device => device.parentDevInst);
        bool Descendant(uint id) {
            var seen = new HashSet<uint>();
            while (seen.Add(id)) {
                if (id == selected.devInst) return true;
                if (!parents.TryGetValue(id, out var parent) || parent == null) break;
                id = parent.Value;
            }
            return false;
        }
        var disks = new List<UsbDiskInfo>();
        foreach (var disk in UsbDeviceInventory.GetInterfaces(DiskClass, token).Where(disk => Descendant(disk.device.devInst))) {
            token.ThrowIfCancellationRequested();
            using var handle = DeviceIoControlQuery.Open(disk.path);
            int? number = null;
            long? size = null;
            bool? writable = null;
            if (!handle.IsInvalid) {
                var buffer = new byte[256];
                if (DeviceIoControlQuery.Query(handle, 0x2D1080, buffer, out var count, out _, 0) && count >= 12) {
                    var value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
                    if (value <= int.MaxValue) number = (int)value;
                }
                token.ThrowIfCancellationRequested();
                if (DeviceIoControlQuery.Query(handle, 0x700A0, buffer, out count, out _, 0) && count >= 32)
                    size = Bytes(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24)));
                token.ThrowIfCancellationRequested();
                if (DeviceIoControlQuery.Query(handle, 0x70024, [], out _, out var error, 0)) writable = true;
                else if (error == 19) writable = false; // ERROR_WRITE_PROTECT
            }
            if (number == null || size == null || writable == null) warnings.Add($"Some disk properties are unavailable: {disk.device.windows.instanceId}");
            disks.Add(new(disk.device.name, disk.device.windows.instanceId, disk.path, number, size, writable, null));
        }
        if (disks.Count == 0) return [];
        var volumes = ReadVolumes(disks.Where(disk => disk.diskNumber.HasValue).Select(disk => disk.diskNumber!.Value).ToHashSet(), token, warnings);
        return disks.Select(disk => disk with { volumes = disk.diskNumber.HasValue && volumes != null
            ? volumes.Where(volume => volume.disks.Contains(disk.diskNumber.Value)).Select(volume => volume.info).ToArray() : null }).ToArray();
    }

    private static long? Bytes(long value) => value is >= 0 and <= 9007199254740991 ? value : null;

    private static string? KernelName(string path)
    {
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal)) return null;
        var target = new StringBuilder(32768);
        return QueryDosDevice(path[4..].TrimEnd('\\'), target, (uint)target.Capacity) != 0 ? target.ToString() : null;
    }

    private static int[]? ExtentDisks(byte[] buffer, int returned)
    {
        if (returned < 8 || returned > buffer.Length) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        // DISK_EXTENT has 8-byte alignment, including on Windows x86.
        if (count > (returned - 8) / 24) return null;
        var disks = new int[count];
        for (var i = 0; i < disks.Length; i++) {
            var disk = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8 + i * 24));
            if (disk > int.MaxValue) return null;
            disks[i] = (int)disk;
        }
        return disks;
    }

    private static List<(int[] disks, UsbVolumeInfo info)>? ReadVolumes(HashSet<int> targets, CancellationToken token, List<string> warnings)
    {
        if (targets.Count == 0) return null;
        var volumeDevices = new Dictionary<string, UsbInterface>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in UsbDeviceInventory.GetInterfaces(VolumeClass, token)) {
            token.ThrowIfCancellationRequested();
            var kernelName = KernelName(device.path);
            if (kernelName != null) volumeDevices.TryAdd(kernelName, device);
        }
        var name = new StringBuilder(1024);
        var search = FindFirstVolume(name, (uint)name.Capacity);
        if (search == new IntPtr(-1)) {
            warnings.Add("Volume enumeration is unavailable.");
            return null;
        }
        var result = new List<(int[], UsbVolumeInfo)>();
        try {
            do {
                token.ThrowIfCancellationRequested();
                var volumeName = name.ToString();
                using var handle = DeviceIoControlQuery.Open(volumeName.TrimEnd('\\'));
                if (handle.IsInvalid) continue;
                var extents = new byte[4096];
                var ok = DeviceIoControlQuery.Query(handle, 0x560000, extents, out var returned, out var error, 0);
                if (!ok && error == 234 && returned >= 4) {
                    var count = BinaryPrimitives.ReadUInt32LittleEndian(extents);
                    if (count <= 2730) {
                        extents = new byte[8 + count * 24];
                        token.ThrowIfCancellationRequested();
                        ok = DeviceIoControlQuery.Query(handle, 0x560000, extents, out returned, out _, 0);
                    }
                }
                var diskNumbers = ok ? ExtentDisks(extents, returned) : null;
                if (diskNumbers == null || !diskNumbers.Any(targets.Contains)) continue;
                var paths = new char[4096];
                var pathsOk = GetVolumePaths(volumeName, paths, (uint)paths.Length, out var required);
                if (!pathsOk && Marshal.GetLastWin32Error() == 234 && required <= 65536) {
                    paths = new char[required];
                    pathsOk = GetVolumePaths(volumeName, paths, required, out required);
                }
                var mounts = pathsOk ? new string(paths).Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
                var label = new StringBuilder(261);
                var fs = new StringBuilder(261);
                token.ThrowIfCancellationRequested();
                var infoOk = GetVolumeInformation(volumeName, label, (uint)label.Capacity, out var serial, out _, out _, fs, (uint)fs.Capacity);
                token.ThrowIfCancellationRequested();
                var spaceOk = GetDiskFreeSpace(volumeName, out var available, out var total, out _);
                token.ThrowIfCancellationRequested();
                uint? clusterSize = GetClusterSize(volumeName, out var sectors, out var bytes, out _, out _) &&
                    (ulong)sectors * bytes is > 0 and <= uint.MaxValue ? sectors * bytes : null;
                var kernel = KernelName(volumeName);
                var pnp = kernel != null && volumeDevices.TryGetValue(kernel, out var device) ? device : null;
                result.Add((diskNumbers, new(volumeName, mounts, infoOk ? label.ToString() : null, infoOk ? fs.ToString() : null,
                    spaceOk && total <= long.MaxValue ? Bytes((long)total) : null,
                    spaceOk && available <= long.MaxValue ? Bytes((long)available) : null,
                    pnp?.path, kernel, infoOk ? $"{serial >> 16:X4}-{serial & 65535:X4}" : null, clusterSize, pnp?.device.windows)));
            } while (FindNextVolume(search, name, (uint)name.Capacity));
            if (Marshal.GetLastWin32Error() != 18) {
                warnings.Add("Volume enumeration was interrupted.");
                return null;
            }
        }
        finally { FindVolumeClose(search); }
        return result;
    }
}
