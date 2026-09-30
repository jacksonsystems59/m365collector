using M365Collector.Contracts;
using System.Globalization;
namespace M365Collector.Entra;
public abstract class GraphAuditCollector(CollectorApi api,string moduleId,string endpoint,string timeField) : IAuditCollectorModule
{
    public CollectorDefinition Definition=>CollectorCatalog.Get(moduleId);
    protected abstract StoredEvent Parse(Guid tenant,System.Text.Json.JsonElement value);
    public async Task<CollectionOutcome> CollectAsync(Customer customer,IAuditStore store,CancellationToken ct)
    {
        var end=DateTimeOffset.UtcNow;var saved=store.Checkpoint(customer.TenantId,moduleId,"through");var start=saved==null?end.AddDays(-1):DateTimeOffset.Parse(saved,CultureInfo.InvariantCulture).AddMinutes(-10);long added=0;
        // Commit each page, but advance the watermark only when the bounded time slice completes.
        while(start<end)
        {
            var until=start.AddDays(1)<end?start.AddDays(1):end;
            var filter=$"{timeField} ge {start.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ} and {timeField} lt {until.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
            string? url=$"https://graph.microsoft.com/v1.0/auditLogs/{endpoint}?$filter={Uri.EscapeDataString(filter)}&$top=500";var seen=new HashSet<string>();
            while(url!=null)
            {
                if(!seen.Add(url)||seen.Count>10000)throw new InvalidDataException("Graph pagination repeated or exceeded safety limit.");
                var page=await api.SendAsync(customer,Definition,url,null,ct);var events=page.Data.GetProperty("value").EnumerateArray().Select(e=>Parse(customer.TenantId,e)).ToArray();added+=store.Persist(customer.TenantId,moduleId,events);url=page.Next;
            }
            store.Checkpoint(customer.TenantId,moduleId,"through",until.ToString("O"));start=until;
        }
        return new(added);
    }
}
