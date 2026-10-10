using System.Collections.Concurrent;

namespace WinToolBridge.Events;

/// <summary>Backend-defined event names and connection-scoped notification routing.</summary>
public sealed class EventRegistry
{
    private readonly ConcurrentDictionary<string, byte> _events = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<EventSession, byte> _sessions = new();

    public void Register(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        _events.TryAdd(eventName, 0);
    }

    internal bool Contains(string eventName) => _events.ContainsKey(eventName);

    public bool HasSubscribers(string eventName) =>
        _sessions.Keys.Any(session => session.IsSubscribed(eventName));

    public EventSession CreateSession(Func<object, CancellationToken, Task> sendAsync, CancellationToken token)
    {
        var session = new EventSession(this, sendAsync, token);
        _sessions.TryAdd(session, 0);
        return session;
    }

    internal void Remove(EventSession session) => _sessions.TryRemove(session, out _);

    // Publishers await delivery attempts so failures are observed by their existing logging flow.
    public Task PublishAsync(string eventName, object? data, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Contains(eventName)) throw new ArgumentException("Unsupported event.", nameof(eventName));
        return Task.WhenAll(_sessions.Keys.Select(session => session.PublishAsync(eventName, data, token)));
    }
}
