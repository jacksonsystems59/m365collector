using M365Collector.Contracts;
namespace M365Collector.Modules.TenantIdentity;
public sealed class TenantIdentityModule(IAppOnlyIdentityReader reader) : ICollectorModule
{
    public ModuleManifest Manifest => new("tenant-identity", "Tenant Identity", Product.Version, ["Microsoft Graph v1.0"], [new("Organization.Read.All", "Read tenant name and verified domains using app-only authentication.")], [], true);
    public async Task<Contracts.TenantIdentity> CollectAsync(Customer customer, CancellationToken ct)
    {
        var identity = await reader.ReadAsync(customer, ct);
        if (identity.TenantId != customer.TenantId) throw new InvalidDataException("Collector tenant mismatch."); return identity;
    }
    public static readonly string[] FutureModules = ["Exchange Message Trace"];
}
