using System.Text.Json;

namespace WinToolBridge.Commands;

public interface ICommandHandler
{
    string Action { get; }

    // Opt in for side effects that must not be repeated by a transport retry.
    bool RequiresDeduplication => false;

    Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token);
}
