using System.ServiceProcess;
using M365Collector.Contracts;
namespace M365Collector.Core;
public interface IServiceControl
{
    Task StopAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task VerifyAsync(RuntimePaths paths, string version, DateTimeOffset since, CancellationToken ct);
}
public sealed class WindowsServiceControl : IServiceControl
{
    public async Task StopAsync(CancellationToken ct)
    {
        using var service = new ServiceController(Product.ServiceName); service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        await WaitStatus(ServiceControllerStatus.Stopped, ct);
    }
    public async Task StartAsync(CancellationToken ct)
    {
        using var service = new ServiceController(Product.ServiceName); service.Refresh();
        if (service.Status == ServiceControllerStatus.Running) return;
        service.Start(); await WaitStatus(ServiceControllerStatus.Running, ct);
    }
    private static async Task WaitStatus(ServiceControllerStatus status, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var service = new ServiceController(Product.ServiceName);
        while (true) { timeout.Token.ThrowIfCancellationRequested(); service.Refresh(); if (service.Status == status) return; await Task.Delay(500, timeout.Token); }
    }
    public async Task VerifyAsync(RuntimePaths paths, string version, DateTimeOffset since, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                using var service = new ServiceController(Product.ServiceName); service.Refresh();
                var heartbeat = JsonFile.Read<Heartbeat>(paths.Heartbeat);
                if (service.Status == ServiceControllerStatus.Running && heartbeat.Version == version && heartbeat.Time >= since && heartbeat.Time > DateTimeOffset.UtcNow.AddSeconds(-30) && string.Equals(heartbeat.DataRoot, paths.Root, StringComparison.OrdinalIgnoreCase) && heartbeat.Schema > 0) return;
            }
            catch (IOException) { } catch (System.Text.Json.JsonException) { }
            await Task.Delay(500, timeout.Token);
        }
    }
}
