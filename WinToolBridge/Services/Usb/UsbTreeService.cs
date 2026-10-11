namespace WinToolBridge.Services.Usb;

/// <summary>One managed snapshot per revision, shared by commands and notifications.</summary>
public sealed class UsbTreeService : IDisposable, IAsyncDisposable
{
    private readonly Func<string?, CancellationToken, UsbEnumeration> _enumerate;
    private readonly Func<UsbEnumeration, string, CancellationToken, UsbNodeDetails?> _readDetails;
    private readonly SemaphoreSlim _queries = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _lifetime;
    private long _revision = 1;
    private Snapshot? _snapshot;
    private Task<Snapshot>? _rebuild;
    private bool _disposed;
    private Task _disposal = Task.CompletedTask;
    private sealed record Snapshot(long revision, long timestamp, UsbEnumeration data, UsbTreeListNode[] nodes);

    public UsbTreeService(Func<string?, CancellationToken, UsbEnumeration>? enumerate = null,
        ImageAssetService? images = null, string? assetBaseUrl = null,
        Func<UsbEnumeration, string, CancellationToken, UsbNodeDetails?>? readDetails = null)
    {
        _lifetime = _cts.Token;
        _enumerate = enumerate ?? ((nodeId, token) => UsbInfoProvider.Enumerate(nodeId, token, images, assetBaseUrl));
        _readDetails = readDetails ?? ((snapshot, nodeId, token) => snapshot.querySnapshot != null
            ? UsbInfoProvider.ReadDetails(snapshot, nodeId, token) : _enumerate(nodeId, token).details);
    }

    public UsbTreeChangedData MarkChanged()
    {
        lock (_sync) {
            if (!_disposed) _revision++;
            return new(_revision, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken token)
    {
        while (true) {
            token.ThrowIfCancellationRequested();
            Task<Snapshot> rebuild;
            lock (_sync) {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_snapshot?.revision == _revision) return _snapshot;
                if (_rebuild == null || _rebuild.IsCompleted) {
                    // A disconnected caller cancels its wait, not another client's shared build.
                    _rebuild = Task.Run(RebuildAsync, _lifetime);
                    _ = _rebuild.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                rebuild = _rebuild;
            }
            var snapshot = await rebuild.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            lock (_sync) { if (snapshot.revision == _revision) return snapshot; }
        }
    }

    private async Task<Snapshot> RebuildAsync()
    {
        await _queries.WaitAsync(_lifetime);
        try {
            while (true) {
                _lifetime.ThrowIfCancellationRequested();
                long revision;
                lock (_sync) revision = _revision;
                var data = _enumerate(null, _lifetime);
                _lifetime.ThrowIfCancellationRequested();
                lock (_sync) {
                    if (revision != _revision) continue;
                    return _snapshot = new(revision, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), data, data.nodes.Select(UsbTreeListNode.From).ToArray());
                }
            }
        }
        finally { _queries.Release(); }
    }

    public async Task<UsbTreeChangedData> RefreshAsync(CancellationToken token)
    {
        var snapshot = await GetSnapshotAsync(token);
        return new(snapshot.revision, snapshot.timestamp);
    }

    public async Task<UsbTreeResult> GetTreeAsync(CancellationToken token)
    {
        var snapshot = await GetSnapshotAsync(token);
        token.ThrowIfCancellationRequested();
        return new(snapshot.revision, snapshot.nodes, snapshot.timestamp, snapshot.data.warnings);
    }

    public async Task<UsbNodeDetailsResult> GetNodeDetailsAsync(string nodeId, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
        while (true) {
            var snapshot = await GetSnapshotAsync(linked.Token);
            await _queries.WaitAsync(linked.Token);
            try {
                lock (_sync) { if (snapshot.revision != _revision) continue; }
                if (!snapshot.data.nodes.Any(node => node.nodeId == nodeId)) {
                    if (snapshot.data.warnings.Length != 0) throw new InvalidOperationException("USB_NODE_UNAVAILABLE: The USB tree could not be queried completely. Refresh the USB tree.");
                    throw new KeyNotFoundException("USB_NODE_NOT_FOUND: The node is no longer available. Refresh the USB tree.");
                }
                var details = await Task.Run(() => _readDetails(snapshot.data, nodeId, linked.Token), linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                lock (_sync) { if (snapshot.revision != _revision) continue; }
                if (details == null) throw new KeyNotFoundException("USB_NODE_NOT_FOUND: The node is no longer available. Refresh the USB tree.");
                return new(snapshot.revision, details, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            finally { _queries.Release(); }
        }
    }

    internal async Task<WinToolBridge.Services.Storage.StorageUsbConnection?> GetDiskConnectionAsync(string instanceId, CancellationToken token)
    {
        while (true) {
            var snapshot = await GetSnapshotAsync(token);
            var targets = snapshot.data.querySnapshot?.targets;
            if (targets == null) return null;
            var disk = snapshot.data.nodes.FirstOrDefault(node => node.kind is "DiskDevice" or "Device" &&
                targets.TryGetValue(node.nodeId, out var target) &&
                string.Equals(target.device?.windows.instanceId, instanceId, StringComparison.OrdinalIgnoreCase));
            if (disk == null) return null;
            var byId = snapshot.data.nodes.ToDictionary(node => node.nodeId);
            var physical = disk;
            while (physical.kind != "Device" && physical.parentId != null && byId.TryGetValue(physical.parentId, out var parent))
                physical = parent;
            if (physical.kind != "Device") return null;
            // Existing detail flow validates the current physical device and rereads its hub port.
            var result = await GetNodeDetailsAsync(physical.nodeId, token);
            if (result.revision != snapshot.revision) continue;
            var details = result.details;
            if (details.node.connectionStatus != "DeviceConnected") return null;
            return new(details.node.nodeId, details.node.speed, details.port?.rxSpeedMbps, details.port?.txSpeedMbps,
                details.port?.rxLaneCount, details.port?.txLaneCount,
                details.usb?.speedCapabilities ?? details.port?.deviceSpeedCapabilities);
        }
    }

    public void Dispose()
    {
        lock (_sync) {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _disposal = DisposeResourcesAsync(_rebuild);
        }
    }

    private async Task DisposeResourcesAsync(Task? rebuild)
    {
        try { if (rebuild != null) await rebuild; }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Observed by the requester and rebuild continuation. */ }
        await _queries.WaitAsync();
        try {
            lock (_sync) { _snapshot = null; _rebuild = null; }
            _cts.Dispose();
        }
        finally { _queries.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _disposal;
    }
}
