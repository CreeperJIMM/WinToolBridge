using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinToolBridge.Commands;
using WinToolBridge.Resources;

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
    public const string SERVER_VERSION = "1.1.0";
    public const int DEFAULT_PORT = 58240;

    private static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://localhost:3000",
        "https://crjim.com"
    };

    public int Port { get; }
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly CommandRegistry _registry = new();
    private bool _isDisposed = false;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _unackedRetries = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public LocalBridgeServer(int port = DEFAULT_PORT)
    {
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public void Start()
    {
        _listener.Start();
        LogInfo(string.Format(Strings.ServerListening, Port, SERVER_VERSION));
        Task.Run(() => AcceptConnectionsAsync(_cts.Token));
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
                context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
                context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");

                if (context.Request.HttpMethod == "OPTIONS") {
                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    context.Response.Close();
                    continue;
                }

                if (context.Request.IsWebSocketRequest) {
                    var clientIp = context.Request.RemoteEndPoint.ToString();
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    _ = ProcessSessionAsync(wsContext.WebSocket, clientIp, token);
                }
                else {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                }
            }
            catch (Exception) {
                if (token.IsCancellationRequested) break;
            }
        }
    }

    private async Task ProcessSessionAsync(WebSocket socket, string clientIp, CancellationToken token)
    {
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

                var rawJson = Encoding.UTF8.GetString(ms.ToArray());
                var msg = JsonSerializer.Deserialize<ProtocolMessage>(rawJson);
                if (msg == null) continue;

                if (msg.type != "PNG") {
                    LogRecv(rawJson);
                }

                _ = HandleMessageAsync(socket, msg, token);
            }
        }
        catch (Exception ex) {
            if (socket.State != WebSocketState.Closed && socket.State != WebSocketState.Aborted) {
                LogWarn(string.Format(Strings.ConnectionError, ex.Message));
            }
        }
        finally {
            foreach (var key in _unackedRetries.Keys) {
                if (_unackedRetries.TryRemove(key, out var cts)) {
                    try { cts.Cancel(); cts.Dispose(); } catch { }
                }
            }

            try {
                if (socket.State == WebSocketState.Open) {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                }
                socket.Dispose();
            }
            catch { }

            LogError(string.Format(Strings.ClientDisconnected, clientIp));
        }
    }

    private async Task HandleMessageAsync(WebSocket socket, ProtocolMessage msg, CancellationToken token)
    {
        if (msg.type == "PNG") {
            await SendSilentRawAsync(socket, new { type = "POG" }, token);
            return;
        }

        if (msg.type == "ACK" && !string.IsNullOrEmpty(msg.ackId)) {
            if (_unackedRetries.TryRemove(msg.ackId, out var retryCts)) {
                try {
                    retryCts.Cancel();
                    retryCts.Dispose();
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
            var maxVer = ParseVersionSafe(clientVerStr);

            if (serverVer < minVer || serverVer > maxVer) {
                LogWarn(string.Format(Strings.VersionIncompatible, SERVER_VERSION, clientMinStr, clientVerStr));
                await SendLoggedRawAsync(socket, new {
                    type = "RST",
                    payload = new {
                        error = "Version out of range",
                        clientVersion = clientVerStr,
                        minVersion = clientMinStr,
                        serverVersion = SERVER_VERSION
                    }
                }, token);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Version out of range", token);
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
            if (!string.IsNullOrEmpty(msg.msgId)) {
                await SendLoggedRawAsync(socket, new { type = "ACK", ackId = msg.msgId }, token);
            }

            var action = msg.payload?.TryGetProperty("action", out var actProp) == true ? actProp.GetString() ?? "" : "";
            var reqId = msg.payload?.TryGetProperty("reqId", out var rIdProp) == true ? rIdProp.GetString() : msg.msgId;
            var parameters = msg.payload?.TryGetProperty("params", out var pProp) == true ? pProp : (JsonElement?)null;

            var (found, result, error) = await _registry.DispatchAsync(action, parameters, token);

            await SendDatWithRetryAsync(socket, new {
                reqId = reqId,
                status = found && error == null ? "OK" : "ERROR",
                error = error,
                data = result
            }, token);
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

    public async Task SendDatWithRetryAsync(WebSocket socket, object payloadData, CancellationToken token)
    {
        var msgId = $"b_dat_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..6]}";
        var messageObj = new {
            type = "DAT",
            msgId = msgId,
            payload = payloadData
        };

        var cts = new CancellationTokenSource();
        _unackedRetries[msgId] = cts;

        await SendLoggedRawAsync(socket, messageObj, token);

        _ = Task.Run(async () => {
            int retryCount = 0;
            while (!cts.Token.IsCancellationRequested && socket.State == WebSocketState.Open && retryCount < 3) {
                try {
                    await Task.Delay(3000, cts.Token);
                    retryCount++;
                    LogWarn(string.Format(Strings.RetryingSend, msgId, retryCount));
                    await SendLoggedRawAsync(socket, messageObj, token);
                }
                catch (TaskCanceledException) {
                    break;
                }
            }
            if (_unackedRetries.TryRemove(msgId, out var removedCts)) {
                removedCts.Dispose();
            }
        }, token);
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
        if (_isDisposed) return;

        try {
            _cts.Cancel();

            foreach (var cts in _unackedRetries.Values) {
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }
            _unackedRetries.Clear();

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

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Stop();
        _cts.Dispose();
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }
}