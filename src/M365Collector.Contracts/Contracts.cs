namespace M365Collector.Contracts;

public static class ProductInfo
{
    public const string Version = "0.1.0";
    public const string ServiceName = "M365CollectorService";
    public const int ConfigSchema = 1;
    public const int DatabaseSchema = 1;
    public static object Describe() => new { product = "M365Collector", version = Version,
        serviceName = ServiceName, configSchema = ConfigSchema, databaseSchema = DatabaseSchema };
}

public sealed record RuntimeConfig(int SchemaVersion, string DataRoot, string InstallRoot,
    string InstalledVersion, bool SetupComplete, string AdministratorSid, int CollectionIntervalMinutes = 60);
public enum CustomerState { Draft, Active }
public sealed record CertificateReference(string Thumbprint, string Store = "My", string Location = "LocalMachine");
public sealed record Customer(Guid TenantId, string DisplayName, string DefaultDomain, Guid ClientId,
    CertificateReference? Certificate, CustomerState State, DateTimeOffset? ValidatedAt = null);
public sealed record VerifiedDomain(string Name, bool IsDefault, bool IsInitial);
public sealed record TenantIdentity(Guid TenantId, string DisplayName, IReadOnlyList<VerifiedDomain> VerifiedDomains,
    DateTimeOffset CollectedAt)
{
    public string DefaultDomain => VerifiedDomains.FirstOrDefault(d => d.IsDefault)?.Name ?? "";
    public string InitialDomain => VerifiedDomains.FirstOrDefault(d => d.IsInitial)?.Name ?? "";
}
public sealed record PermissionRequirement(string Name, Guid Id, string Reason);
public sealed record DependencyRequirement(string Name, string MinimumVersion, bool Required);
public sealed record DependencyResult(string Name, bool Required, bool Present, string Detail);
public sealed record ModuleManifest(int SchemaVersion, string Id, string Name, string Description, string Version,
    IReadOnlyList<string> RequiredApis, IReadOnlyList<PermissionRequirement> Permissions,
    IReadOnlyList<DependencyRequirement> PowerShellModules, bool Implemented);
public sealed record CollectionResult(bool Success, TenantIdentity? Identity, string Code)
{
    public static CollectionResult Failed(string code) => new(false, null, code);
}
public sealed record ServiceHealth(int SchemaVersion, string Version, string InstanceId, DateTimeOffset StartedAt,
    DateTimeOffset HeartbeatAt, string Status, string? LastErrorCode)
{
    public bool IsFresh(DateTimeOffset now) => HeartbeatAt <= now.AddSeconds(5) && now - HeartbeatAt < TimeSpan.FromSeconds(45);
}
public sealed record CollectionJob(long Id, Guid TenantId, string ModuleId, string Kind, string Status,
    string? ResultCode, TenantIdentity? Identity);
public interface ICollectorModule
{
    ModuleManifest Manifest { get; }
    Task<IReadOnlyList<DependencyResult>> ValidateDependenciesAsync(CancellationToken cancellationToken);
    Task<CollectionResult> CollectAsync(Customer customer, CancellationToken cancellationToken);
}
public interface ITenantIdentityReader
{
    Task<TenantIdentity> ReadAsync(Customer customer, CancellationToken cancellationToken);
}
public enum ApplicationRole { Administrator, Operator, ReadOnly }
public enum ApplicationAction { View, Configure, Onboard, Collect }
public interface IAuthorizationPolicy { bool Allows(ApplicationRole role, ApplicationAction action); }
