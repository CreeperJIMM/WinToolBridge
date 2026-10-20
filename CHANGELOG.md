## [1.3.2]

### What's New in v1.3.2

- **ATA Information Updates**:
  - Added ATA standard versions, NCQ queue depth, and APM/AAM support, enabled states, and current levels.
  - Added GPL, read look-ahead, and SATA power-management capabilities, including HIPM, DIPM, and DevSleep, with reported enabled states where available.
  - Expanded ATA SMART attribute names while preserving raw values and existing health calculations.

## [1.3.1]

### What's New in v1.3.1

- **ATA/SATA HDD and SSD Support**:
  - Added read-only Identify and SMART queries for directly attached ATA/SATA HDDs and SSDs, with disk type, HDD rotation speed, sector sizes, SATA link speeds, and supported capabilities.
  - Added ATA SMART attributes and health status, with temperature, power-on statistics, and sector error counts where supported. Unrecognized vendor attributes retain raw values without assuming units or remaining lifetime.
  - Reused the existing administrator request and helper workflow for restricted SMART queries, while retaining basic specifications under standard privileges where available.
  - Omitted unavailable optional protocol-specific fields from responses. USB-to-SATA and RAID-specific pass-through are not included.

## [1.3.0]

### What's New in v1.3.0

- **Physical Disk Information and SMART**:
  - Added `STORAGE:GET_DISKS` and `STORAGE:GET_DISK_DETAILS` for physical disk enumeration and on-demand details, including model, firmware, serial number, capacity, mounted paths, and section-level availability.
  - Added Explorer drive icons and identification of the disk containing the running Windows installation.
  - Added direct NVMe Identify and SMART/Health queries, with temperature, spare capacity, estimated wear, power-on statistics, host read/write totals, and full-width raw counters. Large decimal counters are returned as strings to avoid precision loss in the browser.
  - Excludes disks whose SMART query explicitly reports unsupported; access-denied, unknown-protocol, and transient failures remain visible with their availability status.

- **Realtek USB–NVMe SMART**:
  - Added fixed read-only Identify and SMART queries for known RTL9210, RTL9211, and RTL9220 bridge IDs, including Sabrent RTL9211 and Actions AM8180 variants.
  - Basic disk information remains available when SMART requires administrator privileges. Responses indicate `requiresElevation` without opening UAC automatically.
  - SATA protocols, RAID mode switching, and unknown custom bridge IDs are not included. Compatibility still depends on the device's firmware and operating mode.

- **Administrator Helper and Tray Controls**:
  - Added `ElevationService` and a separate administrator helper so the main application can retain standard privileges while the helper handles supported privileged disk queries.
  - Added `SYS:REQUEST_ELEVATION` for explicit webpage requests, returning success, cancellation, or failure, and a Windows UAC shield image through the existing local icon endpoint.
  - Added mutually exclusive tray actions to request administrator privileges or return the helper to standard mode. A main application launched as administrator instead shows a disabled status with instructions to exit and reopen normally.
  - Webpage elevation and helper disconnection update the tray state, including before the menu is first opened. Returning to standard mode or exiting closes the helper connection.
  - Helper communication uses a restricted local named pipe, process identity checks, validated disk IDs, bounded messages, serialized queries, and cancellation handling. It does not accept arbitrary commands or executable paths.

- **Disk Connection Information**:
  - Added current and maximum PCIe generation and lane counts for direct NVMe endpoints when Windows exposes the properties, preserving unavailable values instead of substituting upstream controller links.
  - Added `NVM Express` and `UASP (NVM Express)` interface labels where identifiable.
  - Reuses USB device-tree queries to associate USB disks with their physical device node and return negotiated receive/transmit rates, lane counts, and declared device capabilities independently of SMART permissions.

## [1.2.1]

### What's Fixed in v1.2.1

- Improved shortcut window visibility when Windows declines foreground activation, reducing cases where opened windows remain behind the browser.
- Fixed USB device properties windows remaining in the background after opening, making successful launches appear unresponsive.

## [1.2.0]

### What's New in v1.2.0

- **.NET 10 Upgrade**:
  - Updated the Windows application and release build SDK to .NET 10.
  - Fixed network adapter enumeration to exclude entries that support neither IPv4 nor IPv6, including additional filter and packet driver entries exposed after the framework upgrade.

- **System Information Shortcuts**:
  - Added `SYS:OPEN_DRIVE` to open an available local drive's root directory in File Explorer from the system information dashboard.
  - Added `SYS:OPEN_GPU_CONTROL_PANEL` for AMD, NVIDIA, and Intel graphics control apps, with support for known desktop installations and Microsoft Store packages.

- **Tray Update Checks**:
  - Added a single-level tray menu section, separated by dividers, with automatic checks enabled by default, a manual check action, and a GitHub link.
  - Automatic checks run after startup and remember the user's preference. A newer stable release prompts the user to open the latest release page.
  - Added timestamped console logs for check starts, version results, failures, and page opening. Logs recorded before the console is first opened are buffered for later display.

- **WebSocket Reliability and Validation**:
  - Allowed backend versions newer than the frontend while retaining the frontend's minimum compatible version check and client version information in the handshake.
  - Limited simultaneous client connections to eight and released connection slots on disconnect or failed upgrades.
  - Scoped outgoing message retries and acknowledgements to each connection and cancelled retries when that connection closes.
  - Prevented retried commands from opening the same window or repeating an operation, while preserving the original result for subsequent retries.
  - Added limits for oversized messages and excessive requests, and improved cancellation of network adapter shortcuts after disconnect.
  - Added protocol structure and parameter validation, rejected binary WebSocket messages, and returned errors for malformed requests when a request ID is available.
  - Improved WebSocket close handling with a bounded wait and safer repeated shutdown and disposal.

- **USB Device Tree**:
  - Added an interactive tree of USB host controllers, root hubs, hubs, ports, and connected devices, including child Plug and Play devices, disks, and volumes.
  - Automatically updates when devices are connected or removed, with highlights to make changes easier to find.
  - Shows device and driver information, USB version, connection speed, supported capabilities, and power state. Storage devices also show capacity and volume information, with shortcuts to open mounted drives.
  - Identifies companion USB 2.0 and USB 3.x ports and highlights the corresponding port when selected.
  - Includes device icons, keyboard navigation, smart default expansion, and shortcuts to device properties.
