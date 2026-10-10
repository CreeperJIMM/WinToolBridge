using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinToolBridge;

/// <summary>Bounds command work and remembers retries for one WebSocket connection.</summary>
internal sealed class ConnectionRequestTracker : IDisposable
{
    internal const int MaxPendingCommands = 16;
    internal const int MaxRememberedRequests = 256;
    // Match the frontend's default 30-second request timeout, starting at completion.
    // Never expire commands that are still running.
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _requests = new(StringComparer.Ordinal);
    private int _pending;
    private bool _disposed;

    internal sealed class Entry(string fingerprint, bool remembered = true)
    {
        internal string Fingerprint { get; } = fingerprint;
        internal bool Remembered { get; } = remembered;
        internal string ResponseMessageId { get; } = $"b_dat_{Guid.NewGuid():N}";
        internal JsonElement? Response;
        internal bool Finished;
        internal long CompletedAt;
    }

    internal enum Admission { New, Pending, Completed, Conflict, Busy }

    internal Admission Admit(string id, JsonElement payload, bool eventCommand, out Entry? entry, out JsonElement? response,
        bool deduplicate)
    {
        var fingerprint = deduplicate
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText()))) : "";
        lock (_sync) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            response = null;
            if (!deduplicate) {
                if (_pending >= MaxPendingCommands) {
                    entry = null;
                    return Admission.Busy;
                }
                // Read-only requests count toward concurrency, but consume no retry history.
                entry = new("", remembered: false);
                _pending++;
                return Admission.New;
            }
            foreach (var expired in _requests.Where(pair => pair.Value.Response.HasValue &&
                Stopwatch.GetElapsedTime(pair.Value.CompletedAt) >= Retention).Select(pair => pair.Key).ToArray()) {
                _requests.Remove(expired);
            }
            if (_requests.TryGetValue(id, out entry)) {
                if (entry.Fingerprint != fingerprint) return Admission.Conflict;
                response = entry.Response;
                return response.HasValue ? Admission.Completed : Admission.Pending;
            }
            // Event commands finish synchronously, so OFF/RESET remain available
            // when the normal command slots are occupied.
            if ((!eventCommand && _pending >= MaxPendingCommands) || _requests.Count >= MaxRememberedRequests) {
                entry = null;
                return Admission.Busy;
            }
            entry = new(fingerprint);
            _requests.Add(id, entry);
            _pending++;
            return Admission.New;
        }
    }

    internal void Complete(Entry entry, JsonElement response)
    {
        lock (_sync) {
            if (_disposed || entry.Finished) return;
            entry.Finished = true;
            if (entry.Remembered) {
                entry.Response = response;
                entry.CompletedAt = Stopwatch.GetTimestamp();
            }
            _pending--;
        }
    }

    internal void Abandon(string id, Entry entry)
    {
        lock (_sync) {
            if (!_disposed && !entry.Finished &&
                (!entry.Remembered || _requests.GetValueOrDefault(id) == entry)) {
                if (entry.Remembered) _requests.Remove(id);
                entry.Finished = true;
                _pending--;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync) {
            _disposed = true;
            _requests.Clear();
            _pending = 0;
        }
    }
}
