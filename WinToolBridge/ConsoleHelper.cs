using System.Runtime.InteropServices;
using System.Text;
using WinToolBridge.Resources;

namespace WinToolBridge;

public static class ConsoleHelper
{
    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCP(uint wCodePageID);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    private const uint SC_CLOSE = 0xF060;
    private const uint MF_BYCOMMAND = 0x00000000;

    private static bool _isAllocated = false;
    private static System.Windows.Forms.Timer? _minimizeCheckTimer;

    /// <summary>
    /// 即時查詢控制台是否正顯示在螢幕上且未被最小化
    /// </summary>
    public static bool IsVisible {
        get {
            IntPtr hWnd = GetConsoleWindow();
            if (hWnd == IntPtr.Zero) return false;
            return IsWindowVisible(hWnd) && !IsIconic(hWnd);
        }
    }

    /// <summary>
    /// 雙擊切換
    /// </summary>
    public static void ToggleConsole()
    {
        if (IsVisible) {
            HideConsole();
        }
        else {
            ShowConsole();
        }
    }

    public static void ShowConsole()
    {
        if (!_isAllocated) {
            AllocConsole();
            SetConsoleOutputCP(65001);
            SetConsoleCP(65001);
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            var standardOutput = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(standardOutput);

            IntPtr handle = GetConsoleWindow();
            if (handle != IntPtr.Zero) {
                IntPtr sysMenu = GetSystemMenu(handle, false);
                if (sysMenu != IntPtr.Zero) {
                    DeleteMenu(sysMenu, SC_CLOSE, MF_BYCOMMAND);
                }
            }

            Console.Title = string.Format(Strings.ConsoleTitle, LocalBridgeServer.SERVER_VERSION);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("========================================");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(Strings.ConsoleStarted);
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"localhost:{LocalBridgeServer.DEFAULT_PORT}");
            Console.WriteLine($"127.0.0.1:{LocalBridgeServer.DEFAULT_PORT}");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("========================================\n");
            Console.ResetColor();

            StartMinimizeWatcher();
            _isAllocated = true;
        }

        IntPtr hWnd = GetConsoleWindow();
        if (hWnd != IntPtr.Zero) {
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }
    }

    public static void HideConsole()
    {
        IntPtr hWnd = GetConsoleWindow();
        if (hWnd != IntPtr.Zero) {
            ShowWindow(hWnd, SW_HIDE);
        }
    }

    private static void StartMinimizeWatcher()
    {
        _minimizeCheckTimer = new System.Windows.Forms.Timer {
            Interval = 200
        };
        _minimizeCheckTimer.Tick += (s, e) => {
            IntPtr hWnd = GetConsoleWindow();
            if (hWnd != IntPtr.Zero && IsIconic(hWnd)) {
                HideConsole();
            }
        };
        _minimizeCheckTimer.Start();
    }
}