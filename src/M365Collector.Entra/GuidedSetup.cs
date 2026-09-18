using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Security;
using Microsoft.Identity.Client;

namespace M365Collector.Entra;

public sealed record GuidedCheckpoint(int SchemaVersion, Guid TenantId, Guid ClientId, string ApplicationObjectId, string Thumbprint, bool ServicePrincipalCreated);
public sealed class GuidedSetup(HttpClient http, CertificateStore certificates)
{
    // Customer-controlled public client required. We never impersonate a Microsoft-owned client ID.
    public async Task<GuidedCheckpoint> ConfigureAsync(Guid setupClientId, Customer customer,
        IReadOnlyList<PermissionRequirement> permissions, string checkpointPath, IProgress<string> progress, CancellationToken ct)
    {
        WindowsSecurity.RequireAdministrator();
        if (setupClientId == Guid.Empty || customer.TenantId == Guid.Empty || customer.Certificate == null || permissions.Count == 0)
            throw new ArgumentException("Setup client, tenant, certificate and enabled module permissions are required.");
        var client = PublicClientApplicationBuilder.Create(setupClientId.ToString("D"))
            .WithAuthority($"https://login.microsoftonline.com/{customer.TenantId:D}")
            .WithRedirectUri("http://localhost").Build();
        try
        {
            progress.Report("Sign in directly in your browser. M365Collector never receives your password.");
            var auth = await client.AcquireTokenInteractive(["https://graph.microsoft.com/Application.ReadWrite.All"])
                .WithUseEmbeddedWebView(false).WithPrompt(Prompt.SelectAccount).ExecuteAsync(ct);
            if (!Guid.TryParse(auth.TenantId, out var returnedTenant) || returnedTenant != customer.TenantId)
                throw new InvalidDataException("Setup sign-in tenant does not match the customer.");
            GuidedCheckpoint checkpoint;
            if (File.Exists(checkpointPath))
            {
                checkpoint = JsonFiles.Read<GuidedCheckpoint>(checkpointPath);
                if (checkpoint.SchemaVersion != 1 || checkpoint.TenantId != customer.TenantId || checkpoint.Thumbprint != customer.Certificate.Thumbprint)
                    throw new InvalidDataException("An existing setup checkpoint uses a different tenant or certificate. Review the existing Entra app before retrying.");
                progress.Report("Resuming the application recorded by the previous guided setup.");
            }
            else
            {
                progress.Report("Creating the dedicated application with the reviewed permission requirements.");
                using var certificate = certificates.Open(customer.Certificate);
                using var app = await Post("applications", new
                {
                    displayName = "SterlingTech M365Collector - " + customer.TenantId.ToString("D"), signInAudience = "AzureADMyOrg",
                    requiredResourceAccess = new[] { new { resourceAppId = "00000003-0000-0000-c000-000000000000", resourceAccess = permissions.Select(p => new { id = p.Id, type = "Role" }).ToArray() } },
                    keyCredentials = new[] { new { type = "AsymmetricX509Cert", usage = "Verify", key = Convert.ToBase64String(certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert)), displayName = "M365Collector tenant certificate", startDateTime = certificate.NotBefore.ToUniversalTime(), endDateTime = certificate.NotAfter.ToUniversalTime() } }
                }, auth.AccessToken, ct);
                checkpoint = new(1, customer.TenantId, app.RootElement.GetProperty("appId").GetGuid(), app.RootElement.GetProperty("id").GetString()!, customer.Certificate.Thumbprint, false);
                JsonFiles.WriteAtomic(checkpointPath, checkpoint);
            }
            if (!checkpoint.ServicePrincipalCreated)
            {
                using var check = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId%20eq%20'{checkpoint.ClientId:D}'&$select=id");
                check.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
                using var checkResponse = await http.SendAsync(check, ct);
                if (!checkResponse.IsSuccessStatusCode) throw new GraphAccessException((int)checkResponse.StatusCode);
                using var existing = JsonDocument.Parse(await checkResponse.Content.ReadAsStringAsync(ct));
                if (existing.RootElement.GetProperty("value").GetArrayLength() == 0)
                { using var principal = await Post("servicePrincipals", new { appId = checkpoint.ClientId }, auth.AccessToken, ct); }
                checkpoint = checkpoint with { ServicePrincipalCreated = true }; JsonFiles.WriteAtomic(checkpointPath, checkpoint);
            }
            progress.Report("Application configured. Review and grant consent in Entra, then Test Connection through the service.");
            return checkpoint;
        }
        finally
        {
            foreach (var account in await client.GetAccountsAsync()) await client.RemoveAsync(account);
            progress.Report("Administrator Setup Session Ended — in-memory setup tokens discarded. Browser sign-in cookies are managed by Microsoft.");
        }
    }
    private async Task<JsonDocument> Post(string route, object payload, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/" + route) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new GraphAccessException((int)response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
