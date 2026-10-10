using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinToolBridge.Commands;
using WinToolBridge.Events;
using WinToolBridge.Resources;
using WinToolBridge.Services.Usb;
using WinToolBridge.Services;
using WinToolBridge.Http;

namespace WinToolBridge;

public class ProtocolMessage
{
    public string type { get; set; } = "";
    public string? msgId { get; set; }
    public string? ackId { get; set; }
    public JsonElement? payload { get; set; }
}

public class LocalBridgeServer : IDisposable
{
    public static readonly string SERVER_VERSION = typeof(LocalBridgeServer).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(LocalBridgeServer).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
    public const int DEFAULT_PORT = 58240;
    public const int MAX_CONNECTIONS = 8;

    private static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://localhost:3000",
        "https://crjim.com"
    };

    public int Port { get; }
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    public EventRegistry Events { get; } = new();
    public UsbTreeService Usb { get; }
    public ImageAssetService Images { get; } = new();
    private readonly ImageAssetHttpHandler _imageHttp;
    private readonly object _lifecycleLock = new();
    private bool _isDisposed = false;
    private bool _isStopped = false;
    private int _activeConnections;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public LocalBridgeServer(int port = DEFAULT_PORT, UsbTreeService? usb = null)
    {
        Port = port;
        Usb = usb ?? new UsbTreeService(images: Images, assetBaseUrl: $"http://127.0.0.1:{Port}");
        _imageHttp = new ImageAssetHttpHandler(Images);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public void Start()
    {
        lock (_lifecycleLock) {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_isStopped) throw new InvalidOperationException("A stopped server cannot be restarted.");
            _listener.Start();
            LogInfo(string.Format(Strings.ServerListening, Port, SERVER_VERSION));
            var token = _cts.Token;
            Task.Run(() => AcceptConnectionsAsync(token));
        }
    }

    private async Task AcceptConnectionsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener.IsListening) {
            try {
                var context = await _listener.GetContextAsync();

                var rawOrigin = context.Request.Headers["Origin"];
                var origin = rawOrigin?.TrimEnd('/');

                if (string.IsNullOrEmpty(origin) || !AllowedOrigins.Contains(origin)) {
                    LogWarn(string.Format(Strings.SecurityOriginBlocked, rawOrigin ?? Strings.UnknownOrigin));
                    context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    context.Response.Close();
                    continue;
                }

                context.Response.Headers.Add("Access-Control-Allow-Origin", rawOrigin);
                context.Response.Headers.Add("Access-Control-Allow-Private-Network", "true");
                context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, If-None-Match");
                context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, HEAD, POST, OPTIONS");

                if (context.Request.HttpMethod == "OPTIONS") {
                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    context.Response.Close();
                    continue;
                }

                if (context.Request.IsWebSocketRequest) {
                    // Reserve before upgrading; sessions release their own slots on exit.
                    if (Interlocked.Increment(ref _activeConnections) > MAX_CONNECTIONS) {
                        Interlocked.Decrement(ref _activeConnections);
                        context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        context.Response.Close();
                        continue;
                    }

                    bool sessionStarted = false;
                    try {
                        var clientIp = context.Request.RemoteEndPoint.ToString();
                        var wsContext = await context.AcceptWebSocketAsync(null);
                        _ = ProcessLimitedSessionAsync(wsContext.WebSocket, clientIp, token);
                        sessionStarted = true;
                    }
                    finally {
                        if (!sessionStarted) {
                            Interlocked.Decrement(ref _activeConnections);
                        }
                    }
                }
                else {
                    _ = ProcessHttpAsync(context, token);
                }
            }
            catch (Exception) {
                if (token.IsCancellationRequested) break;
            }
        }
    }

    private async Task ProcessLimitedSessionAsync(WebSocket socket, string clientIp, CancellationToken token)
    {
        try {
            await ProcessSessionAsync(socket, clientIp, token);
        }
        finally {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private async Task ProcessHttpAsync(HttpListenerContext context, CancellationToken token)
    {
        try {
            await _imageHttp.HandleAsync(context, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) {
            if (!token.IsCancellationRequested) LogWarn(string.Format(Strings.ConnectionError, ex.Message));
        }
        finally {
            try { context.Response.Close(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException) { }
        }
    }

    private async Task ProcessSessionAsync(WebSocket socket, string clientIp, CancellationToken token)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = sessionCts.Token;
        var unackedRetries = new ConcurrentDictionary<string, CancellationTokenSource>();
        using var events = Events.CreateSession(
            (payload, eventToken) => SendDatWithRetryAsync(socket, payload, unackedRetries, eventToken, "EVT"), token);
        var registry = new CommandRegistry(events, Usb);
        LogSuccess(string.Format(Strings.ClientConnected, clientIp));

        var buffer = new byte[8192];

        try {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested) {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidMessageType, "Text messages required", token);
                    break;
                }

                var rawJson = Encoding.UTF8.GetString(ms.ToArray());
                _ = ProcessMessageAsync(socket, rawJson, registry, unackedRetries, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) {
            if (socket.State != WebSocketState.Closed && socket.State != WebSocketState.Aborted) {
                LogWarn(string.Format(Strings.ConnectionError, ex.Message));
            }
        }
        finally {
            // Cancel only this session's commands and response retries.
            // Each retry task owns disposal of its cancellation source.
            sessionCts.Cancel();
            events.Dispose();
            unackedRetries.Clear();

            bool sendLockTaken = false;
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try {
                // Reply to a received Close without starting a second receive operation.
                // Serialize the close frame with any response already being sent.
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) {
                    await _sendLock.WaitAsync(closeCts.Token);
                    sendLockTaken = true;
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) {
                        await socket.CloseOutputAsync(socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            "Closed", closeCts.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (closeCts.IsCancellationRequested) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
            finally {
                if (sendLockTaken) _sendLock.Release();
                socket.Dispose();
            }

            LogError(string.Format(Strings.ClientDisconnected, clientIp));
        }
    }

    private async Task ProcessMessageAsync(WebSocket socket, string rawJson, CommandRegistry registry,
        ConcurrentDictionary<string, CancellationTokenSource> unackedRetries, CancellationToken token)
    {
        string? msgId = null;
        string? reqId = null;
        try {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                throw new JsonException("Message must be a JSON object.");
            }

            // Recover correlation IDs before deserialization can fail on another field.
            msgId = ReadString(root, "msgId");
            reqId = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                ? ReadString(payload, "reqId") ?? msgId
                : msgId;
            if (string.IsNullOrWhiteSpace(reqId)) reqId = msgId;

            var msg = root.Deserialize<ProtocolMessage>() ?? throw new JsonException("Missing message.");
            if (msg.type != "PNG") LogRecv(rawJson);
            ValidateMessage(msg);
            await HandleMessageAsync(socket, msg, registry, unackedRetries, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (WebSocketException ex) {
            if (!token.IsCancellationRequested) LogWarn(string.Format(Strings.ConnectionError, ex.Message));
        }
        catch (Exception ex) {
            LogWarn(string.Format(Strings.ConnectionError, ex.Message));
            if (string.IsNullOrWhiteSpace(reqId) || token.IsCancellationRequested || socket.State != WebSocketState.Open) return;

            try {
                if (!string.IsNullOrWhiteSpace(msgId)) {
                    await SendLoggedRawAsync(socket, new { type = "ACK", ackId = msgId }, token);
                }
                await SendDatWithRetryAsync(socket, new {
                    reqId,
                    status = "ERROR",
                    error = ex is JsonException ? "Invalid message format." : "Message processing failed.",
                    data = (object?)null
                }, unackedRetries, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
            catch (Exception sendEx) {
                LogWarn(string.Format(Strings.ConnectionError, sendEx.Message));
            }
        }
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static void ValidateMessage(ProtocolMessage msg)
    {
        if (msg.type == "PNG" || msg.type == "RST") return;
        if (msg.type == "ACK") {
            if (string.IsNullOrWhiteSpace(msg.ackId)) throw new JsonException("ACK requires ackId.");
            return;
        }
        if (msg.type != "SYN" && msg.type != "DAT") throw new JsonException("Unsupported message type.");
        if (msg.payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object) {
            throw new JsonException("Payload must be a JSON object.");
        }

        if (msg.type == "SYN") {
            if (string.IsNullOrWhiteSpace(msg.msgId) || string.IsNullOrWhiteSpace(ReadString(payload, "version"))) {
                throw new JsonException("SYN requires msgId and version.");
            }
            foreach (var name in new[] { "minVersion", "browser", "browserVersion" }) {
                if (payload.TryGetProperty(name, out var value) &&
                    value.ValueKind != JsonValueKind.String && value.ValueKind != JsonValueKind.Null) {
                    throw new JsonException($"{name} must be a string.");
                }
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(ReadString(payload, "action"))) throw new JsonException("DAT requires action.");
        if (payload.TryGetProperty("reqId", out var reqId) &&
            (reqId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reqId.GetString()))) {
            throw new JsonException("reqId must be a nonempty string.");
        }
        if (string.IsNullOrWhiteSpace(ReadString(payload, "reqId") ?? msg.msgId)) throw new JsonException("DAT requires a request ID.");
        if (payload.TryGetProperty("params", out var parameters) &&
            parameters.ValueKind != JsonValueKind.Object && parameters.ValueKind != JsonValueKind.Null) {
            throw new JsonException("params must be an object or null.");
        }
    }

    private async Task HandleMessageAsync(WebSocket socket, ProtocolMessage msg, CommandRegistry registry,
        ConcurrentDictionary<string, CancellationTokenSource> unackedRetries, CancellationToken token)
    {
        if (msg.type == "PNG") {
            await SendSilentRawAsync(socket, new { type = "POG" }, token);
            return;
        }

        if (msg.type == "ACK" && !string.IsNullOrEmpty(msg.ackId)) {
            if (unackedRetries.TryRemove(msg.ackId, out var retryCts)) {
                try {
                    retryCts.Cancel();
                }
                catch { }
            }
            return;
        }

        // SYN 握手交握
        if (msg.type == "SYN") {
            var clientVerStr = msg.payload?.TryGetProperty("version", out var v) == true ? v.GetString() ?? "0.0.0" : "0.0.0";
            var clientMinStr = msg.payload?.TryGetProperty("minVersion", out var mv) == true ? mv.GetString() ?? clientVerStr : clientVerStr;

            var browser = msg.payload?.TryGetProperty("browser", out var b) == true ? b.GetString() ?? "Browser" : "Browser";
            var browserVer = msg.payload?.TryGetProperty("browserVersion", out var bv) == true ? bv.GetString() ?? "" : "";

            LogInfo(string.Format(Strings.HandshakeReceived, browser, browserVer, clientMinStr, clientVerStr));

            var serverVer = ParseVersionSafe(SERVER_VERSION);
            var minVer = ParseVersionSafe(clientMinStr);

            if (serverVer < minVer) {
                LogWarn(string.Format(Strings.VersionIncompatible, SERVER_VERSION, clientMinStr, clientVerStr));
                await SendLoggedRawAsync(socket, new {
                    type = "RST",
                    payload = new {
                        error = "Server version below minimum",
                        clientVersion = clientVerStr,
                        minVersion = clientMinStr,
                        serverVersion = SERVER_VERSION
                    }
                }, token);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Server version below minimum", token);
                return;
            }

            await SendLoggedRawAsync(socket, new {
                type = "ACK",
                ackId = msg.msgId,
                payload = new {
                    version = SERVER_VERSION,
                    status = "READY",
                    machine = Environment.MachineName,
                    os = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"
                }
            }, token);
            return;
        }

        // DAT 指令處理
        if (msg.type == "DAT") {
            var action = msg.payload?.TryGetProperty("action", out var actProp) == true ? actProp.GetString() ?? "" : "";
            var reqId = msg.payload?.TryGetProperty("reqId", out var rIdProp) == true ? rIdProp.GetString() : msg.msgId;
            var parameters = msg.payload?.TryGetProperty("params", out var pProp) == true ? pProp : (JsonElement?)null;
            // Event handlers complete synchronously before the first send await, preserving receive order.
            var eventDispatch = action is "EVENT:ON" or "EVENT:OFF" or "EVENT:RESET"
                ? registry.DispatchAsync(action, parameters, token) : null;
            if (!string.IsNullOrEmpty(msg.msgId)) {
                await SendLoggedRawAsync(socket, new { type = "ACK", ackId = msg.msgId }, token);
            }

            var (found, result, error) = await (eventDispatch ?? registry.DispatchAsync(action, parameters, token));

            await SendDatWithRetryAsync(socket, new {
                reqId = reqId,
                status = found && error == null ? "OK" : "ERROR",
                error = error,
                data = result
            }, unackedRetries, token);
        }
    }

    private static Version ParseVersionSafe(string verStr)
    {
        if (Version.TryParse(verStr, out var v)) return v;
        var parts = verStr.Split('.');
        int major = parts.Length > 0 && int.TryParse(parts[0], out var ma) ? ma : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out var mi) ? mi : 0;
        int build = parts.Length > 2 && int.TryParse(parts[2], out var bu) ? bu : 0;
        return new Version(major, minor, build);
    }

    private async Task SendDatWithRetryAsync(WebSocket socket, object payloadData,
        ConcurrentDictionary<string, CancellationTokenSource> unackedRetries, CancellationToken token, string messageType = "DAT")
    {
        token.ThrowIfCancellationRequested();
        var msgId = $"b_dat_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..6]}";
        var messageObj = new {
            type = messageType,
            msgId = msgId,
            payload = payloadData
        };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var retryToken = cts.Token;
        unackedRetries[msgId] = cts;

        try {
            await SendLoggedRawAsync(socket, messageObj, retryToken);
        }
        catch {
            unackedRetries.TryRemove(msgId, out _);
            cts.Dispose();
            throw;
        }

        _ = RetryDatAsync(socket, messageObj, msgId, unackedRetries, cts, retryToken);
    }

    private async Task RetryDatAsync(WebSocket socket, object messageObj, string msgId,
        ConcurrentDictionary<string, CancellationTokenSource> unackedRetries,
        CancellationTokenSource cts, CancellationToken retryToken)
    {
        try {
            int retryCount = 0;
            while (socket.State == WebSocketState.Open && retryCount < 3) {
                await Task.Delay(3000, retryToken);
                retryToken.ThrowIfCancellationRequested();
                retryCount++;
                LogWarn(string.Format(Strings.RetryingSend, msgId, retryCount));
                await SendLoggedRawAsync(socket, messageObj, retryToken);
            }
        }
        catch (OperationCanceledException) when (retryToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) {
            LogWarn(string.Format(Strings.ConnectionError, ex.Message));
        }
        finally {
            unackedRetries.TryRemove(msgId, out _);
            cts.Dispose();
        }
    }

    private async Task SendSilentRawAsync(WebSocket socket, object data, CancellationToken token)
    {
        if (socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));

        await _sendLock.WaitAsync(token);
        try {
            if (socket.State == WebSocketState.Open) {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
        }
        finally {
            _sendLock.Release();
        }
    }

    private async Task SendLoggedRawAsync(WebSocket socket, object data, CancellationToken token)
    {
        if (socket.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(data);
        LogSend(json);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(token);
        try {
            if (socket.State == WebSocketState.Open) {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
        }
        finally {
            _sendLock.Release();
        }
    }

    #region 控制台彩色 Log 輸出

    private static readonly object _consoleLock = new();

    private static void LogRecv(string message)
    {
        lock (_consoleLock) {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("◀ [RECV] ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }

    private static void LogSend(string message)
    {
        lock (_consoleLock) {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("▶ [SEND] ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }

    private static void LogSuccess(string message)
    {
        lock (_consoleLock) {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ {message}");
            Console.ResetColor();
        }
    }

    private static void LogError(string message)
    {
        lock (_consoleLock) {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✖ {message}");
            Console.ResetColor();
        }
    }

    private static void LogWarn(string message)
    {
        lock (_consoleLock) {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠ {message}");
            Console.ResetColor();
        }
    }

    private static void LogInfo(string message)
    {
        lock (_consoleLock) {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"ℹ {message}");
            Console.ResetColor();
        }
    }

    #endregion

    public void Stop()
    {
        lock (_lifecycleLock) {
            if (_isDisposed || _isStopped) return;
            _isStopped = true;
            Usb.Dispose();
            try {
                _cts.Cancel();
                if (_listener.IsListening) {
                    _listener.Stop();
                }
                _listener.Close();
                LogInfo(Strings.ServerStopped);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex) {
                LogError(string.Format(Strings.ServerStopError, ex.Message));
            }
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock) {
            if (_isDisposed) return;
            Stop();
            _isDisposed = true;
            _cts.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
