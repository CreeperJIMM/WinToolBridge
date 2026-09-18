using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinToolBridge.Services;

public static class SystemInfoProvider
{
    #region Win32 API 定義

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint GetDriveType([MarshalAs(UnmanagedType.LPTStr)] string lpRootPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableID, byte[]? pFirmwareTableBuffer, uint bufferSize);

    private const uint RSMB = 0x52534D42; // 'RSMB'
    private const uint DRIVE_UNKNOWN = 0;
    private const uint DRIVE_NO_ROOT_DIR = 1;
    private const uint DRIVE_REMOVABLE = 2;
    private const uint DRIVE_FIXED = 3;
    private const uint DRIVE_REMOTE = 4; // 網路磁碟
    private const uint DRIVE_CDROM = 5;
    private const uint DRIVE_RAMDISK = 6;

    #endregion

    #region SMBIOS 記憶體頻率讀取 (微秒級，免 WMI)

    private static int GetMemoryFrequencyFromSmbios()
    {
        try {
            uint size = GetSystemFirmwareTable(RSMB, 0, null, 0);
            if (size == 0) return 0;

            byte[] buffer = new byte[size];
            if (GetSystemFirmwareTable(RSMB, 0, buffer, size) == 0) return 0;

            int offset = 8;
            int maxSpeed = 0;

            while (offset < buffer.Length) {
                if (offset + 4 > buffer.Length) break;
                byte type = buffer[offset];
                byte length = buffer[offset + 1];
                if (length < 4) break;

                if (type == 17 && offset + length <= buffer.Length) {
                    // 0x15: Speed
                    // 0x20: Configured Memory Clock Speed
                    int configuredSpeed = 0;
                    if (length >= 0x22) {
                        configuredSpeed = BitConverter.ToUInt16(buffer, offset + 0x20);
                    }

                    int speed = 0;
                    if (length >= 0x17) {
                        speed = BitConverter.ToUInt16(buffer, offset + 0x15);
                    }

                    int current = configuredSpeed > 0 ? configuredSpeed : speed;
                    if (current > maxSpeed) {
                        maxSpeed = current;
                    }
                }
                offset += length;
                while (offset < buffer.Length - 1 && !(buffer[offset] == 0 && buffer[offset + 1] == 0)) {
                    offset++;
                }
                offset += 2;
            }

            return maxSpeed;
        }
        catch {
            return 0;
        }
    }

    #endregion

    #region Log 輸出

    private static readonly object _logLock = new();

    private static void LogBenchmark(string target, long elapsedMs, string extra = "")
    {
        lock (_logLock) {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");

            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{target.PadRight(18)} ");

            if (elapsedMs > 500) {
                Console.ForegroundColor = ConsoleColor.Red;
            }
            else if (elapsedMs > 50) {
                Console.ForegroundColor = ConsoleColor.Yellow;
            }
            else {
                Console.ForegroundColor = ConsoleColor.Green;
            }

            Console.Write($"{elapsedMs,4} ms");
            Console.ResetColor();

            if (!string.IsNullOrEmpty(extra)) {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($" ({extra})");
                Console.ResetColor();
            }

            Console.WriteLine();
        }
    }

    #endregion

    public static object GetOsInfo()
    {
        var sw = Stopwatch.StartNew();
        try {
            var res = new {
                name = RuntimeInformation.OSDescription,
                arch = RuntimeInformation.OSArchitecture.ToString(),
                uptimeSeconds = Environment.TickCount64 / 1000
            };
            sw.Stop();
            LogBenchmark("OS & 開機時間", sw.ElapsedMilliseconds);
            return res;
        }
        catch (Exception ex) {
            sw.Stop();
            LogBenchmark("OS & 開機時間", sw.ElapsedMilliseconds, $"例外: {ex.Message}");
            return new { name = "Unknown Windows", arch = "Unknown", uptimeSeconds = 0L };
        }
    }

    public static object GetCpuInfo()
    {
        var sw = Stopwatch.StartNew();
        string model = "Unknown Processor";
        try {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var cpuName = key?.GetValue("ProcessorNameString")?.ToString();
            if (!string.IsNullOrWhiteSpace(cpuName)) {
                model = cpuName.Trim();
            }
            else {
                model = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? model;
            }
        }
        catch { }

        sw.Stop();
        LogBenchmark("CPU 規格", sw.ElapsedMilliseconds, model);
        return new {
            model = model,
            logicalProcessors = Environment.ProcessorCount
        };
    }

