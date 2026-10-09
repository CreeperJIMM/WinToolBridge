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
  - Added protocol structure and parameter validation, rejected binary WebSocket messages, and returned errors for malformed requests when a request ID is available.
  - Improved WebSocket close handling with a bounded wait and safer repeated shutdown and disposal.
