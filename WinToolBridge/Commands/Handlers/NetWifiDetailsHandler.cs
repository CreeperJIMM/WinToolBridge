using System.Text.Json;
using WinToolBridge.Services;

namespace WinToolBridge.Commands.Handlers;

public class NetWifiDetailsHandler : ICommandHandler
{
    public string Action => "NET:GET_WIFI_DETAILS";

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        var wifiTask = Task.Run(NetworkInfoProvider.GetWifiDetails, token);

        var wifi = await wifiTask;

        return new {
            wifi,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }
}