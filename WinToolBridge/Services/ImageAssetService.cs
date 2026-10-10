using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace WinToolBridge.Services;

/// <summary>Stores encoded PNGs for one bridge lifetime, shared across connections.</summary>
public sealed class ImageAssetService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ImageAsset> _assets = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers an already encoded PNG and returns its stable relative HTTP path.
    /// Checks the PNG signature; image decoding belongs to the producing service.
    /// </summary>
    public string RegisterPng(ReadOnlySpan<byte> buffer)
    {
        // Own the bytes before hashing so later changes to the caller's buffer cannot
        // change the content behind a registered address.
        var bytes = buffer.ToArray();
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!bytes.AsSpan().StartsWith(signature)) {
            throw new ArgumentException("A PNG buffer is required.", nameof(buffer));
        }

        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        lock (_sync) {
            if (!_assets.TryGetValue(id, out var asset)) {
                asset = new ImageAsset(id, bytes);
                _assets.Add(id, asset);
            }
            return asset.Path;
        }
    }

    /// <summary>Looks up an opaque image ID, without opening any filesystem path.</summary>
    public bool TryGet(string? id, [NotNullWhen(true)] out ImageAsset? asset)
    {
        lock (_sync) {
            asset = null;
            return id != null && _assets.TryGetValue(id, out asset);
        }
    }

    /// <summary>Releases cached entries. Readers already opened remain usable.</summary>
    public void Clear()
    {
        lock (_sync) { _assets.Clear(); }
    }
}

/// <summary>Immutable response metadata and independently readable PNG content.</summary>
public sealed class ImageAsset
{
    private readonly byte[] _bytes;

    internal ImageAsset(string id, byte[] bytes)
    {
        Id = id;
        _bytes = bytes;
    }

    public string Id { get; }
    public string Path => $"/assets/icons/{Id}.png";
    public string ContentType => "image/png";
    public int ContentLength => _bytes.Length;
    public string ETag => $"\"{Id}\"";

    // Do not expose the backing array, even through a writable stream or GetBuffer.
    public Stream OpenRead() => new MemoryStream(_bytes, 0, _bytes.Length,
        writable: false, publiclyVisible: false);
}
