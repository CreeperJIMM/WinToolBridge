using System.Text.Json;
using WinToolBridge.Services.Usb;

namespace WinToolBridge.Commands.Handlers;

public sealed class UsbTreeHandler(UsbTreeService usb) : ICommandHandler
{
    public string Action => "USB:GET_TREE";

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        if (parameters is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Object) })
            throw new ArgumentException("USB:GET_TREE parameters must be an object or null.");
        return await usb.GetTreeAsync(token);
    }
}
