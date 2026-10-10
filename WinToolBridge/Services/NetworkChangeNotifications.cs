using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;

namespace WinToolBridge.Services;

/// <summary>Callbacks only enqueue work; native rows and buffers never escape their callback.</summary>
public sealed class NetworkChangeNotifications : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void IpCallback(IntPtr context, IntPtr row, int notificationType);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WlanCallback(IntPtr data, IntPtr context);

    [DllImport("iphlpapi.dll")]
    private static extern uint NotifyIpInterfaceChange(ushort family, IpCallback callback, IntPtr context, [MarshalAs(UnmanagedType.U1)] bool initial, out IntPtr handle);
    [DllImport("iphlpapi.dll")]
    private static extern uint NotifyUnicastIpAddressChange(ushort family, IpCallback callback, IntPtr context, [MarshalAs(UnmanagedType.U1)] bool initial, out IntPtr handle);
    [DllImport("iphlpapi.dll")]
    private static extern uint NotifyRouteChange2(ushort family, IpCallback callback, IntPtr context, [MarshalAs(UnmanagedType.U1)] bool initial, out IntPtr handle);
    [DllImport("iphlpapi.dll")]
    private static extern uint CancelMibChangeNotify2(IntPtr handle);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanRegisterNotification(IntPtr handle, uint sources, [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicates, WlanCallback? callback, IntPtr context, IntPtr reserved, out uint previousSources);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    private readonly Action _notify;
    private readonly IpCallback _ipCallback;
    private readonly WlanCallback _wlanCallback;
    private readonly NetworkStatusChangedEventHandler _statusCallback;
    private IntPtr _interfaceHandle, _addressHandle, _routeHandle, _wlanHandle;
    private bool _statusRegistered;
    private int _disposed;

    public NetworkChangeNotifications(Action notify)
    {
        _notify = notify;
        _ipCallback = (_, _, _) => Notify();
        _wlanCallback = (_, _) => Notify();
        _statusCallback = _ => Notify();
        try {
            Log("interface", NotifyIpInterfaceChange(0, _ipCallback, IntPtr.Zero, false, out _interfaceHandle));
            Log("address", NotifyUnicastIpAddressChange(0, _ipCallback, IntPtr.Zero, false, out _addressHandle));
            Log("route", NotifyRouteChange2(0, _ipCallback, IntPtr.Zero, false, out _routeHandle));
            try {
                NetworkInformation.NetworkStatusChanged += _statusCallback;
                _statusRegistered = true;
            }
            catch (Exception ex) {
                Console.Error.WriteLine($"[NET:INFO_CHANGED] Network status registration failed: {ex.Message}");
            }
            var opened = WlanOpenHandle(2, IntPtr.Zero, out _, out _wlanHandle);
            Log("WLAN open", opened);
            if (opened == 0) {
                // MSM includes signal/roaming notifications. Fall back to ACM when permission is unavailable.
                var result = WlanRegisterNotification(_wlanHandle, 0x8 | 0x10, true, _wlanCallback, IntPtr.Zero, IntPtr.Zero, out _);
                if (result != 0) {
                    Log("WLAN signal/roaming", result);
                    result = WlanRegisterNotification(_wlanHandle, 0x8, true, _wlanCallback, IntPtr.Zero, IntPtr.Zero, out _);
                }
                Log("WLAN", result);
            }
        }
        catch {
            Dispose();
            throw;
        }
    }

    private static void Log(string source, uint result)
    {
        if (result != 0) Console.Error.WriteLine($"[NET:INFO_CHANGED] {source} notification registration failed: {result}");
    }

    private void Notify()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _notify(); }
        catch (Exception ex) { Console.Error.WriteLine($"[NET:INFO_CHANGED] Notification failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_statusRegistered) NetworkInformation.NetworkStatusChanged -= _statusCallback;
        // Never unregister on a native callback thread: cancellation waits for callbacks to return.
        foreach (var handle in new[] { _interfaceHandle, _addressHandle, _routeHandle }) {
            if (handle != IntPtr.Zero) CancelMibChangeNotify2(handle);
        }
        if (_wlanHandle != IntPtr.Zero) {
            WlanRegisterNotification(_wlanHandle, 0, true, null, IntPtr.Zero, IntPtr.Zero, out _);
            WlanCloseHandle(_wlanHandle, IntPtr.Zero);
        }
        GC.KeepAlive(_ipCallback);
        GC.KeepAlive(_wlanCallback);
    }
}
