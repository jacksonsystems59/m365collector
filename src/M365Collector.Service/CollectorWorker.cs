using Microsoft.Extensions.Hosting;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Entra;
using M365Collector.Modules.TenantIdentity;
using M365Collector.Storage;
using M365Collector.Modules.SignInActivity;
using M365Collector.Modules.DirectoryChanges;
using M365Collector.Modules.UnifiedAudit;
namespace M365Collector.Service;
public sealed class CollectorWorker(RuntimePaths paths, CollectorStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Verify(); paths.Create(); store.RecoverInterruptedRuns();
        foreach(var customer in store.GetCustomers())store.EnsureModules(customer.TenantId);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        var module = new TenantIdentityModule(new AppOnlyIdentityReader(http));
        var api=new CollectorApi(http,new CertificateTokenProvider());
        var scheduler=new CollectionScheduler(store,paths,module,[new SignInActivityModule(api),new DirectoryChangesModule(api),new UnifiedAuditModule(api)]);
        using var collectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var collection = Collect(module, scheduler, collectionCancellation.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (collection.IsFaulted) await collection;
                if(store.Read("SELECT MAX(Version) FROM SchemaVersion",r=>r.GetInt32(0)).Single()!=CollectorStore.CurrentSchema)throw new InvalidDataException("Schema mismatch.");
                JsonFile.Write(paths.Heartbeat, new Heartbeat(DateTimeOffset.UtcNow, Product.Version, paths.Root, CollectorStore.CurrentSchema));
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        finally { collectionCancellation.Cancel(); try { await collection; } catch (OperationCanceledException) when (collectionCancellation.IsCancellationRequested) { } }
    }
    private async Task Collect(TenantIdentityModule module, CollectionScheduler scheduler, CancellationToken ct)
    {
        var running=new Dictionary<(Guid,string),Task>();
        try
        {
          while (!ct.IsCancellationRequested)
          {
            foreach(var finished in running.Where(x=>x.Value.IsCompleted).ToArray()){try{await finished.Value;}catch(Exception e){paths.Log("Service","Isolated collection failure: "+e.GetType().Name);}running.Remove(finished.Key);}
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
            foreach(var customer in store.GetCustomers())
            {
                foreach(var state in store.Modules(customer.TenantId))
                {
                    var key=(customer.TenantId,state.ModuleId);
                    if(running.Count<4&&!running.ContainsKey(key)&&CollectionScheduler.IsDue(state,DateTimeOffset.UtcNow))running[key]=scheduler.RunAsync(customer,state.ModuleId,ct);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
          }
        }
        finally{await Task.WhenAll(running.Values);}
    }
}
