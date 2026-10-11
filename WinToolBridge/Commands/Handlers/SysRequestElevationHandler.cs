using System.Text.Json;
using WinToolBridge.Services;

namespace WinToolBridge.Commands.Handlers;

public sealed record RequestElevationResult(string status);

public sealed class SysRequestElevationHandler(ElevationService elevation) : ICommandHandler
{
    public string Action => "SYS:REQUEST_ELEVATION";
    public bool RequiresDeduplication => true;

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        if (parameters is { ValueKind: not JsonValueKind.Null } value &&
            (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any()))
            throw new ArgumentException("Elevation takes no parameters.");
        token.ThrowIfCancellationRequested();
        var result = await elevation.ElevateAsync(token);
        return new RequestElevationResult(result switch {
            ElevationResult.Success => "success",
            ElevationResult.Cancelled => "cancelled",
            _ => "failed"
        });
    }
}
