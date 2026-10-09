using WinToolBridge.Resources;

namespace WinToolBridge;

static class Program
{
    private static LocalBridgeServer? _server;

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
            _server.Start();
        }
        catch (Exception ex) {
            string err = string.Format(Strings.PortStartFailed, LocalBridgeServer.DEFAULT_PORT, ex.Message);
            MessageBox.Show(err, Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (var trayMenu = new TrayMenu(_server.Port, isSilent)) {
            Application.ApplicationExit += OnApplicationExit;
            Application.Run();
        }
        Environment.Exit(0);
    }

    private static void OnApplicationExit(object? sender, EventArgs e)
    {
        ConsoleHelper.HideConsole();

        if (_server != null) {
            _server.Stop();
            _server.Dispose();
        }
    }
}
