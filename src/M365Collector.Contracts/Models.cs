namespace M365Collector.Contracts;
public static class Product { public const string Version = "0.0.3"; public const string ServiceName = "M365CollectorService"; }
public enum LocalRole { Administrator, Operator, ReadOnly }
public sealed record LocalUser(string Name, LocalRole Role);
public sealed record Customer(Guid TenantId, string Name, Guid ClientId, string Thumbprint, TenantIdentity? Identity = null);
public sealed record VerifiedDomain(string Name, bool IsDefault, bool IsInitial);
public sealed record TenantIdentity(Guid TenantId, string DisplayName, IReadOnlyList<VerifiedDomain> Domains, DateTimeOffset CollectedAt);
public sealed record Permission(string Name, string Why);
public sealed record ModuleManifest(string Id, string DisplayName, string Version, string[] RequiredApis, Permission[] ApplicationPermissions, string[] PowerShellDependencies, bool CanCollect, string Status = "Enabled");
public interface ICollectorModule { ModuleManifest Manifest { get; } Task<TenantIdentity> CollectAsync(Customer customer, CancellationToken cancellationToken); }
public interface ICustomerRepository { IReadOnlyList<Customer> GetCustomers(); void SaveCustomer(Customer customer); }
public interface IAppOnlyIdentityReader { Task<TenantIdentity> ReadAsync(Customer customer, CancellationToken cancellationToken); }
public interface IBootstrapSession : IAsyncDisposable
{
    Task<Guid> IdentifyTenantAsync(CancellationToken cancellationToken);
    Task<Guid> ProvisionAsync(Guid tenant, byte[] publicCertificate, IProgress<string> progress, CancellationToken cancellationToken);
}
public sealed record ConnectionRequest(Guid Id, Customer Customer, string State, string? Error);
public sealed record Heartbeat(DateTimeOffset Time, string Version, string DataRoot, int Schema);
public sealed record AuditRecord(Guid TenantId, DateTimeOffset Timestamp, string? User, string? Workload, string? Action, string? File, string? Folder, string? Site, string? Ip, string? Location, string? Result);
