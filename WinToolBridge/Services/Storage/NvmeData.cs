using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace WinToolBridge.Services.Storage;

internal static class NvmeData
{
    internal static string? Text(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    internal static (string? model, string? firmware, string? serial, string? version, bool trim, bool cache) Identify(byte[] data)
    {
        if (data.Length < 4096) throw new InvalidDataException("Truncated NVMe Identify data.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(data) == 0 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(516)) == 0)
            throw new InvalidDataException("Invalid NVMe controller identity.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80));
        return (Text(data.AsSpan(24, 40)), Text(data.AsSpan(64, 8)), Text(data.AsSpan(4, 20)),
            version == 0 ? null : $"{version >> 16}.{(version >> 8) & 255}.{version & 255}",
            (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(520)) & 4) != 0, (data[525] & 1) != 0);
    }

    internal static (StorageHealth health, StorageUsage usage, StorageSmart smart) Health(byte[] data)
    {
        if (data.Length < 512) throw new InvalidDataException("Truncated NVMe health log.");
        var attributes = new List<StorageSmartAttribute>();
        BigInteger Read(int offset, int length) => new(data.AsSpan(offset, length), isUnsigned: true, isBigEndian: false);
        string Decimal(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
        void Add(int id, string key, int offset, int length, string unit) {
            var value = Read(offset, length);
            var hex = Convert.ToHexString(data.AsSpan(offset, length).ToArray().Reverse().ToArray());
            attributes.Add(new(id.ToString("X2"), key, hex, Decimal(value), unit));
        }

        Add(1, "criticalWarning", 0, 1, "bitmask");
        Add(2, "compositeTemperature", 1, 2, "kelvin");
        Add(3, "availableSpare", 3, 1, "percent");
        Add(4, "availableSpareThreshold", 4, 1, "percent");
        Add(5, "percentageUsed", 5, 1, "percent");
        string[] counters = ["dataUnitsRead", "dataUnitsWritten", "hostReadCommands", "hostWriteCommands",
            "controllerBusyTime", "powerCycles", "powerOnHours", "unsafeShutdowns", "mediaErrors", "errorLogEntries"];
        for (var i = 0; i < counters.Length; i++)
            Add(6 + i, counters[i], 32 + i * 16, 16, i < 2 ? "dataUnits" : i == 4 ? "minutes" : i == 6 ? "hours" : "count");
        Add(0x10, "warningTemperatureTime", 192, 4, "minutes");
        Add(0x11, "criticalTemperatureTime", 196, 4, "minutes");
        for (var i = 0; i < 8; i++) {
            // Zero means this temperature sensor is not implemented.
            if (Read(200 + i * 2, 2) != 0) Add(0x12 + i, $"temperatureSensor{i + 1}", 200 + i * 2, 2, "kelvin");
        }
        Add(0x1A, "thermalManagementTemperature1TransitionCount", 216, 4, "count");
        Add(0x1B, "thermalManagementTemperature2TransitionCount", 220, 4, "count");
        Add(0x1C, "thermalManagementTemperature1TotalTime", 224, 4, "seconds");
        Add(0x1D, "thermalManagementTemperature2TotalTime", 228, 4, "seconds");

        var temperature = (int)Read(1, 2);
        var health = new StorageHealth(data[0] != 0 || data[3] < data[4] || data[5] >= 100 ? "warning" : "ok",
            data[0], temperature == 0 ? null : Math.Round(temperature - 273.15, 2), data[3], data[4], data[5], Math.Max(0, 100 - data[5]));
        // NVMe data units are thousands of 512-byte units, rounded up by the device.
        var usage = new StorageUsage(Decimal(Read(128, 16)), Decimal(Read(112, 16)),
            Decimal(Read(32, 16) * 512000), Decimal(Read(48, 16) * 512000), Decimal(Read(144, 16)));
        return (health, usage, new("NVMe", attributes.ToArray()));
    }

    internal static byte[] ProtocolPayload(byte[] buffer, int returned, int expectedLength, uint dataType, uint protocol = 3)
    {
        // STORAGE_PROTOCOL_DATA_DESCRIPTOR: 8-byte header + 40-byte protocol structure.
        if (returned < 48 || returned > buffer.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(buffer) != 48 ||
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)) != 48 ||
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)) != protocol ||
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12)) != dataType)
            throw new InvalidDataException("Invalid storage protocol descriptor.");
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(24));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(28));
        if (offset < 40 || length < expectedLength || 8UL + offset + length > (ulong)returned)
            throw new InvalidDataException("Invalid storage protocol payload range.");
        return buffer.AsSpan(checked(8 + (int)offset), expectedLength).ToArray();
    }
}
