using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace WinToolBridge.Commands.Handlers;

public class SysInfoHandler : ICommandHandler
{
    public string Action => "SYS:GET_INFO";

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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        var result = new {
            os = GetFriendlyOsName(),
            machineName = Environment.MachineName,
            cpuModel = GetCpuModelName(),
            logicalCores = Environment.ProcessorCount,
            totalMemoryMb = GetTotalPhysicalMemoryMb()
        };

        return Task.FromResult<object?>(result);
    }

    /// <summary>
    /// 取得 CPU 完整名稱
    /// </summary>
    private static string GetCpuModelName()
    {
        try {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var cpuName = key?.GetValue("ProcessorNameString")?.ToString();
            if (!string.IsNullOrWhiteSpace(cpuName)) {
                return cpuName.Trim();
            }
        }
        catch { }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown Processor";
    }

    /// <summary>
    /// 取得實體記憶體總量 (MB)
    /// </summary>
    private static long GetTotalPhysicalMemoryMb()
    {
        try {
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus)) {
                return (long)(memStatus.ullTotalPhys / (1024 * 1024));
            }
        }
        catch { }

        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
    }

    /// <summary>
    /// 取得作業系統版本
    /// </summary>
    private static string GetFriendlyOsName()
    {
        return $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
    }
}