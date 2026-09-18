using System.ServiceProcess;

namespace SterlingMonitor;

public sealed class WindowsServices : IServiceAccess
{
    public static List<ServiceInfo> List()
    {
        var controllers = ServiceController.GetServices();
        try
        {
            return controllers.Select(s => {
                ServiceState state;
                try { state = ConvertState(s.Status); } catch { state = ServiceState.Unavailable; }
                return new ServiceInfo(s.ServiceName, s.DisplayName, state);
            }).OrderBy(s => s.DisplayName).ToList();
        }
        finally { foreach (var service in controllers) service.Dispose(); }
    }
    private static ServiceState ConvertState(ServiceControllerStatus status) => Enum.TryParse<ServiceState>(status.ToString(), out var state) ? state : ServiceState.Unavailable;
    public Task<Observation> ObserveAsync(ServicePolicy policy, DateTimeOffset now, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var service = new ServiceController(policy.Name);
            var state = ConvertState(service.Status);
            if (state != ServiceState.Running) return new Observation(state, false, $"Windows reports {state}.");
            if (!string.IsNullOrWhiteSpace(policy.HeartbeatPath))
            {
                var file = new FileInfo(policy.HeartbeatPath);
                if (!file.Exists) return new Observation(state, false, "Heartbeat file is missing or inaccessible.");
                var age = now - new DateTimeOffset(file.LastWriteTimeUtc);
                if (age.TotalSeconds < -60) return new Observation(state, false, "Heartbeat timestamp is in the future; check the system clock.");
                if (age.TotalSeconds > policy.HeartbeatMaxAgeSeconds) return new Observation(state, false, $"Heartbeat is stale ({(int)age.TotalSeconds}s old).");
                return new Observation(state, true, "Running; heartbeat is fresh.");
            }
            return new Observation(state, true, "Running (Windows status only; no heartbeat configured).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { return new Observation(ServiceState.Unavailable, false, $"Status check failed ({ex.GetType().Name}); check service existence and permissions."); }
    }, token);

    public async Task ControlAsync(string name, ServiceAction action, CancellationToken token)
    {
        using var service = new ServiceController(name);
        service.Refresh();
        if (action is ServiceAction.Stop or ServiceAction.Restart)
        {
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                if (!service.CanStop) throw new InvalidOperationException("This service does not accept stop commands.");
                // Never stop dependent services implicitly.
                var dependents = service.DependentServices;
                try
                {
                    if (dependents.Any(s => s.Status != ServiceControllerStatus.Stopped))
                        throw new InvalidOperationException("Running dependent services must be stopped separately.");
                }
                finally { foreach (var dependent in dependents) dependent.Dispose(); }
                service.Stop(false);
                await WaitAsync(service, ServiceControllerStatus.Stopped, token);
            }
        }
        if (action is ServiceAction.Start or ServiceAction.Restart)
        {
            service.Refresh();
            if (service.Status == ServiceControllerStatus.Running) return;
            if (service.Status == ServiceControllerStatus.Paused) service.Continue();
            else if (service.Status == ServiceControllerStatus.Stopped) service.Start();
            else throw new InvalidOperationException("The service is transitioning. Wait for it to settle before starting it.");
            await WaitAsync(service, ServiceControllerStatus.Running, token);
        }
    }
    private static async Task WaitAsync(ServiceController service, ServiceControllerStatus desired, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        do
        {
            token.ThrowIfCancellationRequested();
            service.Refresh();
            if (service.Status == desired) return;
            await Task.Delay(300, token);
        } while (DateTimeOffset.UtcNow < deadline);
        throw new System.TimeoutException("The service did not reach the requested state within 30 seconds.");
    }
}
