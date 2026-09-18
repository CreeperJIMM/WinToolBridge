using System.Text.Json;
using WinToolBridge.Services;

namespace WinToolBridge.Commands.Handlers;

public class NetPortDetailsHandler : ICommandHandler
{
    public string Action => "NET:GET_PORT_DETAILS";

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        var portsTask = Task.Run(NetworkInfoProvider.GetListeningPorts, token);

        var ports = await portsTask;

        return new {
            ports,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }
}