# M365Collector 0.1.0

M365Collector is an MSP-oriented Windows application for collecting Microsoft 365 tenant data through dedicated Entra application identities. This foundation release implements portable bootstrap, first-run setup, customer management and the Tenant Identity collector. It does not implement large audit, Exchange, Teams or SharePoint collectors, or automatic updates.

**Release status:** implementation and local validation candidate. Windows Service installation, machine-certificate ACLs and live Entra onboarding must pass the acceptance checklist before publishing v0.1.0. Automated tests use synthetic tenants; they do not certify production Microsoft connectivity.

The authoritative repository is `jacksonsystems59/m365collector`. The existing `SterlingMonitor/` tree is a separate application and is not part of this solution or package.

## Deploy and configure

1. Extract the complete `M365Collector-0.1.0-win-x64.zip` to a local folder. Keep the `service/` directory and `release.json` beside the GUI. The package is self-contained; no .NET SDK/runtime installation is required on the destination.
2. Launch `M365Collector.exe` as an authorised Windows administrator. The version is visible in the title, sidebar, wizard and Settings.
3. Follow Welcome → System Requirements → Dependency Check → Data Storage → Runtime Structure → Windows Service → Application Security → Setup Verification.
4. Choose an empty dedicated local NTFS folder, e.g. `D:\M365CollectorData`. The wizard checks free space and writes a temporary probe. UNC shares, junctions, drive roots and folders overlapping binaries are rejected. At least 512 MB is required initially; plan capacity for accumulated data and logs.
5. Setup creates protected folders and SQLite, copies validated package files to `%ProgramFiles%\M365Collector\0.1.0`, and installs `M365CollectorService` with automatic startup. It runs as LocalService with its own service SID and restart recovery.
6. The wizard verifies a fresh service heartbeat and matching version, records the initial Windows administrator SID, and opens Add Customer.

Only the installation locator (DataRoot, InstallRoot, version and schema) is stored in HKLM `SOFTWARE\SterlingTech\M365Collector`. Configuration, database, logs, reports and collected data remain under DataRoot. Relaunching a copied executable locates the registered runtime. Incomplete setup can be resumed; missing/corrupt registered state fails closed instead of creating a replacement. No runtime files are written beside executables.

Windows Server 2019+ or Windows 11 x64 and outbound HTTPS to Microsoft login/Graph are required. PowerShell is detected and its version displayed, but no PowerShell module is required or installed by v0.1.0. Package dependencies are bundled. Proxies requiring interactive authentication and national cloud endpoints are not supported in this release.

## Customers and Entra onboarding

The Add Customer page contains both workflows and all steps, required permissions, certificate export, links, copy controls and Test Connection. Each customer uses a stable GUID tenant folder and a separate application/certificate. Saving as Draft is allowed before authentication is complete. Editing a customer invalidates activation until it is tested again.

**Guided (preferred):** supply an organisation-approved setup public-client app ID. Register that setup client in Entra with the Mobile and desktop platform, `http://localhost` redirect URI and delegated Graph `Application.ReadWrite.All`, with administrator consent. It must support the target tenant. M365Collector does not ship or borrow a Microsoft-owned setup client identity. Review the temporary delegated permission before opening system-browser sign-in; MFA and Conditional Access apply normally. Guided setup creates a dedicated single-tenant runtime application, uploads its public certificate, configures the displayed application permission and creates the service principal. A versioned checkpoint allows resuming partial setup. Review and grant runtime admin consent in Entra, then return to Test Connection. The in-memory delegated cache is discarded after every attempt and the UI shows Administrator Setup Session Ended. Browser cookies remain under the browser/Microsoft account's control.

**Manual:** create a single-tenant `SterlingTech M365Collector` registration in the correct customer tenant, record tenant/client IDs, create or select a machine certificate in the GUI, export its public `.cer`, upload it to Certificates & secrets, and add Graph **Application** `Organization.Read.All`. Remove unrelated default permissions, have an authorised admin review and grant consent, and test the connection. In-app instructions explain each step. If tenant policy forbids self-signed certificates, use an organisation-issued non-exportable RSA CNG machine certificate and Prepare key access.

Test Connection saves a Draft and queues a job in SQLite. **The Windows Service performs app-only authentication and `/organization` retrieval**, proving it can access the private key while no administrator is signed in. The returned tenant ID must match. Only successful validation activates a customer; discovered default domain and identity are saved. Continue to Collection Modules to enable/disable Tenant Identity per customer or queue collection. Collection runs hourly by default and independently of the GUI.

Guided setup configures permissions but does not silently grant consent. Permission requests derive from the implemented module manifest. Creating the app requires temporary delegated `Application.ReadWrite.All`; the runtime only requests `Organization.Read.All`. If a network timeout happens after Entra accepts a create request but before a checkpoint is saved, inspect Entra for an orphan registration before retrying. Automatic deletion of remote applications is intentionally not attempted.

## Architecture and storage

