# Physical disk queries

Read-only, request/response APIs using the existing correlated DAT/ACK flow. No events, polling, automatic elevation, driver installation, or disk writes are added. An ACK still acknowledges receipt, not command completion.

## Commands

- `STORAGE:GET_DISKS`, params `null` or `{}`: returns `{ disks, timestamp }`.
- `STORAGE:GET_DISK_DETAILS`, params `{ "diskId": "disk-<64 uppercase hex characters>" }`: returns `{ details, timestamp, elevationIconUrl }`; the icon URL is optional and points to the local Windows UAC shield image asset.

Use `storageCommands.getDisks()` and `storageCommands.getDiskDetails(diskId)` from the frontend command barrel. Types are defined in `src/lib/wintool/types/storage.ts`; the localized page is `/wintool/disk-info`.

Disk IDs hash the enumerated interface path and PnP instance ID. They identify currently enumerated devices, not array positions or drive letters, and are not guaranteed to survive hardware/driver changes. Details requests re-enumerate and resolve the ID; no client-provided path is opened. Invalid IDs, missing devices, and detected removal during details queries use the existing command error response.

## List

Each disk contains `diskId`, `model`, `busType`, `sizeBytes`, `mountPoints`, `availability`, `mediaType` (`hdd`, `ssd`, `unknown`) and `protocol` (`ATA`, `NVMe`, `unknown`). Media type and transport/protocol are independent; SATA SSDs also use ATA. Enumeration reuses the existing SetupAPI interface enumerator with GUID_DEVINTERFACE_DISK; it does not scan arbitrary PhysicalDrive numbers. When the storage descriptor is readable, non-direct-access devices and Virtual/FileBackedVirtual/Spaces bus types are excluded. A device whose descriptor cannot be read remains visible with partial PnP information; its physical nature cannot be confirmed in that case.

`iconUrl` is the actual Explorer drive icon from SHGetFileInfo, served as a PNG through the existing shared image assets. The Windows volume's drive root takes precedence; otherwise the first drive letter is used. Missing letters or Shell failures return null and the frontend uses its fallback icon. `isSystemDisk` means the physical disk contains the volume of the currently running Windows installation, determined from GetVolumePathName and volume extents. It does not mean the EFI partition and does not assume C:. A confirmed match is true; a complete scan without a match is false; unknown or incomplete mapping is null. Both fields are optional in the frontend to support an older bridge.

The list probes direct NVMe SMART/Health and excludes devices only when the query explicitly returns `notSupported`. This is an unsupported query result from the driver, not definitive proof that the underlying hardware has no SMART capability. Supported Realtek bridges remain listed and signal `requiresElevation` when needed; pass-through is deferred to details. Direct ATA/SATA disks remain visible, including disks with unsupported/disabled SMART. The list reads ATA Identify with access 0, but defers SMART pass-through to details and signals `requiresElevation` when an administrator helper is needed. Other USB bridges, RAID and unimplemented protocols remain visible with unknown hardware SMART support. Unknown protocol, access-denied and transient failures remain visible. Filtering does not depend on filesystem type or removable-media status. Details reread SMART for a fresh result.

Capacity is the device's physical capacity, not the size of its first partition. Volume extents determine mount associations, including volumes spanning multiple disks and directory mount points. `mountPoints: []` means no mounted paths were found in a complete scan; `null` means unknown. An incomplete scan may return known paths with `availability.mountPoints.status: "partial"`.

## Details

- `identity`: model, firmware, serialNumber, busType, sizeBytes, mediaType, protocol; optional nvmeVersion, rotationRateRpm, logicalSectorSizeBytes, physicalSectorSizeBytes. ATA RPM 1 identifies solid state; valid rotational RPM identifies HDD; unknown/reserved values stay unknown.
- `mountPoints`: the disk's current mounted paths.
- `capabilities`: optional smartSupported, smartEnabled (ATA only), trimSupported, trimEnabled (Windows DEVICE_TRIM_DESCRIPTOR), volatileWriteCacheSupported, writeCacheEnabled (ATA). Support does not imply currently enabled.
- `health`: protocol and status; optional temperatureCelsius. NVMe adds criticalWarning, availableSparePercentage, availableSpareThresholdPercentage, percentageUsed, estimatedRemainingLifePercentage. ATA may add reallocatedSectors, pendingSectors, offlineUncorrectableSectors, interfaceCrcErrors when the model is reliably interpreted.
- `usage`: protocol, optional powerOnHours, powerCycles, hostReadBytes, hostWrittenBytes, unsafeShutdowns. ATA provides only reliably interpreted counters; no fabricated read/write totals or equivalence between power-off retract counts and unsafe shutdowns.
- `smart`: protocol (`NVMe` or `ATA`) and attributes (`id`, stable English `key`, rawHex, decimal rawValue, unit). ATA adds current, worst, flags and optional threshold, with actual ATA IDs. NVMe preserves its existing familiar table ordering and omits ATA-only fields.
- `connection`: optional current/max PCIe generation/lane fields plus optional `interfaceName`. Direct NVMe disks query Windows PnP properties 9–12 from the first PCI ancestor, requiring NVMe class 01/08/02. They never substitute an upstream bridge/RAID/VMD controller link. Missing or unknown properties are omitted, with `partial`/`unavailable` availability. USB and other buses return `notSupported` for PCIe queries. `maximum` describes the endpoint capability, not an end-to-end path guarantee or measured throughput. Queries run in the main process without automatic elevation.

