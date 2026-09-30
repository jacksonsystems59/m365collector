using System.Collections.Concurrent;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Storage;
using M365Collector.Modules.TenantIdentity;
namespace M365Collector.Service;
public sealed class CollectionScheduler(CollectorStore store,RuntimePaths paths,TenantIdentityModule identity,IReadOnlyList<IAuditCollectorModule> modules)
{
    private readonly ConcurrentDictionary<(Guid,string),byte> active=new();
    public static bool IsDue(ModuleState state,DateTimeOffset now)=>state.Enabled&&state.Health!=CollectorHealth.Collecting&&(state.Requested||state.LastAttempt==null||state.LastAttempt.Value.AddMinutes(state.ScheduleMinutes)<=now);
    public async Task<bool> RunAsync(Customer customer,string module,CancellationToken ct)
    {
        var key=(customer.TenantId,module);if(!active.TryAdd(key,0))return false;
        string? run=null;long before=0;
        try
        {
            var state=store.Modules(customer.TenantId).Single(m=>m.ModuleId==module);if(!state.Enabled)return false;
            before=state.TotalRecords;run=store.StartRun(customer.TenantId,module,DateTimeOffset.UtcNow);
            CollectionOutcome outcome;
            if(module=="tenant-identity")
            {
                var result=await identity.CollectAsync(customer,ct);store.SaveCustomer(customer with{Identity=result});JsonFile.Write(Path.Combine(paths.CustomerDirectory(customer.TenantId),"Data","tenant-identity.json"),result);store.CollectionResult(customer.TenantId,"Succeeded");outcome=new(0);
            }
            else outcome=await modules.Single(m=>m.Definition.Id==module).CollectAsync(customer,store,ct);
            store.FinishRun(customer.TenantId,module,run,outcome.Attention==null?CollectorHealth.Healthy:CollectorHealth.Attention,outcome.Added,outcome.Attention,outcome.Attention==null?"API access verified":"Partial / check prerequisites");
            store.Retain(customer.TenantId,module,DateTimeOffset.UtcNow);return true;
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested)
        {
            if(run!=null)store.FinishRun(customer.TenantId,module,run,CollectorHealth.Attention,Math.Max(0,store.Modules(customer.TenantId).Single(m=>m.ModuleId==module).TotalRecords-before),"Collection stopped with the service; saved pages remain and retry uses safe overlap.","Not checked");return false;
        }
        catch(Exception error)
        {
            if(run!=null)
            {
                var attention=error as CollectorAttentionException;
                var message=attention?.Message??$"{error.GetType().Name}: collection could not complete. Check connectivity, certificate validity and permissions. Saved pages are retained.";
                store.FinishRun(customer.TenantId,module,run,attention?.MissingPermission==true?CollectorHealth.NotConfigured:attention!=null?CollectorHealth.Attention:CollectorHealth.Failed,Math.Max(0,store.Modules(customer.TenantId).Single(m=>m.ModuleId==module).TotalRecords-before),message,attention?.MissingPermission==true?"Additional permission / consent required":"Not verified");
            }
            paths.Log("Service",$"Tenant {customer.TenantId}; module {module}: {error.GetType().Name}");return false;
        }
        finally{active.TryRemove(key,out _);}
    }
}
