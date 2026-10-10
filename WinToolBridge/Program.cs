using WinToolBridge.Resources;
using WinToolBridge.Events.Handlers;
using WinToolBridge.Services;
using WinToolBridge.Services.Usb;

namespace WinToolBridge;

static class Program
{
    private static LocalBridgeServer? _server;
    private static SystemInfoChangedHandler? _systemInfoEvents;
    private static SystemChangeNotificationWindow? _systemNotifications;
    private static NetworkInfoChangedHandler? _networkInfoEvents;
    private static NetworkChangeNotifications? _networkNotifications;
    private static UsbTreeChangedHandler? _usbEvents;
    private static UsbChangeNotificationWindow? _usbNotifications;

    [STAThread]
    static void Main(string[] args)
    {
        bool isSilent = args.Any(arg => string.Equals(arg, "-silent", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, "Global\\WinToolBridge_SingleInstance_Mutex", out bool isNewInstance);
        if (!isNewInstance) {
            MessageBox.Show(Strings.AlreadyRunning, Strings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        try {
            _server = new LocalBridgeServer();
            _systemInfoEvents = new SystemInfoChangedHandler(_server.Events);
            _systemNotifications = new SystemChangeNotificationWindow(_systemInfoEvents.NotifyChange);
            _networkInfoEvents = new NetworkInfoChangedHandler(_server.Events);
            _networkNotifications = new NetworkChangeNotifications(_networkInfoEvents.NotifyChange);
            _usbEvents = new UsbTreeChangedHandler(_server.Events, _server.Usb);
            _usbNotifications = new UsbChangeNotificationWindow(_usbEvents.NotifyChange);
            _server.Start();
        }
        catch (Exception ex) {
            _usbNotifications?.Dispose();
            _usbEvents?.Dispose();
            _networkNotifications?.Dispose();
            _networkInfoEvents?.Dispose();
            _systemNotifications?.Dispose();
            _systemInfoEvents?.Dispose();
            _server?.Dispose();
            string err = string.Format(Strings.PortStartFailed, LocalBridgeServer.DEFAULT_PORT, ex.Message);
            MessageBox.Show(err, Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try {
            using var trayMenu = new TrayMenu(_server.Port, isSilent);
            Application.ApplicationExit += OnApplicationExit;
            Application.Run();
        }
        finally {
            Application.ApplicationExit -= OnApplicationExit;
            OnApplicationExit(null, EventArgs.Empty);
        }
        Environment.Exit(0);
    }

    private static void OnApplicationExit(object? sender, EventArgs e)
    {
        ConsoleHelper.HideConsole();
        _usbNotifications?.Dispose();
        _usbEvents?.Dispose();
        _networkNotifications?.Dispose();
        _networkInfoEvents?.Dispose();
        _systemNotifications?.Dispose();
        _systemInfoEvents?.Dispose();

        if (_server != null) {
            _server.Stop();
            _server.Dispose();
        }
    }
}
