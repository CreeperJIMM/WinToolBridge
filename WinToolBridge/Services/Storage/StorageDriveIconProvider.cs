using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WinToolBridge.Services.Storage;

/// <summary>Loads Explorer's drive-root icon for a backend-enumerated mounted volume.</summary>
internal static class StorageDriveIconProvider
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr icon;
        public int index;
        public uint attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string displayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string typeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern UIntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    internal static bool IsDriveRoot(string path) => path.Length == 3 &&
        char.ToUpperInvariant(path[0]) is >= 'A' and <= 'Z' && path[1] == ':' && path[2] == '\\';

    internal static string? SelectRoot(string[]? mountPoints, string? windowsMountPoint)
    {
        var roots = (mountPoints ?? []).Where(IsDriveRoot).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return roots.FirstOrDefault(root => string.Equals(root, windowsMountPoint, StringComparison.OrdinalIgnoreCase))
            ?? roots.FirstOrDefault();
    }

    internal static string? GetUrl(string? root, ImageAssetService assets, string assetBaseUrl, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (root == null || !IsDriveRoot(root)) return null;
        // Called on PhysicalDiskService's background worker; balance COM initialization.
        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) return null;
        var info = new ShellFileInfo { displayName = "", typeName = "" };
        try {
            token.ThrowIfCancellationRequested();
            // SHGFI_ICON | SHGFI_ADDOVERLAYS, with the actual drive path (not USEFILEATTRIBUTES).
            if (SHGetFileInfo(root, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x120) == UIntPtr.Zero || info.icon == IntPtr.Zero)
                return null;
            token.ThrowIfCancellationRequested();
            using var icon = Icon.FromHandle(info.icon);
            using var bitmap = icon.ToBitmap();
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            token.ThrowIfCancellationRequested();
            return assetBaseUrl.TrimEnd('/') + assets.RegisterPng(png.GetBuffer().AsSpan(0, checked((int)png.Length)));
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException) {
            return null; // Shell/icon failure must not prevent disk enumeration.
        }
        finally {
            if (info.icon != IntPtr.Zero) DestroyIcon(info.icon);
            if (initialized >= 0) CoUninitialize();
        }
    }
}
