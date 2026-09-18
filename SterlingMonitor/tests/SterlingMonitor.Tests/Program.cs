using SterlingMonitor;
using System.Reflection;

internal static class Program
{
    private static int passed;
    private static readonly string temp = Path.Combine(Path.GetTempPath(), "SterlingMonitor-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset epoch = DateTimeOffset.UtcNow;
    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(temp);
        try
        {
            RunAsync().GetAwaiter().GetResult();
            UiSmoke(args.FirstOrDefault());
            Console.WriteLine($"PASS: {passed} checks. No Windows services were modified and no external emails were sent.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(temp, true); }
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        passed++; Console.WriteLine("PASS: " + name);
    }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or InvalidDataException) { Check(true, name); return; }
        throw new Exception("FAILED: " + name);
    }
    private static (MonitorEngine Engine, FakeServices Services, FakeSender Sender, MonitorSettings Settings) Setup()
    {
        var services = new FakeServices(); var sender = new FakeSender();
        var engine = new MonitorEngine(services, sender, new ActivityLog(temp));
        return (engine, services, sender, new MonitorSettings { Smtp = new() { Enabled = true }, Services = [new() { Name = "Example", DisplayName = "Example service", FailureSeconds = 30 }] });
    }
    private static async Task RunAsync()
    {
        var x = Setup();
        await x.Engine.TickAsync(x.Settings, epoch, default);
        await x.Engine.TickAsync(x.Settings, epoch.AddSeconds(29), default);
        Check(x.Sender.Messages.Count == 0, "Grace period suppresses transient failures");
        await x.Engine.TickAsync(x.Settings, epoch.AddSeconds(30), default);
        Check(x.Sender.Messages.Count == 1 && !x.Sender.Messages[0].Recovery, "Persistent failure sends an alert");
        await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(5), default);
        Check(x.Sender.Messages.Count == 1, "Repeat alerts respect the configured interval");
        await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(16), default);
        Check(x.Sender.Messages.Count == 2, "Persistent incident sends a repeat alert");
        x.Services.Current = new(ServiceState.Running, true, "Healthy");
        await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(17), default);
        await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(18), default);
        Check(x.Sender.Messages.Count == 3 && x.Sender.Messages[^1].Recovery, "Recovery is sent once");

        x = Setup(); x.Sender.Fail = true; x.Settings.Services[0].FailureSeconds = 0;
        await x.Engine.TickAsync(x.Settings, epoch, default);
        await x.Engine.TickAsync(x.Settings, epoch.AddSeconds(30), default);
        Check(x.Sender.Attempts == 1, "SMTP failure retries are rate-limited");
        x.Sender.Fail = false;
        await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(1), default);
        Check(x.Sender.Messages.Count == 1, "Failed alert retries before normal repeat interval");
        x.Sender.Fail = true; x.Services.Current = new(ServiceState.Running, true, "Healthy");
        await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(2), default);
        x.Sender.Fail = false; await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(3), default);
        Check(x.Sender.Messages.Count == 2 && x.Sender.Messages[^1].Recovery, "Failed recovery email is retried");

        x = Setup(); var p = x.Settings.Services[0]; p.FailureSeconds = 0; p.AutoRestart = true; p.MaxRestarts = 2; p.RestartCooldownMinutes = 1; x.Services.FailControl = true;
        await x.Engine.TickAsync(x.Settings, epoch, default); await x.Engine.TickAsync(x.Settings, epoch.AddSeconds(30), default);
        Check(x.Services.Commands.Count == 1, "Automatic recovery observes cooldown");
        await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(1), default); await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(2), default);
        Check(x.Services.Commands.Count == 2, "Failed restart attempts are bounded");
        x.Services.Current = new(ServiceState.Running, true, "Healthy"); await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(3), default);
        x.Services.Current = new(ServiceState.Stopped, false, "Stopped"); await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(4), default);
        Check(x.Services.Commands.Count == 2, "Brief healthy interval does not reset restart budget");
        x.Services.Current = new(ServiceState.Running, true, "Healthy"); await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(5), default); await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(10), default);
        x.Services.Current = new(ServiceState.Stopped, false, "Stopped"); await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(11), default);
        Check(x.Services.Commands.Count == 3, "Five minutes of continuous health resets restart budget");

        x = Setup(); x.Settings.Services[0].FailureSeconds = 0; x.Settings.Services[0].AutoRestart = true;
        await x.Engine.ManualAsync("Example", ServiceAction.Stop, default); await x.Engine.TickAsync(x.Settings, DateTimeOffset.UtcNow, default);
        Check(x.Services.Commands.Count == 1 && x.Sender.Messages.Count == 0 && x.Engine.Views[0].Health == "Maintenance", "Manual stop suspends alerts and automatic recovery");
        await x.Engine.SetMaintenanceAsync("Example", false, default); await x.Engine.TickAsync(x.Settings, DateTimeOffset.UtcNow, default);
        Check(x.Sender.Messages.Count == 1 && x.Services.Commands.Count == 2, "Resume ends maintenance");

        x = Setup(); x.Settings.Services[0].Level = AlertLevel.LogOnly; x.Settings.Services[0].FailureSeconds = 0;
        await x.Engine.TickAsync(x.Settings, epoch, default);
        Check(x.Sender.Messages.Count == 0 && x.Engine.Views[0].Health == "Unhealthy", "LogOnly detects incidents without email");
        x.Settings.Services[0].Enabled = false; await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(1), default);
        Check(x.Engine.Views[0].Health == "Disabled", "Disabled service is not monitored");

        x = Setup(); x.Settings.Services[0].FailureSeconds = 0; x.Settings.Services[0].AutoRestart = true;
        x.Services.Current = new(ServiceState.StartPending, false, "Pending"); await x.Engine.TickAsync(x.Settings, epoch, default);
        Check(x.Sender.Messages.Count == 1 && x.Services.Commands.Count == 0, "Stuck pending state alerts without competing control commands");
        x.Services.Current = new(ServiceState.Running, false, "Stale heartbeat"); await x.Engine.TickAsync(x.Settings, epoch.AddMinutes(1), default);
        Check(x.Services.Commands.Single() == ServiceAction.Restart, "Stale heartbeat triggers restart for a running service");

        x = Setup(); x.Settings.Services.Add(new() { Name = "Second", DisplayName = "Second service", FailureSeconds = 0 }); x.Settings.Services[0].FailureSeconds = 0;
        await x.Engine.TickAsync(x.Settings, epoch, default);
        Check(x.Sender.Messages.Select(m => m.ServiceName).Distinct().Count() == 2, "Multiple services maintain independent alert state");

        var encrypted = SecretStore.Protect("test-secret-not-a-real-password");
        Check(!encrypted.Contains("test-secret") && SecretStore.Unprotect(encrypted) == "test-secret-not-a-real-password", "DPAPI credential round-trip");
        var store = new SettingsStore(Path.Combine(temp, "settings")); var saved = new MonitorSettings(); saved.Smtp.ProtectedPassword = encrypted; store.Save(saved);
        Check(store.Load().Smtp.ProtectedPassword == encrypted && !File.ReadAllText(store.FilePath).Contains("test-secret"), "Settings persist only encrypted password");
        File.WriteAllText(store.FilePath, "{ broken"); Reject(() => store.Load(), "Corrupt settings fail closed");
        Reject(() => new MonitorSettings { Services = [new() { Name = "same" }, new() { Name = "SAME" }] }.Validate(), "Duplicate services rejected case-insensitively");
        Reject(() => new ServicePolicy { Name = "service", HeartbeatPath = @"\\server\share\heartbeat" }.Validate(), "Network heartbeat paths rejected");
        Reject(() => new SmtpSettings { Host = "smtp.example.com", From = "sender@example.com", Recipients = "invalid", Authenticate = false }.Validate(), "Invalid recipients rejected");

        var available = WindowsServices.List(); Check(available.Count > 0, "Read-only Windows service enumeration succeeds");
        var windows = new WindowsServices();
        var missing = await windows.ObserveAsync(new() { Name = "SterlingMonitor-Nonexistent-" + Guid.NewGuid() }, epoch, default);
        Check(missing.State == ServiceState.Unavailable && !missing.Healthy, "Missing service becomes an unhealthy observation");
        var running = available.FirstOrDefault(s => s.State == ServiceState.Running);
        if (running != null)
        {
            var heartbeat = Path.Combine(temp, "heartbeat"); File.WriteAllText(heartbeat, "ok");
            var policy = new ServicePolicy { Name = running.Name, HeartbeatPath = heartbeat, HeartbeatMaxAgeSeconds = 120 };
            Check((await windows.ObserveAsync(policy, DateTimeOffset.UtcNow, default)).Healthy, "Fresh heartbeat accepted");
            File.SetLastWriteTimeUtc(heartbeat, DateTime.UtcNow.AddMinutes(-5));
            Check(!(await windows.ObserveAsync(policy, DateTimeOffset.UtcNow, default)).Healthy, "Stale heartbeat detected on a running service");
            File.Delete(heartbeat);
            Check(!(await windows.ObserveAsync(policy, DateTimeOffset.UtcNow, default)).Healthy, "Missing heartbeat detected on a running service");
        }
    }
    private static void UiSmoke(string? screenshotDirectory)
    {
        ApplicationConfiguration.Initialize();
        var type = typeof(MonitorEngine).Assembly.GetType("SterlingMonitor.MainForm")!;
        var store = new SettingsStore(Path.Combine(temp, "ui"));
        using var form = (Form)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [store, new MonitorSettings(), new ActivityLog(temp), false, true], null)!;
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new(-30000, -30000); form.Show(); Application.DoEvents();
        foreach (var page in new[] { "Dashboard", "ShowSettings", "ShowActivity", "ShowAbout" })
        {
            if (page != "Dashboard") type.GetMethod(page, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            form.PerformLayout(); Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            if (screenshotDirectory != null) { Directory.CreateDirectory(screenshotDirectory); bitmap.Save(Path.Combine(screenshotDirectory, page + ".png")); }
            Check(true, "UI renders: " + page);
        }
        // Preview mode disables polling. Dispose the tray icon explicitly.
        ((NotifyIcon)type.GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!).Dispose();
        foreach (var (name, argument) in new (string, object)[] { ("PolicyDialog", new ServicePolicy { Name = "TestService", DisplayName = "Example Windows service" }), ("ServicePicker", new List<ServiceInfo> { new("TestService", "Example Windows service", ServiceState.Running) }) })
        {
            var dialogType = typeof(MonitorEngine).Assembly.GetType("SterlingMonitor." + name)!;
            using var dialog = (Form)Activator.CreateInstance(dialogType, [argument])!;
            dialog.ShowInTaskbar = false; dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new(-30000, -30000); dialog.Show(); Application.DoEvents();
            if (name == "PolicyDialog")
            {
                static IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
                Check(Equals(Descendants(dialog).OfType<ComboBox>().Single().SelectedItem, AlertLevel.Critical), "Policy dialog preserves the configured alert level after display");
            }
            using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
            if (screenshotDirectory != null) bitmap.Save(Path.Combine(screenshotDirectory, name + ".png"));
            Check(true, "UI renders: " + name);
        }
    }
}

internal sealed class FakeServices : IServiceAccess
{
    public Observation Current = new(ServiceState.Stopped, false, "Stopped");
    public bool FailControl;
    public List<ServiceAction> Commands = [];
    public Task<Observation> ObserveAsync(ServicePolicy policy, DateTimeOffset now, CancellationToken token) => Task.FromResult(Current);
    public Task ControlAsync(string name, ServiceAction action, CancellationToken token) { Commands.Add(action); return FailControl ? Task.FromException(new InvalidOperationException("Test failure")) : Task.CompletedTask; }
}
internal sealed class FakeSender : IAlertSender
{
    public bool Fail;
    public int Attempts;
    public List<AlertMessage> Messages = [];
    public Task SendAsync(SmtpSettings settings, AlertMessage message, CancellationToken token)
    {
        Attempts++; if (Fail) return Task.FromException(new IOException("Simulated SMTP outage")); Messages.Add(message); return Task.CompletedTask;
    }
}
