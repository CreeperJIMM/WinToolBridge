# WinToolBridge

A communication bridge bringing native app experience to the web.

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Target: .NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows%20x64-lightgrey.svg)]()
[![Platform: Windows ARM64](https://img.shields.io/badge/Platform-Windows%20ARM64-lightgrey.svg)]()

---

## Overview

Modern web browsers run inside strict security sandboxes, preventing web applications from directly interacting with low-level Windows APIs or reading hardware diagnostics.

**WinToolBridge** is a lightweight, low-footprint Windows background daemon. It bridges web applications with native Windows capabilities over a secure local WebSocket connection, delivering a seamless native desktop experience right within the browser without requiring external dependencies.

---

## Key Features

- [x] **Portable & Zero Dependencies**
- [x] **Strict Security Boundary**
- [x] **Multi-Language (i18n)**
- [x] **Silent Background Operation**
- [x] **High Performance**
---

## Getting Started

### Installation & Usage
1. Download the latest `WinToolBridge.exe` (x64) or `WinToolBridge_arm64.exe` from the **[Releases](https://github.com/CreeperJIMM/WinToolBridge/releases)** page.
2. Run `WinToolBridge.exe` (it will minimize to the system tray).
3. Visit **[https://crjim.com/wintool](https://crjim.com/wintool)** to start using native tools directly in your browser.

### Tray Controls
* **Toggle Console**: Double-click the tray icon to view or hide live communication logs.
* **Launch at Startup**: Right-click the tray icon to toggle automatic startup with Windows.
* **Administrator Mode**: Request administrator privileges for the separate helper using the Windows UAC action.

---

## Security & Privacy

* **Localhost Only**: The WebSocket server strictly listens on `127.0.0.1`.
* **Explicit Elevation**: The administrator helper exposes only validated, backend-defined operations over a restricted local named pipe. Disk queries do not write disk data, switch bridge modes, enable SMART, or install drivers.
* **Zero Telemetry**: No analytics or private data are collected. All communication stays strictly between your browser and your local machine.

---

## License

This project is licensed under the **[GNU General Public License v3.0 (GPL-3.0)](LICENSE)**.
