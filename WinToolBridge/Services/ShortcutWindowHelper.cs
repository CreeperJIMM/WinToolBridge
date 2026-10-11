using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.Core;

namespace WinToolBridge.Services;

internal static class ShortcutWindowHelper
{
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);

    public static async Task StartAsync(ProcessStartInfo startInfo, CancellationToken token,
        string[]? shellHosts = null, bool matchStartedProcessOnly = false)
    {
        token.ThrowIfCancellationRequested();
        var previous = GetVisibleWindows();
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo);
        if (process == null && !startInfo.UseShellExecute) {
            throw new InvalidOperationException("Unable to open shortcut application.");
        }

        await TryBringToFrontAsync(window => WithWindowProcess(window, candidate => {
            if (process != null && candidate.Id == process.Id &&
                (shellHosts == null || !previous.Contains(window))) return true;
            if (matchStartedProcessOnly) return false;
            if (shellHosts != null) {
                return !previous.Contains(window) && shellHosts.Contains(candidate.ProcessName,
                    StringComparer.OrdinalIgnoreCase);
            }
            return Path.IsPathFullyQualified(startInfo.FileName) &&
                string.Equals(candidate.MainModule?.FileName, startInfo.FileName,
                    StringComparison.OrdinalIgnoreCase);
        }), previous, token);
    }

    public static async Task InvokeShellAsync(Action invoke, Func<string, bool> matchesTitle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var previous = GetVisibleWindows();
        token.ThrowIfCancellationRequested();
        // Invoke synchronously on the caller's STA thread, before the first await.
        invoke();

        await TryBringToFrontAsync(window => {
            var className = new StringBuilder(32);
            if (GetClassName(window, className, className.Capacity) == 0 ||
                className.ToString() != "#32770") return false;
            var title = new StringBuilder(512);
            if (GetWindowText(window, title, title.Capacity) == 0) return false;
            return matchesTitle(title.ToString());
        }, previous, token);
    }

    public static async Task<bool> LaunchPackageAsync(AppListEntry entry, string family,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var previous = GetVisibleWindows();
        token.ThrowIfCancellationRequested();
        if (!await entry.LaunchAsync().AsTask(token)) return false;

        await TryBringToFrontAsync(window => {
            if (HasPackageFamily(window, family)) return true;
            var matches = false;
            EnumChildWindows(window, (child, _) => {
                matches = HasPackageFamily(child, family);
                return !matches;
            }, IntPtr.Zero);
            return matches;
        }, previous, token);
        return true;
    }

    private static async Task TryBringToFrontAsync(Func<IntPtr, bool> matches,
        HashSet<IntPtr> previous, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(2)) {
            token.ThrowIfCancellationRequested();
            foreach (var window in GetVisibleWindows().OrderBy(previous.Contains)) {
                token.ThrowIfCancellationRequested();
                if (!matches(window)) continue;
                if (IsIconic(window)) ShowWindowAsync(window, 9); // SW_RESTORE
                if (!SetForegroundWindow(window)) {
                    RaiseWithoutActivation(window);
                }
                return;
            }
            await Task.Delay(100, token);
        }
    }

    private static void RaiseWithoutActivation(IntPtr window)
    {
        // A WebSocket request does not grant foreground permission. Raise only
        // this matching window, without input-queue attachment or keyboard input.
        // Async positioning keeps an unresponsive target from blocking the bridge.
        const uint flags = 0x0001 | 0x0002 | 0x0010 | 0x4000; // NOSIZE | NOMOVE | NOACTIVATE | ASYNCWINDOWPOS
        var wasTopmost = (GetWindowLong(window, -20) & 0x0008) != 0; // GWL_EXSTYLE / WS_EX_TOPMOST
        var raised = false;
        try {
            raised = SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, flags); // HWND_TOPMOST
        }
        finally {
            // Preserve an existing topmost window; otherwise restore normal stacking.
            if (raised && !wasTopmost) {
                SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, flags); // HWND_NOTOPMOST
            }
        }
    }

    private static HashSet<IntPtr> GetVisibleWindows()
    {
        var windows = new HashSet<IntPtr>();
        EnumWindows((window, _) => {
            if (IsWindowVisible(window)) windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool WithWindowProcess(IntPtr window, Func<Process, bool> matches)
    {
        GetWindowThreadProcessId(window, out var id);
        if (id == 0) return false;
        try {
            using var process = Process.GetProcessById(checked((int)id));
            return matches(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            Win32Exception or NotSupportedException or OverflowException) {
            return false;
        }
    }

    private static bool HasPackageFamily(IntPtr window, string family) =>
        WithWindowProcess(window, process => {
            uint length = 0;
            if (GetPackageFamilyName(process.Handle, ref length, null) != 122 || length == 0) {
                return false; // ERROR_INSUFFICIENT_BUFFER
            }
            var name = new StringBuilder(checked((int)length));
            return GetPackageFamilyName(process.Handle, ref length, name) == 0 &&
                string.Equals(name.ToString(), family, StringComparison.OrdinalIgnoreCase);
        });
}
