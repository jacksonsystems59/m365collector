using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using M365Collector.Contracts;
using M365Collector.Security;

namespace M365Collector.Entra;

public sealed class AppOnlyIdentityReader(HttpClient http) : IAppOnlyIdentityReader
{
    public async Task<TenantIdentity> ReadAsync(Customer customer, CancellationToken cancellationToken)
    {
        using var certificate = Certificates.Find(customer.Thumbprint);
        var app = ConfidentialClientApplicationBuilder.Create(customer.ClientId.ToString()).WithAuthority("https://login.microsoftonline.com/" + customer.TenantId).WithCertificate(certificate).Build();
        var token = await app.AcquireTokenForClient(["https://graph.microsoft.com/.default"]).ExecuteAsync(cancellationToken);
        if (!Guid.TryParse(token.TenantId, out var tokenTenant) || tokenTenant != customer.TenantId) throw new InvalidDataException("App-only token tenant mismatch.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/organization?$select=id,displayName,verifiedDomains");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Tenant Identity returned HTTP {(int)response.StatusCode}. Verify Organization.Read.All application consent and tenant policy.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseIdentity(json.RootElement, customer.TenantId);
    }
    public static TenantIdentity ParseIdentity(JsonElement root, Guid expected)
    {
        var values = root.GetProperty("value"); if (values.GetArrayLength() != 1) throw new InvalidDataException("Expected one tenant organization.");
        var org = values[0]; var id = org.GetProperty("id").GetGuid(); if (id != expected) throw new InvalidDataException("Tenant identity mismatch.");
        var domains = org.GetProperty("verifiedDomains").EnumerateArray().Select(d => new VerifiedDomain(d.GetProperty("name").GetString()!, d.GetProperty("isDefault").GetBoolean(), d.GetProperty("isInitial").GetBoolean())).ToArray();
        return new(id, org.GetProperty("displayName").GetString() ?? "", domains, DateTimeOffset.UtcNow);
    }
}

