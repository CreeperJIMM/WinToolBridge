using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DnsClient;

namespace WinToolBridge.Services;

public class DnsTargetServer
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("serverIp")]
    public string ServerIp { get; set; } = "";
}

public class DnsServerBenchmarkResult
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("recordType")]
    public string RecordType { get; set; } = "A";

    [JsonPropertyName("latencyMs")]
    public long LatencyMs { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "OK";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("records")]
    public List<string> Records { get; set; } = new();
}

public class DnsBenchmarkResponse
{
    [JsonPropertyName("domain")]
    public string Domain { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "A";

    [JsonPropertyName("results")]
    public List<DnsServerBenchmarkResult> Results { get; set; } = new();
}

public class DnsBenchmarkService
{
    [DllImport("iphlpapi.dll", CharSet = CharSet.Auto)]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "A", "AAAA", "CNAME", "TXT", "MX", "NS"
    };

    public async Task<DnsBenchmarkResponse> ResolveBenchmarkAsync(string rawDomain, string rawType, CancellationToken token)
    {
        var domain = rawDomain?.Trim().TrimEnd('.') ?? "";
        var type = string.IsNullOrWhiteSpace(rawType) ? "A" : rawType.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253 ||
            !Regex.IsMatch(domain, @"^([a-zA-Z0-9_-]+\.)+[a-zA-Z]{2,}$")) {
            throw new ArgumentException("無效的網域名稱格式");
        }

        if (!AllowedTypes.Contains(type)) {
            throw new ArgumentException($"不支援的查詢類型: {type}");
        }

        var targets = new List<DnsTargetServer>
        {
            new() { Provider = "Cloudflare", ServerIp = "1.1.1.1" },
            new() { Provider = "Google", ServerIp = "8.8.8.8" },
            new() { Provider = "HiNet", ServerIp = "168.95.1.1" },
            new() { Provider = "TWNIC", ServerIp = "101.101.101.101" },
            new() { Provider = "Quad9", ServerIp = "9.9.9.9" },
            new() { Provider = "OpenDNS", ServerIp = "208.67.222.222" },
            new() { Provider = "AliDNS", ServerIp = "223.5.5.5" }
        };

        var primaryDns = GetPrimaryWanDnsServer();
        if (!string.IsNullOrEmpty(primaryDns)) {
            targets.Insert(0, new DnsTargetServer {
                Provider = "System",
                ServerIp = primaryDns
            });
        }

        var qType = ParseQueryType(type);
        var tasks = targets.Select(t => QuerySingleServerAsync(t, domain, qType, type, token));
        var results = await Task.WhenAll(tasks);

        return new DnsBenchmarkResponse {
            Domain = domain,
            Type = type,
            Results = results.ToList()
        };
    }

    private static async Task<DnsServerBenchmarkResult> QuerySingleServerAsync(
        DnsTargetServer target,
        string domain,
        QueryType qType,
        string typeStr,
        CancellationToken token)
    {
        var result = new DnsServerBenchmarkResult {
            Provider = target.Provider,
            Server = target.ServerIp,
            RecordType = typeStr
        };

        if (!IPAddress.TryParse(target.ServerIp, out var ipAddr)) {
            result.Status = "ERROR";
            result.Error = "無效的 DNS 伺服器位址";
            return result;
        }

        try {
            var endpoint = new IPEndPoint(ipAddr, 53);
            var client = new LookupClient(new LookupClientOptions(endpoint) {
                Timeout = TimeSpan.FromSeconds(3),
                UseCache = false,
                Retries = 0
            });

            var sw = Stopwatch.StartNew();
            var queryResult = await client.QueryAsync(domain, qType, QueryClass.IN, token);
            sw.Stop();

            result.LatencyMs = sw.ElapsedMilliseconds;

            if (queryResult.HasError) {
                result.Status = "ERROR";
                result.Error = queryResult.ErrorMessage;
                return result;
            }

            var records = new List<string>();
            foreach (var record in queryResult.Answers) {
                switch (record) {
                    case DnsClient.Protocol.ARecord a:
                        records.Add(a.Address.ToString());
                        break;
                    case DnsClient.Protocol.AaaaRecord aaaa:
                        records.Add(aaaa.Address.ToString());
                        break;
                    case DnsClient.Protocol.CNameRecord cname:
                        records.Add(cname.CanonicalName.Value.TrimEnd('.'));
                        break;
                    case DnsClient.Protocol.TxtRecord txt:
                        records.AddRange(txt.Text);
                        break;
                    case DnsClient.Protocol.MxRecord mx:
                        records.Add($"{mx.Preference} {mx.Exchange.Value.TrimEnd('.')}");
                        break;
                    case DnsClient.Protocol.NsRecord ns:
                        records.Add(ns.NSDName.Value.TrimEnd('.'));
                        break;
                }
            }

            result.Records = records;
            result.Status = "OK";
        }
        catch (Exception ex) {
            result.Status = "ERROR";
            result.Error = ex.Message;
        }

        return result;
    }

    private static QueryType ParseQueryType(string type) => type switch {
        "A" => QueryType.A,
        "AAAA" => QueryType.AAAA,
        "CNAME" => QueryType.CNAME,
        "TXT" => QueryType.TXT,
        "MX" => QueryType.MX,
        "NS" => QueryType.NS,
        _ => QueryType.A
    };

    /// <summary>
    /// 獲取 Primary WAN 網卡的 DNS
    /// </summary>
    private static string? GetPrimaryWanDnsServer()
    {
        try {
            uint? primaryIfIndex = null;
            try {
                byte[] ipBytes = IPAddress.Parse("8.8.8.8").GetAddressBytes();
                if (GetBestInterface(BitConverter.ToUInt32(ipBytes, 0), out uint index) == 0) {
                    primaryIfIndex = index;
                }
            }
            catch { }

            var allAdapters = NetworkInterface.GetAllNetworkInterfaces();

            if (primaryIfIndex.HasValue) {
                foreach (var ni in allAdapters) {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    var ipProps = ni.GetIPProperties();
                    var ipv4Props = ipProps.GetIPv4Properties();
                    var ipv6Props = ipProps.GetIPv6Properties();
                    int ifIndex = ipv4Props?.Index ?? ipv6Props?.Index ?? -1;

                    if (ifIndex == (int)primaryIfIndex.Value) {
                        var dns = ipProps.DnsAddresses
                            .FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(d));

                        if (dns != null) return dns.ToString();

                        var gateway = ipProps.GatewayAddresses
                            .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(g.Address));

                        if (gateway != null) return gateway.Address.ToString();
                    }
                }
            }

            var candidate = allAdapters.FirstOrDefault(ni =>
                ni.OperationalStatus == OperationalStatus.Up &&
                (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
                !ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                !ni.Description.Contains("Radmin", StringComparison.OrdinalIgnoreCase) &&
                !ni.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase));

            if (candidate != null) {
                var dns = candidate.GetIPProperties().DnsAddresses
                    .FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(d));
                if (dns != null) return dns.ToString();
            }

            return null;
        }
        catch {
            return null;
        }
    }
}