    public static object GetMemoryInfo()
    {
        var sw = Stopwatch.StartNew();
        try {
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus)) {
                int freq = GetMemoryFrequencyFromSmbios();
                long totalMb = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                long availMb = (long)(memStatus.ullAvailPhys / (1024 * 1024));
                long usedMb = totalMb - availMb;

                sw.Stop();
                LogBenchmark("實體記憶體", sw.ElapsedMilliseconds, $"{memStatus.dwMemoryLoad}% | {freq} MHz");

                return new {
                    totalMb = totalMb,
                    availableMb = availMb,
                    usedMb = usedMb,
                    loadPercentage = (int)memStatus.dwMemoryLoad,
                    frequencyMHz = freq
                };
            }
        }
        catch { }

        long fallbackMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        sw.Stop();
        LogBenchmark("實體記憶體", sw.ElapsedMilliseconds, "Fallback");
        return new {
            totalMb = fallbackMb,
            availableMb = 0L,
            usedMb = 0L,
            loadPercentage = 0,
            frequencyMHz = 0
        };
    }

    public static object GetMotherboardInfo()
    {
        var sw = Stopwatch.StartNew();
        string manufacturer = "Unknown";
        string product = "Unknown";
        string biosVersion = "Unknown";
        string biosDate = "Unknown";

        try {
            using var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (biosKey != null) {
                manufacturer = biosKey.GetValue("BaseBoardManufacturer")?.ToString()?.Trim() ?? manufacturer;
                product = biosKey.GetValue("BaseBoardProduct")?.ToString()?.Trim() ?? product;
                biosVersion = biosKey.GetValue("BIOSVersion")?.ToString()?.Trim() ?? biosVersion;
                biosDate = biosKey.GetValue("BIOSReleaseDate")?.ToString()?.Trim() ?? biosDate;
            }
        }
        catch { }

        sw.Stop();
        LogBenchmark("主機板與 BIOS", sw.ElapsedMilliseconds, $"{product} / {biosVersion}");
        return new {
            manufacturer,
            product,
            biosVersion,
            biosDate
        };
    }

    public static List<object> GetGpuInfo()
    {
        var sw = Stopwatch.StartNew();
        var list = new List<object>();

        try {
            const string videoClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var baseKey = Registry.LocalMachine.OpenSubKey(videoClassKey);
            if (baseKey != null) {
                foreach (var subKeyName in baseKey.GetSubKeyNames()) {
                    if (subKeyName.Length == 4 && int.TryParse(subKeyName, out _)) {
                        using var subKey = baseKey.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        var driverDesc = subKey.GetValue("DriverDesc")?.ToString();
                        var driverVersion = subKey.GetValue("DriverVersion")?.ToString() ?? "Unknown";

                        if (!string.IsNullOrWhiteSpace(driverDesc)) {
                            list.Add(new {
                                model = driverDesc.Trim(),
                                driverVersion = driverVersion.Trim()
                            });
                        }
                    }
                }
            }
        }
        catch { }

        sw.Stop();
        LogBenchmark("顯示卡規格", sw.ElapsedMilliseconds, $"{list.Count} 張顯卡");
        return list;
    }

    public static List<object> GetStorageInfo()
    {
        var totalSw = Stopwatch.StartNew();
        var list = new List<object>();

        try {
            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives) {
                var driveSw = Stopwatch.StartNew();
                try {
                    uint type = GetDriveType(drive.Name);
                    if (type == DRIVE_REMOTE) {
                        driveSw.Stop();
                        LogBenchmark($"磁碟 [{drive.Name}]", driveSw.ElapsedMilliseconds, "網路磁碟 (略過)");
                        continue;
                    }

                    if (type == DRIVE_CDROM || type <= DRIVE_NO_ROOT_DIR) {
                        driveSw.Stop();
                        LogBenchmark($"磁碟 [{drive.Name}]", driveSw.ElapsedMilliseconds, "非實體 (略過)");
                        continue;
                    }

                    if (!drive.IsReady) {
                        driveSw.Stop();
                        LogBenchmark($"磁碟 [{drive.Name}]", driveSw.ElapsedMilliseconds, "未就緒");
                        continue;
                    }

                    double totalGb = Math.Round((double)drive.TotalSize / (1024 * 1024 * 1024), 1);
                    double freeGb = Math.Round((double)drive.AvailableFreeSpace / (1024 * 1024 * 1024), 1);
                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本機磁碟" : drive.VolumeLabel;

                    driveSw.Stop();
                    LogBenchmark($"磁碟 [{drive.Name}]", driveSw.ElapsedMilliseconds, $"{label} ({totalGb} GB)");

                    list.Add(new {
                        name = drive.Name,
                        label = label,
                        format = drive.DriveFormat,
                        driveType = drive.DriveType.ToString(),
                        totalGb = totalGb,
                        freeGb = freeGb,
                        usedPercentage = (int)Math.Round(((totalGb - freeGb) / totalGb) * 100)
                    });
                }
                catch (Exception ex) {
                    driveSw.Stop();
                    LogBenchmark($"磁碟 [{drive.Name}]", driveSw.ElapsedMilliseconds, $"例外: {ex.Message}");
                }
            }
        }
        catch (Exception ex) {
            LogBenchmark("磁碟枚舉", 0, $"失敗: {ex.Message}");
        }

        totalSw.Stop();
        LogBenchmark("磁碟模組總計", totalSw.ElapsedMilliseconds);
        return list;
    }

    public static object GetBatteryStatus()
    {
        var sw = Stopwatch.StartNew();
        try {
            if (GetSystemPowerStatus(out var status)) {
                bool hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent != 255;
                bool isCharging = status.ACLineStatus == 1;

                sw.Stop();
                LogBenchmark("電源與電池", sw.ElapsedMilliseconds, hasBattery ? $"{status.BatteryLifePercent}%" : "AC 市電");
                return new {
                    isLaptop = hasBattery,
                    percent = hasBattery ? (int)status.BatteryLifePercent : 100,
                    isCharging = isCharging
                };
            }
        }
        catch { }

        sw.Stop();
        LogBenchmark("電源與電池", sw.ElapsedMilliseconds, "Fallback");
        return new {
            isLaptop = false,
            percent = 100,
            isCharging = true
        };
    }
}