public sealed class MicrosoftBootstrap : IBootstrapSession
{
    private IPublicClientApplication? app;
    private AuthenticationResult? result;
    private readonly HttpClient http;
    public const string GraphAppId = "00000003-0000-0000-c000-000000000000";
    public static readonly string[] Scopes = ["https://graph.microsoft.com/Application.ReadWrite.All", "https://graph.microsoft.com/AppRoleAssignment.ReadWrite.All", "https://graph.microsoft.com/Organization.Read.All"];
    private MicrosoftBootstrap(HttpClient http) { this.http = http; }
    public static async Task<MicrosoftBootstrap> SignInAsync(Guid bootstrapClientId, string hint, IntPtr window, bool systemBrowser, HttpClient http, CancellationToken ct)
    {
        var session = new MicrosoftBootstrap(http);
        var builder = PublicClientApplicationBuilder.Create(bootstrapClientId.ToString()).WithAuthority("https://login.microsoftonline.com/organizations");
        if (systemBrowser) builder = builder.WithRedirectUri("http://localhost");
        else builder = builder.WithRedirectUri("ms-appx-web://microsoft.aad.brokerplugin/" + bootstrapClientId).WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows));
        session.app = builder.Build();
        try
        {
            var request = session.app.AcquireTokenInteractive(Scopes).WithParentActivityOrWindow(window).WithPrompt(Prompt.SelectAccount);
            if (!string.IsNullOrWhiteSpace(hint)) request = request.WithExtraQueryParameters(new Dictionary<string,(string value, bool includeInCacheKey)> { ["domain_hint"] = (hint.Trim(), false) });
            if (systemBrowser) request = request.WithUseEmbeddedWebView(false);
            session.result = await request.ExecuteAsync(ct); return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }
    public Task<Guid> IdentifyTenantAsync(CancellationToken ct) => Task.FromResult(Guid.Parse(result?.TenantId ?? throw new InvalidOperationException("Microsoft sign-in is required.")));
    private async Task<JsonDocument> Send(HttpMethod method, string resource, object? body, string operation, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, "https://graph.microsoft.com/v1.0/" + resource);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result!.AccessToken);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var reason = operation == "Grant application consent" ? "Microsoft Graph application consent requires an eligible consent role, such as Privileged Role Administrator, and the delegated AppRoleAssignment.ReadWrite.All grant. Activate eligible PIM roles if needed." : "Application registration operations require the corresponding delegated grants and tenant application-management capability. Application Developer, Application Administrator or Cloud Application Administrator may be appropriate depending on tenant policy.";
            throw new InvalidOperationException($"{operation} failed (HTTP {(int)response.StatusCode}). {reason}");
        }
        var text = await response.Content.ReadAsStringAsync(ct); return JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
    }
    public async Task<Guid> ProvisionAsync(Guid tenant, byte[] publicCertificate, IProgress<string> progress, CancellationToken ct)
    {
        if (tenant != await IdentifyTenantAsync(ct)) throw new InvalidOperationException("Tenant changed during onboarding.");
        progress.Report("Checking delegated grants and tenant application-management capability…");
        var granted = result!.Scopes.Select(s => s.Split('/').Last()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Scopes.Any(s => !granted.Contains(s.Split('/').Last()))) throw new UnauthorizedAccessException("Microsoft did not grant all bootstrap scopes. Use guided or manual setup with an appropriately authorised administrator.");
        using var org = await Send(HttpMethod.Get, "organization?$select=id,displayName", null, "Read tenant", ct);
        progress.Report("Tenant: " + org.RootElement.GetProperty("value")[0].GetProperty("displayName").GetString());
        using var graph = await Send(HttpMethod.Get, $"servicePrincipals?$filter=appId eq '{GraphAppId}'&$select=id,appRoles", null, "Read Graph service principal", ct);
        var resource = graph.RootElement.GetProperty("value")[0];
        var permissionId = resource.GetProperty("appRoles").EnumerateArray().Single(r => r.GetProperty("value").GetString() == "Organization.Read.All" && r.GetProperty("isEnabled").GetBoolean() && r.GetProperty("allowedMemberTypes").EnumerateArray().Any(t => t.GetString() == "Application")).GetProperty("id").GetString();
        progress.Report("Creating dedicated collector application and registering public certificate…");
        using var created = await Send(HttpMethod.Post, "applications", new { displayName = "SterlingTech M365Collector", signInAudience = "AzureADMyOrg", keyCredentials = new[] { new { type = "AsymmetricX509Cert", usage = "Verify", key = Convert.ToBase64String(publicCertificate), displayName = "M365Collector certificate" } }, requiredResourceAccess = new[] { new { resourceAppId = GraphAppId, resourceAccess = new[] { new { id = permissionId, type = "Role" } } } } }, "Create application registration", ct);
        var appId = created.RootElement.GetProperty("appId").GetGuid();
        var objectId = created.RootElement.GetProperty("id").GetString();
        try
        {
            progress.Report("Creating service principal…");
            using var sp = await Send(HttpMethod.Post, "servicePrincipals", new { appId }, "Create service principal", ct);
            var spId = sp.RootElement.GetProperty("id").GetString();
            progress.Report("Granting Organization.Read.All application consent…");
            using var consent = await Send(HttpMethod.Post, "servicePrincipals/" + spId + "/appRoleAssignments", new { principalId = spId, resourceId = resource.GetProperty("id").GetString(), appRoleId = permissionId }, "Grant application consent", ct);
            return appId;
        }
        catch (Exception error)
        {
            try { using var cleanup = await Send(HttpMethod.Delete, "applications/" + objectId, null, "Remove incomplete application", CancellationToken.None); }
            catch { throw new InvalidOperationException($"{error.Message} Incomplete application {appId} remains in tenant {tenant}; remove it in Entra before retrying."); }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        result = null;
        if (app != null) { try { foreach (var account in await app.GetAccountsAsync()) await app.RemoveAsync(account); } finally { app = null; } }
    }
    public async Task GrantCollectorPermissionAsync(Customer customer,CollectorDefinition module,IProgress<string> progress,CancellationToken ct)
    {
        if(await IdentifyTenantAsync(ct)!=customer.TenantId)throw new InvalidOperationException("The Microsoft administrator signed into a different customer tenant. No permissions were changed.");
        var resourceAppId=module.Resource=="https://graph.microsoft.com"?GraphAppId:"c5393580-f805-4401-95e8-94b7a6ef2fc2";
        using var applications=await Send(HttpMethod.Get,$"applications?$filter=appId eq '{customer.ClientId}'&$select=id,requiredResourceAccess",null,"Find existing customer application",ct);
        var application=applications.RootElement.GetProperty("value").EnumerateArray().Single();
        using var principals=await Send(HttpMethod.Get,$"servicePrincipals?$filter=appId eq '{customer.ClientId}'&$select=id",null,"Find customer service principal",ct);
        var principal=principals.RootElement.GetProperty("value").EnumerateArray().Single().GetProperty("id").GetString();
        using var resourceDoc=await Send(HttpMethod.Get,$"servicePrincipals?$filter=appId eq '{resourceAppId}'&$select=id,appRoles",null,"Read required API permissions",ct);
        var resources=resourceDoc.RootElement.GetProperty("value").EnumerateArray().ToArray();
        if(resources.Length!=1)throw new InvalidOperationException("The API service principal is unavailable. Add the Office 365 Management APIs permission through Entra and grant consent, then retry.");
        var resource=resources[0];var role=resource.GetProperty("appRoles").EnumerateArray().Single(r=>r.GetProperty("value").GetString()==module.Permission&&r.GetProperty("isEnabled").GetBoolean()&&r.GetProperty("allowedMemberTypes").EnumerateArray().Any(t=>t.GetString()=="Application")).GetProperty("id").GetString();
        var access=new List<object>();var found=false;
        foreach(var entry in application.GetProperty("requiredResourceAccess").EnumerateArray())
        {
            var appId=entry.GetProperty("resourceAppId").GetString();var permissions=entry.GetProperty("resourceAccess").EnumerateArray().Select(p=>new Dictionary<string,string>{{"id",p.GetProperty("id").GetString()!},{"type",p.GetProperty("type").GetString()!}}).ToList();
            if(appId==resourceAppId){found=true;if(!permissions.Any(p=>p["id"]==role&&p["type"]=="Role"))permissions.Add(new(){{"id",role!},{"type","Role"}});}
            access.Add(new{resourceAppId=appId,resourceAccess=permissions});
        }
        if(!found)access.Add(new{resourceAppId,resourceAccess=new[]{new{id=role,type="Role"}}});
        progress.Report("Adding reviewed permission to existing app: "+module.Permission);
        using var update=await Send(HttpMethod.Patch,"applications/"+application.GetProperty("id").GetString(),new{requiredResourceAccess=access},"Update application permission manifest",ct);
        using var assignments=await Send(HttpMethod.Get,"servicePrincipals/"+principal+"/appRoleAssignments",null,"Read existing consent",ct);
        var resourceId=resource.GetProperty("id").GetString();
        if(!assignments.RootElement.GetProperty("value").EnumerateArray().Any(a=>a.GetProperty("resourceId").GetString()==resourceId&&a.GetProperty("appRoleId").GetString()==role))
        { using var grant=await Send(HttpMethod.Post,"servicePrincipals/"+principal+"/appRoleAssignments",new{principalId=principal,resourceId,appRoleId=role},"Grant application consent",ct); }
        progress.Report("Consent recorded. The service will verify app-only access; propagation may take several minutes.");
    }
}
