using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M365Collector.Contracts;
namespace M365Collector.Entra;
public static class EventParsing
{
    public static JsonElement At(JsonElement value,params string[] path)
    {
        foreach(var name in path){if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty(name,out value))return default;}return value;
    }
    public static string Text(JsonElement value,params string[] path)
    {
        var found=path.Length==0?value:At(value,path);return found.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null?"":found.ValueKind==JsonValueKind.String?found.GetString()??"":found.GetRawText();
    }
    private static DateTimeOffset Time(JsonElement value,string property)
    {
        if(!DateTimeOffset.TryParse(Text(value,property),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out var time))throw new InvalidDataException("Microsoft event is missing a valid timestamp.");return time;
    }
    public static string SignInResult(int code)=>code==0?"Successful":code is 50058 or 50072 or 50074 or 50076 or 50079 or 50158 or 50140 or 65001?"Interrupted":"Failed";
    public static StoredEvent SignIn(Guid tenant,JsonElement e)
    {
        var id=Text(e,"id");if(id.Length==0)throw new InvalidDataException("Missing sign-in ID.");
        var error=At(e,"status","errorCode");var result=error.ValueKind==JsonValueKind.Number&&error.TryGetInt32(out var code)?SignInResult(code):"Unknown";
        return new(tenant,"sign-ins",id,Time(e,"createdDateTime"),Text(e,"userPrincipalName"),Text(e,"userDisplayName"),"Entra","Sign-in",Text(e,"resourceDisplayName"),"",Text(e,"ipAddress"),Text(e,"location","countryOrRegion"),Text(e,"location","state"),Text(e,"location","city"),result,Text(e,"appDisplayName"),Text(e,"clientAppUsed"),e.GetRawText());
    }
    public static StoredEvent Directory(Guid tenant,JsonElement e)
    {
        var id=Text(e,"id");if(id.Length==0)throw new InvalidDataException("Missing directory audit ID.");
        var targets=At(e,"targetResources");var target=targets.ValueKind==JsonValueKind.Array?string.Join("; ",targets.EnumerateArray().Select(t=>Text(t,"displayName")+" "+Text(t,"id")+" "+Text(t,"type"))):"";
        var user=Text(e,"initiatedBy","user","userPrincipalName");if(user.Length==0)user=Text(e,"initiatedBy","user","id");if(user.Length==0)user=Text(e,"initiatedBy","app","displayName");
        var result=Text(e,"result");result=result.Equals("success",StringComparison.OrdinalIgnoreCase)?"Successful":result.Equals("failure",StringComparison.OrdinalIgnoreCase)?"Failed":result;
        return new(tenant,"directory-changes",id,Time(e,"activityDateTime"),user,Text(e,"initiatedBy","user","displayName"),"Entra",Text(e,"activityDisplayName"),target,"",Text(e,"initiatedBy","user","ipAddress"),"","","",result,Text(e,"initiatedBy","app","displayName"),Text(e,"category"),e.GetRawText());
    }
    public static StoredEvent Unified(Guid tenant,JsonElement e)
    {
        var organization=Text(e,"OrganizationId");if(organization.Length>0&&(!Guid.TryParse(organization,out var actual)||actual!=tenant))throw new InvalidDataException("Audit event belongs to a different tenant.");
        var id=Text(e,"Id");if(id.Length==0)id="sha256:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.GetRawText())));
        var result=Text(e,"ResultStatus");result=result.Equals("Succeeded",StringComparison.OrdinalIgnoreCase)||result.Equals("Success",StringComparison.OrdinalIgnoreCase)?"Successful":result.Equals("Failure",StringComparison.OrdinalIgnoreCase)?"Failed":result;
        var obj=Text(e,"ObjectId");var file=Text(e,"SourceFileName");var folder=Text(e,"SourceRelativeUrl");
        return new(tenant,"unified-audit",id,Time(e,"CreationTime"),Text(e,"UserId"),"",Text(e,"Workload"),Text(e,"Operation"),string.Join(" ",new[]{obj,file,Path.GetExtension(file)}.Where(s=>s.Length>0)),string.Join(" ",new[]{Text(e,"SiteUrl"),folder}.Where(s=>s.Length>0)),Text(e,"ClientIP"),"","","",result,Text(e,"ApplicationDisplayName"),Text(e,"RecordType"),e.GetRawText());
    }
}
