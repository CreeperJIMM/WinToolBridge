namespace WinToolBridge.Events;

public sealed class EventSession : IDisposable
{
    private sealed class Subscription(CancellationToken token)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public long Sequence;
    }

    private readonly EventRegistry _registry;
    private readonly Func<object, CancellationToken, Task> _sendAsync;
    private readonly CancellationToken _token;
    private readonly object _lock = new();
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private bool _disposed;

    internal EventSession(EventRegistry registry, Func<object, CancellationToken, Task> sendAsync, CancellationToken token)
    {
        _registry = registry;
        _sendAsync = sendAsync;
        _token = token;
    }

    public object On(string eventName)
    {
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _token.ThrowIfCancellationRequested();
            if (!_registry.Contains(eventName)) throw new ArgumentException("Unsupported event.");
            if (!_subscriptions.TryGetValue(eventName, out var subscription)) {
                subscription = new Subscription(_token);
                _subscriptions.Add(eventName, subscription);
            }
            return new { @event = eventName, subscriptionId = subscription.Id, enabled = true };
        }
    }

    internal bool IsSubscribed(string eventName)
    {
        lock (_lock) {
            return !_disposed && !_token.IsCancellationRequested && _subscriptions.ContainsKey(eventName);
        }
    }

    public object Off(string eventName)
    {
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _token.ThrowIfCancellationRequested();
            if (!_registry.Contains(eventName)) throw new ArgumentException("Unsupported event.");
            if (_subscriptions.Remove(eventName, out var subscription)) Cancel(subscription);
            return new { @event = eventName, enabled = false };
        }
    }

    public object Reset()
    {
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _token.ThrowIfCancellationRequested();
            var cleared = _subscriptions.Count;
            Clear();
            return new { cleared };
        }
    }

    internal async Task PublishAsync(string eventName, object? data, CancellationToken token)
    {
        Task sendTask;
        lock (_lock) {
            if (_disposed || _token.IsCancellationRequested || !_subscriptions.TryGetValue(eventName, out var subscription)) return;
            token.ThrowIfCancellationRequested();
            sendTask = _sendAsync(new {
                @event = eventName,
                subscriptionId = subscription.Id,
                sequence = ++subscription.Sequence,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                data
            }, subscription.Cancellation.Token);
        }
        try {
            await sendTask;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            // OFF, RESET and disconnect cancel this subscription's delivery and retries.
        }
    }

    private static void Cancel(Subscription subscription)
    {
        subscription.Cancellation.Cancel();
        subscription.Cancellation.Dispose();
    }

    private void Clear()
    {
        foreach (var subscription in _subscriptions.Values) Cancel(subscription);
        _subscriptions.Clear();
    }

    public void Dispose()
    {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
        _registry.Remove(this);
    }
}
