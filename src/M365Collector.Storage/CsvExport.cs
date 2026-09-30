using System.Text;
using M365Collector.Contracts;
namespace M365Collector.Storage;
public static class CsvExport
{
    public const int MaximumRows=100000;
    public static string Cell(string text)
    {
        // Spreadsheet formula injection protection also covers leading whitespace/control characters.
        var first=text.TrimStart();if(first.Length>0&&"=+-@".Contains(first[0]))text="'"+text;
        return "\""+text.Replace("\"","\"\"")+"\"";
    }
    public static async Task<int> ExportAsync(CollectorStore store,AuditFilter filter,string customer,string target,CancellationToken ct)
    {
        var count=store.Count(filter);if(count>MaximumRows)throw new InvalidOperationException($"This export contains {count:N0} rows. Narrow filters to at most {MaximumRows:N0} rows.");
        var temporary=target+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            // A read transaction gives the export a consistent snapshot while the service keeps collecting.
            await using var writer=new StreamWriter(temporary,false,new UTF8Encoding(true));
            await writer.WriteLineAsync(string.Join(",",new[]{"Customer",customer,"Tenant",filter.TenantId.ToString(),"Generated UTC",DateTimeOffset.UtcNow.ToString("O"),"From UTC",filter.From.ToUniversalTime().ToString("O"),"To UTC (exclusive)",filter.To.ToUniversalTime().ToString("O"),"Module",filter.ModuleId}.Select(Cell)));
            await writer.WriteLineAsync("Timestamp UTC,User,User display name,Source,Workload,Action,Object/File,Site/Folder,IP,Country,State,City,Result,Application,Category,Event ID");
            var exported=0;
            foreach(var e in store.StreamEvents(filter))
            {
                ct.ThrowIfCancellationRequested();if(++exported>MaximumRows)throw new InvalidOperationException("Export safety limit reached; narrow the filters.");
                await writer.WriteLineAsync(string.Join(",",new[]{e.Timestamp.ToUniversalTime().ToString("O"),e.User,e.UserDisplayName,e.ModuleId,e.Workload,e.Action,e.Object,e.Site,e.Ip,e.Country,e.State,e.City,e.Result,e.Application,e.Category,e.EventId}.Select(Cell)));
            }
            await writer.FlushAsync(ct);await writer.DisposeAsync();File.Move(temporary,target,true);return exported;
        }
        catch{if(File.Exists(temporary))File.Delete(temporary);throw;}
    }
}
