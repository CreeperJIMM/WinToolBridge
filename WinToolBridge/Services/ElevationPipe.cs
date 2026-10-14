using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinToolBridge.Services.Storage;

namespace WinToolBridge.Services;

internal sealed record ElevationRequest(long id, string operation, string? diskId);
internal sealed record ElevationResponse(long id, StorageProtocolResult? result, string? error);

internal static class ElevationPipe
{
    private const int MaximumFrameBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new() {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    internal static async Task WriteAsync<T>(Stream pipe, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length == 0 || bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Invalid elevation frame size.");
        var frame = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bytes.Length);
        bytes.CopyTo(frame, 4);
        await pipe.WriteAsync(frame, token);
        await pipe.FlushAsync(token);
    }

    internal static async Task<T> ReadAsync<T>(Stream pipe, CancellationToken token)
    {
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size <= 0 || size > MaximumFrameBytes) throw new InvalidDataException("Invalid elevation frame size.");
        var bytes = new byte[size];
        await pipe.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("Empty elevation message.");
    }

    internal static void Validate(ElevationRequest request)
    {
        if (request.id <= 0) throw new InvalidDataException("Invalid elevation request id.");
        if (request.operation is "realtekSmart" or "ataSmart") PhysicalDiskService.ValidateId(request.diskId);
        else if (request.operation != "cancel" || request.diskId != null)
            throw new InvalidDataException("Unsupported elevation operation.");
    }
}
