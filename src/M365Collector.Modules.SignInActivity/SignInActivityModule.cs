using M365Collector.Contracts;
using M365Collector.Entra;
namespace M365Collector.Modules.SignInActivity;
public sealed class SignInActivityModule(CollectorApi api) : GraphAuditCollector(api,"sign-ins","signIns","createdDateTime")
{
    protected override StoredEvent Parse(Guid tenant,System.Text.Json.JsonElement value)=>EventParsing.SignIn(tenant,value);
}
