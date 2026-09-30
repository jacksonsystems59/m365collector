using System.Globalization;
using M365Collector.Contracts;
using M365Collector.Entra;
namespace M365Collector.Modules.UnifiedAudit;
public sealed class UnifiedAuditModule(CollectorApi api) : IAuditCollectorModule
{
    public static readonly string[] ContentTypes=["Audit.SharePoint","Audit.Exchange","Audit.AzureActiveDirectory","Audit.General"];
    public CollectorDefinition Definition=>CollectorCatalog.Get("unified-audit");
    public async Task<CollectionOutcome> CollectAsync(Customer customer,IAuditStore store,CancellationToken ct)
    {
        var root=$"https://manage.office.com/api/v1.0/{customer.TenantId}/activity/feed";
        var subscriptions=await api.SendAsync(customer,Definition,root+"/subscriptions/list",null,ct);
        var enabled=subscriptions.Data.EnumerateArray().Where(s=>EventParsing.Text(s,"status").Equals("enabled",StringComparison.OrdinalIgnoreCase)).Select(s=>EventParsing.Text(s,"contentType")).ToHashSet();
        var problems=new List<string>();long added=0;var feedAvailable=false;
        foreach(var type in ContentTypes)
        {
            try
            {
                if(!enabled.Contains(type))
                {
                    await api.SendAsync(customer,Definition,root+"/subscriptions/start?contentType="+type,HttpMethod.Post,ct);
                    var check=await api.SendAsync(customer,Definition,root+"/subscriptions/list",null,ct);
                    if(!check.Data.EnumerateArray().Any(s=>EventParsing.Text(s,"contentType")==type&&EventParsing.Text(s,"status").Equals("enabled",StringComparison.OrdinalIgnoreCase)))throw new CollectorAttentionException(type+" subscription is not enabled. Enable unified auditing in Microsoft Purview and retry.");
                }
                var end=DateTimeOffset.UtcNow;var saved=store.Checkpoint(customer.TenantId,Definition.Id,type);var start=saved==null?end.AddDays(-1):DateTimeOffset.Parse(saved,CultureInfo.InvariantCulture).AddHours(-1);
                var earliest=end.AddDays(-7).AddMinutes(10);
                if(start<earliest){start=earliest;problems.Add(type+": collection gap exceeds Microsoft's seven-day content availability. Earlier blobs cannot be recovered through this API.");}
                while(start<end)
                {
                    var until=start.AddHours(23)<end?start.AddHours(23):end;
                    string? url=$"{root}/subscriptions/content?contentType={type}&startTime={start.UtcDateTime:yyyy-MM-ddTHH:mm:ss}&endTime={until.UtcDateTime:yyyy-MM-ddTHH:mm:ss}";
                    var seen=new HashSet<string>();
                    while(url!=null)
                    {
                        if(!seen.Add(url)||seen.Count>10000)throw new InvalidDataException("Activity feed pagination repeated or exceeded safety limit.");
                        var page=await api.SendAsync(customer,Definition,url,null,ct);
                        foreach(var content in page.Data.EnumerateArray())
                        {
                            feedAvailable=true;var id=EventParsing.Text(content,"contentId");if(id.Length==0)throw new InvalidDataException("Missing activity content ID.");
                            if(EventParsing.Text(content,"contentType")!=type)throw new InvalidDataException("Activity feed content type mismatch.");
                            if(store.ContentProcessed(customer.TenantId,type,id))continue;
                            var blob=await api.SendAsync(customer,Definition,EventParsing.Text(content,"contentUri"),null,ct);
                            var events=blob.Data.EnumerateArray().Select(e=>EventParsing.Unified(customer.TenantId,e)).ToArray();added+=store.Persist(customer.TenantId,Definition.Id,events,type,id);
                        }
                        url=page.Next;
                    }
                    store.Checkpoint(customer.TenantId,Definition.Id,type,until.ToString("O"));start=until;
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(CollectorAttentionException error){problems.Add(type+": "+error.Message);}
            catch(Exception error){problems.Add(type+": "+error.GetType().Name+". Saved pages are retained and the failed window will be retried.");}
        }
        if(!feedAvailable)problems.Add("No activity-feed content is available. Confirm unified auditing is enabled in Microsoft Purview, tenant licensing and ActivityFeed.Read consent; new subscriptions can take time to produce content.");
        return new(added,problems.Count>0?string.Join("\n",problems):null);
    }
}
