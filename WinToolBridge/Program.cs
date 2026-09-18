using System.Reflection;
using WinToolBridge.Resources;

namespace WinToolBridge;

static class Program
{
    private static NotifyIcon? _notifyIcon;
    private static LocalBridgeServer? _server;

    [STAThread]
    static void Main(string[] args)
    {
        bool isSilent = args.Any(arg => string.Equals(arg, "-silent", StringComparison.OrdinalIgnoreCase));

        using var mutex = new System.Threading.Mutex(true, "Global\\WinToolBridge_SingleInstance_Mutex", out bool isNewInstance);
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

        SetupTrayIcon(isSilent);
        Application.ApplicationExit += OnApplicationExit;
        Application.Run();
    }

    private static void SetupTrayIcon(bool isSilent)
    {
        var contextMenu = new ContextMenuStrip();

        var statusText = string.Format(Strings.StatusRunning, LocalBridgeServer.SERVER_VERSION);
        var statusItem = new ToolStripMenuItem(statusText) { Enabled = false };

        var portText = string.Format(Strings.PortDisplay, _server?.Port ?? LocalBridgeServer.DEFAULT_PORT);
        var portItem = new ToolStripMenuItem(portText) { Enabled = false };

        var autoStartItem = new ToolStripMenuItem(Strings.StartOnBoot) {
            Checked = StartupHelper.IsAutoStartEnabled(),
            CheckOnClick = true
        };

        bool isDebug = false;
#if DEBUG
        isDebug = true;
#endif
        if (System.Diagnostics.Debugger.IsAttached) {
            isDebug = true;
        }

        if (isDebug) {
            autoStartItem.Enabled = false;
            autoStartItem.ToolTipText = "Debug 模式下停用此選項";
        }
        else {
            autoStartItem.Click += (s, e) => {
                StartupHelper.SetAutoStart(autoStartItem.Checked);
            };
        }

        var separator = new ToolStripSeparator();

        var exitItem = new ToolStripMenuItem(Strings.ExitApp, null, (s, e) => {
            Application.Exit();
        });

        contextMenu.Items.AddRange(new ToolStripItem[] {
            statusItem,
            portItem,
            autoStartItem,
            separator,
            exitItem
        });

        _notifyIcon = new NotifyIcon {
            Icon = GetAppIcon(),
            ContextMenuStrip = contextMenu,
            Text = string.Format(Strings.TrayTip, LocalBridgeServer.SERVER_VERSION),
            Visible = true
        };

        _notifyIcon.DoubleClick += (s, e) => {
            ConsoleHelper.ToggleConsole();
        };

        _notifyIcon.BalloonTipClicked += (s, e) => {
            ConsoleHelper.ShowConsole();
        };

        if (!isSilent) {
            _notifyIcon.ShowBalloonTip(3000, Strings.BalloonReadyTitle, Strings.BalloonReadyText, ToolTipIcon.Info);
        }
    }

    private static Icon GetAppIcon()
    {
        try {
            var assembly = Assembly.GetExecutingAssembly();

            using var stream = assembly.GetManifestResourceStream("app.ico")
                             ?? assembly.GetManifestResourceStream("WinToolBridge.app.ico");
            if (stream != null) {
                return new Icon(stream);
            }

            var localIconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(localIconPath)) {
                return new Icon(localIconPath);
            }

            var currentExePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(currentExePath) && File.Exists(currentExePath)) {
                var icon = Icon.ExtractAssociatedIcon(currentExePath);
                if (icon != null) return icon;
            }
        }
        catch {

        }

        return SystemIcons.Application;
    }

    private static void OnApplicationExit(object? sender, EventArgs e)
    {
        ConsoleHelper.HideConsole();

        if (_notifyIcon != null) {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        if (_server != null) {
            _server.Stop();
            _server.Dispose();
        }
    }
}