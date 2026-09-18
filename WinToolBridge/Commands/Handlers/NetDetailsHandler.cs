using System.Text.Json;
using WinToolBridge.Services;

namespace WinToolBridge.Commands.Handlers;

public class NetDetailsHandler : ICommandHandler
{
    public string Action => "NET:GET_DETAILS";

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        var topologyTask = Task.Run(NetworkInfoProvider.GetNetworkTopology, token);

        var topology = await topologyTask;

        return new {
            network = topology,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }
}