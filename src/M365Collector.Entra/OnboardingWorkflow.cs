using M365Collector.Contracts;
namespace M365Collector.Entra;
public static class OnboardingWorkflow
{
    public static async Task<Customer> ProvisionAsync(IBootstrapSession session, string name, Func<Guid, (string Thumbprint, byte[] Public)> createCertificate, IProgress<string> progress, CancellationToken ct)
    {
        await using (session)
        {
            var tenant = await session.IdentifyTenantAsync(ct); progress.Report("Tenant identified: " + tenant);
            var certificate = createCertificate(tenant);
            var client = await session.ProvisionAsync(tenant, certificate.Public, progress, ct);
            return new Customer(tenant, name, client, certificate.Thumbprint);
        }
    }
}
