using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinToolBridge.Services.Usb;

/// <summary>Loads only icons requested by USB tree nodes. Owns no USB device handles.</summary>
internal sealed class UsbDeviceIconProvider(ImageAssetService assets, string assetBaseUrl) : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint size; public Guid classGuid; public uint devInst; public IntPtr reserved; }

    private sealed class DeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DeviceInfoSet() : base(true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern DeviceInfoSet SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parent);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfo(DeviceInfoSet set, string instanceId, IntPtr parent, uint flags, ref DeviceInfo info);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiLoadDeviceIcon(DeviceInfoSet set, ref DeviceInfo info, uint width, uint height, uint flags, out IntPtr icon);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    private DeviceInfoSet? _set;
    private readonly Dictionary<string, string?> _urls = new(StringComparer.OrdinalIgnoreCase);

    internal static string PhysicalIconInstance(UsbPnpDevice device, Dictionary<uint, UsbPnpDevice[]> children)
    {
        // Keep a specific device's own icon. A generic composite/USB/HID parent can
        // instead use a real camera function, or one unambiguous function category.
        if (device.windows.deviceClass is not ("USB" or "USBDevice" or "HIDClass")) return device.windows.instanceId;
        var functions = new List<UsbPnpDevice>();
        var visited = new HashSet<uint>();
        void Visit(UsbPnpDevice parent, int depth)
        {
            if (depth > 64 || !visited.Add(parent.devInst) || !children.TryGetValue(parent.devInst, out var nested)) return;
            foreach (var child in nested) {
                if (device.containerId.HasValue && child.containerId != device.containerId) continue;
                if (child.windows.deviceClass is "Camera" or "Image" or "Keyboard" or "Mouse" or "Net" or "DiskDrive" or "MEDIA") functions.Add(child);
                Visit(child, depth + 1);
            }
        }
        Visit(device, 0);
        var cameras = functions.Where(item => item.windows.deviceClass is "Camera" or "Image").ToArray();
        var candidates = cameras.Length > 0 ? cameras : functions.Select(item => item.windows.deviceClass).Distinct().Count() == 1 ? functions.ToArray() : [];
        return candidates.OrderBy(item => item.windows.instanceId, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.windows.instanceId ?? device.windows.instanceId;
    }

    public string? GetUrl(string? instanceId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        if (_urls.TryGetValue(instanceId, out var existing)) return existing;
        _set ??= SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (_set.IsInvalid) return null;
        var info = new DeviceInfo { size = (uint)Marshal.SizeOf<DeviceInfo>() };
        string? url = null;
        var iconHandle = IntPtr.Zero;
        try {
            if (SetupDiOpenDeviceInfo(_set, instanceId, IntPtr.Zero, 0, ref info) &&
                SetupDiLoadDeviceIcon(_set, ref info, 64, 64, 0, out iconHandle) && iconHandle != IntPtr.Zero) {
                token.ThrowIfCancellationRequested();
                using var icon = Icon.FromHandle(iconHandle);
                using var bitmap = icon.ToBitmap();
                using var png = new MemoryStream();
                bitmap.Save(png, ImageFormat.Png);
                token.ThrowIfCancellationRequested();
                var path = assets.RegisterPng(png.GetBuffer().AsSpan(0, checked((int)png.Length)));
                url = assetBaseUrl.TrimEnd('/') + path;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException) {
            // An unavailable or unconvertible icon must not fail USB enumeration.
        }
        finally {
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
        }
        _urls[instanceId] = url;
        return url;
    }

    public void Dispose() => _set?.Dispose();
}
