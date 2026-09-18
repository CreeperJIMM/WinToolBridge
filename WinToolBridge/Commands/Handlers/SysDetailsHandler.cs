using System.Text.Json;
using WinToolBridge.Services;

namespace WinToolBridge.Commands.Handlers;

public class SysDetailsHandler : ICommandHandler
{
    public string Action => "SYS:GET_DETAILS";

    public async Task<object?> HandleAsync(JsonElement? parameters, CancellationToken token)
    {
        var osTask = Task.Run(SystemInfoProvider.GetOsInfo, token);
        var cpuTask = Task.Run(SystemInfoProvider.GetCpuInfo, token);
        var memoryTask = Task.Run(SystemInfoProvider.GetMemoryInfo, token);
        var mbTask = Task.Run(SystemInfoProvider.GetMotherboardInfo, token);
        var gpuTask = Task.Run(SystemInfoProvider.GetGpuInfo, token);
        var storageTask = Task.Run(SystemInfoProvider.GetStorageInfo, token);
        var batteryTask = Task.Run(SystemInfoProvider.GetBatteryStatus, token);

        await Task.WhenAll(osTask, cpuTask, memoryTask, mbTask, gpuTask, storageTask, batteryTask);

        return new {
            machineName = Environment.MachineName,
            os = await osTask,
            cpu = await cpuTask,
            memory = await memoryTask,
            motherboard = await mbTask,
            gpu = await gpuTask,
            storage = await storageTask,
            battery = await batteryTask,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }
}