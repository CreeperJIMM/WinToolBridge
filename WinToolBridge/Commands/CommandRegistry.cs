using System.Text.Json;
using WinToolBridge.Commands.Handlers;

namespace WinToolBridge.Commands;

public class CommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _handlers = new();

    public CommandRegistry()
    {
        Register(new SysInfoHandler());
        Register(new SysDetailsHandler());
        Register(new SysOpenShortcutHandler());
        Register(new NetDetailsHandler());
        Register(new NetWifiDetailsHandler());
        Register(new NetPortDetailsHandler());
        Register(new NetDnsBenchmarkHandler());
    }

    public void Register(ICommandHandler handler)
    {
        _handlers[handler.Action] = handler;
    }

    public async Task<(bool found, object? result, string? error)> DispatchAsync(
        string action,
        JsonElement? parameters,
        CancellationToken token)
    {
        if (!_handlers.TryGetValue(action, out var handler)) {
            return (false, null, $"未支援的指令動作: {action}");
        }

        try {
            var data = await handler.HandleAsync(parameters, token);
            return (true, data, null);
        }
        catch (Exception ex) {
            return (true, null, ex.Message);
        }
    }
}