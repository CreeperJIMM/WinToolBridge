using System.Net.NetworkInformation;
using System.Text.Json;
using System.Runtime.InteropServices;
using WinToolBridge.Services;

namespace WinToolBridge.Commands.Handlers;

public class NetOpenAdapterDetailsHandler : ICommandHandler
{
    public string Action => "NET:OPEN_ADAPTER_DETAILS";
    public bool RequiresDeduplication => true;

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (parameters is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) {
            throw new ArgumentException("網卡名稱不可為空");
        }
        var adapterName = name.GetString();

        if (string.IsNullOrWhiteSpace(adapterName)) {
            throw new ArgumentException("網卡名稱不可為空");
        }

        var realAdapters = NetworkInterface.GetAllNetworkInterfaces();
        var matched = realAdapters.FirstOrDefault(a =>
            string.Equals(a.Name, adapterName, StringComparison.OrdinalIgnoreCase));

        if (matched == null) {
            throw new ArgumentException($"找不到指定的本機網路介面: {adapterName}");
        }

        var adapterNames = realAdapters.Select(a => a.Name).OrderByDescending(n => n.Length).ToArray();
        var tcs = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() => {
            object? shell = null, folder = null, items = null;
            try {
                token.ThrowIfCancellationRequested();
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) {
                    throw new PlatformNotSupportedException("無法載入 Shell.Application");
                }

                shell = Activator.CreateInstance(shellType);
                if (shell == null) {
                    throw new InvalidOperationException("無法建立 Shell 實例");
                }

                token.ThrowIfCancellationRequested();
                folder = ((dynamic)shell).NameSpace("::{7007ACC7-3202-11D1-AAD2-00805FC1270E}");
                if (folder == null) {
                    throw new InvalidOperationException("無法存取網路連線資料夾");
                }

                token.ThrowIfCancellationRequested();
                items = ((dynamic)folder).Items();
                // Index explicitly rather than retaining a hidden COM enumerator.
                var count = (int)((dynamic)items).Count;
                for (var index = 0; index < count; index++) {
                    token.ThrowIfCancellationRequested();
                    object? item = null;
                    try {
                        item = ((dynamic)items).Item(index);
                        if (item != null && string.Equals((string)((dynamic)item).Name, matched.Name, StringComparison.OrdinalIgnoreCase)) {
                            token.ThrowIfCancellationRequested();
                            var activation = ShortcutWindowHelper.InvokeShellAsync(
                                () => ((dynamic)item).InvokeVerb(),
                                caption => MatchesAdapterWindowTitle(caption, matched.Name, adapterNames), token);
                            tcs.TrySetResult(activation);
                            return;
                        }
                    }
                    finally { ReleaseCom(item); }
                }
                throw new InvalidOperationException($"未在連線資料夾中找到 {matched.Name}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) {
                tcs.TrySetCanceled(token);
            }
            catch (Exception ex) {
                tcs.TrySetException(ex);
            }
            finally {
                ReleaseCom(items);
                ReleaseCom(folder);
                ReleaseCom(shell);
            }
        }) { IsBackground = true, Name = "WinToolBridge network adapter properties" };

        thread.SetApartmentState(ApartmentState.STA);
        token.ThrowIfCancellationRequested();
        thread.Start();

        var activationTask = await tcs.Task;
        await activationTask;
        return new { opened = matched.Name };
    }

    private static bool MatchesAdapterWindowTitle(string caption, string adapterName, string[] adapterNames)
    {
        // Prefer the longest adapter name so "Ethernet" cannot match "Ethernet 2 Status".
        var titleAdapter = adapterNames.FirstOrDefault(name =>
            string.Equals(caption, name, StringComparison.OrdinalIgnoreCase) ||
            (caption.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                caption.Length > name.Length && char.IsWhiteSpace(caption[name.Length])));
        return string.Equals(titleAdapter, adapterName, StringComparison.OrdinalIgnoreCase);
    }

    private static void ReleaseCom(object? value)
    {
        try {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException or COMException) {
            Console.Error.WriteLine($"[NET:OPEN_ADAPTER_DETAILS] COM cleanup failed: {ex.Message}");
        }
    }
}
