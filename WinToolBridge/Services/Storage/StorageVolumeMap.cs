using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WinToolBridge.Services.Storage;

internal static class StorageVolumeMap
{
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
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, StringBuilder volumePath, uint length);

    internal static string? WindowsMountPoint(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows)) return null;
        var path = new StringBuilder(32768);
        return GetVolumePathName(windows, path, (uint)path.Capacity) ? path.ToString() : null;
    }

    internal static bool? IsSystemDisk(string[]? mounts, string? windowsMountPoint, StorageAvailability state)
    {
        if (mounts == null || windowsMountPoint == null) return null;
        if (mounts.Contains(windowsMountPoint, StringComparer.OrdinalIgnoreCase)) return true;
        // An incomplete volume scan cannot establish that the Windows volume is absent.
        return state.status == "available" ? false : null;
    }

    internal static (Dictionary<uint, string[]> paths, StorageAvailability availability) Read(CancellationToken token)
    {
        var paths = new Dictionary<uint, List<string>>();
        var state = new StorageAvailability("available");
        var name = new StringBuilder(1024);
        token.ThrowIfCancellationRequested();
        var search = FindFirstVolume(name, (uint)name.Capacity);
        if (search == new IntPtr(-1)) {
            var error = Marshal.GetLastWin32Error();
            return ([], error == 18 ? state : PhysicalDiskService.Failure(new Win32Exception(error)));
        }
        try {
            do {
                token.ThrowIfCancellationRequested();
                var volume = name.ToString();
                var mounts = new char[1024];
                var ok = GetVolumePaths(volume, mounts, (uint)mounts.Length, out var required);
                if (!ok && Marshal.GetLastWin32Error() == 234 && required <= 65536) {
                    token.ThrowIfCancellationRequested();
                    mounts = new char[required];
                    ok = GetVolumePaths(volume, mounts, required, out _);
                }
                if (!ok) { state = new("partial", Marshal.GetLastWin32Error(), "Some volume mount paths could not be read."); continue; }
                var names = new string(mounts).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (names.Length == 0) continue;
                token.ThrowIfCancellationRequested();
                using var handle = DeviceIoControlQuery.Open(volume.TrimEnd('\\'));
                if (handle.IsInvalid) { state = new("partial", Marshal.GetLastWin32Error(), "Some mounted volumes could not be opened."); continue; }
                var buffer = new byte[4096];
                ok = DeviceIoControlQuery.Query(handle, 0x560000, buffer, out var count, out var error, 0);
                if (!ok && error == 234 && count >= 4) {
                    var extentCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                    if (extentCount <= 2730) {
                        token.ThrowIfCancellationRequested();
                        buffer = new byte[8 + extentCount * 24];
                        ok = DeviceIoControlQuery.Query(handle, 0x560000, buffer, out count, out error, 0);
                    }
                }
                if (!ok) { state = new("partial", error, "Some volume disk mappings could not be read."); continue; }
                try {
                    foreach (var disk in ExtentDisks(buffer, count)) {
                        if (!paths.TryGetValue(disk, out var entries)) paths[disk] = entries = [];
                        entries.AddRange(names);
                    }
                }
                catch (InvalidDataException) { state = new("partial", null, "Invalid volume disk mapping."); }
            } while (FindNextVolume(search, name, (uint)name.Capacity));
            var lastError = Marshal.GetLastWin32Error();
            if (lastError != 18) state = new("partial", lastError, "Volume enumeration was interrupted.");
        }
        finally { FindVolumeClose(search); }
        token.ThrowIfCancellationRequested();
        return (paths.ToDictionary(pair => pair.Key, pair => pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray()), state);
    }

    internal static uint[] ExtentDisks(byte[] buffer, int returned)
    {
        if (returned < 8 || returned > buffer.Length) throw new InvalidDataException("Truncated volume extents.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        // DISK_EXTENT begins at offset 8 and is 24 bytes on all Windows targets.
        if (count > (returned - 8) / 24) throw new InvalidDataException("Invalid volume extent count.");
        return Enumerable.Range(0, (int)count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8 + i * 24))).Distinct().ToArray();
    }
}
