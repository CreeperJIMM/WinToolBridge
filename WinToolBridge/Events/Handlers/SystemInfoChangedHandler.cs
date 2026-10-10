using System.Threading.Channels;
using WinToolBridge.Services;

namespace WinToolBridge.Events.Handlers;

/// <summary>Coalesces native notifications and publishes only the changing system-info fields.</summary>
public sealed class SystemInfoChangedHandler : IDisposable, IAsyncDisposable
{
    public const string EventName = "SYS:INFO_CHANGED";
    private readonly EventRegistry _events;
    private readonly Func<CancellationToken, Task<object>> _readSnapshot;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private readonly Channel<byte> _changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly Task _worker;
    private readonly object _lifecycleLock = new();
    private bool _disposed;
    private Task _disposal = Task.CompletedTask;

    public SystemInfoChangedHandler(EventRegistry events, Func<CancellationToken, Task<object>>? readSnapshot = null)
    {
        _events = events;
        _readSnapshot = readSnapshot ?? SystemInfoProvider.GetDynamicInfoAsync;
        _token = _cts.Token;
        events.Register(EventName);
        _worker = Task.Run(ProcessChangesAsync);
    }

    public void NotifyChange()
    {
        if (!_token.IsCancellationRequested && _events.HasSubscribers(EventName)) _changes.Writer.TryWrite(0);
    }

    private async Task ProcessChangesAsync()
    {
        try {
            while (await _changes.Reader.WaitToReadAsync(_token)) {
                // A single plug/unplug can generate several Windows messages. This is not a polling timer.
                await Task.Delay(300, _token);
                while (_changes.Reader.TryRead(out _)) { }
                if (!_events.HasSubscribers(EventName)) continue;
                try {
                    var snapshot = await _readSnapshot(_token);
                    _token.ThrowIfCancellationRequested();
                    await _events.PublishAsync(EventName, snapshot, _token);
                }
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
                catch (Exception ex) {
                    Console.Error.WriteLine($"[{EventName}] Update failed: {ex.Message}");
                }
                // Notifications received during a query remain queued for the next snapshot.
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception ex) {
            Console.Error.WriteLine($"[{EventName}] Notification worker failed: {ex.Message}");
        }
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
        try {
            await _worker;
        }
        finally {
            _cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _disposal;
    }
}
