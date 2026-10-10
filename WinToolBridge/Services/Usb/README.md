# USB tree protocol

First version is read-only. Commands use the existing DAT/ACK/correlated result flow; errors use the existing `status: "ERROR"` and `error` string. No external application, driver, package or elevated bridge is required by the implementation. Windows/device access restrictions are returned as partial node errors or command errors.

## Commands

- `USB:GET_TREE`, params omitted/null/`{}`: returns `{ revision, nodes, timestamp, warnings }`.
- `USB:GET_NODE_DETAILS`, params `{ "nodeId": "usb-..." }`: returns `{ revision, details, timestamp }`.

`nodes` is a flat list with `nodeId`, nullable `parentId`, `kind` (`Controller`, `RootHub`, `Hub`, `Port`, `Device`, `Usb4HostRouter`, `Usb4Router`, `PnpDevice`), `name`, nullable `portNumber`, `connectionStatus`, nullable `speed`, nullable hex-string `vendorId`/`productId` and nullable `error`. Hierarchy is controller → root hub → port → device/hub → port. Empty ports remain in the tree. Port numbers are hub-local logical numbers, not chassis labels; paired USB 2/3 ports are not merged into a physical connector.

Port nodes also include nullable `port` metadata in the initial tree response, using the same `UsbPortInfo` contract as details. Connector properties, supported protocols and companion mappings are queried for empty and connected ports alike, with companion IDs resolved after the full tree is enumerated. These hub GET queries do not read each device's string or configuration descriptors. Detail reads reuse the port metadata from their enumeration; non-Port tree nodes leave this field null. An inaccessible field remains null rather than a fabricated zero/false.

USB4 host routers are discovered by the Windows `Usb4HostRouter` service and include their actual present PnP descendants. `Usb4DeviceRouter` identifies router descendants; other descendants use `PnpDevice`. These form separate roots, outside the xHCI hub tree. Their details expose Windows properties, with no fabricated USB descriptors or link speeds. Status uses the PnP problem code (`Disabled` for code 22). Vendor connection managers using other services are not covered by this detection.

Connection statuses preserve the USB driver enum names, with `Available` for controller/root hub and `Unknown` for unavailable port data. Speed is the operating speed class (`LowSpeed`, `FullSpeed`, `HighSpeed`, `SuperSpeed`, `SuperSpeedPlus`) or null. It is not measured throughput, and SuperSpeedPlus alone does not establish an exact Gbit/s rate. VID/PID are four uppercase hexadecimal digits when a valid descriptor is available.

`details` contains `node`, nullable `usb`, nullable `windows`, nullable `hub`, nullable `port` and `warnings`:

- `usb`: USB/device BCD versions, class/subclass/protocol, configuration count/current configuration, device address and nullable manufacturer/product/serial strings. Also contains `openPipeCount` (open data pipes, excluding control endpoint zero), `isSelfPowered` and `maxPowerMilliAmps`. Power fields describe the active configuration's declaration, not live consumption. MaxPower units use 2 mA at USB 2 speeds and 8 mA at SuperSpeed. Connected Port details also include their attached device's descriptor summary.
- `windows`: instance ID, description/manufacturer/class/service/location, hardware IDs, status flags/problem code, driver provider/version/date/INF. These fields describe the selected PnP device, not every child function of a composite device.
- `hub`: port count and bus-powered flag.
- `port`: nullable user-connectable/Type-C/debug flags, supported-protocol list, raw `properties`, `hasMultipleCompanions`, `deviceIsSuperSpeedCapable` and `deviceIsSuperSpeedPlusCapable`. Connected devices/hubs expose their upstream port information too. `companions` is null when unavailable, empty when no mapping exists, otherwise an array of `{ nodeId, companionIndex, portNumber, hubSymbolicLink }`. A nullable nodeId resolves only to a Port in the returned enumeration; an unavailable companion Hub still retains its reported name and number. Enumerates multi-companion indexes with a 256-entry guard and warning on truncation. Unsupported queries stay null; false means a successful query reported the flag unset.

Node IDs are opaque, derived from backend-enumerated identity and location. Clients must use an ID returned by the tree; a path, instance ID, executable or arbitrary device name is never accepted as an open target. IDs are stable for an unchanged enumerated identity/location, not globally permanent device identifiers. A replacement without a usable PnP identity may share a location-based fallback ID.

Details re-enumerate the current topology instead of returning a cached entry. Missing nodes return `USB_NODE_NOT_FOUND: ...`; if enumeration is incomplete and absence cannot be established, they return `USB_NODE_UNAVAILABLE: ...`. Unavailable fields remain null. One inaccessible controller/hub/port is preserved with its error instead of failing all other branches. Names prefer a nonblank Friendly Name, including composite devices. The fallback is PnP's cached `DEVPKEY_Device_BusReportedDeviceDesc`, then a nonblank Device Description. Tree reads do not fetch each device's string descriptors.

## Change notifications

Subscribe with `EVENT:ON`, `{ "event": "USB:TREE_CHANGED" }`. The event's `data` is only `{ revision, timestamp }`; it never queries or carries the tree. Existing OFF/RESET/disconnect cleanup and ACK/retry behavior apply.

A hidden tray-thread window registers USB controller/hub/device interface notifications. Arrival/removal, PnP node changes and resume invalidate the tree. PnP node changes can also concern unrelated devices: this event means "the USB tree may have changed", not proof of a USB insertion or a precise action log. A 300 ms notification window coalesces bursts without periodic polling or device scans.

`revision` starts at 1 for each server instance, increments immediately for each invalidation (even without subscribers), and is shared across its connections and commands. It is independent of per-subscription event `sequence`. Reads return their starting revision; a native notification during enumeration can make that response older than the latest event. Physical enumeration is not an atomic Windows snapshot.

Frontend integration should subscribe before the initial GET_TREE, serialize refreshes, remember the highest notified revision, and read again if a change arrived during a read or its result is older than that revision. Keep selection only while its node ID exists. The frontend retains details when the selected node fields are unchanged, avoiding a descriptor-query/notification feedback loop; manual refresh always reloads details. Reconnecting discards subscriptions; reset revision comparisons for the new server connection, re-subscribe and read the current tree. No offline notification replay is provided.

## Implementation and verification

`UsbDeviceInventory` uses SetupAPI and Config Manager, and reads the system driver registry. `UsbInfoProvider` opens only enumerated controller/hub paths and issues GET IOCTLs. USB structures use the SDK's packed byte layout; pointer-bearing SetupAPI structures use native alignment. Safe handles, finally blocks and registry `using` scopes release owned resources. The controller handle closes before downstream enumeration. String descriptors are requested only for the selected device/hub. Hub IOCTLs use overlapped handles; after a three-second wait they request cancellation and await actual completion before releasing the OVERLAPPED structure, event and buffer. Cancellation is cooperative and cannot guarantee a bounded wait for a driver that ignores it. The connection token is checked between native operations.

Build the solution, then verify tree and detail reads, device removal, change notifications and reconnection through the frontend. Real USB insertion/removal, nested hubs, driver-specific access and release architecture behavior require hardware/platform interaction tests.
