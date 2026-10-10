using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinToolBridge.Services.Usb;

/// <summary>Connection-oriented USB enumeration using SetupAPI and read-only hub IOCTLs.</summary>
public static class UsbInfoProvider
{
    // FILE_DEVICE_USB (0x22), METHOD_BUFFERED, FILE_ANY_ACCESS; functions from usbiodef.h.
    private static uint Ioctl(uint function) => (0x22u << 16) | (function << 2);
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static string Version(ushort value) => $"{value >> 8:X}.{value & 255:X2}";
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    private static string Id(string kind, string location, string? instance = null) =>
        "usb-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "|" + location.ToUpperInvariant() + "|" + instance?.ToUpperInvariant())));
    private static string HubPath(string name) => name.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\.\" + name[4..] :
        name.StartsWith(@"\\.\", StringComparison.Ordinal) ? name : @"\\.\" + name;

    private static SafeFileHandle Open(string path)
    {
        // USBView uses GENERIC_WRITE to open hub/control handles; only GET IOCTLs are sent here.
        return DeviceIoControlQuery.Open(path, 0x40000000); // FILE_FLAG_OVERLAPPED
    }

    private static bool Query(SafeFileHandle handle, uint function, byte[] data, out int returned, out int error) =>
        DeviceIoControlQuery.Query(handle, Ioctl(function), data, out returned, out error);

    private static string? Name(SafeFileHandle handle, uint function, uint? port = null)
    {
        var lengthOffset = port.HasValue ? 4 : 0;
        var textOffset = lengthOffset + 4;
        var probe = new byte[12];
        if (port.HasValue) Put32(probe, 0, port.Value);
        var ok = Query(handle, function, probe, out _, out var error);
        if (!ok && error is not (122 or 234)) return null;
        var length = U32(probe, lengthOffset);
        if (length <= textOffset || length > 65536 || ((length - textOffset) & 1) != 0) return null;
        var buffer = new byte[length];
        if (port.HasValue) Put32(buffer, 0, port.Value);
        if (!Query(handle, function, buffer, out var count, out _) || count <= textOffset) return null;
        var actual = U32(buffer, lengthOffset);
        if (actual > count || actual <= textOffset || ((actual - textOffset) & 1) != 0) return null;
        var text = Encoding.Unicode.GetString(buffer, textOffset, (int)actual - textOffset).Split('\0')[0];
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string Status(uint status) => status switch {
        0 => "NoDeviceConnected", 1 => "DeviceConnected", 2 => "DeviceFailedEnumeration",
        3 => "DeviceGeneralFailure", 4 => "DeviceCausedOvercurrent", 5 => "DeviceNotEnoughPower",
        6 => "DeviceNotEnoughBandwidth", 7 => "DeviceHubNestedTooDeeply", 8 => "DeviceInLegacyHub",
        9 => "DeviceEnumerating", 10 => "DeviceReset", _ => "Unknown"
    };

    private static string? Speed(SafeFileHandle hub, uint port, byte speed)
    {
        var v2 = new byte[16];
        Put32(v2, 0, port);
        Put32(v2, 4, 16);
        Put32(v2, 8, 7); // Caller understands USB 1.1, 2.0 and 3.0.
        if (Query(hub, 279, v2, out var count, out _) && count >= 16) {
            var flags = U32(v2, 12);
            if ((flags & 4) != 0) return "SuperSpeedPlus";
            if ((flags & 1) != 0) return "SuperSpeed";
        }
        return speed switch { 0 => "LowSpeed", 1 => "FullSpeed", 2 => "HighSpeed", 3 => "SuperSpeed", _ => null };
    }

    private static byte[]? StringDescriptor(SafeFileHandle hub, uint port, byte index, ushort language, CancellationToken token,
        out string? failure)
    {
        token.ThrowIfCancellationRequested();
        var request = new byte[12 + 255];
        Put32(request, 0, port);
        request[4] = 0x80;
        request[5] = 6; // GET_DESCRIPTOR
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), (ushort)(0x300 | index));
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(8), language);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(10), 255);
        if (!Query(hub, 260, request, out var count, out var error)) {
            failure = $"Win32 error {error}: {new Win32Exception(error).Message}";
            return null;
        }
        var length = request[12];
        if (count < 14 || request[13] != 3 || length < 2 || (length & 1) != 0 || count < 12 + length) {
            failure = "The returned string descriptor is incomplete or invalid.";
            return null;
        }
        failure = null;
        return request.AsSpan(12, length).ToArray();
    }

    private static double? SublinkSpeedMbps(uint speed, uint laneCount)
    {
        // USB_DEVICE_CAPABILITY_SUPERSPEEDPLUS_SPEED (usbspec.h). The IOCTL's
        // lane count is zero-based; each lane contributes its declared speed.
        var mantissa = speed >> 16;
        if (mantissa == 0 || laneCount > 15 || (speed & 0x3F00) != 0 || ((speed >> 14) & 3) > 1) return null;
        var unit = ((speed >> 4) & 3) switch { 0 => 0.000001, 1 => 0.001, 2 => 1.0, _ => 1000.0 };
        return mantissa * unit * (laneCount + 1);
    }

    private static UsbPortInfo PortInfo(SafeFileHandle hub, uint port, CancellationToken token, List<string> warnings)
    {
        bool? user = null, typeC = null, debug = null;
        uint? properties = null;
        bool? multi = null, superSpeed = null, superSpeedPlus = null;
        double? rxSpeedMbps = null, txSpeedMbps = null;
        int? rxLaneCount = null, txLaneCount = null;
        List<UsbPortCompanion>? companions = null;
        for (ushort index = 0; index < 256; index++) {
            token.ThrowIfCancellationRequested();
            var connector = new byte[4096];
            Put32(connector, 0, port);
            BinaryPrimitives.WriteUInt16LittleEndian(connector.AsSpan(12), index);
            var ok = Query(hub, 278, connector, out var count, out var error);
            var length = U32(connector, 4);
            if ((ok || error is 122 or 234) && length > connector.Length && length <= 65536) {
                connector = new byte[length];
                Put32(connector, 0, port);
                BinaryPrimitives.WriteUInt16LittleEndian(connector.AsSpan(12), index);
                ok = Query(hub, 278, connector, out count, out error);
                length = U32(connector, 4);
            }
            if (!ok || count < 18 || length < 18 || length > count || (length & 1) != 0) {
                warnings.Add("USB connector properties or companion mapping is unavailable or incomplete.");
                break;
            }
            var flags = U32(connector, 8);
            properties ??= flags;
            user ??= (flags & 1) != 0;
            debug ??= (flags & 2) != 0;
            multi ??= (flags & 4) != 0;
            typeC ??= (flags & 8) != 0;
            companions ??= [];
            var companionPort = U16(connector, 14);
            var name = Encoding.Unicode.GetString(connector, 16, (int)length - 16).Split('\0')[0];
            if (companionPort == 0) break;
            if (string.IsNullOrWhiteSpace(name)) {
                warnings.Add("USB companion port has no hub identity.");
                break;
            }
            companions.Add(new(Id("Port", HubPath(name) + "|" + companionPort), index, companionPort, name));
            if (multi != true) break;
            if (index == 255) warnings.Add("USB companion mapping exceeds the supported enumeration limit.");
        }
        string[]? protocols = null;
        var v2 = new byte[16];
        Put32(v2, 0, port);
        Put32(v2, 4, 16);
        Put32(v2, 8, 7);
        if (Query(hub, 279, v2, out var returned, out _) && returned >= 16) {
            var flags = U32(v2, 8);
            var names = new List<string>();
            if ((flags & 1) != 0) names.Add("USB 1.1");
            if ((flags & 2) != 0) names.Add("USB 2.0");
            if ((flags & 4) != 0) names.Add("USB 3.0");
            protocols = names.ToArray();
            var capabilities = U32(v2, 12);
            superSpeed = (capabilities & 2) != 0;
            superSpeedPlus = (capabilities & 8) != 0;
            if ((capabilities & 4) != 0) {
                token.ThrowIfCancellationRequested();
                // GET_NODE_CONNECTION_SUPERSPEEDPLUS_INFORMATION, usbioctl.h:
                // six packed ULONGs, including the two four-byte speed unions.
                // Older Windows/hub drivers may not support this GET IOCTL.
                var precise = new byte[24];
                Put32(precise, 0, port);
                Put32(precise, 4, 24);
                if (Query(hub, 289, precise, out var count, out _) && count >= 24 &&
                    U32(precise, 0) == port && U32(precise, 4) >= 24 && U32(precise, 4) <= count) {
                    rxSpeedMbps = SublinkSpeedMbps(U32(precise, 8), U32(precise, 12));
                    txSpeedMbps = SublinkSpeedMbps(U32(precise, 16), U32(precise, 20));
                    if (rxSpeedMbps.HasValue) rxLaneCount = (int)U32(precise, 12) + 1;
                    if (txSpeedMbps.HasValue) txLaneCount = (int)U32(precise, 20) + 1;
                }
            }
        }
        return new(user, typeC, debug, protocols, properties, multi, companions?.ToArray(), superSpeed, superSpeedPlus,
            rxSpeedMbps, txSpeedMbps, rxLaneCount, txLaneCount);
    }

    private static UsbSpeedCapabilities? DecodeBosSpeeds(byte[] bos)
    {
        if (bos.Length < 5 || bos[0] != 5 || bos[1] != 15 || U16(bos, 2) != bos.Length) return null;
        double? maximum = null, rx = null, tx = null;
        var offset = 5;
        var capabilities = 0;
        var plus = false;
        while (offset < bos.Length) {
            if (offset + 3 > bos.Length || bos[offset] < 3 || offset + bos[offset] > bos.Length || bos[offset + 1] != 16) return null;
            var length = bos[offset];
            if (bos[offset + 2] == 3) {
                if (length != 10) return null;
                var supported = U16(bos, offset + 4);
                if ((supported & 0xFFF0) != 0) return null;
                maximum = (supported & 8) != 0 ? 5000 : (supported & 4) != 0 ? 480 : (supported & 2) != 0 ? 12 : (supported & 1) != 0 ? 1.5 : null;
            }
            else if (bos[offset + 2] == 10) {
                plus = true;
                if (length < 12) return null;
                var count = (int)(U32(bos, offset + 4) & 31) + 1;
                if (length != 12 + count * 4) return null;
                for (var index = 0; index < count; index++) {
                    var attribute = U32(bos, offset + 12 + index * 4);
                    var speed = SublinkSpeedMbps(attribute, 0);
                    if (speed == null) return null;
                    if ((attribute & 0x80) == 0) rx = Math.Max(rx ?? 0, speed.Value);
                    else tx = Math.Max(tx ?? 0, speed.Value);
                }
            }
            capabilities++;
            offset += length;
        }
        if (capabilities != bos[4]) return null;
        // SSP attributes declare per-lane capability, not the maximum aggregate lane count.
        return plus ? rx.HasValue && tx.HasValue ? new(null, rx, tx) : null : maximum.HasValue ? new(maximum, null, null) : null;
    }

    private static UsbSpeedCapabilities? BosSpeeds(SafeFileHandle hub, uint port, CancellationToken token)
    {
        byte[]? Read(ushort length) {
            token.ThrowIfCancellationRequested();
            var request = new byte[12 + length];
            Put32(request, 0, port);
            request[4] = 0x80;
            request[5] = 6;
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), 0x0F00); // BOS descriptor
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(10), length);
            return Query(hub, 260, request, out var returned, out _) && returned >= 12 + length
                ? request.AsSpan(12, length).ToArray() : null;
        }
        var header = Read(5);
        if (header == null || header[0] != 5 || header[1] != 15) return null;
        var length = U16(header, 2);
        if (length < 5) return null;
        var bos = length == 5 ? header : Read(length);
        return bos == null ? null : DecodeBosSpeeds(bos);
    }

    private static UsbDeviceDescriptor? Descriptor(SafeFileHandle hub, uint port, byte[] connection,
        CancellationToken token, List<string> warnings, UsbSpeedCapabilities? treeCapabilities = null)
    {
        if (connection[4] != 18 || connection[5] != 1) return null;
        // String 0 supplies language IDs. A single failed language query used to
        // produce a misleading warning for every manufacturer/product/serial index.
        var hasStrings = connection[18] != 0 || connection[19] != 0 || connection[20] != 0;
        string? languageFailure = null;
        var languages = hasStrings ? StringDescriptor(hub, port, 0, 0, token, out languageFailure) : null;
        ushort? language = languages is { Length: >= 4 } ? U16(languages, 2) : null;
        if (hasStrings && (!language.HasValue || language.Value == 0)) {
            language = null;
            warnings.Add($"USB string language descriptor is unavailable. {languageFailure ?? "No valid language ID was returned."}");
        }
        string? Read(byte index)
        {
            if (index == 0 || !language.HasValue) return null;
            var descriptor = StringDescriptor(hub, port, index, language.Value, token, out var failure);
            if (descriptor == null) {
                warnings.Add($"USB string descriptor {index} is unavailable. {failure}");
                return null;
            }
            return Encoding.Unicode.GetString(descriptor, 2, descriptor.Length - 2);
        }
        var capabilities = treeCapabilities ?? (U16(connection, 6) >= 0x0201 ? BosSpeeds(hub, port, token) : null);
        bool? selfPowered = null;
        int? maxPower = null;
        // Configuration values need not match descriptor indexes. Read just the headers
        // until the active configuration is found, and do this only for selected details.
        for (var index = 0; connection[22] != 0 && index < connection[21]; index++) {
            token.ThrowIfCancellationRequested();
            var request = new byte[12 + 9];
            Put32(request, 0, port);
            request[4] = 0x80;
            request[5] = 6;
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), (ushort)(0x200 | index));
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(10), 9);
            if (!Query(hub, 260, request, out var count, out _) || count < 21 || request[12] != 9 || request[13] != 2) {
                warnings.Add("USB configuration descriptor is unavailable.");
                break;
            }
            if (request[17] != connection[22]) continue;
            if ((request[19] & 0x80) == 0) {
                warnings.Add("USB configuration power attributes are invalid.");
                break;
            }
            selfPowered = (request[19] & 0x40) != 0;
            // Units follow the current bus mode, including USB 3 devices attached at USB 2 speed.
            maxPower = connection[23] <= 3 ? request[20] * (connection[23] == 3 ? 8 : 2) : null;
            break;
        }
        return new(Version(U16(connection, 6)), Version(U16(connection, 16)), connection[8], connection[9], connection[10],
            connection[21], connection[22], U16(connection, 25), Read(connection[18]), Read(connection[19]), Read(connection[20]),
            checked((int)U32(connection, 27)), selfPowered, maxPower, capabilities);
    }

    public static UsbNodeDetails? ReadDetails(UsbEnumeration snapshot, string nodeId, CancellationToken token,
        Action<string, double>? timing = null)
    {
        var stageStarted = timing != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        void Stage(string name)
        {
            if (timing == null) return;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            stageStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            timing(name, elapsed);
        }
        token.ThrowIfCancellationRequested();
        var state = snapshot.querySnapshot ?? throw new InvalidOperationException("USB snapshot has no query identities.");
        if (!state.targets.TryGetValue(nodeId, out var target)) return null;
        var node = snapshot.nodes.First(item => item.nodeId == nodeId);
        var warnings = new List<string>();
        if (node.error != null) warnings.Add(node.error);
        var device = target.device != null ? UsbDeviceInventory.Refresh(target.device, token) : null;
        if (target.device != null && device == null) throw MissingNode();
        Stage("selected_pnp_refresh");
        UsbDeviceDescriptor? descriptor = null;
        var port = target.portId != null ? snapshot.nodes.First(item => item.nodeId == target.portId).port : null;
        if (target.hubPath != null && target.portNumber is { } index) {
            using var handle = Open(target.hubPath);
            if (handle.IsInvalid) throw MissingNode();
            var connection = new byte[4096];
            Put32(connection, 0, (uint)index);
            if (!Query(handle, 274, connection, out var count, out var error) || count < 35)
                throw new InvalidOperationException($"USB_NODE_UNAVAILABLE: Connection information is unavailable (Win32 error {error}). Refresh the USB tree.");
            var status = Status(U32(connection, 31));
            var cachedPort = snapshot.nodes.First(item => item.nodeId == target.portId);
            // Reject a removed/replaced physical device even before the notification arrives.
            var physical = snapshot.nodes.FirstOrDefault(item => item.parentId == target.portId);
            var expected = physical != null ? state.targets[physical.nodeId].device : null;
            if (status != cachedPort.connectionStatus || (expected?.driverKey != null &&
                !string.Equals(Name(handle, 264, (uint)index), expected.driverKey, StringComparison.OrdinalIgnoreCase))) throw MissingNode();
            if (physical?.vendorId != null && (connection[4] != 18 || connection[5] != 1 ||
                physical.vendorId != U16(connection, 12).ToString("X4") || physical.productId != U16(connection, 14).ToString("X4"))) throw MissingNode();
            var freshPort = PortInfo(handle, (uint)index, token, warnings);
            var portIds = snapshot.nodes.Where(item => item.kind == "Port").Select(item => item.nodeId).ToHashSet();
            port = freshPort with { deviceUsbVersion = port?.deviceUsbVersion, deviceSpeedCapabilities = port?.deviceSpeedCapabilities,
                companions = freshPort.companions?.Select(companion => companion with {
                    nodeId = companion.nodeId != null && portIds.Contains(companion.nodeId) ? companion.nodeId : null
                }).ToArray() };
            if (node.kind is "Port" or "Device" or "Hub") {
                node = node with { connectionStatus = status, speed = U32(connection, 31) == 1 ? Speed(handle, (uint)index, connection[23]) : null,
                    vendorId = connection[4] == 18 && connection[5] == 1 ? U16(connection, 12).ToString("X4") : null,
                    productId = connection[4] == 18 && connection[5] == 1 ? U16(connection, 14).ToString("X4") : null,
                    port = node.kind == "Port" ? port : node.port };
                descriptor = Descriptor(handle, (uint)index, connection, token, warnings, port.deviceSpeedCapabilities);
            }
        }
        else warnings.AddRange(target.portWarnings);
        Stage("selected_port_and_descriptors");
        var disks = device != null && node.kind is "Device" or "PnpDevice" or "DiskDevice"
            ? UsbStorageInfoProvider.Read(device, state.devices, token, warnings) : null;
        UsbVolumeInfo? volume = null;
        if (target.volume != null) {
            var diskTarget = node.parentId != null ? state.targets.GetValueOrDefault(node.parentId)?.device : null;
            if (diskTarget == null) throw MissingNode();
            var refreshedDisk = UsbDeviceInventory.Refresh(diskTarget, token) ?? throw MissingNode();
            volume = UsbStorageInfoProvider.Read(refreshedDisk, state.devices, token, warnings)
                .SelectMany(disk => disk.volumes ?? []).FirstOrDefault(item => item.volumeName == target.volume.volumeName);
            if (volume == null) throw MissingNode();
        }
        token.ThrowIfCancellationRequested();
        Stage("selected_storage");
        return new(node, descriptor, device?.windows ?? volume?.windows, target.hub, port, warnings.ToArray(), disks, volume);
    }

    private static KeyNotFoundException MissingNode() => new("USB_NODE_NOT_FOUND: The node is no longer available. Refresh the USB tree.");

    public static UsbEnumeration Enumerate(string? detailNodeId, CancellationToken token,
        ImageAssetService? images = null, string? assetBaseUrl = null, Action<string, double>? timing = null)
    {
        var stageStarted = timing != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        void Stage(string name)
        {
            if (timing == null) return;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            stageStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            timing(name, elapsed);
        }
        token.ThrowIfCancellationRequested();
        var controllers = UsbDeviceInventory.GetInterfaces(UsbDeviceInventory.ControllerClass, token);
        Stage("controller_inventory");
        var hubs = UsbDeviceInventory.GetInterfaces(UsbDeviceInventory.HubClass, token)
            .ToDictionary(item => HubPath(item.path), item => item.device, StringComparer.OrdinalIgnoreCase);
        Stage("hub_inventory");
        var presentDevices = UsbDeviceInventory.GetPresentDevices(token);
        Stage("all_pnp_inventory");
        var devices = new Dictionary<string, UsbPnpDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in presentDevices) {
            if (!string.IsNullOrEmpty(device.driverKey)) devices.TryAdd(device.driverKey, device);
        }
        var nodes = new List<UsbTreeNode>();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var portInfoById = new Dictionary<string, UsbPortInfo>();
        var portWarningsById = new Dictionary<string, string[]>();
        var iconInstances = new Dictionary<string, string>();
        var nodeDevices = new Dictionary<string, UsbPnpDevice>();
        var hubPaths = new Dictionary<string, string>();
        var hubInfos = new Dictionary<string, UsbHubInfo>();
        var nodeVolumes = new Dictionary<string, UsbVolumeInfo>();
        UsbNodeDetails? selected = null;
        using var icons = images != null && assetBaseUrl != null ? new UsbDeviceIconProvider(images, assetBaseUrl) : null;

        void Add(UsbTreeNode node, UsbPnpDevice? device, UsbHubInfo? hubInfo = null,
            SafeFileHandle? parentHub = null, byte[]? connection = null)
        {
            if (hubInfo != null) hubInfos[node.nodeId] = hubInfo;
            // Connector metadata belongs to the tree, including empty ports. These GET
            // IOCTLs read hub metadata and device speed capabilities;
            // string/configuration descriptors remain detail-only.
            if (node.kind == "Port" && parentHub != null && node.portNumber.HasValue) {
                var portWarnings = new List<string>();
                var info = PortInfo(parentHub, (uint)node.portNumber.Value, token, portWarnings);
                if (connection is { Length: >= 35 } && connection[4] == 18 && connection[5] == 1) {
                    info = info with { deviceUsbVersion = Version(U16(connection, 6)) };
                    if (U32(connection, 31) == 1) {
                        var capabilities = info.deviceIsSuperSpeedPlusCapable == true ? BosSpeeds(parentHub, (uint)node.portNumber.Value, token) :
                            info.deviceIsSuperSpeedCapable == true && info.deviceIsSuperSpeedPlusCapable == false ? new UsbSpeedCapabilities(5000, null, null) :
                            info.deviceIsSuperSpeedCapable == false && info.deviceIsSuperSpeedPlusCapable == false && connection[23] == 2 ? new UsbSpeedCapabilities(480, null, null) : null;
                        info = info with { deviceSpeedCapabilities = capabilities };
                    }
                }
                portInfoById[node.nodeId] = info;
                portWarningsById[node.nodeId] = portWarnings.ToArray();
                node = node with { port = info };
            }
            if (device != null) {
                nodeDevices[node.nodeId] = device;
                if (icons != null) iconInstances[node.nodeId] = device.windows.instanceId;
            }
            nodes.Add(node);
            if (node.nodeId != detailNodeId) return;
            var detailWarnings = new List<string>();
            if (node.error != null) detailWarnings.Add(node.error);
            // Logical PnP functions can be several levels below the physical device.
            // Resolve their owning port without querying USB descriptors on the function.
            var ancestor = node;
            while (ancestor.kind != "Port" && ancestor.parentId != null) {
                var parent = nodes.Find(item => item.nodeId == ancestor.parentId);
                if (parent == null) break;
                ancestor = parent;
            }
            var portId = ancestor.kind == "Port" ? ancestor.nodeId : null;
            var portInfo = portId != null && portInfoById.TryGetValue(portId, out var found) ? found : null;
            var descriptorStarted = timing != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var descriptor = parentHub != null && connection != null && node.portNumber.HasValue
                ? Descriptor(parentHub, (uint)node.portNumber.Value, connection, token, detailWarnings, portInfo?.deviceSpeedCapabilities) : null;
            timing?.Invoke("nested_selected_descriptors", System.Diagnostics.Stopwatch.GetElapsedTime(descriptorStarted).TotalMilliseconds);
            if (portId != null && portWarningsById.TryGetValue(portId, out var relatedWarnings)) detailWarnings.AddRange(relatedWarnings);
            var storageStarted = timing != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var disks = device != null && node.kind is "Device" or "PnpDevice" or "DiskDevice"
                ? UsbStorageInfoProvider.Read(device, presentDevices, token, detailWarnings) : null;
            timing?.Invoke("nested_selected_storage", System.Diagnostics.Stopwatch.GetElapsedTime(storageStarted).TotalMilliseconds);
            selected = new(node, descriptor, device?.windows, hubInfo, portInfo, detailWarnings.ToArray(), disks);
        }

        void EnumerateHub(string path, string parentId, bool root, int? upstreamPort, UsbPnpDevice? device,
            SafeFileHandle? parentHub = null, byte[]? connection = null, int depth = 0)
        {
            token.ThrowIfCancellationRequested();
            path = HubPath(path);
            var kind = root ? "RootHub" : "Hub";
            var nodeId = Id(kind, path, device?.windows.instanceId);
            hubPaths[nodeId] = path;
            var status = connection == null ? "Available" : Status(U32(connection, 31));
            var speed = connection == null || parentHub == null ? null : Speed(parentHub, (uint)upstreamPort!.Value, connection[23]);
            var vendor = connection is { Length: >= 35 } && connection[4] == 18 ? U16(connection, 12).ToString("X4") : null;
            var product = connection is { Length: >= 35 } && connection[4] == 18 ? U16(connection, 14).ToString("X4") : null;
            var node = new UsbTreeNode(nodeId, parentId, kind, device?.name ?? (root ? "USB Root Hub" : "USB Hub"), upstreamPort, status, speed, vendor, product, null);
            if (depth > 16 || !visited.Add(path)) {
                Add(node with { error = "USB hub topology contains a cycle or exceeds the supported depth." }, device);
                return;
            }
            using var handle = Open(path);
            if (handle.IsInvalid) {
                Add(node with { error = new Win32Exception(Marshal.GetLastWin32Error()).Message }, device);
                return;
            }
            // usbioctl.h uses pack(1): NodeType[4], USB_HUB_DESCRIPTOR[71], HubIsBusPowered[1].
            var info = new byte[76];
            if (!Query(handle, 258, info, out var returned, out var error) || returned < 76) {
                Add(node with { error = error == 0 ? "USB hub information is incomplete." : new Win32Exception(error).Message }, device);
                return;
            }
            var portCount = info[6];
            Add(node, device, new(portCount, info[75] != 0), parentHub, connection);
            for (uint port = 1; port <= portCount; port++) {
                token.ThrowIfCancellationRequested();
                var portId = Id("Port", path + "|" + port);
                // Packed EX fixed header is 35 bytes; extra space accommodates USB_PIPE_INFO entries.
                var data = new byte[4096];
                Put32(data, 0, port);
                if (!Query(handle, 274, data, out returned, out error) || returned < 35) {
                    Add(new(portId, nodeId, "Port", $"Port {port}", (int)port, "Unknown", null, null, null,
                        error == 0 ? "USB connection information is incomplete." : new Win32Exception(error).Message), null, null, handle);
                    continue;
                }
                var connectionStatus = U32(data, 31);
                var portSpeed = connectionStatus == 1 ? Speed(handle, port, data[23]) : null;
                Add(new(portId, nodeId, "Port", $"Port {port}", (int)port, Status(connectionStatus), portSpeed, null, null, null), null, null, handle, data);
                if (connectionStatus == 0) continue;
                var driverKey = Name(handle, 264, port);
                var pnp = driverKey != null && devices.TryGetValue(driverKey, out var found) ? found : null;
                if (data[24] != 0 && connectionStatus == 1) {
                    var external = Name(handle, 261, port);
                    if (external != null) {
                        var externalPath = HubPath(external);
                        if (pnp == null) hubs.TryGetValue(externalPath, out pnp);
                        EnumerateHub(externalPath, portId, false, (int)port, pnp, handle, data, depth + 1);
                        continue;
                    }
                }
                var hasDescriptor = data[4] == 18 && data[5] == 1;
                var vid = hasDescriptor ? U16(data, 12).ToString("X4") : null;
                var pid = hasDescriptor ? U16(data, 14).ToString("X4") : null;
                var deviceId = Id("Device", path + "|" + port, pnp?.windows.instanceId ?? vid + ":" + pid);
                Add(new(deviceId, portId, data[24] != 0 ? "Hub" : "Device", pnp?.name ?? "USB device", (int)port,
                    Status(connectionStatus), portSpeed, vid, pid,
                    data[24] != 0 ? "USB hub name is unavailable; downstream ports could not be enumerated." : null), pnp, null, handle, data);
            }
        }

        foreach (var controller in controllers.OrderBy(item => item.path, StringComparer.OrdinalIgnoreCase)) {
            token.ThrowIfCancellationRequested();
            var id = Id("Controller", controller.path, controller.device.windows.instanceId);
            var node = new UsbTreeNode(id, null, "Controller", controller.device.name, null, "Available", null, null, null, null);
            using var handle = Open(controller.path);
            if (handle.IsInvalid) {
                Add(node with { error = new Win32Exception(Marshal.GetLastWin32Error()).Message }, controller.device);
                continue;
            }
            var rootName = Name(handle, 258);
            // The controller is no longer needed while querying its root hub and devices.
            handle.Dispose();
            Add(rootName == null ? node with { error = "USB root hub name is unavailable." } : node, controller.device);
            if (rootName == null) continue;
            var rootPath = HubPath(rootName);
            hubs.TryGetValue(rootPath, out var rootDevice);
            EnumerateHub(rootPath, id, true, null, rootDevice);
        }

        Stage("usb_topology_including_selected");
        // USB4 host routers are PnP devices managed by Usb4HostRouter, not xHCI hubs.
        // Include their actual PnP descendants; never infer support from a display name.
        var pnpChildren = presentDevices.Where(device => device.parentDevInst.HasValue)
            .GroupBy(device => device.parentDevInst!.Value).ToDictionary(group => group.Key, group => group.ToArray());
        var seenPnp = new HashSet<uint>();
        void AddRouter(UsbPnpDevice device, string? parentId, int depth = 0)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 64 || !seenPnp.Add(device.devInst)) return;
            var service = device.windows.service;
            var kind = string.Equals(service, "Usb4HostRouter", StringComparison.OrdinalIgnoreCase) ? "Usb4HostRouter" :
                string.Equals(service, "Usb4DeviceRouter", StringComparison.OrdinalIgnoreCase) ? "Usb4Router" : "PnpDevice";
            var nodeId = Id(kind, device.windows.instanceId);
            var status = device.windows.problemCode switch { 22 => "Disabled", > 0 => "DeviceGeneralFailure", null => "Unknown", _ => "Available" };
            Add(new(nodeId, parentId, kind, device.name, null, status, null, null, null, null), device);
            if (pnpChildren.TryGetValue(device.devInst, out var children)) {
                foreach (var child in children) AddRouter(child, nodeId, depth + 1);
            }
        }
        foreach (var router in presentDevices.Where(device => string.Equals(device.windows.service, "Usb4HostRouter", StringComparison.OrdinalIgnoreCase)))
            AddRouter(router, null);

        // Physical USB devices are enumerated through hub ports. Their composite
        // functions (HID, audio, etc.) belong to the PnP parent tree, not extra ports.
        // Stop at already represented devices so hubs and other USB branches are
        // never duplicated beneath a logical function.
        var representedPnp = nodeDevices.Values.Select(device => device.devInst).ToHashSet();
        void AddFunction(UsbPnpDevice device, string parentId, int depth = 0)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 64 || !representedPnp.Add(device.devInst)) return;
            // Volume PDOs may live outside the disk's PnP branch. Attach them below
            // disks using volume extents rather than duplicating the raw PnP hierarchy.
            if (string.Equals(device.windows.deviceClass, "Volume", StringComparison.OrdinalIgnoreCase)) return;
            var identity = string.IsNullOrEmpty(device.windows.instanceId) ? "devinst:" + device.devInst : device.windows.instanceId;
            var kind = string.Equals(device.windows.deviceClass, "DiskDrive", StringComparison.OrdinalIgnoreCase) ? "DiskDevice" : "PnpDevice";
            var nodeId = Id(kind, identity);
            var status = device.windows.problemCode switch { 22 => "Disabled", > 0 => "DeviceGeneralFailure", null => "Unknown", _ => "Available" };
            Add(new(nodeId, parentId, kind, device.name, null, status, null, null, null, null), device);
            if (pnpChildren.TryGetValue(device.devInst, out var children)) {
                foreach (var child in children) AddFunction(child, nodeId, depth + 1);
            }
        }
        foreach (var deviceNode in nodes.Where(node => node.kind == "Device").ToArray()) {
            if (nodeDevices.TryGetValue(deviceNode.nodeId, out var device) &&
                pnpChildren.TryGetValue(device.devInst, out var children)) {
                foreach (var child in children) AddFunction(child, deviceNode.nodeId);
            }
        }
        Stage("pnp_hierarchy_including_selected");
        var knownDisks = (selected?.disks ?? []).ToDictionary(disk => disk.instanceId, StringComparer.OrdinalIgnoreCase);
        foreach (var diskNode in nodes.Where(node => node.kind == "DiskDevice").ToArray()) {
            token.ThrowIfCancellationRequested();
            var diskDevice = nodeDevices[diskNode.nodeId];
            if (!knownDisks.TryGetValue(diskDevice.windows.instanceId, out var disk)) {
                var diskWarnings = new List<string>();
                var diskResults = UsbStorageInfoProvider.Read(diskDevice, presentDevices, token, diskWarnings);
                disk = diskResults.FirstOrDefault(item => string.Equals(item.instanceId, diskDevice.windows.instanceId, StringComparison.OrdinalIgnoreCase));
                warnings.AddRange(diskWarnings);
            }
            foreach (var volume in disk?.volumes ?? []) {
                token.ThrowIfCancellationRequested();
                var nodeId = Id("Volume", diskDevice.windows.instanceId + "|" + volume.volumeName);
                var volumeDevice = volume.windows != null ? presentDevices.FirstOrDefault(device =>
                    string.Equals(device.windows.instanceId, volume.windows.instanceId, StringComparison.OrdinalIgnoreCase)) : null;
                var name = string.Join("、", volume.mountPoints);
                if (!string.IsNullOrWhiteSpace(volume.label)) name = string.IsNullOrEmpty(name) ? volume.label : name + " · " + volume.label;
                if (string.IsNullOrEmpty(name)) name = volume.windows?.description ?? "Volume";
                var status = volume.windows?.problemCode switch { 22 => "Disabled", > 0 => "DeviceGeneralFailure", _ => "Available" };
                Add(new(nodeId, diskNode.nodeId, "Volume", name, null, status, null, null, null, null), volumeDevice);
                nodeVolumes[nodeId] = volume;
                if (selected?.node.nodeId == nodeId) selected = selected with { volume = volume, windows = volume.windows };
            }
        }
        Stage("disk_volume_hierarchy");
        token.ThrowIfCancellationRequested();
        var portIds = nodes.Where(node => node.kind == "Port").Select(node => node.nodeId).ToHashSet();
        UsbPortInfo? ResolvePort(UsbPortInfo? info) => info?.companions is { } companions
            ? info with { companions = companions.Select(companion => companion with {
                    nodeId = companion.nodeId != null && portIds.Contains(companion.nodeId) ? companion.nodeId : null
                }).ToArray() } : info;
        // Physical-device images are separate from each PnP function's icon.
        // Query only direct children of ports, after all USB handles have closed.
        var containerIds = nodes.Where(node => node.parentId != null && portIds.Contains(node.parentId))
            .Select(node => nodeDevices.TryGetValue(node.nodeId, out var device) ? device.containerId : null)
            .Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        var containerIcons = images != null && assetBaseUrl != null
            ? UsbContainerIconProvider.Read(containerIds, images, assetBaseUrl, token) : [];
        Stage("devices_and_printers_icons");
        for (var index = 0; index < nodes.Count; index++) {
            if (nodes[index].port != null) nodes[index] = nodes[index] with { port = ResolvePort(nodes[index].port) };
            // All hub/controller handles are closed before loading and encoding icons.
            if (icons != null && iconInstances.TryGetValue(nodes[index].nodeId, out var instanceId)) {
                nodes[index] = nodes[index] with { iconUrl = icons.GetUrl(instanceId, token) };
            }
            if (nodes[index].parentId != null && portIds.Contains(nodes[index].parentId!) &&
                nodeDevices.TryGetValue(nodes[index].nodeId, out var device)) {
                var containerUrl = device.containerId is { } container && containerIcons.TryGetValue(container, out var foundIcon) ? foundIcon : null;
                var fallbackInstance = nodes[index].kind == "Device" ? UsbDeviceIconProvider.PhysicalIconInstance(device, pnpChildren) : device.windows.instanceId;
                nodes[index] = nodes[index] with { deviceIconUrl = containerUrl ?? icons?.GetUrl(fallbackInstance, token) };
            }
        }
        if (selected != null) selected = selected with {
            node = nodes.First(node => node.nodeId == selected.node.nodeId), port = ResolvePort(selected.port)
        };
        Stage("pnp_icons_and_finish");
        if (nodes.Any(node => node.error != null)) warnings.Add("Some USB nodes could not be queried completely. See each node's error field.");
        var byId = nodes.ToDictionary(node => node.nodeId);
        var targets = new Dictionary<string, UsbQueryTarget>();
        foreach (var node in nodes) {
            var ancestor = node;
            while (ancestor.kind != "Port" && ancestor.parentId != null && byId.TryGetValue(ancestor.parentId, out var parent)) ancestor = parent;
            var portId = ancestor.kind == "Port" ? ancestor.nodeId : null;
            var hubPath = portId != null && ancestor.parentId != null && hubPaths.TryGetValue(ancestor.parentId, out var path) ? path : null;
            targets[node.nodeId] = new(nodeDevices.GetValueOrDefault(node.nodeId), hubPath,
                portId != null ? ancestor.portNumber : null, portId, hubInfos.GetValueOrDefault(node.nodeId),
                portId != null ? portWarningsById.GetValueOrDefault(portId) ?? [] : [], nodeVolumes.GetValueOrDefault(node.nodeId));
        }
        return new(nodes.ToArray(), selected, warnings.ToArray()) { querySnapshot = new(targets, presentDevices) };
    }
}
