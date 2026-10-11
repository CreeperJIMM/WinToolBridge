using System.Text.Json;
using WinToolBridge.Commands.Handlers;
using WinToolBridge.Events;
using WinToolBridge.Services.Usb;
using WinToolBridge.Services.Storage;
using WinToolBridge.Services;

namespace WinToolBridge.Commands;

public class CommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _handlers = new();

    public CommandRegistry(EventSession? events = null, UsbTreeService? usb = null,
        ImageAssetService? images = null, string? assetBaseUrl = null, ElevationService? elevation = null)
    {
        Register(new SysInfoHandler());
        Register(new SysDetailsHandler());
        Register(new SysOpenShortcutHandler());
        Register(new SysOpenDriveHandler());
        Register(new SysOpenGpuControlPanelHandler());
        if (elevation != null) Register(new SysRequestElevationHandler(elevation));
        Register(new NetDetailsHandler());
        Register(new NetWifiDetailsHandler());
        Register(new NetPortDetailsHandler());
        Register(new NetDnsBenchmarkHandler());
        Register(new NetOpenAdapterDetailsHandler());
        usb ??= new UsbTreeService();
        var storage = new PhysicalDiskService(images, assetBaseUrl, elevation, usb);
        Register(new StorageDisksHandler(storage));
        Register(new StorageDiskDetailsHandler(storage));
        Register(new UsbTreeHandler(usb));
        Register(new UsbNodeDetailsHandler(usb));
        Register(new UsbOpenDevicePropertiesHandler(usb));
        if (events != null) {
            Register(new EventCommandHandler("EVENT:ON", events));
            Register(new EventCommandHandler("EVENT:OFF", events));
            Register(new EventCommandHandler("EVENT:RESET", events));
        }
    }

    public void Register(ICommandHandler handler)
    {
        _handlers[handler.Action] = handler;
    }

    internal bool RequiresDeduplication(string action) =>
        _handlers.TryGetValue(action, out var handler) && handler.RequiresDeduplication;

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
