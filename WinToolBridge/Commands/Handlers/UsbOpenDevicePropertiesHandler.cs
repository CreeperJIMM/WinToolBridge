using System.Diagnostics;
using System.Text.Json;
using WinToolBridge.Services.Usb;

namespace WinToolBridge.Commands.Handlers;

public sealed class UsbOpenDevicePropertiesHandler(UsbTreeService usb, Action<ProcessStartInfo>? start = null) : ICommandHandler
{
    public string Action => "USB:OPEN_DEVICE_PROPERTIES";
    public bool RequiresDeduplication => true;

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (parameters is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("nodeId", out var id) || id.ValueKind != JsonValueKind.String)
            throw new ArgumentException("A nodeId string is required.");
        var nodeId = id.GetString();
        if (nodeId == null || nodeId.Length != 68 || !nodeId.StartsWith("usb-", StringComparison.Ordinal) ||
            nodeId.Skip(4).Any(character => character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw new ArgumentException("Invalid USB nodeId.");

        // Reuse the cached tree and the existing live-device validation before opening.
        var result = await usb.GetNodeDetailsAsync(nodeId, token);
        var instanceId = result.details.windows?.instanceId;
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new InvalidOperationException("USB_DEVICE_PROPERTIES_UNAVAILABLE: This node has no Windows device properties.");
        await UsbDevicePropertiesService.OpenAsync(instanceId, token, start);
        return new { nodeId, opened = true };
    }
}
