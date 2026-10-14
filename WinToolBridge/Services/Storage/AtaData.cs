using System.Buffers.Binary;
using System.Globalization;

namespace WinToolBridge.Services.Storage;

internal static class AtaData
{
    internal sealed record Identification(string? model, string? firmware, string? serial, string mediaType,
        int? rotationRateRpm, StorageCapabilities capabilities, double? currentSpeed, double? maximumSpeed);

    internal static Identification Identify(byte[] data)
    {
        if (data.Length != 512 || data.All(b => b == 0) || data.All(b => b == 255) || (Word(data, 0) & 0x8000) != 0)
            throw new InvalidDataException("Invalid ATA Identify data.");
        // Word 255's signature indicates that the optional integrity checksum is present.
        if (data[510] == 0xA5 && data.Sum(b => (int)b) % 256 != 0)
            throw new InvalidDataException("Invalid ATA Identify checksum.");
        var supportedValid = (Word(data, 83) & 0xC000) == 0x4000;
        var enabledValid = (Word(data, 87) & 0xC000) == 0x4000;
        var rpm = Word(data, 217);
        var media = rpm == 1 ? "ssd" : rpm is >= 0x401 and < 0xFFFF ? "hdd" : "unknown";
        var capabilities = new StorageCapabilities(supportedValid ? (Word(data, 82) & 1) != 0 : null, null,
            supportedValid ? (Word(data, 82) & 32) != 0 : null,
            enabledValid ? (Word(data, 85) & 1) != 0 : null,
            Word(data, 169) != 0xFFFF ? (Word(data, 169) & 1) != 0 : null,
            enabledValid ? (Word(data, 85) & 32) != 0 : null);
        double? maximum = null, current = null;
        if (Word(data, 76) is not (0 or 0xFFFF)) {
            maximum = (Word(data, 76) & 8) != 0 ? 6 : (Word(data, 76) & 4) != 0 ? 3 : (Word(data, 76) & 2) != 0 ? 1.5 : null;
            if (Word(data, 77) != 0xFFFF) current = ((Word(data, 77) >> 1) & 7) switch { 1 => 1.5, 2 => 3, 3 => 6, _ => null };
        }
        return new(Text(data, 27, 20), Text(data, 23, 4), Text(data, 10, 10), media,
            media == "hdd" ? rpm : null, capabilities, current, maximum);
    }

    internal static StorageIdentity Apply(StorageIdentity identity, Identification value) => identity with {
        model = value.model ?? identity.model, firmware = value.firmware ?? identity.firmware,
        serialNumber = value.serial ?? identity.serialNumber, protocol = "ATA", mediaType = value.mediaType,
        rotationRateRpm = value.rotationRateRpm
    };

    internal static bool ValidSmart(byte[] data) => data.Length == 512 &&
        Word(data, 0) is not (0 or 0xFFFF) && data.Sum(b => (int)b) % 256 == 0 &&
        Enumerable.Range(0, 30).Any(i => data[2 + i * 12] is not (0 or 255));

    internal static (StorageHealth health, StorageUsage? usage, StorageSmart smart) Health(
        byte[] data, byte[]? thresholds, string? model, bool? deviceHealthy)
    {
        if (!ValidSmart(data)) throw new InvalidDataException("Invalid ATA SMART data or checksum.");
        if (thresholds != null && !ValidSmart(thresholds)) throw new InvalidDataException("Invalid ATA SMART thresholds.");
        var attributes = new List<StorageSmartAttribute>();
        var values = new Dictionary<int, long>();
        var failed = false;
        // Units in vendor raw attributes are not portable. Only interpret the tested DT02 HDD family.
        var known = model?.StartsWith("TOSHIBA DT02", StringComparison.OrdinalIgnoreCase) == true;
        for (var offset = 2; offset < 362; offset += 12) {
            var id = data[offset];
            if (id is 0 or 255) continue;
            if (values.ContainsKey(id)) throw new InvalidDataException("Duplicate ATA SMART attribute.");
            long raw = 0;
            for (var i = 0; i < 6; i++) raw |= (long)data[offset + 5 + i] << (8 * i);
            values.Add(id, raw);
            int? threshold = null;
            if (thresholds != null) for (var t = 2; t < 362; t += 12)
                if (thresholds[t] == id) { threshold = thresholds[t + 1]; break; }
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 1));
            if (threshold > 0 && data[offset + 3] <= threshold && (flags & 1) != 0) failed = true;
            var key = known ? id switch {
                5 => "reallocatedSectors", 9 => "powerOnHours", 12 => "powerCycles",
                192 => "powerOffRetractCount", 193 => "loadUnloadCycles", 194 => "temperature",
                196 => "reallocationEvents", 197 => "pendingSectors", 198 => "offlineUncorrectableSectors",
                199 => "interfaceCrcErrors", _ => $"ataAttribute{id:X2}"
            } : $"ataAttribute{id:X2}";
            attributes.Add(new(id.ToString("X2"), key,
                Convert.ToHexString(data.AsSpan(offset + 5, 6).ToArray().Reverse().ToArray()),
                raw.ToString(CultureInfo.InvariantCulture), known && id == 9 ? "hours" : known && id is 5 or 12 or 192 or 193 or 196 or 197 or 198 or 199 ? "count" : "raw",
                data[offset + 3], data[offset + 4], threshold, flags));
        }
        long? Read(int id) => known && values.TryGetValue(id, out var value) ? value : null;
        var temperature = Read(194) is { } packed ? (int)(packed & 255) : (int?)null;
        if (temperature is <= 0 or >= 150) temperature = null;
        var status = deviceHealthy == false || failed || Read(197) > 0 || Read(198) > 0 || Read(5) > 0
            ? "warning" : deviceHealthy == true ? "ok" : "unknown";
        var health = new StorageHealth(status, temperatureCelsius: temperature, protocol: "ATA",
            reallocatedSectors: Read(5), pendingSectors: Read(197),
            offlineUncorrectableSectors: Read(198), interfaceCrcErrors: Read(199));
        var hours = Read(9)?.ToString(CultureInfo.InvariantCulture);
        var cycles = Read(12)?.ToString(CultureInfo.InvariantCulture);
        var usage = hours != null || cycles != null ? new StorageUsage(hours, cycles, protocol: "ATA") : null;
        return (health, usage, new("ATA", attributes.ToArray()));
    }

    private static ushort Word(byte[] data, int index) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(index * 2));
    private static string? Text(byte[] data, int first, int count)
    {
        var bytes = data.AsSpan(first * 2, count * 2).ToArray();
        for (var i = 0; i < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        return NvmeData.Text(bytes);
    }
}
