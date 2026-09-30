using M365Collector.Contracts;
using M365Collector.Entra;
namespace M365Collector.Modules.DirectoryChanges;
public sealed class DirectoryChangesModule(CollectorApi api) : GraphAuditCollector(api,"directory-changes","directoryAudits","activityDateTime")
{
    protected override StoredEvent Parse(Guid tenant,System.Text.Json.JsonElement value)=>EventParsing.Directory(tenant,value);
}
