using M365Collector.Contracts;
using M365Collector.Entra;

namespace M365Collector.Modules.TenantIdentity;

public sealed class TenantIdentityModule(ITenantIdentityReader reader) : ICollectorModule
{
    public static ModuleManifest Definition { get; } = new(1, "tenant-identity", "Tenant Identity",
        "Verify app-only authentication and collect tenant identity and verified domains.", ProductInfo.Version,
        ["Microsoft Graph v1.0 /organization"],
        [new("Organization.Read.All", Guid.Parse("498476ce-e0fe-48b0-b801-37ba7e2685c6"), "Read tenant identity and verified domains.")], [], true);
    public ModuleManifest Manifest => Definition;
    public Task<IReadOnlyList<DependencyResult>> ValidateDependenciesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DependencyResult>>([new("Native Microsoft Graph HTTPS", true, true, "No PowerShell modules required.")]);
    public async Task<CollectionResult> CollectAsync(Customer customer, CancellationToken cancellationToken)
    {
        try
        {
            var identity = await reader.ReadAsync(customer, cancellationToken);
            return identity.TenantId == customer.TenantId && identity.TenantId != Guid.Empty
                ? new(true, identity, "Success") : CollectionResult.Failed("TenantMismatch");
        }
        catch (Exception exception) { return CollectionResult.Failed(AuthenticationErrors.Code(exception)); }
    }
}
public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleManifest> All { get; } = new[] { TenantIdentityModule.Definition }.Concat(
        new[] { "Sign-in Activity", "Failed Sign-ins", "Location / IP Activity", "Exchange Message Trace", "SharePoint & OneDrive Audit", "Teams Activity", "Directory Changes" }
        .Select(name => new ModuleManifest(1, name.ToLowerInvariant().Replace(' ', '-'), name, "Coming in a future version", ProductInfo.Version, [], [], [], false))).ToArray();
    public static IReadOnlyList<PermissionRequirement> Permissions(IEnumerable<string> enabledModules)
    {
        var modules = enabledModules.Select(id => All.Single(m => m.Id == id)).ToArray();
        if (modules.Any(m => !m.Implemented)) throw new NotSupportedException("An unimplemented module cannot be enabled.");
        return modules.SelectMany(m => m.Permissions).DistinctBy(p => p.Id).ToArray();
    }
}
