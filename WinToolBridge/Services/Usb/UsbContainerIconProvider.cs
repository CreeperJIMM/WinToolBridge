using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WinToolBridge.Services.Usb;

/// <summary>Reads physical-device images from the Devices and Printers Shell folder, never PnP class icons.</summary>
internal static class UsbContainerIconProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid format; public uint id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ImageSize { public int width; public int height; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int type; public int width; public int height; public int widthBytes;
        public ushort planes; public ushort bitsPixel; public IntPtr bits;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint size; public int width; public int height; public ushort planes; public ushort bitCount;
        public uint compression; public uint sizeImage; public int xPixelsPerMeter; public int yPixelsPerMeter;
        public uint colorsUsed; public uint colorsImportant;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    // Include inherited slots explicitly; GetCLSID is the twelfth method after IUnknown.
    [ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IntPtr other, uint hint, out int order);
        [PreserveSig] int GetPropertyStore(uint flags, ref Guid iid, out IntPtr store);
        [PreserveSig] int GetPropertyStoreWithCreateObject(uint flags, IntPtr create, ref Guid iid, out IntPtr store);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr keys, uint count, uint flags, ref Guid iid, out IntPtr store);
        [PreserveSig] int GetPropertyDescriptionList(ref PropertyKey key, ref Guid iid, out IntPtr list);
        [PreserveSig] int Update(IntPtr context);
        [PreserveSig] int GetProperty(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetCLSID(ref PropertyKey key, out Guid value);
        [PreserveSig] int GetFileTime(ref PropertyKey key, out long value);
        [PreserveSig] int GetInt32(ref PropertyKey key, out int value);
        [PreserveSig] int GetString(ref PropertyKey key, out IntPtr value);
        [PreserveSig] int GetUInt32(ref PropertyKey key, out uint value);
        [PreserveSig] int GetUInt64(ref PropertyKey key, out ulong value);
        [PreserveSig] int GetBool(ref PropertyKey key, [MarshalAs(UnmanagedType.Bool)] out bool value);
    }

    [ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, out IShellItem? item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems clone);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(ImageSize size, uint flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string name, IntPtr context, ref Guid iid, out IShellItem? item);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    public static Dictionary<Guid, string> Read(HashSet<Guid> containers, ImageAssetService assets, string assetBaseUrl, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (containers.Count == 0) return [];
        // UsbTreeService runs enumeration on a background worker already. Reuse its
        // apartment rather than creating a new managed/native thread for every query.
        var initialized = CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) return []; // RPC_E_CHANGED_MODE: existing STA
        try {
            var result = ReadShell(containers, assets, assetBaseUrl, token);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally { if (initialized >= 0) CoUninitialize(); }
    }

    private static Dictionary<Guid, string> ReadShell(HashSet<Guid> containers, ImageAssetService assets, string assetBaseUrl, CancellationToken token)
    {
        Dictionary<Guid, string> result = [];
        IShellItem? folder = null;
        object? enumerator = null;
        try {
            token.ThrowIfCancellationRequested();
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName("shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}", IntPtr.Zero, ref iid, out folder) < 0 || folder == null) return result;
            var handler = new Guid("94F60519-2850-4924-AA5A-D15E84868039"); // BHID_EnumItems
            iid = typeof(IEnumShellItems).GUID;
            if (folder.BindToHandler(IntPtr.Zero, ref handler, ref iid, out enumerator) < 0) return result;
            var items = (IEnumShellItems)enumerator;
            var key = new PropertyKey { format = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), id = 2 }; // PKEY_Devices_ContainerId
            var machineKey = new PropertyKey { format = new("78C34FC8-104A-4ACA-9EA4-524D52996E57"), id = 70 }; // PKEY_Devices_LocalMachine
            while (true) {
                token.ThrowIfCancellationRequested();
                IShellItem? item = null;
                try {
                    if (items.Next(1, out item, out var fetched) != 0 || fetched != 1 || item == null) break;
                    if (((IShellItem2)item).GetCLSID(ref key, out var container) < 0 || !containers.Contains(container) || result.ContainsKey(container)) continue;
                    // Integrated functions can share the PC container. Its laptop/desktop
                    // image describes the computer, not each USB device inside it.
                    if (((IShellItem2)item).GetBool(ref machineKey, out var localMachine) >= 0 && localMachine) continue;
                    token.ThrowIfCancellationRequested();
                    var bitmap = IntPtr.Zero;
                    try {
                        // Shell default: physical-device thumbnail, or the folder item's icon if no thumbnail exists.
                        // No SetupDi/PnP fallback is used for this image.
                        if (((IShellItemImageFactory)item).GetImage(new() { width = 64, height = 64 }, 0, out bitmap) < 0 || bitmap == IntPtr.Zero) continue;
                        token.ThrowIfCancellationRequested();
                        var png = EncodePng(bitmap);
                        if (png == null) continue;
                        token.ThrowIfCancellationRequested();
                        result[container] = assetBaseUrl.TrimEnd('/') + assets.RegisterPng(png);
                    }
                    finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); }
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or ExternalException) {
                    // An unavailable Shell image must not prevent the USB tree from loading.
                }
                finally { if (item != null) Marshal.ReleaseComObject(item); }
                if (result.Count == containers.Count) break;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { }
        finally {
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            if (folder != null) Marshal.ReleaseComObject(folder);
        }
        return result;
    }

    private static byte[]? EncodePng(IntPtr handle)
    {
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try {
            // GetObject avoids a BITMAPINFO palette write when inspecting a non-32-bit source.
            if (GetObject(handle, Marshal.SizeOf<NativeBitmap>(), out var native) == 0 || native.width <= 0 || native.width > 256 || native.height == 0 || Math.Abs((long)native.height) > 256) return null;
            var height = Math.Abs(native.height);
            var info = new BitmapInfo { size = (uint)Marshal.SizeOf<BitmapInfo>(), width = native.width };
            info.height = -height; // Top-down BGRA, preserving Shell bitmap alpha.
            info.planes = 1;
            info.bitCount = 32;
            info.compression = 0;
            info.sizeImage = (uint)(info.width * height * 4);
            using var source = new Bitmap(info.width, height, PixelFormat.Format32bppArgb);
            var data = source.LockBits(new Rectangle(0, 0, info.width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try {
                if (GetDIBits(dc, handle, 0, (uint)height, data.Scan0, ref info, 0) != height) return null;
            }
            finally { source.UnlockBits(data); }
            using var image = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(image)) {
                graphics.Clear(Color.Transparent);
                var scale = Math.Min(64.0 / source.Width, 64.0 / source.Height);
                var width = (int)Math.Round(source.Width * scale);
                var scaledHeight = (int)Math.Round(source.Height * scale);
                graphics.DrawImage(source, new Rectangle((64 - width) / 2, (64 - scaledHeight) / 2, width, scaledHeight));
            }
            using var png = new MemoryStream();
            image.Save(png, ImageFormat.Png);
            return png.ToArray();
        }
        finally { DeleteDC(dc); }
    }
}
