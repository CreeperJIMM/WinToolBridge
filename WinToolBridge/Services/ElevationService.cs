using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WinToolBridge.Services.Storage;

namespace WinToolBridge.Services;

public enum ElevationResult { Success, Cancelled, Failed }

/// <summary>Owns an optional elevated helper. The bridge's own token never changes.</summary>
public sealed class ElevationService : IDisposable
{
    internal const string HelperArgument = "--elevation-helper";
    private const string PipePrefix = "WinToolBridge.Elevation.";
    private readonly object _gate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly SemaphoreSlim _queries = new(1, 1);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private NamedPipeServerStream? _pipe;
    private Process? _helper;
    private bool _disposed;
    private long _nextQueryId;
    private long _pendingQueryId;
    private TaskCompletionSource<StorageProtocolResult>? _pendingQuery;

    public bool IsMainProcessElevated { get; } = IsElevated();
    public bool IsHelperElevated { get { lock (_gate) return _pipe != null; } }
    public event EventHandler? StateChanged;

    public async Task<StorageProtocolResult> ReadRealtekSmartAsync(string diskId, CancellationToken token = default)
    {
        PhysicalDiskService.ValidateId(diskId);
        token.ThrowIfCancellationRequested();
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsMainProcessElevated) return await RealtekNvmeSmartReader.ReadAsync(diskId, token);
        if (!IsHelperElevated) return RealtekNvmeSmartReader.NeedsElevation();
        // The caller can stop waiting immediately, but the wire remains serialized
        // until the cancelled native query replies or the helper disconnects.
        return await QueryHelperAsync(diskId, "realtekSmart", token).WaitAsync(token);
    }

    public async Task<StorageProtocolResult> ReadAtaSmartAsync(string diskId, CancellationToken token = default)
    {
        PhysicalDiskService.ValidateId(diskId);
        token.ThrowIfCancellationRequested();
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsMainProcessElevated) return await AtaSmartReader.ReadAsync(diskId, token);
        if (!IsHelperElevated) return AtaSmartReader.NeedsElevation();
        return await QueryHelperAsync(diskId, "ataSmart", token).WaitAsync(token);
    }

    private async Task<StorageProtocolResult> QueryHelperAsync(string diskId, string operation, CancellationToken token)
    {
        CancellationTokenSource lifetime;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        }
        using (lifetime) {
            await _queries.WaitAsync(lifetime.Token);
            NamedPipeServerStream? pipe = null;
            long id = 0;
            try {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var completion = new TaskCompletionSource<StorageProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) {
                    lifetime.Token.ThrowIfCancellationRequested();
                    pipe = _pipe;
                    if (pipe == null) return operation == "ataSmart" ? AtaSmartReader.NeedsElevation() : RealtekNvmeSmartReader.NeedsElevation();
                    id = ++_nextQueryId;
                    _pendingQueryId = id;
                    _pendingQuery = completion;
                }
                // Write the full frame without caller cancellation to avoid leaving a partial frame.
                await WriteRequestAsync(pipe, new(id, operation, diskId), deadline.Token);
                using var cancel = lifetime.Token.Register(() => { _ = CancelQueryAsync(pipe, id); });
                return await completion.Task.WaitAsync(deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException) {
                if (pipe != null) Disconnect(pipe);
                return StorageProtocolResult.Failure(new("unavailable", null, "Administrator helper disconnected or did not complete the query."));
            }
            finally {
                lock (_gate) {
                    if (_pendingQueryId == id) {
                        _pendingQuery = null;
                        _pendingQueryId = 0;
                    }
                }
                _queries.Release();
            }
        }
    }

    private async Task WriteRequestAsync(NamedPipeServerStream pipe, ElevationRequest request,
        CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await _writes.WaitAsync(timeout.Token);
        try { await ElevationPipe.WriteAsync(pipe, request, timeout.Token); }
        finally { _writes.Release(); }
    }

    private async Task CancelQueryAsync(NamedPipeServerStream pipe, long id)
    {
        try { await WriteRequestAsync(pipe, new(id, "cancel", null)); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { Disconnect(pipe); }
    }

    public async Task<ElevationResult> ElevateAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken shutdownToken;
        CancellationTokenSource linkedSource;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            shutdownToken = _shutdown.Token;
            linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
        }
        using var linked = linkedSource;
        await _operations.WaitAsync(linked.Token);
        NamedPipeServerStream? pipe = null;
        Process? helper = null;
        try {
            linked.Token.ThrowIfCancellationRequested();
            if (IsMainProcessElevated || IsHelperElevated) return ElevationResult.Success;

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable) ||
                !string.Equals(Path.GetFileNameWithoutExtension(executable), "WinToolBridge", StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("Elevation requires the WinToolBridge executable.");
            }

            var pipeName = PipePrefix + Guid.NewGuid().ToString("N");
            using var identity = WindowsIdentity.GetCurrent();
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);

            var start = new ProcessStartInfo(executable) {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add(HelperArgument);
            start.ArgumentList.Add(pipeName);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            // ShellExecute may wait for UAC consent; keep that wait off the tray UI thread.
            helper = await Task.Run(() => {
                linked.Token.ThrowIfCancellationRequested();
                return Process.Start(start);
            });
            if (helper == null) throw new InvalidOperationException("The elevated helper did not start.");
            linked.Token.ThrowIfCancellationRequested();

            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(readyTimeout.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) || clientId != helper.Id) {
                throw new IOException("Unexpected elevation pipe client.");
            }
            var ready = new byte[1];
            if (await pipe.ReadAsync(ready, readyTimeout.Token) != 1 || ready[0] != 1) {
                throw new IOException("The helper did not confirm administrator privileges.");
            }

            var connectedPipe = pipe;
            lock (_gate) {
                linked.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                _pipe = pipe;
                _helper = helper;
                pipe = null;
                helper = null;
            }
            NotifyStateChanged();
            _ = ObserveHelperAsync(connectedPipe, shutdownToken);
            return ElevationResult.Success;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) {
            return ElevationResult.Cancelled;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) {
            return ElevationResult.Cancelled;
        }
        catch (Exception ex) {
            ConsoleHelper.WriteLog($"[{DateTime.Now:HH:mm:ss}] [Elevation] Start failed: {ex.Message}");
            return ElevationResult.Failed;
        }
        finally {
            // Disconnecting makes the helper exit, even if startup or the handshake failed.
            pipe?.Dispose();
            helper?.Dispose();
            _operations.Release();
        }
    }

    public void Downgrade()
    {
        NamedPipeServerStream? pipe;
        lock (_gate) pipe = _pipe;
        if (pipe != null) Disconnect(pipe);
    }

    private void Disconnect(NamedPipeServerStream pipe)
    {
        bool owned;
        lock (_gate) {
            owned = ReferenceEquals(_pipe, pipe);
            if (owned) {
                _pipe = null;
                _helper?.Dispose();
                _helper = null;
                _pendingQuery?.TrySetResult(StorageProtocolResult.Failure(new("unavailable", null, "Administrator helper disconnected.")));
            }
        }
        pipe.Dispose();
        if (owned) NotifyStateChanged();
    }

    private async Task ObserveHelperAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try {
            while (true) {
                var response = await ElevationPipe.ReadAsync<ElevationResponse>(pipe, cancellationToken);
                if (response.id <= 0 || (response.result == null) == (response.error == null))
                    throw new InvalidDataException("Invalid elevation response.");
                lock (_gate) {
                    if (ReferenceEquals(_pipe, pipe) && _pendingQueryId == response.id)
                        _pendingQuery?.TrySetResult(response.result ?? StorageProtocolResult.Failure(new("error", null, response.error)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or ObjectDisposedException) { }
        finally { Disconnect(pipe); }
    }

    private void NotifyStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) {
            ConsoleHelper.WriteLog($"[{DateTime.Now:HH:mm:ss}] [Elevation] State notification failed: {ex.Message}");
        }
    }

    internal static bool IsHelperMode(string[] args) =>
        args.Any(arg => string.Equals(arg, HelperArgument, StringComparison.OrdinalIgnoreCase));

    internal static async Task RunHelperAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != HelperArgument || !IsElevated() ||
            !args[1].StartsWith(PipePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1][PipePrefix.Length..], "N", out _) ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) || parentId <= 0) return;

        try {
            using var parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != parentId) return;
            await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            await ServeHelperAsync(pipe);
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or Win32Exception or InvalidOperationException or ArgumentException) {
            // A failed handshake, parent exit, or explicit downgrade ends helper mode quietly.
        }
    }

    internal static async Task ServeHelperAsync(Stream pipe,
        Func<string, CancellationToken, Task<StorageProtocolResult>>? read = null,
        Func<string, CancellationToken, Task<StorageProtocolResult>>? readAta = null)
    {
        using var disconnected = new CancellationTokenSource();
        CancellationTokenSource? queryCancellation = null;
        Task? query = null;
        long activeId = 0;
        try {
            while (true) {
                var request = await ElevationPipe.ReadAsync<ElevationRequest>(pipe, disconnected.Token);
                ElevationPipe.Validate(request);
                if (request.operation == "cancel") {
                    if (activeId == request.id) queryCancellation?.Cancel();
                    continue;
                }
                // At most one native operation runs at a time. Await a completed
                // response before accepting the next query; cancellation remains readable.
                if (query != null) await query;
                queryCancellation?.Dispose();
                queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(disconnected.Token);
                activeId = request.id;
                query = CompleteHelperQueryAsync(pipe, request, queryCancellation.Token, disconnected.Token,
                    request.operation == "ataSmart" ? readAta ?? AtaSmartReader.ReadAsync : read ?? RealtekNvmeSmartReader.ReadAsync);
            }
        }
        finally {
            disconnected.Cancel();
            if (query != null) {
                try { await query; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            queryCancellation?.Dispose();
        }
    }

    private static async Task CompleteHelperQueryAsync(Stream pipe, ElevationRequest request,
        CancellationToken queryToken, CancellationToken connectionToken,
        Func<string, CancellationToken, Task<StorageProtocolResult>> read)
    {
        ElevationResponse response;
        try {
            response = new(request.id, await read(request.diskId!, queryToken), null);
        }
        catch (OperationCanceledException) {
            response = new(request.id, null, "Disk query was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or ArgumentException) {
            response = new(request.id, StorageProtocolResult.Failure(PhysicalDiskService.Failure(ex)), null);
        }
        try { await ElevationPipe.WriteAsync(pipe, response, connectionToken); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        lock (_gate) {
            if (_disposed) return;
            _disposed = true;
        }
        _shutdown.Cancel();
        Downgrade();
        _shutdown.Dispose();
        // The semaphore has no native wait handle; active startup releases it while unwinding.
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
