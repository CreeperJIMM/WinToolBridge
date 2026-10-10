namespace WinToolBridge.Services.Usb;

// Managed values only; every detail query opens and closes its own native resources.
internal sealed record UsbQueryTarget(UsbPnpDevice? device, string? hubPath, int? portNumber,
    string? portId, UsbHubInfo? hub, string[] portWarnings, UsbVolumeInfo? volume);

internal sealed record UsbQuerySnapshot(Dictionary<string, UsbQueryTarget> targets, List<UsbPnpDevice> devices);