PCIe property definitions: https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/pciprop.h
Property API: https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_propertyw

USB disk details include optional `connection.usb` and `availability.usbConnection`.
The command registry shares its existing `UsbTreeService` with storage. Mapping
uses the disk's PnP instance ID in the cached topology and walks to its physical
USB Device node; the existing detail query rereads the current hub connection.
The returned `nodeId` identifies that physical device. `rxSpeedMbps` and
`txSpeedMbps` describe current aggregate rates independently; do not add them.
`speedCapabilities` describes the device's declared maximum, including explicitly
per-lane SSP capability. Missing current rates never fall back to capability
rates. USB lookup failure leaves disk/SMART information usable. No UAC is
requested for USB queries. The disk page displays 「目前USB連線」 and
「裝置USB上限」, with a tooltip explaining per-lane capability when applicable.

- `availability`: section-level results; `timestamp` is Unix milliseconds at completion, not an atomic snapshot across all OS calls.

Direct NVMe Identify and the 512-byte SMART/Health log use IOCTL_STORAGE_QUERY_PROPERTY on a handle opened with desired access 0. That path is unchanged.

Realtek RTL9210/9211/9220 USB-NVMe query candidates are identified from the USB ancestor's known VID/PID pairs: `0BDA:9210`, `0BDA:9211`, `0BDA:9220`, `2EB9:9211` (Sabrent RTL9211), and `1DE1:E101` (Actions AM8180 / RTL9210). Matching is exact and does not depend on a display name or drive letter. Candidate identification does not guarantee compatibility with every firmware or SATA/RAID mode. `RealtekNvmeSmartReader` uses synchronous IOCTL_SCSI_PASS_THROUGH and fixed opcode E4 data-in Identify/SMART commands. Its handle requires read/write access, but it performs no disk writes, mode switches, resets, SMART enabling, or self-tests. Identify and SMART failures are independent and preserve successful sections.

`PhysicalDiskService` uses the shared `ElevationService.ReadRealtekSmartAsync` interface. An elevated main process reads directly; an ordinary process delegates to an already elevated helper; otherwise basic details remain available and Identify/SMART return `accessDenied` with `requiresElevation: true`. List/detail reads never trigger UAC. Enable administrator mode from the tray or explicitly call `SYS:REQUEST_ELEVATION` with null/empty parameters; the command returns `{ status: "success" | "cancelled" | "failed" }`. The webpage refreshes its list and selected disk on success and uses Toast feedback for all outcomes. The same service updates the tray state. The list does not issue Realtek pass-through probes. Unknown custom VID/PID bridges, Realtek RAID mode switching and other USB/RAID protocols remain unimplemented and visible with unknown SMART support. Direct ATA/SATA uses the same helper through ReadAtaSmartAsync, with a validated ataSmart operation.

SMART includes 01–0F, warning/critical temperature time (10–11), implemented temperature sensors (12–19), and thermal-management counters (1A–1D). A zero sensor is omitted because it is not implemented. Zero thermal-management counters are preserved as raw data, not proof that the feature is supported. Raw hex retains each field's full width, including 128-bit counters; unlike some displays, it is not truncated to six bytes.

All usage counters and raw decimal values are strings to avoid JavaScript precision loss. One NVMe read/write data unit represents 512,000 bytes, rounded up by the device; the byte totals are therefore derived quantities. Capacities are safe integer byte values; frontend formatting uses decimal byte units (KB/GB/TB). Temperature is Kelvin minus 273.15; frontend may round for display. An unavailable temperature is omitted.

NVMe health `warning` means a nonzero critical warning, spare below threshold, or percentageUsed >= 100. `ok` only means none of these conditions is reported; it is not a guarantee against failure. Remaining life is `max(0, 100 - percentageUsed)`, an estimate, not a failure date. Nonzero historical error-log counts alone do not determine health status.

