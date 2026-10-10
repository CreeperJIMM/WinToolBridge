using System.Text.Json;
using WinToolBridge.Services.Usb;

namespace WinToolBridge.Commands.Handlers;

public sealed class UsbNodeDetailsHandler(UsbTreeService usb) : ICommandHandler
{
    public string Action => "USB:GET_NODE_DETAILS";

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("nodeId", out var id) || id.ValueKind != JsonValueKind.String)
            throw new ArgumentException("A nodeId string is required.");
        var nodeId = id.GetString();
        if (nodeId == null || nodeId.Length != 68 || !nodeId.StartsWith("usb-", StringComparison.Ordinal) ||
            nodeId.Skip(4).Any(character => character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw new ArgumentException("Invalid USB nodeId.");
        return await usb.GetNodeDetailsAsync(nodeId, token);
    }
}
