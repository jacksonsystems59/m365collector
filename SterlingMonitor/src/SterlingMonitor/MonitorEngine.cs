namespace SterlingMonitor;

public sealed class MonitorEngine(IServiceAccess services, IAlertSender sender, ActivityLog log)
{
    private sealed class Runtime
    {
        public DateTimeOffset? FailedSince, HealthySince, LastAlert, LastAttempt, LastRestart;
        public DateTimeOffset MaintenanceUntil;
        public bool Incident, AlertDelivered;
        public int Restarts;
        public string LastDetail = "";
    }
    private readonly Dictionary<string, Runtime> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1, 1);
    public event Action<AlertMessage>? IncidentChanged;
    public IReadOnlyList<ServiceView> Views { get; private set; } = [];

    public async Task TickAsync(MonitorSettings settings, DateTimeOffset now, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) return;
        try
        {
            foreach (var name in states.Keys.Where(n => !settings.Services.Any(p => p.Enabled && p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToArray()) states.Remove(name);
            var views = new List<ServiceView>();
            foreach (var policy in settings.Services)
            {
                token.ThrowIfCancellationRequested();
                if (!policy.Enabled) { views.Add(new(policy.Name, policy.DisplayName, "—", "Disabled", policy.Level.ToString(), "Off", "Monitoring disabled.")); continue; }
                if (!states.TryGetValue(policy.Name, out var runtime)) states[policy.Name] = runtime = new();
                var observation = await services.ObserveAsync(policy, now, token);
                var maintenance = runtime.MaintenanceUntil > now;
                if (!maintenance) await EvaluateAsync(settings.Smtp, policy, runtime, observation, now, token);
                views.Add(new(policy.Name, policy.DisplayName, observation.State.ToString(), maintenance ? "Maintenance" : observation.Healthy ? "Healthy" : runtime.Incident ? "Unhealthy" : "Grace period",
                    policy.Level.ToString(), policy.AutoRestart ? $"{runtime.Restarts}/{policy.MaxRestarts} attempts" : "Manual", maintenance ? $"Alerts and recovery suspended until {runtime.MaintenanceUntil.LocalDateTime:t}." : observation.Detail));
            }
            Views = views;
        }
        finally { gate.Release(); }
    }

    private async Task EvaluateAsync(SmtpSettings smtp, ServicePolicy policy, Runtime state, Observation observation, DateTimeOffset now, CancellationToken token)
    {
        if (observation.Healthy)
        {
            state.FailedSince = null;
            state.HealthySince ??= now;
            if (state.Incident)
            {
                log.Write($"RECOVERED {policy.Name}: {observation.Detail}");
                IncidentChanged?.Invoke(new(policy.Name, policy.Level, true, observation.Detail, now));
                state.Incident = false;
                state.LastAttempt = null;
            }
            if (state.AlertDelivered && policy.EmailRecovery && policy.Level != AlertLevel.LogOnly && smtp.Enabled)
            {
                if (await DeliverAsync(smtp, new(policy.Name, policy.Level, true, observation.Detail, now), state, now, token)) state.AlertDelivered = false;
            }
            else if (!policy.EmailRecovery || policy.Level == AlertLevel.LogOnly) state.AlertDelivered = false;
            // Sustained health is required before replenishing automatic restart attempts.
            if (now - state.HealthySince >= TimeSpan.FromMinutes(5)) { state.Restarts = 0; state.LastRestart = null; }
            return;
        }
        state.HealthySince = null;
        state.FailedSince ??= now;
        if (now - state.FailedSince < TimeSpan.FromSeconds(policy.FailureSeconds)) return;
        if (!state.Incident)
        {
            state.Incident = true;
            state.LastAlert = null;
            state.LastAttempt = null;
            log.Write($"{policy.Level.ToString().ToUpperInvariant()} {policy.Name}: {observation.Detail}");
            IncidentChanged?.Invoke(new(policy.Name, policy.Level, false, observation.Detail, now));
        }
        if (state.LastDetail != observation.Detail)
        {
            state.LastDetail = observation.Detail;
            log.Write($"STATUS {policy.Name}: {observation.Detail}");
        }
        if (smtp.Enabled && policy.Level != AlertLevel.LogOnly && (state.LastAlert is null || now - state.LastAlert >= TimeSpan.FromMinutes(policy.RepeatMinutes)))
        {
            if (await DeliverAsync(smtp, new(policy.Name, policy.Level, false, observation.Detail, now), state, now, token))
            { state.AlertDelivered = true; state.LastAlert = now; }
        }
        if (policy.AutoRestart && state.Restarts < policy.MaxRestarts &&
            observation.State is ServiceState.Stopped or ServiceState.Paused or ServiceState.Running &&
            (state.LastRestart is null || now - state.LastRestart >= TimeSpan.FromMinutes(policy.RestartCooldownMinutes)))
        {
            state.Restarts++;
            state.LastRestart = now;
            log.Write($"AUTO RECOVERY {policy.Name}: attempt {state.Restarts}/{policy.MaxRestarts}.");
            try
            {
                await services.ControlAsync(policy.Name, observation.State == ServiceState.Running ? ServiceAction.Restart : ServiceAction.Start, token);
                log.Write($"AUTO RECOVERY {policy.Name}: command completed; health will be checked on the next poll.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { log.Write($"AUTO RECOVERY FAILED {policy.Name}: {ex.GetType().Name}. Check permissions, dependencies and service configuration."); }
        }
    }
    private async Task<bool> DeliverAsync(SmtpSettings smtp, AlertMessage message, Runtime state, DateTimeOffset now, CancellationToken token)
    {
        if (state.LastAttempt is not null && now - state.LastAttempt < TimeSpan.FromMinutes(1)) return false;
        state.LastAttempt = now;
        try
        {
            await sender.SendAsync(smtp, message, token);
            log.Write($"EMAIL accepted by SMTP server: {message.ServiceName} ({(message.Recovery ? "recovery" : message.Level)}).");
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.Write($"EMAIL FAILED {message.ServiceName}: {SmtpSender.FailureHint(ex)} Retry in at least 60 seconds."); return false; }
    }
    public async Task ManualAsync(string name, ServiceAction action, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!states.TryGetValue(name, out var state)) states[name] = state = new();
            state.MaintenanceUntil = DateTimeOffset.UtcNow.AddMinutes(5);
            state.FailedSince = null;
            log.Write($"MANUAL {action}: {name}. Five-minute maintenance window started.");
            await services.ControlAsync(name, action, token);
            log.Write($"MANUAL {action}: {name} completed.");
        }
        finally { gate.Release(); }
    }
    public async Task SetMaintenanceAsync(string name, bool enable, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!states.TryGetValue(name, out var state)) states[name] = state = new();
            state.MaintenanceUntil = enable ? DateTimeOffset.UtcNow.AddMinutes(30) : DateTimeOffset.MinValue;
            state.FailedSince = null;
            log.Write($"MAINTENANCE {name}: {(enable ? "30 minutes" : "ended")}.");
        }
        finally { gate.Release(); }
    }
}