## Partial data and cancellation

Availability keys are `identity`, `size`, `diskNumber`, `mountPoints`, `smart`, plus `identify`, `trim`, `sectorSize`, `connection`, and `usbConnection` as applicable. Statuses: `available`, `partial`, `notSupported`, `accessDenied`, `unavailable`, `error`. Each contains nullable `errorCode`, diagnostic `reason`, and `requiresElevation` (false by default; optional in the frontend for older bridges). Frontend localization uses the status/key; reason is diagnostic text. Partial query failures preserve successfully read sections. Optional protocol-dependent specifications, capabilities, health, usage, SMART and connection fields are omitted when no value exists. Inapplicable values are never replaced by zero; availability distinguishes denied/failed queries. Existing basic metadata/mount nullability is preserved. Entire enumeration failures propagate as command errors.

Cancellation is checked before and after native queries and before new work. Direct NVMe uses the existing DeviceIoControlQuery lifetime/timeout handling. Realtek SCSI commands specify a two-second driver timeout; cancellation cannot forcibly interrupt an already executing synchronous driver request. The helper receives cancellation and stops before subsequent queries; the parent drains its reply before reusing the connection. Disconnect/downgrade completes pending requests with unavailable data. No WebSocket retry timing or client deadline was changed (default client deadline: 30 seconds).

Reference: https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices

Realtek command reference: https://github.com/hiyohiyo/CrystalDiskInfo/blob/master/AtaSmart.cpp
Synchronous pass-through requirement: https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddscsi/ni-ntddscsi-ioctl_scsi_pass_through

## Direct ATA/SATA and frontend migration

General specs use IOCTL_STORAGE_QUERY_PROPERTY with access 0: ATA Identify and StorageAccessAlignmentProperty. SATA connection adds currentSataSpeedGbps and maximumSataSpeedGbps, omitting PCIe fields. rotationRateRpm is only emitted for a reported rotational medium. NVMe version, SSD wear percentages and other mutually inapplicable fields are omitted. The corresponding frontend types and validation accept omitted optional fields and distinguish ATA from NVMe. The disk page displays media-specific specifications and SMART tables and reuses the existing explicit elevation UI. Deploy the compatible frontend together with this response contract.

ATA SMART requires a synchronous read/write handle even though all commands are read-only. AtaSmartReader only permits IDENTIFY DEVICE, SMART READ DATA, SMART READ THRESHOLDS and SMART RETURN STATUS. It re-enumerates the diskId, confirms a direct ATA/SATA bus before pass-through, validates native layout/length/status and SMART checksums, and checks removal afterwards. No arbitrary paths, commands, writes, enable/disable, self-tests or driver installation are accepted. Requests do not trigger UAC; missing helper privileges return availability.smart accessDenied with requiresElevation. Disabled SMART remains disabled, with notSupported diagnostics. Cancelled elevation leaves normal specs usable.

The same helper serializes realtekSmart and ataSmart operations with correlated cancellation, a 15-second helper deadline and existing disconnect/shutdown cleanup. ATA commands have a three-second driver timeout (at most four commands); caller cancellation is cooperative and is checked before starting another native query. Transport retry timing and the 30-second frontend command deadline are unchanged.

ATA data always includes normalized attributes and six-byte raw values. Only the tested TOSHIBA DT02 HDD family currently interprets temperature (C2 low byte), power-on hours (09), power cycles (0C), reallocated sectors (05), pending sectors (C5), offline uncorrectable sectors (C6), and interface CRC errors (C7). Other vendors, including SATA SSD wear data, remain raw with unknown units where necessary. ATA health status is warning for device-reported failure, a failed prefailure threshold, or known nonzero reallocated/pending/uncorrectable counts; otherwise ok only if SMART RETURN STATUS succeeds, unknown if it cannot be obtained. CRC history alone does not decide disk health. No HDD health percentage is invented. Missing thresholds/status preserve attributes and return partial availability.

Example ATA identity: {"model":"TOSHIBA DT02ACA200","mediaType":"hdd","protocol":"ATA","rotationRateRpm":7200,"logicalSectorSizeBytes":512,"physicalSectorSizeBytes":4096}. Successful ATA health has protocol ATA and never includes NVMe wear/spare fields; successful NVMe health retains its existing numeric fields and adds protocol NVMe. Zero counters and false capability values are retained.

ATA API references:
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddstor/ne-ntddstor-_storage_protocol_ata_data_type
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddscsi/ni-ntddscsi-ioctl_ata_pass_through
