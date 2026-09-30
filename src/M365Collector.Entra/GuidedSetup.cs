namespace M365Collector.Entra;
public static class GuidedSetup
{
    public const string Script = """
        # M365Collector 0.0.2 — review before running in elevated PowerShell.
        # Optional dependency: Install-Module Microsoft.Graph -Scope CurrentUser
        # No administrator password is requested or handled by this script.
        $ErrorActionPreference = 'Stop'
        Import-Module Microsoft.Graph.Authentication
        $tenant = [guid](Read-Host 'Customer tenant ID (from Entra overview)')
        $thumbprint = Read-Host 'Thumbprint of certificate created by M365Collector in LocalMachine\My'
        if ($thumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Invalid thumbprint' }
        $cert = Get-Item -LiteralPath "Cert:\LocalMachine\My\$thumbprint"
        if (-not $cert.HasPrivateKey) { throw 'Certificate has no private key' }
        $app = $null
        try {
            Write-Host 'Sign in using Microsoft-controlled UI. Activate application-management and Graph consent capabilities as needed.'
            Connect-MgGraph -TenantId $tenant -Scopes 'Application.ReadWrite.All','AppRoleAssignment.ReadWrite.All','Organization.Read.All' -ContextScope Process -NoWelcome
            $graph = (Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId eq '00000003-0000-0000-c000-000000000000'").value[0]
            $role = $graph.appRoles | Where-Object { $_.value -eq 'Organization.Read.All' -and $_.isEnabled -and $_.allowedMemberTypes -contains 'Application' }
            if (-not $role) { throw 'Graph permission not found' }
            Write-Host 'Creating dedicated application, public certificate and required permission manifest.'
            $body = @{
                displayName = 'SterlingTech M365Collector'; signInAudience = 'AzureADMyOrg'
                keyCredentials = @(@{ type='AsymmetricX509Cert'; usage='Verify'; key=[Convert]::ToBase64String($cert.RawData) })
                requiredResourceAccess = @(@{ resourceAppId='00000003-0000-0000-c000-000000000000'; resourceAccess=@(@{ id=$role.id; type='Role' }) })
            } | ConvertTo-Json -Depth 10
            $app = Invoke-MgGraphRequest -Method POST -Uri 'https://graph.microsoft.com/v1.0/applications' -Body $body -ContentType 'application/json'
            $sp = Invoke-MgGraphRequest -Method POST -Uri 'https://graph.microsoft.com/v1.0/servicePrincipals' -Body (@{appId=$app.appId}|ConvertTo-Json) -ContentType 'application/json'
            Write-Host 'Granting Graph application consent; this operation may require Privileged Role Administrator.'
            $grant = @{principalId=$sp.id; resourceId=$graph.id; appRoleId=$role.id}|ConvertTo-Json
            $null = Invoke-MgGraphRequest -Method POST -Uri "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignments" -Body $grant -ContentType 'application/json'
            Disconnect-MgGraph | Out-Null
            Write-Host 'Administrator session ended. Verifying certificate app-only access (consent propagation can take several minutes).'
            Connect-MgGraph -TenantId $tenant -ClientId $app.appId -Certificate $cert -ContextScope Process -NoWelcome
            $org = (Invoke-MgGraphRequest -Method GET -Uri 'https://graph.microsoft.com/v1.0/organization?$select=id,displayName,verifiedDomains').value[0]
            if ([guid]$org.id -ne $tenant) { throw 'Tenant mismatch' }
            Write-Host "Tenant: $($org.displayName)"
        } finally {
            Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null
            if ($app) {
                Write-Host "Tenant ID: $tenant"
                Write-Host "Client ID: $($app.appId)"
                Write-Host "Certificate: $thumbprint"
                Write-Host 'Paste these values into M365Collector and select Test Connection & Save. The service must verify access under its own identity.'
                Write-Host 'If provisioning failed, inspect this dedicated application in Entra; remove it before starting a new provisioning attempt.'
            }
        }
        """;
}
