# Secure Entra onboarding

## Automatic path

Configure a bootstrap **public-client** application belonging to your organisation. For MSP use across customer directories, register it with Accounts in any organisational directory. Enable public-client flows. Configure these redirects for the modes you intend to use:

- WAM: `ms-appx-web://microsoft.aad.brokerplugin/<bootstrap-client-id>`
- System browser: `http://localhost` under Mobile and desktop applications.

Configure Microsoft Graph delegated permissions `Application.ReadWrite.All`, `AppRoleAssignment.ReadWrite.All` and `Organization.Read.All`. These are powerful bootstrap permissions; protect and govern this application accordingly. Customer tenant consent/policy must permit its use. Enter its client ID under Administration. No client secret, password or persisted delegated token cache is used.

Add Customer initially requests only a customer name and optional known domain/tenant hint. Connect opens a progress window and Microsoft-controlled WAM authentication. The system-browser button provides an explicit alternative if WAM is unavailable or unsuitable. MFA, FIDO/passkeys and Conditional Access are enforced by Microsoft; failures are surfaced without attempting to bypass them.

After sign-in the workflow identifies the token tenant, creates a unique non-exportable RSA-3072 certificate in LocalMachine/My, reads Graph's permission manifest, creates a dedicated single-tenant `SterlingTech M365Collector` app, registers its public certificate, creates the service principal and grants the application `Organization.Read.All` role. The permission ID is discovered by its exact name and allowed member type instead of being guessed or shared with unrelated modules.

Delegated scopes are checked before provisioning. Graph operations provide the authoritative capability check, including tenant policy and active PIM status. Application registration management and Graph application consent are separate privileges. A user who can create/manage applications may be unable to grant Microsoft Graph application permissions. Errors identify the failed operation and explain the missing category of capability; the UI does not blanket-require Global Administrator. Privileged Role Administrator is a documented least-privileged built-in role for granting Microsoft Graph app roles; equivalent custom capabilities and tenant restrictions may affect the outcome.

The dedicated application is removed on partial automatic provisioning failure where Graph permits cleanup. If cleanup fails, its client ID and tenant are shown for explicit Entra cleanup. Locally created certificates remain available for inspection; private keys are not exported or automatically deleted. On successful provisioning, the in-memory bootstrap accounts are removed in `DisposeAsync` on every exit path. WAM's operating-system account itself remains under Windows control. No administrator session is retained for service collection.

Customer IDs/certificate reference remain on the manual panel if later app-only verification fails, so retry **Test Connection & Save** after consent propagation instead of creating duplicate applications. The service must open/sign with the private key, acquire an app-only token for the requested tenant, read `/organization`, and confirm the returned tenant ID before the customer is saved.

## Manual and guided paths

Advanced / Manual Setup has Entra and copy buttons, Tenant/Client ID inputs, certificate creation, a public-CER Save dialog and the exact permission and reason. The public export is DER X.509 (`.cer`); no PFX/private-key export exists. Private-key ACLs grant the service read access and administrators/SYSTEM management access.

Guided PowerShell Setup saves a reviewable script through a Save dialog. Run it explicitly in an elevated PowerShell window. Install the optional Microsoft.Graph module yourself if needed; the product does not install every Graph/PowerShell module. The script uses `Connect-MgGraph -ContextScope Process`, creates an app/SP/permission assignment with the certificate created by the GUI, disconnects the delegated session, verifies app-only access and always calls `Disconnect-MgGraph` in `finally`. It prints IDs to paste into the manual panel. The final GUI verification still runs under the Windows Service identity. It never uses Exchange Online as generic onboarding or places administrator passwords in arguments.

Manual external certificates must be RSA, valid, in LocalMachine/My and readable by the service SID. The GUI-created certificate is recommended because it sets the intended ACL and non-exportable policy. Consent propagation and tenant network policy can delay verification; failure is not treated as success.

## Microsoft references

- [MSAL.NET with WAM](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam)
- [Grant app roles and supported administrator roles](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-approleassignedto?view=graph-rest-1.0)
- [Programmatic permission grants](https://learn.microsoft.com/en-us/graph/permissions-grant-via-msgraph)

Live tenant consent and Conditional Access behavior require testing against an authorised test tenant. Automated tests use fake bootstrap sessions and verify unconditional cleanup; they do not claim live Microsoft onboarding acceptance.

## Existing customers: 0.0.2 collector permissions

Do not recreate the customer app. From Collection Modules choose Update Tenant Permissions, review the displayed tenant/application/resource/permission and consent using the configured bootstrap client. Sign-ins and Directory Changes require Graph AuditLog.Read.All; Unified Audit requires Office 365 Management APIs ActivityFeed.Read. Both are application permissions with tenant admin consent. The existing certificate and other permissions remain. Manual Entra consent is also supported. See [collector prerequisites](COLLECTOR-MODULES.md#consent-and-prerequisites).
