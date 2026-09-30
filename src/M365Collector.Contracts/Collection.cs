namespace M365Collector.Contracts;

public enum CollectorHealth { Disabled, NotConfigured, Ready, Collecting, Healthy, Attention, Failed }
public sealed record CollectorDefinition(string Id, string Name, string Description, string Resource, string Permission, string Reason, int DefaultMinutes = 15);
public static class CollectorCatalog
{
    public static readonly CollectorDefinition[] All =
    [
        new("tenant-identity", "Tenant Identity", "Tenant name, domains and identity.", "https://graph.microsoft.com", "Organization.Read.All", "Read the tenant name and verified domains.", 60),
        new("sign-ins", "Entra Sign-in Activity", "Sign-ins, failures and Microsoft-provided IP/location information.", "https://graph.microsoft.com", "AuditLog.Read.All", "Read tenant sign-in and audit activity."),
        new("directory-changes", "Directory Changes", "Who changed users, groups, applications and other directory objects.", "https://graph.microsoft.com", "AuditLog.Read.All", "Read directory audit activity."),
        new("unified-audit", "Microsoft 365 Unified Audit", "SharePoint, OneDrive, Exchange and general audit events, including Teams when identified by Microsoft.", "https://manage.office.com", "ActivityFeed.Read", "Read the Microsoft 365 Management Activity feed.")
    ];
    public static readonly int[] Intervals = [5, 15, 30, 60, 360, 720, 1440];
    public static CollectorDefinition Get(string id) => All.SingleOrDefault(m => m.Id == id) ?? throw new ArgumentException("Unknown collector module.");
    public static void ValidateTenant(Guid id) { if (id == Guid.Empty) throw new ArgumentException("A tenant ID is required."); }
}
public sealed record ModuleState(Guid TenantId, string ModuleId, bool Enabled, int ScheduleMinutes, int RetentionDays, bool RetentionEnabled, bool Requested, CollectorHealth Health, DateTimeOffset? LastAttempt, DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, string? Error, long TotalRecords, long LastAdded, string PermissionStatus, string DependencyStatus);
public sealed record StoredEvent(Guid TenantId, string ModuleId, string EventId, DateTimeOffset Timestamp, string User, string UserDisplayName, string Workload, string Action, string Object, string Site, string Ip, string Country, string State, string City, string Result, string Application, string Category, string RawJson);
public sealed record AuditFilter(Guid TenantId, DateTimeOffset From, DateTimeOffset To, string ModuleId = "", string Workload = "", string User = "", string Action = "", string Object = "", string Site = "", string Ip = "", string Location = "", string Result = "", string Application = "", string Category = "");
public sealed record CollectorRun(string Id, Guid TenantId, string ModuleId, DateTimeOffset Started, DateTimeOffset? Ended, string State, long Added, string? Error);
public sealed record CollectionOutcome(long Added, string? Attention = null);
public interface IAuditStore
{
    string? Checkpoint(Guid tenant, string module, string key);
    void Checkpoint(Guid tenant, string module, string key, string value);
    bool ContentProcessed(Guid tenant, string contentType, string contentId);
    int Persist(Guid tenant, string module, IReadOnlyList<StoredEvent> events, string? contentType = null, string? contentId = null);
}
public interface IAuditCollectorModule
{
    CollectorDefinition Definition { get; }
    Task<CollectionOutcome> CollectAsync(Customer customer, IAuditStore store, CancellationToken ct);
}
public sealed class CollectorAttentionException(string message, bool missingPermission = false) : Exception(message)
{
    public bool MissingPermission { get; } = missingPermission;
}
