using System.Text.Json;
using System.Text.Json.Serialization;

namespace SterlingMonitor;

public enum AlertLevel { LogOnly, Warning, Critical }
public enum TlsMode { StartTls, ImplicitTls }
public enum ServiceState { Running, Stopped, Paused, StartPending, StopPending, ContinuePending, PausePending, Unavailable }
public enum ServiceAction { Start, Stop, Restart }

public sealed class MonitorSettings
{
    public int SchemaVersion { get; set; } = 1;
    public int PollSeconds { get; set; } = 15;
    public bool StartMinimized { get; set; }
    public SmtpSettings Smtp { get; set; } = new();
    public List<ServicePolicy> Services { get; set; } = [];
    public MonitorSettings Copy() => JsonSerializer.Deserialize<MonitorSettings>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Unsupported settings version.");
        if (PollSeconds is < 5 or > 300) throw new ArgumentException("Polling interval must be 5–300 seconds.");
        if (Services.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Services.Count)
            throw new ArgumentException("A service may only be added once.");
        foreach (var policy in Services) policy.Validate();
        if (Smtp.Enabled) Smtp.Validate();
    }
}

public sealed class ServicePolicy
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public AlertLevel Level { get; set; } = AlertLevel.Critical;
    public int FailureSeconds { get; set; } = 30;
    public int RepeatMinutes { get; set; } = 15;
    public bool EmailRecovery { get; set; } = true;
    public bool AutoRestart { get; set; }
    public int MaxRestarts { get; set; } = 3;
    public int RestartCooldownMinutes { get; set; } = 5;
    public string HeartbeatPath { get; set; } = "";
    public int HeartbeatMaxAgeSeconds { get; set; } = 120;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Choose a service.");
        if (!Enum.IsDefined(Level)) throw new ArgumentException("Invalid alert level.");
        if (FailureSeconds is < 0 or > 3600 || RepeatMinutes is < 1 or > 1440 || MaxRestarts is < 1 or > 10 || RestartCooldownMinutes is < 1 or > 1440 || HeartbeatMaxAgeSeconds is < 10 or > 86400)
            throw new ArgumentException("Service policy values are outside the supported range.");
        if (HeartbeatPath.Length > 0 && (!Path.IsPathFullyQualified(HeartbeatPath) || HeartbeatPath.StartsWith(@"\\")))
            throw new ArgumentException("Use an absolute local heartbeat path, not a network share.");
    }
}

public sealed class SmtpSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public TlsMode Tls { get; set; } = TlsMode.StartTls;
    public bool Authenticate { get; set; } = true;
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public string From { get; set; } = "";
    public string Recipients { get; set; } = "";
    public string SubjectPrefix { get; set; } = "[SterlingMonitor]";
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Contains('/') || Host.Any(char.IsWhiteSpace)) throw new ArgumentException("Enter an SMTP hostname without a protocol or spaces.");
        if (Port is < 1 or > 65535 || !Enum.IsDefined(Tls)) throw new ArgumentException("Invalid SMTP port or TLS mode.");
        if (!ValidMailbox(From)) throw new ArgumentException("Enter a valid sender email address, including its domain.");
        if (RecipientList().Length == 0 || RecipientList().Any(x => !ValidMailbox(x))) throw new ArgumentException("Enter recipient addresses including domains, separated by semicolons.");
        if (SubjectPrefix.Contains('\r') || SubjectPrefix.Contains('\n')) throw new ArgumentException("Subject prefix must be one line.");
        if (Authenticate && (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(ProtectedPassword))) throw new ArgumentException("SMTP authentication requires a username and password.");
    }
    public string[] RecipientList() => Recipients.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static bool ValidMailbox(string value) => !value.Contains('\r') && !value.Contains('\n') && MimeKit.MailboxAddress.TryParse(value, out var mailbox) && mailbox.Address.LastIndexOf('@') > 0 && !mailbox.Address.EndsWith('@');
}

public record ServiceInfo(string Name, string DisplayName, ServiceState State);
public record Observation(ServiceState State, bool Healthy, string Detail);
public record ServiceView(string Name, string DisplayName, string State, string Health, string Level, string Recovery, string Detail);
public record AlertMessage(string ServiceName, AlertLevel Level, bool Recovery, string Detail, DateTimeOffset At);

public interface IServiceAccess
{
    Task<Observation> ObserveAsync(ServicePolicy policy, DateTimeOffset now, CancellationToken token);
    Task ControlAsync(string name, ServiceAction action, CancellationToken token);
}
public interface IAlertSender
{
    Task SendAsync(SmtpSettings settings, AlertMessage message, CancellationToken token);
}
