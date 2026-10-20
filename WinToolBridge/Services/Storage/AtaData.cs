using System.Buffers.Binary;
using System.Globalization;

namespace WinToolBridge.Services.Storage;

internal static class AtaData
{
    internal sealed record Identification(string? model, string? firmware, string? serial, string mediaType,
        int? rotationRateRpm, StorageCapabilities capabilities, double? currentSpeed, double? maximumSpeed,
        string? majorVersion, string? minorVersion, int? queueDepth);

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
        // Support, enabled state and current levels are separate Identify fields.
        var sataValid = Word(data, 76) is not (0 or 0xFFFF) && (Word(data, 76) & 1) == 0;
        var sataSupportValid = sataValid && Word(data, 78) != 0xFFFF && (Word(data, 78) & 1) == 0;
        var sataEnabledValid = sataSupportValid && Word(data, 79) != 0xFFFF && (Word(data, 79) & 1) == 0;
        bool? Supported(int word, int bit, bool valid) => valid ? (Word(data, word) & (1 << bit)) != 0 : null;
        bool? Enabled(bool? supported, int word, int bit, bool valid) =>
            supported == true ? Supported(word, bit, valid) : null;
        var apm = Supported(83, 3, supportedValid);
        var aam = Supported(83, 9, supportedValid);
        var lookAhead = Supported(82, 6, supportedValid);
        var dipm = Supported(78, 3, sataSupportValid);
        var devSleep = Supported(78, 8, sataSupportValid);
        var apmEnabled = Enabled(apm, 86, 3, enabledValid);
        var aamEnabled = Enabled(aam, 86, 9, enabledValid);
        capabilities = capabilities with {
            apmSupported = apm, apmEnabled = apmEnabled,
            apmLevel = apmEnabled == true && (Word(data, 91) & 255) is >= 1 and <= 254 ? Word(data, 91) & 255 : null,
            aamSupported = aam, aamEnabled = aamEnabled,
            aamLevel = aamEnabled == true && (Word(data, 94) & 255) is >= 128 and <= 254 ? Word(data, 94) & 255 : null,
            ncqSupported = Supported(76, 8, sataValid),
            gplSupported = Supported(84, 5, (Word(data, 84) & 0xC000) == 0x4000),
            readLookAheadSupported = lookAhead, readLookAheadEnabled = Enabled(lookAhead, 85, 6, enabledValid),
            hipmSupported = Supported(76, 9, sataValid),
            dipmSupported = dipm, dipmEnabled = Enabled(dipm, 79, 3, sataEnabledValid),
            devSleepSupported = devSleep, devSleepEnabled = Enabled(devSleep, 79, 8, sataEnabledValid)
        };
        double? maximum = null, current = null;
        if (Word(data, 76) is not (0 or 0xFFFF)) {
            maximum = (Word(data, 76) & 8) != 0 ? 6 : (Word(data, 76) & 4) != 0 ? 3 : (Word(data, 76) & 2) != 0 ? 1.5 : null;
            if (Word(data, 77) != 0xFFFF) current = ((Word(data, 77) >> 1) & 7) switch { 1 => 1.5, 2 => 3, 3 => 6, _ => null };
        }
        return new(Text(data, 27, 20), Text(data, 23, 4), Text(data, 10, 10), media,
            media == "hdd" ? rpm : null, capabilities, current, maximum, MajorVersion(Word(data, 80)),
            MinorVersion(Word(data, 81)), capabilities.ncqSupported == true && Word(data, 75) != 0xFFFF
                ? (Word(data, 75) & 31) + 1 : null);
    }

    internal static StorageIdentity Apply(StorageIdentity identity, Identification value) => identity with {
        model = value.model ?? identity.model, firmware = value.firmware ?? identity.firmware,
        serialNumber = value.serial ?? identity.serialNumber, protocol = "ATA", mediaType = value.mediaType,
        rotationRateRpm = value.rotationRateRpm, ataMajorVersion = value.majorVersion,
        ataMinorVersion = value.minorVersion, queueDepth = value.queueDepth
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
                199 => "interfaceCrcErrors",
                1 => "readErrorRate",
                2 => "throughputPerformance",
                3 => "spinUpTime",
                4 => "startStopCount",
                7 => "seekErrorRate",
                8 => "seekTimePerformance",
                10 => "spinRetryCount",
                191 => "gSenseErrorRate",
                220 => "diskShift",
                222 => "loadedHours",
                223 => "loadUnloadRetryCount",
                224 => "loadFriction",
                226 => "loadInTime",
                240 => "headFlyingHours",
                _ => $"ataAttribute{id:X2}"
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

    private static string? MajorVersion(ushort value)
    {
        if (value is 0 or 0xFFFF) return null;
        for (var bit = 15; bit >= 1; bit--)
            if ((value & (1 << bit)) != 0) return bit switch {
                >= 9 and <= 13 => $"ACS-{bit - 7}", 8 => "ATA8-ACS",
                >= 4 and <= 7 => $"ATA/ATAPI-{bit}", <= 3 => $"ATA-{bit}", _ => $"0x{value:X4}"
            };
        return null;
    }

    private static string? MinorVersion(ushort value) => value switch {
        0 or 0xFFFF => null,
        0x001F => "ACS-3 Revision 3b", 0x006D => "ACS-3 Revision 5", 0x011B => "ACS-3 Revision 4",
        0x010A => "ACS-3 (ANSI INCITS 522-2014)",
        0x0031 => "ACS-2 Revision 2", 0x0110 => "ACS-2 Revision 3", 0x0082 => "ACS-2 (ANSI INCITS 482-2012)",
        0x005E => "ACS-4 Revision 5", 0x009C => "ACS-4 (ANSI INCITS 529-2018)",
        0x0030 => "ACS-5 Revision 10", 0x0025 => "ACS-6 Revision 7", 0x0070 => "ACS-6 Revision 11",
        0x0073 => "ACS-6 Revision 2", 0x0028 => "ATA8-ACS Revision 6", 0x0029 => "ATA8-ACS Revision 4",
        // Preserve unrecognized revision codes instead of inventing a revision number.
        _ => $"0x{value:X4}"
    };

    private static ushort Word(byte[] data, int index) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(index * 2));
    private static string? Text(byte[] data, int first, int count)
    {
        var bytes = data.AsSpan(first * 2, count * 2).ToArray();
        for (var i = 0; i < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        return NvmeData.Text(bytes);
    }
}
