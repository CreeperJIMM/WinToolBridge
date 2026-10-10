using System.Threading.Channels;
using WinToolBridge.Services.Usb;

namespace WinToolBridge.Events.Handlers;

public sealed class UsbTreeChangedHandler : IDisposable, IAsyncDisposable
{
    public const string EventName = "USB:TREE_CHANGED";
    private readonly EventRegistry _events;
    private readonly UsbTreeService _usb;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private readonly Channel<byte> _changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly object _notificationLock = new();
    private readonly object _lifecycleLock = new();
    private UsbTreeChangedData? _latest;
    private readonly Task _worker;
    private bool _disposed;
    private Task _disposal = Task.CompletedTask;

    public UsbTreeChangedHandler(EventRegistry events, UsbTreeService usb)
    {
        _events = events;
        _usb = usb;
        _token = _cts.Token;
        events.Register(EventName);
        _worker = Task.Run(ProcessChangesAsync);
    }

    public void NotifyChange()
    {
        lock (_notificationLock) {
            if (_token.IsCancellationRequested) return;
            // Invalidate even without subscribers, and before any query can claim the new revision.
            _latest = _usb.MarkChanged();
            if (_events.HasSubscribers(EventName)) _changes.Writer.TryWrite(0);
        }
    }

    private async Task ProcessChangesAsync()
    {
        long publishedRevision = 0;
        try {
            while (await _changes.Reader.WaitToReadAsync(_token)) {
                await Task.Delay(300, _token);
                UsbTreeChangedData? data;
                lock (_notificationLock) {
                    while (_changes.Reader.TryRead(out _)) { }
                    data = _latest;
                }
                if (data == null || !_events.HasSubscribers(EventName)) continue;
                try {
                    data = await _usb.RefreshAsync(_token);
                    if (data.revision > publishedRevision && _events.HasSubscribers(EventName)) {
                        await _events.PublishAsync(EventName, data, _token);
                        publishedRevision = data.revision;
                    }
                }
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
                catch (Exception ex) { Console.Error.WriteLine($"[{EventName}] Notification failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"[{EventName}] Notification worker failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        lock (_lifecycleLock) {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _changes.Writer.TryComplete();
            _disposal = DisposeResourcesAsync();
        }
    }

    private async Task DisposeResourcesAsync()
    {
        try { await _worker; }
        finally { _cts.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _disposal;
    }
}
