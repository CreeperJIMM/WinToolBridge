using System.Net.NetworkInformation;
using System.Text.Json;

namespace WinToolBridge.Commands.Handlers;

public class NetOpenAdapterDetailsHandler : ICommandHandler
{
    public string Action => "NET:OPEN_ADAPTER_DETAILS";

    public Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        var adapterName = parameters?.TryGetProperty("name", out var n) == true ? n.GetString() : null;

        if (string.IsNullOrWhiteSpace(adapterName)) {
            throw new ArgumentException("網卡名稱不可為空");
        }

        var realAdapters = NetworkInterface.GetAllNetworkInterfaces();
        var matched = realAdapters.FirstOrDefault(a =>
            string.Equals(a.Name, adapterName, StringComparison.OrdinalIgnoreCase));

        if (matched == null) {
            throw new ArgumentException($"找不到指定的本機網路介面: {adapterName}");
        }

        var tcs = new TaskCompletionSource<object?>();

        var thread = new Thread(() => {
            try {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) {
                    tcs.SetException(new PlatformNotSupportedException("無法載入 Shell.Application"));
                    return;
                }

                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) {
                    tcs.SetException(new InvalidOperationException("無法建立 Shell 實例"));
                    return;
                }

                dynamic folder = shell.NameSpace("::{7007ACC7-3202-11D1-AAD2-00805FC1270E}");
                if (folder == null) {
                    tcs.SetException(new InvalidOperationException("無法存取網路連線資料夾"));
                    return;
                }

                bool invoked = false;
                foreach (var item in folder.Items()) {
                    if (string.Equals(item.Name, matched.Name, StringComparison.OrdinalIgnoreCase)) {
                        item.InvokeVerb();
                        invoked = true;
                        break;
                    }
                }

                if (invoked) {
                    tcs.SetResult(new { opened = matched.Name });
                }
                else {
                    tcs.SetException(new InvalidOperationException($"未在連線資料夾中找到 {matched.Name}"));
                }
            }
            catch (Exception ex) {
                tcs.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }
}