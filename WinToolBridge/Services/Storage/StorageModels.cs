using System.Text.Json.Serialization;

namespace WinToolBridge.Services.Storage;

public sealed record StorageAvailability(string status, int? errorCode = null, string? reason = null,
    bool requiresElevation = false);
public sealed record StorageDisk(string diskId, string? model, string? busType, long? sizeBytes,
    string[]? mountPoints, Dictionary<string, StorageAvailability> availability, string? iconUrl = null, bool? isSystemDisk = null,
    string mediaType = "unknown", string protocol = "unknown");
public sealed record StorageDisksResult(StorageDisk[] disks, long timestamp);
public sealed record StorageIdentity(string? model, string? firmware, string? serialNumber, string? busType,
    long? sizeBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? nvmeVersion,
    string mediaType = "unknown", string protocol = "unknown",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? rotationRateRpm = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? logicalSectorSizeBytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? physicalSectorSizeBytes = null);
public sealed record StorageCapabilities(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? smartSupported,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? trimEnabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? volatileWriteCacheSupported,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? smartEnabled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? trimSupported = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? writeCacheEnabled = null);
public sealed record StorageHealth(string status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? criticalWarning = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? temperatureCelsius = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? availableSparePercentage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? availableSpareThresholdPercentage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? percentageUsed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? estimatedRemainingLifePercentage = null,
    string protocol = "NVMe",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? reallocatedSectors = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? pendingSectors = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? offlineUncorrectableSectors = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? interfaceCrcErrors = null);
public sealed record StorageUsage(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? powerOnHours = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? powerCycles = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? hostReadBytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? hostWrittenBytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? unsafeShutdowns = null,
    string protocol = "NVMe");
public sealed record StorageSmartAttribute(string id, string key, string rawHex, string rawValue, string unit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? current = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? worst = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? threshold = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? flags = null);
public sealed record StorageSmart(string protocol, StorageSmartAttribute[] attributes);
public sealed record StorageConnection(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? currentPcieGeneration,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? currentLaneCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? maximumPcieGeneration,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? maximumLaneCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? interfaceName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StorageUsbConnection? usb = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? currentSataSpeedGbps = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? maximumSataSpeedGbps = null);
public sealed record StorageUsbConnection(string nodeId, string? speed, double? rxSpeedMbps, double? txSpeedMbps,
    int? rxLaneCount, int? txLaneCount, WinToolBridge.Services.Usb.UsbSpeedCapabilities? speedCapabilities);
public sealed record StorageDiskDetails(string diskId, StorageIdentity identity, string[]? mountPoints,
    StorageConnection connection, StorageCapabilities capabilities,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StorageHealth? health,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StorageUsage? usage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StorageSmart? smart, Dictionary<string, StorageAvailability> availability);
public sealed record StorageDiskDetailsResult(StorageDiskDetails details, long timestamp, string? elevationIconUrl = null);

public sealed record StorageProtocolResult(StorageIdentity? identity, StorageCapabilities capabilities,
    StorageHealth? health, StorageUsage? usage, StorageSmart? smart,
    StorageAvailability identifyAvailability, StorageAvailability smartAvailability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StorageConnection? connection = null)
{
    internal static StorageProtocolResult Failure(StorageAvailability state) =>
        new(null, new(null, null, null), null, null, null, state, state);
}
