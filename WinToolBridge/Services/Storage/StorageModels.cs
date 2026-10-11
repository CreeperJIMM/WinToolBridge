namespace WinToolBridge.Services.Storage;

public sealed record StorageAvailability(string status, int? errorCode = null, string? reason = null,
    bool requiresElevation = false);
public sealed record StorageDisk(string diskId, string? model, string? busType, long? sizeBytes,
    string[]? mountPoints, Dictionary<string, StorageAvailability> availability, string? iconUrl = null, bool? isSystemDisk = null);
public sealed record StorageDisksResult(StorageDisk[] disks, long timestamp);
public sealed record StorageIdentity(string? model, string? firmware, string? serialNumber, string? busType,
    long? sizeBytes, string? nvmeVersion);
public sealed record StorageCapabilities(bool? smartSupported, bool? trimEnabled, bool? volatileWriteCacheSupported);
public sealed record StorageHealth(string status, int criticalWarning, double? temperatureCelsius,
    int availableSparePercentage, int availableSpareThresholdPercentage, int percentageUsed,
    int estimatedRemainingLifePercentage);
public sealed record StorageUsage(string powerOnHours, string powerCycles, string hostReadBytes,
    string hostWrittenBytes, string unsafeShutdowns);
public sealed record StorageSmartAttribute(string id, string key, string rawHex, string rawValue, string unit);
public sealed record StorageSmart(string protocol, StorageSmartAttribute[] attributes);
public sealed record StorageConnection(int? currentPcieGeneration, int? currentLaneCount,
    int? maximumPcieGeneration, int? maximumLaneCount, string? interfaceName = null, StorageUsbConnection? usb = null);
public sealed record StorageUsbConnection(string nodeId, string? speed, double? rxSpeedMbps, double? txSpeedMbps,
    int? rxLaneCount, int? txLaneCount, WinToolBridge.Services.Usb.UsbSpeedCapabilities? speedCapabilities);
public sealed record StorageDiskDetails(string diskId, StorageIdentity identity, string[]? mountPoints,
    StorageConnection connection, StorageCapabilities capabilities, StorageHealth? health,
    StorageUsage? usage, StorageSmart? smart, Dictionary<string, StorageAvailability> availability);
public sealed record StorageDiskDetailsResult(StorageDiskDetails details, long timestamp, string? elevationIconUrl = null);
