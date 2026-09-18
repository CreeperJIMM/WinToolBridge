using System.Text.Json;

namespace WinToolBridge.Commands;

public interface ICommandHandler
{
    string Action { get; }

    Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token);
}