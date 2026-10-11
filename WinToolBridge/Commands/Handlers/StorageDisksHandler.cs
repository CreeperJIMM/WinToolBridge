using System.Text.Json;
using WinToolBridge.Services.Storage;

namespace WinToolBridge.Commands.Handlers;

public sealed class StorageDisksHandler(PhysicalDiskService storage) : ICommandHandler
{
    public string Action => "STORAGE:GET_DISKS";

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        if (parameters is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Object) })
            throw new ArgumentException("Parameters must be an object or null.");
        return await storage.GetDisksAsync(token);
    }
}
