using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinToolBridge.Services;

/// <summary>A hidden top-level window on the tray's UI thread; message-only windows miss device broadcasts.</summary>
public sealed class SystemChangeNotificationWindow : NativeWindow, IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_DEVICECHANGE = 0x0219;
    private const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x00000002;

    private readonly Action _notifyChange;
    private readonly List<IntPtr> _powerNotifications = new();
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceBroadcastHeader
    {
        public uint Size;
        public uint DeviceType;
        public uint Reserved;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    public SystemChangeNotificationWindow(Action notifyChange)
    {
        _notifyChange = notifyChange;
        try {
            CreateHandle(new CreateParams { Caption = "WinToolBridge System Notifications", ExStyle = 0x80 });
            RegisterPowerSetting(new Guid("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1")); // Battery percentage
            RegisterPowerSetting(new Guid("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548")); // AC/DC power source
        }
        catch {
            Dispose();
            throw;
        }
    }

    private void RegisterPowerSetting(Guid setting)
    {
        var handle = RegisterPowerSettingNotification(Handle, ref setting, 0);
        if (handle == IntPtr.Zero) {
            // Basic power broadcasts still work when a specific notification cannot be registered.
            Console.Error.WriteLine($"[SYS:INFO_CHANGED] Power notification registration failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return;
        }
        _powerNotifications.Add(handle);
    }

    protected override void WndProc(ref Message message)
    {
        if (!_disposed) {
            var eventType = message.WParam.ToInt64();
            bool changed = message.Msg == WM_POWERBROADCAST && eventType is
                PBT_APMPOWERSTATUSCHANGE or PBT_POWERSETTINGCHANGE or PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND;
            if (message.Msg == WM_DEVICECHANGE &&
                (eventType is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE) && message.LParam != IntPtr.Zero) {
                var header = Marshal.PtrToStructure<DeviceBroadcastHeader>(message.LParam);
                changed = header.Size >= Marshal.SizeOf<DeviceBroadcastHeader>() && header.DeviceType == DBT_DEVTYP_VOLUME;
            }
            if (changed) _notifyChange();
        }
        base.WndProc(ref message);
    }

    // Create and dispose on the tray UI thread.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var handle in _powerNotifications) UnregisterPowerSettingNotification(handle);
        _powerNotifications.Clear();
        if (Handle != IntPtr.Zero) DestroyHandle();
        GC.SuppressFinalize(this);
    }
}
