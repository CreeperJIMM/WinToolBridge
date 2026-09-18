using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinToolBridge.Services;

public static class NetworkInfoProvider
{
    #region Win32 API - IP Helper & Routing

    [DllImport("iphlpapi.dll", CharSet = CharSet.Auto)]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved = 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved = 0);

    private const int AF_INET = 2;
    private const int MIB_TCP_STATE_LISTEN = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state; public uint localAddr; public byte localPort1; public byte localPort2;
        public byte localPort3; public byte localPort4; public uint remoteAddr; public byte remotePort1;
        public byte remotePort2; public byte remotePort3; public byte remotePort4; public int owningPid;
        public ushort LocalPort => (ushort)((localPort1 << 8) | localPort2);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr; public byte localPort1; public byte localPort2;
        public byte localPort3; public byte localPort4; public int owningPid;
        public ushort LocalPort => (ushort)((localPort1 << 8) | localPort2);
    }

    #endregion

    #region 1. 網路拓撲與介面狀態 (NET:GET_DETAILS)

    private static uint? GetDefaultGatewayInterfaceIndex()
    {
        try {
            byte[] ipBytes = IPAddress.Parse("8.8.8.8").GetAddressBytes();
            if (GetBestInterface(BitConverter.ToUInt32(ipBytes, 0), out uint index) == 0) return index;
        }
        catch { }
        return null;
    }

    private static string DetectMedium(NetworkInterface ni)
    {
        string desc = ni.Description;
        if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
            ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            return "Virtual";

        if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "WiFi";
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) return "Ethernet";
        return "Other";
    }

    public static object GetNetworkTopology()
    {
        uint? primaryIfIndex = GetDefaultGatewayInterfaceIndex();
        var allAdapters = NetworkInterface.GetAllNetworkInterfaces();
        var physicalAdapters = new List<object>();
        var virtualTunnels = new List<object>();
        object? primaryWanObj = null;

        foreach (var ni in allAdapters) {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.OperationalStatus != OperationalStatus.Up)
                continue;

            IPInterfaceProperties ipProps;
            try { ipProps = ni.GetIPProperties(); } catch { continue; }

            IPv4InterfaceProperties? ipv4Props = null;
            try { ipv4Props = ipProps.GetIPv4Properties(); } catch { }

            IPv6InterfaceProperties? ipv6Props = null;
            try { ipv6Props = ipProps.GetIPv6Properties(); } catch { }

            int ifIndex = ipv4Props?.Index ?? ipv6Props?.Index ?? -1;
            bool isPrimaryWan = primaryIfIndex.HasValue && ifIndex == (int)primaryIfIndex.Value;
            string medium = DetectMedium(ni);

            var ipv4 = ipProps.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            var ipv6 = ipProps.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6);

            var gateways = ipProps.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork).Select(g => g.Address.ToString()).ToList();
            var dnsServers = ipProps.DnsAddresses.Select(d => d.ToString()).ToList();

            string mac = "";
            try { mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))); } catch { }

            var adapterInfo = new {
                index = ifIndex,
                name = ni.Name,
                description = ni.Description,
                medium = medium,
                isPrimaryWan = isPrimaryWan,
                speedMbps = ni.Speed > 0 ? ni.Speed / 1_000_000 : 0,
                ip = ipv4?.Address.ToString() ?? "",
                ipv6 = ipv6?.Address.ToString() ?? "",
                netmask = ipv4?.IPv4Mask?.ToString() ?? "",
                gateways = gateways,
                dnsServers = dnsServers,
                macAddress = mac,
                isDhcpEnabled = ipv4Props?.IsDhcpEnabled ?? false
            };

            if (isPrimaryWan) primaryWanObj = adapterInfo;
            if (medium == "Virtual") virtualTunnels.Add(adapterInfo);
            else physicalAdapters.Add(adapterInfo);
        }

        return new { primaryWan = primaryWanObj ?? physicalAdapters.FirstOrDefault(), physicalAdapters = physicalAdapters, virtualTunnels = virtualTunnels };
    }

    #endregion

    #region 2. Wi-Fi 詳細射頻參數 (NET:GET_WIFI_DETAILS) - .NET 8 WinRT API (零延遲版)

    private static int FrequencyToChannel(int freqKHz)
    {
        int mhz = freqKHz / 1000;
        if (mhz == 2484) return 14;
        if (mhz >= 2412 && mhz <= 2472) return (mhz - 2412) / 5 + 1;
        if (mhz >= 5170 && mhz <= 5825) return (mhz - 5170) / 5 + 34;
        if (mhz >= 5925 && mhz <= 7125) return (mhz - 5925) / 5 + 1;
        return 0;
    }

    private static string FrequencyToBand(int freqKHz)
    {
        int mhz = freqKHz / 1000;
        if (mhz >= 2400 && mhz < 2500) return "2.4 GHz";
        if (mhz >= 5000 && mhz < 5900) return "5 GHz";
        if (mhz >= 5900 && mhz <= 7125) return "6 GHz (Wi-Fi 6E/7)";
        return "Unknown";
    }

    public static object? GetWifiDetails()
    {
        return Task.Run(async () => await GetWifiDetailsAsync()).GetAwaiter().GetResult();
    }

    public static async Task<object?> GetWifiDetailsAsync()
    {
        try {
            var connectionProfiles = global::Windows.Networking.Connectivity.NetworkInformation.GetConnectionProfiles();

            var wifiProfile = connectionProfiles.FirstOrDefault(p =>
                p.IsWlanConnectionProfile &&
                p.GetNetworkConnectivityLevel() != global::Windows.Networking.Connectivity.NetworkConnectivityLevel.None);

            if (wifiProfile == null) return null;

            string currentSsid = wifiProfile.ProfileName;
            byte? signalBars = wifiProfile.GetSignalBars();
            int signalQualityPercent = signalBars.HasValue ? (int)(signalBars.Value * 20) : 0;

            var access = await global::Windows.Devices.WiFi.WiFiAdapter.RequestAccessAsync();
            if (access != global::Windows.Devices.WiFi.WiFiAccessStatus.Allowed) {
                return new { interfaceName = "Wi-Fi", ssid = currentSsid, bssid = "", signalQualityPercent, rssi = -100, frequencyMhz = 0, band = "Unknown", channel = 0 };
            }

            var adapters = await global::Windows.Devices.WiFi.WiFiAdapter.FindAllAdaptersAsync();
            if (adapters == null || adapters.Count == 0) return null;

            foreach (var adapter in adapters) {
                if (adapter.NetworkReport?.AvailableNetworks == null) continue;

                var targetNetwork = adapter.NetworkReport.AvailableNetworks
                    .Where(n => string.Equals(n.Ssid, currentSsid, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(n => n.NetworkRssiInDecibelMilliwatts)
                    .FirstOrDefault();

                if (targetNetwork != null) {
                    int freqKhz = targetNetwork.ChannelCenterFrequencyInKilohertz;
                    int rssi = (int)targetNetwork.NetworkRssiInDecibelMilliwatts;
                    int calculatedQuality = Math.Clamp((rssi + 100) * 2, 0, 100);

                    return new
                    {
                        interfaceName = adapter.NetworkAdapter?.NetworkAdapterId.ToString() ?? "Wi-Fi",
                        ssid = targetNetwork.Ssid,
                        bssid = targetNetwork.Bssid,
                        signalQualityPercent = calculatedQuality,
                        rssi = rssi,
                        frequencyMhz = freqKhz / 1000,
                        band = FrequencyToBand(freqKhz),
                        channel = FrequencyToChannel(freqKhz)
                    };
                }
            }
            return new { interfaceName = "Wi-Fi", ssid = currentSsid, bssid = "", signalQualityPercent, rssi = -100, frequencyMhz = 0, band = "Unknown", channel = 0 };
        }
        catch (Exception ex) {
            Debug.WriteLine($"[GetWifiDetailsAsync Error] {ex}");
            return null;
        }
    }

    #endregion

    #region 3. 監聽通訊埠與處理程序反查 (NET:GET_PORT_DETAILS)

    public static List<object> GetListeningPorts()
    {
        var list = new List<object>();
        var procCache = new Dictionary<int, (string Name, string Path)>();

        (string Name, string Path) GetProcessDetails(int pid)
        {
            if (pid <= 0) return ("System Idle", "");
            if (pid == 4) return ("System", "");
            if (procCache.TryGetValue(pid, out var cached)) return cached;

            try {
                using var proc = Process.GetProcessById(pid);
                string path = "";
                try { path = proc.MainModule?.FileName ?? ""; } catch { }
                var val = (proc.ProcessName + ".exe", path);
                procCache[pid] = val;
                return val;
            }
            catch { var val = ($"PID: {pid}", ""); procCache[pid] = val; return val; }
        }

        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL);
        IntPtr tcpTablePtr = Marshal.AllocHGlobal(size);
        try {
            if (GetExtendedTcpTable(tcpTablePtr, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL) == 0) {
                int rowCount = Marshal.ReadInt32(tcpTablePtr);
                IntPtr rowPtr = IntPtr.Add(tcpTablePtr, 4);
                for (int i = 0; i < rowCount; i++) {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, Marshal.SizeOf<MIB_TCPROW_OWNER_PID>());
                    if (row.state == MIB_TCP_STATE_LISTEN) {
                        var (procName, procPath) = GetProcessDetails(row.owningPid);
                        list.Add(new { protocol = "TCP", port = (int)row.LocalPort, address = new IPAddress(row.localAddr).ToString(), pid = row.owningPid, processName = procName, processPath = procPath });
                    }
                }
            }
        }
        finally { Marshal.FreeHGlobal(tcpTablePtr); }

        int udpSize = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref udpSize, true, AF_INET, UDP_TABLE_OWNER_PID);
        IntPtr udpTablePtr = Marshal.AllocHGlobal(udpSize);
        try {
            if (GetExtendedUdpTable(udpTablePtr, ref udpSize, true, AF_INET, UDP_TABLE_OWNER_PID) == 0) {
                int rowCount = Marshal.ReadInt32(udpTablePtr);
                IntPtr rowPtr = IntPtr.Add(udpTablePtr, 4);
                for (int i = 0; i < rowCount; i++) {
                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, Marshal.SizeOf<MIB_UDPROW_OWNER_PID>());
                    var (procName, procPath) = GetProcessDetails(row.owningPid);
                    list.Add(new { protocol = "UDP", port = (int)row.LocalPort, address = new IPAddress(row.localAddr).ToString(), pid = row.owningPid, processName = procName, processPath = procPath });
                }
            }
        }
        finally { Marshal.FreeHGlobal(udpTablePtr); }

        list.Sort((a, b) => ((int)((dynamic)a).port).CompareTo((int)((dynamic)b).port));
        return list;
    }

    #endregion
}