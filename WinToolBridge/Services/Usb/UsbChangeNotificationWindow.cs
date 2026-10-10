using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinToolBridge.Services.Usb;

/// <summary>USB interface arrival/removal and PnP tree invalidation, on the tray UI thread.</summary>
public sealed class UsbChangeNotificationWindow : NativeWindow, IDisposable
{
    private readonly Action _notify;
    private readonly List<IntPtr> _registrations = new();
    private bool _disposed;

    [DllImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, IntPtr filter, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    public UsbChangeNotificationWindow(Action notify)
    {
        _notify = notify;
        try {
            CreateHandle(new CreateParams { Caption = "WinToolBridge USB Notifications", ExStyle = 0x80 });
            foreach (var guid in new[] { UsbDeviceInventory.ControllerClass, UsbDeviceInventory.HubClass, UsbDeviceInventory.DeviceClass }) {
                var filter = Marshal.AllocHGlobal(32);
                try {
                    Marshal.Copy(new byte[32], 0, filter, 32);
                    Marshal.WriteInt32(filter, 32);
                    Marshal.WriteInt32(filter, 4, 5); // DBT_DEVTYP_DEVICEINTERFACE
                    Marshal.StructureToPtr(guid, IntPtr.Add(filter, 12), false);
                    var registration = RegisterDeviceNotification(Handle, filter, 0);
                    if (registration == IntPtr.Zero)
                        Console.Error.WriteLine($"[USB:TREE_CHANGED] Interface notification registration failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                    else _registrations.Add(registration);
                }
                finally { Marshal.FreeHGlobal(filter); }
            }
        }
        catch { Dispose(); throw; }
    }

    protected override void WndProc(ref Message message)
    {
        if (!_disposed) {
            var change = false;
            if (message.Msg == 0x0219) { // WM_DEVICECHANGE
                var type = message.WParam.ToInt64();
                // PnP changes also cover enabling/disabling and enumeration completion. This is
                // an invalidation hint and can include unrelated devices; it never enumerates here.
                change = type == 0x0007; // DBT_DEVNODES_CHANGED
                if ((type is 0x8000 or 0x8004) && message.LParam != IntPtr.Zero &&
                    Marshal.ReadInt32(message.LParam) >= 28 && Marshal.ReadInt32(message.LParam, 4) == 5) {
                    var guid = Marshal.PtrToStructure<Guid>(IntPtr.Add(message.LParam, 12));
                    change = guid == UsbDeviceInventory.ControllerClass || guid == UsbDeviceInventory.HubClass || guid == UsbDeviceInventory.DeviceClass;
                }
            }
            if (message.Msg == 0x0218 && message.WParam.ToInt64() == 0x0012) change = true; // Resume
            if (change) {
                try { _notify(); }
                catch (Exception ex) { Console.Error.WriteLine($"[USB:TREE_CHANGED] Notification failed: {ex.Message}"); }
            }
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var handle in _registrations) UnregisterDeviceNotification(handle);
        _registrations.Clear();
        if (Handle != IntPtr.Zero) DestroyHandle();
        GC.SuppressFinalize(this);
    }
}
