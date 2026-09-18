using System.Text.Json;
using WinToolBridge.Services;

namespace WinToolBridge.Commands.Handlers;

public class NetDnsBenchmarkHandler : ICommandHandler
{
    public string Action => "NET:RESOLVE_DNS_BENCHMARK";

    private readonly DnsBenchmarkService _dnsService = new();

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        if (parameters == null) {
            throw new ArgumentException("缺少查詢參數");
        }

        var domain = parameters.Value.TryGetProperty("domain", out var d) ? d.GetString() : null;
        var type = parameters.Value.TryGetProperty("type", out var t) ? t.GetString() : "A";

        if (string.IsNullOrWhiteSpace(domain)) {
            throw new ArgumentException("未提供網域名稱");
        }

        return await _dnsService.ResolveBenchmarkAsync(domain, type ?? "A", token);
    }
}