using System.Text.Json;
using WinToolBridge.Services.Storage;

namespace WinToolBridge.Commands.Handlers;

public sealed class StorageDiskDetailsHandler(PhysicalDiskService storage) : ICommandHandler
{
    public string Action => "STORAGE:GET_DISK_DETAILS";

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("diskId", out var id) || id.ValueKind != JsonValueKind.String)
            throw new ArgumentException("A diskId string is required.");
        var diskId = id.GetString();
        PhysicalDiskService.ValidateId(diskId);
        return await storage.GetDiskDetailsAsync(diskId!, token);
    }
}
