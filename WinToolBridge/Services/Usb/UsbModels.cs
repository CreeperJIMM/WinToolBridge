namespace WinToolBridge.Services.Usb;

public sealed record UsbTreeNode(
    string nodeId, string? parentId, string kind, string name, int? portNumber,
    string connectionStatus, string? speed, string? vendorId, string? productId, string? error, UsbPortInfo? port = null,
    string? iconUrl = null, string? deviceIconUrl = null);

public sealed record UsbPortSummary(string[]? supportedProtocols, double? rxSpeedMbps, double? txSpeedMbps, string[] companionNodeIds);
public sealed record UsbTreeListNode(string nodeId, string? parentId, string kind, string name, int? portNumber,
    string connectionStatus, string? speed, bool hasError, UsbPortSummary? port, string? iconUrl, string? deviceIconUrl)
{
    internal static UsbTreeListNode From(UsbTreeNode node) => new(node.nodeId, node.parentId, node.kind, node.name,
        node.portNumber, node.connectionStatus, node.speed, node.error != null,
        node.port == null ? null : new(node.port.supportedProtocols, node.port.rxSpeedMbps, node.port.txSpeedMbps,
            (node.port.companions ?? []).Where(companion => companion.nodeId != null).Select(companion => companion.nodeId!).Distinct().ToArray()),
        node.iconUrl, node.deviceIconUrl);
}

public sealed record UsbDeviceDescriptor(
    string usbVersion, string deviceVersion, int deviceClass, int deviceSubclass, int deviceProtocol,
    int configurationCount, int currentConfiguration, int deviceAddress,
    string? manufacturer, string? product, string? serialNumber, int? openPipeCount = null,
    bool? isSelfPowered = null, int? maxPowerMilliAmps = null, UsbSpeedCapabilities? speedCapabilities = null);

public sealed record UsbSpeedCapabilities(double? maximumSpeedMbps, double? maximumRxLaneSpeedMbps, double? maximumTxLaneSpeedMbps);

public sealed record UsbWindowsInfo(
    string instanceId, string? description, string? manufacturer, string? deviceClass,
    string? service, string? location, string[] hardwareIds, uint? statusFlags, uint? problemCode,
    string? driverProvider, string? driverVersion, string? driverDate, string? driverInf, UsbPowerInfo? power = null,
    string? busReportedDescription = null);

public sealed record UsbPowerInfo(string? state, string[] supportedStates, string[] wakeFromStates);
public sealed record UsbDiskInfo(string name, string instanceId, string devicePath, int? diskNumber,
    long? sizeBytes, bool? isWritable, UsbVolumeInfo[]? volumes);
public sealed record UsbVolumeInfo(string volumeName, string[] mountPoints, string? label, string? fileSystem,
    long? sizeBytes, long? availableBytes, string? devicePath = null, string? kernelName = null,
    string? serialNumber = null, uint? clusterSizeBytes = null, UsbWindowsInfo? windows = null);

public sealed record UsbHubInfo(int portCount, bool? isBusPowered);
public sealed record UsbPortCompanion(string? nodeId, int companionIndex, int portNumber, string hubSymbolicLink);
public sealed record UsbPortInfo(bool? isUserConnectable, bool? isTypeC, bool? isDebugCapable, string[]? supportedProtocols,
    uint? properties = null, bool? hasMultipleCompanions = null, UsbPortCompanion[]? companions = null,
    bool? deviceIsSuperSpeedCapable = null, bool? deviceIsSuperSpeedPlusCapable = null,
    double? rxSpeedMbps = null, double? txSpeedMbps = null,
    int? rxLaneCount = null, int? txLaneCount = null, string? deviceUsbVersion = null,
    UsbSpeedCapabilities? deviceSpeedCapabilities = null);
public sealed record UsbNodeDetails(UsbTreeNode node, UsbDeviceDescriptor? usb, UsbWindowsInfo? windows, UsbHubInfo? hub, UsbPortInfo? port, string[] warnings,
    UsbDiskInfo[]? disks = null, UsbVolumeInfo? volume = null);
public sealed record UsbEnumeration(UsbTreeNode[] nodes, UsbNodeDetails? details, string[] warnings)
{
    // Trusted enumeration identities only. Never serialized or populated by the client.
    internal UsbQuerySnapshot? querySnapshot { get; init; }
}
public sealed record UsbTreeResult(long revision, UsbTreeListNode[] nodes, long timestamp, string[] warnings);
public sealed record UsbNodeDetailsResult(long revision, UsbNodeDetails details, long timestamp);
public sealed record UsbTreeChangedData(long revision, long timestamp);
