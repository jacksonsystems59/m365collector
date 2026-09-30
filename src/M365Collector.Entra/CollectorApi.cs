using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Identity.Client;
using M365Collector.Contracts;
using M365Collector.Security;

namespace M365Collector.Entra;

public interface ICollectorTokenProvider
{
    Task<string> AcquireAsync(Customer customer,string resource,bool forceRefresh,CancellationToken ct);
}
public sealed class CertificateTokenProvider : ICollectorTokenProvider
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid,Guid,string,string),(string Token,DateTimeOffset Expires)> cache=new();
    public async Task<string> AcquireAsync(Customer customer,string resource,bool forceRefresh,CancellationToken ct)
    {
        if(resource is not ("https://graph.microsoft.com" or "https://manage.office.com"))throw new ArgumentException("Unsupported Microsoft resource.");
        var key=(customer.TenantId,customer.ClientId,customer.Thumbprint,resource);
        if(!forceRefresh&&cache.TryGetValue(key,out var cached)&&cached.Expires>DateTimeOffset.UtcNow.AddMinutes(2))return cached.Token;
        using var cert=Certificates.Find(customer.Thumbprint);
        var app=ConfidentialClientApplicationBuilder.Create(customer.ClientId.ToString()).WithAuthority("https://login.microsoftonline.com/"+customer.TenantId).WithCertificate(cert).Build();
        var result=await app.AcquireTokenForClient([resource+"/.default"]).WithForceRefresh(forceRefresh).ExecuteAsync(ct);
        if(!Guid.TryParse(result.TenantId,out var tenant)||tenant!=customer.TenantId)throw new InvalidDataException("App-only token tenant mismatch.");
        cache[key]=(result.AccessToken,result.ExpiresOn);return result.AccessToken;
    }
}
public sealed record ApiPage(JsonElement Data,string? Next);
public sealed class CollectorApi(HttpClient http,ICollectorTokenProvider tokens,Func<TimeSpan,CancellationToken,Task>? delay=null)
{
    private readonly Func<TimeSpan,CancellationToken,Task> wait=delay??Task.Delay;
    public static void ValidateUrl(Customer customer,string resource,string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length!=0)throw new InvalidDataException("Unsafe Microsoft pagination/content URI.");
        var allowed=resource=="https://graph.microsoft.com"?uri.Host=="graph.microsoft.com"&&(uri.AbsolutePath=="/v1.0/auditLogs/signIns"||uri.AbsolutePath=="/v1.0/auditLogs/directoryAudits"):
            resource=="https://manage.office.com"&&uri.Host=="manage.office.com"&&(uri.AbsolutePath.StartsWith($"/api/v1.0/{customer.TenantId}/activity/feed/",StringComparison.OrdinalIgnoreCase)||uri.AbsolutePath.StartsWith($"/api/v1/{customer.TenantId}/activity/feed/",StringComparison.OrdinalIgnoreCase));
        if(!allowed)throw new InvalidDataException("Microsoft continuation URI does not match the customer/resource.");
    }
    public async Task<ApiPage> SendAsync(Customer customer,CollectorDefinition module,string url,HttpMethod? method,CancellationToken ct)
    {
        ValidateUrl(customer,module.Resource,url);var force=false;
        for(var attempt=0;attempt<5;attempt++)
        {
            ct.ThrowIfCancellationRequested();var token=await tokens.AcquireAsync(customer,module.Resource,force,ct);
            try{CheckRoleWhenReadable(token,module.Permission);}catch(CollectorAttentionException)when(!force){force=true;continue;}
            try
            {
                using var request=new HttpRequestMessage(method??HttpMethod.Get,url);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
                using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
                if(response.StatusCode==HttpStatusCode.Unauthorized&&!force){force=true;continue;}
                if(response.StatusCode==HttpStatusCode.TooManyRequests||(int)response.StatusCode>=500)
                {
                    if(attempt==4)throw new CollectorAttentionException("Microsoft remained throttled or unavailable after bounded retries. Collection will resume on its next scheduled run.");
                    var pause=response.Headers.RetryAfter?.Delta??(response.Headers.RetryAfter?.Date-DateTimeOffset.UtcNow)??TimeSpan.FromSeconds(Math.Pow(2,attempt)+Random.Shared.NextDouble());
                    if(pause<TimeSpan.Zero)pause=TimeSpan.Zero;
                    // Long Retry-After values are respected, with cancellation allowing prompt service shutdown.
                    if(pause>TimeSpan.FromHours(24))throw new CollectorAttentionException("Microsoft requested an unusually long retry delay. Check service status before retrying.");
                    await wait(pause,ct);continue;
                }
                if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    throw new CollectorAttentionException($"Microsoft denied access. Verify {module.Permission} application consent, certificate/tenant policy and required tenant licensing (Entra P1/P2 for sign-ins).",true);
                if(!response.IsSuccessStatusCode)throw new CollectorAttentionException($"Microsoft returned HTTP {(int)response.StatusCode}. Check tenant licensing and audit availability; Unified Audit may require enabling auditing in Microsoft Purview.");
                if(response.Content.Headers.ContentLength>64L*1024*1024)throw new InvalidDataException("Microsoft response exceeds the 64 MB safety limit.");
                await using var stream=await response.Content.ReadAsStreamAsync(ct);using var buffer=new MemoryStream();var bytes=new byte[81920];int read;
                while((read=await stream.ReadAsync(bytes,ct))>0){if(buffer.Length+read>64L*1024*1024)throw new InvalidDataException("Microsoft response exceeds the 64 MB safety limit.");buffer.Write(bytes,0,read);}
                buffer.Position=0;using var json=await JsonDocument.ParseAsync(buffer,cancellationToken:ct);
                var next=response.Headers.TryGetValues("NextPageUri",out var headers)?headers.Single():null;
                if(json.RootElement.ValueKind==JsonValueKind.Object&&json.RootElement.TryGetProperty("@odata.nextLink",out var link))next=link.GetString();
                if(next!=null)ValidateUrl(customer,module.Resource,next);
                return new(json.RootElement.Clone(),next);
            }
            catch(HttpRequestException) when(attempt<4){await wait(TimeSpan.FromSeconds(Math.Pow(2,attempt)),ct);}
            catch(TaskCanceledException) when(!ct.IsCancellationRequested&&attempt<4){await wait(TimeSpan.FromSeconds(Math.Pow(2,attempt)),ct);}
        }
        throw new CollectorAttentionException("Microsoft authentication or networking could not complete after bounded retries.");
    }
    private static void CheckRoleWhenReadable(string token,string permission)
    {
        // Some Microsoft resources issue opaque tokens. In that case the API is the authority.
        try
        {
            var parts=token.Split('.');if(parts.Length!=3)return;var payload=parts[1].Replace('-','+').Replace('_','/');payload=payload.PadRight((payload.Length+3)/4*4,'=');using var json=JsonDocument.Parse(Convert.FromBase64String(payload));
            if(json.RootElement.TryGetProperty("roles",out var roles)&&roles.ValueKind==JsonValueKind.Array&&!roles.EnumerateArray().Any(r=>r.GetString()==permission))throw new CollectorAttentionException("Additional Microsoft application permission required: "+permission+". Use Update Tenant Permissions, grant consent, then retry.",true);
        }
        catch(FormatException){}catch(JsonException){}
    }
}
