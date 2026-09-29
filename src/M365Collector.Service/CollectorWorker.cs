using Microsoft.Extensions.Hosting;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Entra;
using M365Collector.Modules.TenantIdentity;
using M365Collector.Storage;
namespace M365Collector.Service;
public sealed class CollectorWorker(RuntimePaths paths, CollectorStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Verify(); paths.Create();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var module = new TenantIdentityModule(new AppOnlyIdentityReader(http));
        using var collectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var collection = Collect(module, collectionCancellation.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (collection.IsFaulted) await collection;
                store.Verify(); JsonFile.Write(paths.Heartbeat, new Heartbeat(DateTimeOffset.UtcNow, Product.Version, paths.Root, CollectorStore.CurrentSchema));
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        finally { collectionCancellation.Cancel(); try { await collection; } catch (OperationCanceledException) when (collectionCancellation.IsCancellationRequested) { } }
    }
    private async Task Collect(TenantIdentityModule module, CancellationToken ct)
    {
        var nextScheduled = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            foreach (var request in store.Requests().Where(r => r.State == "Pending"))
            {
                try
                {
                    var identity = await module.CollectAsync(request.Customer, ct);
                    paths.CustomerDirectory(identity.TenantId);
                    store.FinishRequest(request.Id, request.Customer with { Identity = identity }, null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { store.FinishRequest(request.Id, request.Customer, "Service app-only verification failed (" + error.GetType().Name + "). Check certificate access, tenant/client IDs, Organization.Read.All consent and network connectivity; retry after consent propagation."); }
            }
            if (DateTimeOffset.UtcNow >= nextScheduled)
            {
                foreach (var customer in store.GetCustomers())
                {
                    if (store.Read("SELECT Enabled FROM ModuleConfiguration WHERE TenantId=$id AND ModuleId='tenant-identity'", r => r.GetBoolean(0), ("$id", customer.TenantId.ToString())).SingleOrDefault() != true) continue;
                    try
                    {
                        var identity = await module.CollectAsync(customer, ct); store.SaveCustomer(customer with { Identity = identity });
                        JsonFile.Write(Path.Combine(paths.CustomerDirectory(customer.TenantId), "Data", "tenant-identity.json"), identity); store.CollectionResult(customer.TenantId, "Succeeded");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) { store.CollectionResult(customer.TenantId, "Failed: " + error.GetType().Name); paths.Log("Service", $"Tenant {customer.TenantId}: {error.GetType().Name}"); }
                }
                nextScheduled = DateTimeOffset.UtcNow.AddHours(1);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }
}
