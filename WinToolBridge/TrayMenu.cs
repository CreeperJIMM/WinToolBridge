using System.Reflection;
using WinToolBridge.Resources;

namespace WinToolBridge;

public sealed class TrayMenu : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private Icon? _icon;
    private ToolStripMenuItem? _automaticUpdateItem;
    private ToolStripMenuItem? _manualUpdateItem;
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _isCheckingUpdates;
    private bool _isExiting;

    public TrayMenu(int port, bool isSilent)
    {
        try {
            SetupTrayIcon(port, isSilent);
            Application.Idle += OnFirstIdle;
            Application.ApplicationExit += OnApplicationExit;
        }
        catch {
            Dispose();
            throw;
        }
    }

    private void SetupTrayIcon(int port, bool isSilent)
    {
        _contextMenu = new ContextMenuStrip();
        var contextMenu = _contextMenu;

        var statusText = string.Format(Strings.StatusRunning, LocalBridgeServer.SERVER_VERSION);
        var statusItem = new ToolStripMenuItem(statusText) { Enabled = false };

        var portText = string.Format(Strings.PortDisplay, port);
        var portItem = new ToolStripMenuItem(portText) { Enabled = false };

        var autoStartItem = new ToolStripMenuItem(Strings.StartOnBoot) {
            Checked = StartupHelper.IsAutoStartEnabled(),
            CheckOnClick = true
        };

        const string debugDisabledTip = "Debug 模式下停用此選項";
        bool isDebug = false;
#if DEBUG
        isDebug = true;
#endif
        if (System.Diagnostics.Debugger.IsAttached) {
            isDebug = true;
        }

        if (isDebug) {
            autoStartItem.Enabled = false;
            autoStartItem.ToolTipText = debugDisabledTip;
        }
        else {
            autoStartItem.Click += (s, e) => {
                StartupHelper.SetAutoStart(autoStartItem.Checked);
            };
        }

        var separator = new ToolStripSeparator();

        _automaticUpdateItem = new ToolStripMenuItem(UpdateText("AutomaticUpdateCheck")) {
            Checked = !isDebug && UpdateChecker.IsAutomaticCheckEnabled(),
            Enabled = !isDebug,
            ToolTipText = isDebug ? debugDisabledTip : string.Empty,
            CheckOnClick = true
        };
        _automaticUpdateItem.Click += (s, e) => {
            try {
                UpdateChecker.SetAutomaticCheckEnabled(_automaticUpdateItem.Checked);
                LogUpdate($"Automatic checks {(_automaticUpdateItem.Checked ? "enabled" : "disabled")}.");
            }
            catch (Exception ex) {
                _automaticUpdateItem.Checked = !_automaticUpdateItem.Checked;
                LogUpdate($"Failed to save preference: {ex.Message}");
                MessageBox.Show(UpdateText("UpdateSettingsFailed"), Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        _manualUpdateItem = new ToolStripMenuItem(UpdateText("ManualUpdateCheck"));
        _manualUpdateItem.Click += async (s, e) => await CheckForUpdatesAsync(manual: true);
        var githubItem = new ToolStripMenuItem(UpdateText("ViewGitHub"), null, (s, e) => OpenUpdatePage(latest: false));
        if (isDebug) LogUpdate("Automatic check skipped in debug mode.");

        var exitItem = new ToolStripMenuItem(Strings.ExitApp, null, (s, e) => {
            Application.Exit();
        });

        contextMenu.Items.AddRange(new ToolStripItem[] {
            statusItem,
            portItem,
            autoStartItem,
            separator,
            _automaticUpdateItem,
            _manualUpdateItem,
            githubItem,
            new ToolStripSeparator(),
            exitItem
        });

        _notifyIcon = new NotifyIcon {
            Icon = _icon = GetAppIcon(),
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

    private static string UpdateText(string key) => Strings.ResourceManager.GetString(key, Strings.Culture) ?? key;

    private static void LogUpdate(string message) => ConsoleHelper.WriteLog($"[{DateTime.Now:HH:mm:ss}] [Update] {message}");

    private async void OnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        if (_automaticUpdateItem is { Enabled: true, Checked: true }) await CheckForUpdatesAsync(manual: false);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_isExiting || _isCheckingUpdates) return;
        _isCheckingUpdates = true;
        if (_manualUpdateItem != null) _manualUpdateItem.Enabled = false;
        LogUpdate($"{(manual ? "Manual" : "Automatic")} check started. Current version: v{LocalBridgeServer.SERVER_VERSION}.");
        try {
            var result = await UpdateChecker.CheckAsync(_shutdownCts.Token);
            if (result == null) LogUpdate("No published stable release found.");
            else LogUpdate($"Latest version: v{result.LatestVersion}. {(result.HasUpdate ? "New version available." : "Already up to date.")}");
            if (_isExiting || (!manual && _automaticUpdateItem?.Checked != true)) return;

            if (result?.HasUpdate == true) {
                var answer = MessageBox.Show(UpdateText("UpdateAvailableMessage"), UpdateText("UpdateAvailableTitle"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) OpenUpdatePage(latest: true);
            }
            else if (manual) {
                var message = result == null ? UpdateText("NoPublishedRelease") :
                    string.Format(UpdateText("UpdateUpToDate"), LocalBridgeServer.SERVER_VERSION);
                MessageBox.Show(message, UpdateText("UpdateMenu"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException) when (_isExiting) {
            LogUpdate("Check cancelled during shutdown.");
        }
        catch (Exception ex) {
            LogUpdate($"Check failed: {ex.Message}");
            if (manual && !_isExiting) {
                MessageBox.Show(UpdateText("UpdateCheckFailed"), UpdateText("UpdateMenu"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally {
            _isCheckingUpdates = false;
            if (!_isExiting && _manualUpdateItem != null) _manualUpdateItem.Enabled = true;
        }
    }

    private static void OpenUpdatePage(bool latest)
    {
        try {
            if (latest) UpdateChecker.OpenLatestRelease();
            else UpdateChecker.OpenGitHub();
            LogUpdate(latest ? "Opened latest release page." : "Opened GitHub page.");
        }
        catch (Exception ex) {
            LogUpdate($"Failed to open page: {ex.Message}");
            MessageBox.Show(UpdateText("UpdateOpenPageFailed"), Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        return (Icon)SystemIcons.Application.Clone();
    }

    private void OnApplicationExit(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_isExiting) return;
        _isExiting = true;
        Application.Idle -= OnFirstIdle;
        Application.ApplicationExit -= OnApplicationExit;
        _shutdownCts.Cancel();
        if (_notifyIcon != null) {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _contextMenu?.Dispose();
        _icon?.Dispose();
        _shutdownCts.Dispose();
    }
}