| Project | Responsibility |
| --- | --- |
| Contracts | Version, configuration, customer, health, module and authorisation contracts |
| Core | Path validation, setup detection, atomic JSON, registry locator, structured logging and package validation |
| Storage | SQLite repositories, transactional versioned migrations, customer isolation and service job queue |
| Security | Windows administrator authentication, role policy, LocalMachine certificate and private-key ACL management |
| Entra | MSAL app-only/system-browser setup authentication and Microsoft Graph REST access |
| Modules.TenantIdentity | Dependency/permission manifest and small functional collector |
| Service | Independent scheduler, queued validation/collection, health and structured logs; maintenance CLI |
| GUI | WinForms wizard, customer onboarding, module selection and administration/navigation |

DataRoot contains `Config`, `Logs`, `Database`, `Cache`, `Service`, `Reports`, `Exports`, `Temp` and `Customers/<TenantId>/{Data,Audit,Reports,Exports,Cache}`. SQLite indexes customer/configuration metadata, roles, enabled modules, jobs, health and current tenant identity. Timestamped versioned identity JSON snapshots remain in each customer's Data folder. Logs use daily JSONL files; retention and archival are operator-managed in v0.1.0. Audit Explorer and Reports are explicit future placeholders, kept separate for eventual large raw audit storage and query indexes. Export metadata contracts are described in [architecture](docs/ARCHITECTURE.md).

## Security model

The v0.1.0 application administrator mechanism is **elevated Windows Administrators membership**. Windows owns authentication; M365Collector stores no passwords. Runtime folders are limited to Administrators, SYSTEM and the service SID. Operator/Read Only role contracts are reserved for future providers; no UI access is granted to those roles yet. This is a trusted-administrator, single-host deployment; it does not isolate one local administrator from another.

Certificates live in **LocalMachine/My** with non-exportable RSA CNG keys. The OS manages private-key storage outside DataRoot; configuration contains only safe certificate references. Public certificate exports remain under DataRoot. Key access is restricted to Administrators, SYSTEM and `NT SERVICE\M365CollectorService`. No administrator passwords, private key material, access tokens, refresh tokens or raw authentication response bodies are persisted or logged. Logs use fixed event/error codes. Protect the host and DataRoot backups with organisation-approved disk/backup encryption; SQLite itself is not encrypted.

Generated certificates expire after one year. Rotate by creating a replacement, uploading the new public certificate, saving its new reference, validating through the service and only then removing the old Entra credential/certificate. Each tenant must have its own app and certificate. Entra-issued broader permissions on a manually configured app are not automatically revoked; review them in Entra.

## Version and future upgrades

`service\M365Collector.Service.exe --version` returns JSON without reading runtime/customer data. Assembly/file metadata, HKLM, `release.json`, GUI and service health expose version **0.1.0**. Configuration, module manifests, health, snapshots and setup checkpoints carry schema versions. SQLite uses `PRAGMA user_version` and an ordered `schema_migrations` ledger, applies migrations transactionally and rejects newer schemas.

No GitHub release polling, downloading, installation or automatic self-update is implemented. See [upgrade contract](docs/UPGRADES.md) for the safe stop/backup/replace/migrate/start/verify/rollback sequence and maintenance commands. Binaries use version-specific directories; backups stay under DataRoot. Release checksums detect corruption, not publisher authenticity; code signing and trusted distribution remain release-operations responsibilities.

## Build, test and package

Use Windows x64 with the .NET 10 SDK and NuGet access:

```powershell
dotnet restore M365Collector.sln --locked-mode
dotnet build M365Collector.sln -c Release --no-restore
dotnet test M365Collector.sln -c Release
pwsh -File scripts/Build-Release.ps1
```

The package script cleans, builds, tests and publishes two self-contained win-x64 applications into `dist/M365Collector-0.1.0-win-x64/`, creates a per-file SHA-256 manifest and generates the predictable ZIP plus `.sha256`. Keep all source and lockfiles in this repository. Generated binaries and test results are ignored by Git. Run the GUI from a full published package for first-run installation; a plain GUI `bin/` build does not include the service package.

## Release gate

Complete [Windows and Entra acceptance](docs/ACCEPTANCE.md) before release. Check that exact source is committed and pushed to **jacksonsystems59/m365collector**. With uncommitted/unpushed source, stop at **SOURCE READY FOR COMMIT/PUSH** and report build/test results, changed source, package files and limitations. Do not publish before confirmation. After matching source is pushed, verify `gh auth status`, repository identity and absence of `v0.1.0`. Publish tag `v0.1.0`, title `M365Collector v0.1.0`, as a normal release with ZIP/checksum assets. Never overwrite an existing release. This repository does not contain an automatic publish action.

Microsoft references: [Graph organization permissions](https://learn.microsoft.com/en-us/graph/api/organization-list?view=graph-rest-1.0), [application provisioning](https://learn.microsoft.com/en-us/graph/tutorial-applications-basics), [MSAL system browser](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/using-web-browsers).
