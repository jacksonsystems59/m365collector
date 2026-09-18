using System.Net.Http.Headers;
using System.Text.Json;
using M365Collector.Contracts;
using M365Collector.Security;
using Microsoft.Identity.Client;

namespace M365Collector.Entra;

public sealed class TenantIdentityReader(HttpClient http, CertificateStore certificates) : ITenantIdentityReader
{
    public async Task<TenantIdentity> ReadAsync(Customer customer, CancellationToken cancellationToken)
    {
        if (customer.TenantId == Guid.Empty || customer.ClientId == Guid.Empty || customer.Certificate is null)
            throw new InvalidOperationException("Certificate authentication configuration is incomplete.");
        using var certificate = certificates.Open(customer.Certificate);
        var client = ConfidentialClientApplicationBuilder.Create(customer.ClientId.ToString("D"))
            .WithAuthority($"https://login.microsoftonline.com/{customer.TenantId:D}")
            .WithCertificate(certificate).Build();
        // MSAL's token cache stays in memory. No administrator or persistent token cache.
        var token = await client.AcquireTokenForClient(["https://graph.microsoft.com/.default"]).ExecuteAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/organization?$select=id,displayName,verifiedDomains");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new GraphAccessException((int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return Parse(json.RootElement, customer.TenantId);
    }
    public static TenantIdentity Parse(JsonElement json, Guid expectedTenant)
    {
        var organizations = json.GetProperty("value");
        if (organizations.GetArrayLength() != 1) throw new InvalidDataException("Expected exactly one tenant.");
        var tenant = organizations[0]; var id = tenant.GetProperty("id").GetGuid();
        if (id != expectedTenant || id == Guid.Empty) throw new InvalidDataException("Tenant identity does not match the configured tenant.");
        var domains = tenant.GetProperty("verifiedDomains").EnumerateArray().Select(domain => new VerifiedDomain(
            domain.GetProperty("name").GetString()!, domain.GetProperty("isDefault").GetBoolean(), domain.GetProperty("isInitial").GetBoolean())).ToArray();
        return new TenantIdentity(id, tenant.GetProperty("displayName").GetString() ?? "", domains, DateTimeOffset.UtcNow);
    }
}
public sealed class GraphAccessException(int status) : Exception("Microsoft Graph rejected the request.") { public int Status { get; } = status; }
public static class AuthenticationErrors
{
    public static string Code(Exception exception) => exception switch
    {
        GraphAccessException { Status: 401 } => "AuthenticationRejected",
        GraphAccessException { Status: 403 } => "PermissionDenied",
        GraphAccessException { Status: 429 } => "GraphThrottled",
        GraphAccessException => "GraphUnavailable",
        MsalException => "EntraAuthenticationFailed",
        InvalidDataException => "TenantMismatch",
        OperationCanceledException => "CancelledOrTimedOut",
        HttpRequestException => "NetworkUnavailable",
        _ => "CertificateOrConfigurationError"
    };
    public static string Explain(string code) => code switch
    {
        "PermissionDenied" => "Grant administrator consent for Organization.Read.All (Application) in the correct tenant, then retry.",
        "EntraAuthenticationFailed" or "AuthenticationRejected" => "Check tenant/client IDs, certificate expiry and public certificate upload. Allow time for Entra replication, then retry.",
        "TenantMismatch" => "Microsoft returned a different tenant or an invalid identity response. Customer activation was blocked.",
        "GraphThrottled" => "Microsoft Graph is throttling requests. Wait before retrying.",
        "NetworkUnavailable" or "GraphUnavailable" => "Check outbound HTTPS access to Microsoft login and Graph endpoints.",
        "CancelledOrTimedOut" => "The request was cancelled or timed out. Retry when ready.",
        _ => "Check the certificate reference, private-key service access and customer configuration. Error code: " + code
    };
}
