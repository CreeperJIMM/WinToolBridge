using System.Text.Json;
using WinToolBridge.Commands;

namespace WinToolBridge.Events;

public sealed class EventCommandHandler(string action, EventSession session) : ICommandHandler
{
    public string Action { get; } = action;
    public bool RequiresDeduplication => true;

    public Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Action == "EVENT:RESET") {
            if (parameters is JsonElement reset && reset.ValueKind != JsonValueKind.Null &&
                (reset.ValueKind != JsonValueKind.Object || reset.EnumerateObject().Any())) {
                throw new ArgumentException("EVENT:RESET accepts only empty parameters.");
            }
            return Task.FromResult<object?>(session.Reset());
        }

        if (parameters is not JsonElement value || value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("event", out var name) || name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString())) {
            throw new ArgumentException("A nonempty event string is required.");
        }

        return Task.FromResult<object?>(Action == "EVENT:ON" ? session.On(name.GetString()!) : session.Off(name.GetString()!));
    }
}
