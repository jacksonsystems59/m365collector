# M365Collector 0.0.1

Clean foundation rebuild of the SterlingTech Microsoft 365 collection platform for MSP technicians. This source replaces the abandoned prototype. Git history and repository identity are retained.

## Start from the portable release

1. Download `M365Collector-0.0.1-win-x64.zip` and its `.sha256` companion from [GitHub Releases](https://github.com/jacksonsystems59/m365collector/releases).
2. Verify the ZIP using `Get-FileHash -Algorithm SHA256`, then extract the complete package to a local folder.
3. Run `GUI\M365Collector.GUI.exe`. Windows requests administrator elevation. No separately installed .NET runtime, GitHub CLI, PAT or GitHub account is required.
4. Follow the First Run Wizard: requirements, connectivity, dedicated DataRoot, runtime folders, service installation, local Administrator, verification, first customer.
5. After setup, use `C:\Program Files\M365Collector\GUI\M365Collector.GUI.exe`. The extracted download can be archived. Updates replace the installed copy; do not keep launching an old extracted GUI.

Requirements: Windows 10 1809 / Windows Server 2019 or later on x64, PowerShell 5.1+ for optional guided onboarding, administrator elevation and HTTPS access to Microsoft login, Microsoft Graph and public GitHub Releases. Use an OS edition still receiving security support. The package includes .NET 10. Runtime storage needs at least 512 MB; release staging and backups need additional space.

## Existing prototype installations

This is a clean rebuild, **not a migration of 0.1.0 prototype databases**. The wizard will not take over a service with a different executable path or repurpose a nonempty data folder. Back up the old installation/data first. When ready to retire that prototype, use an elevated terminal:

```powershell
sc.exe stop M365CollectorService
sc.exe delete M365CollectorService
```

Close Services consoles and wait for service deletion to finish. Keep the old runtime data archived and select a new empty DataRoot for 0.0.1. These commands remove service registration, not your files. Old application folders are not automatically removed. A running older prototype on the development machine was not changed by the rebuild.

## What works

- Portable, self-contained wizard; configurable protected DataRoot; SQLite transactional versioned migrations.
- Independent automatic Windows Service with database checks and fresh heartbeat verification.
- Local Administrator, Operator and Read Only accounts; salted PBKDF2-SHA256 password hashes, lockout and idle screen locking.
- Microsoft-controlled WAM sign-in, explicit system-browser alternative, automatic Graph provisioning with a configured bootstrap public client, guided `Connect-MgGraph` and manual Entra setup.
- Dedicated app registration and non-exportable machine certificate per tenant. Public CER export uses a Windows Save dialog.
- Service-side certificate app-only validation before saving a customer. Tenant Identity collection records tenant ID, display name, initial/default and verified domains, and collection time.
- Hourly service collection, per-customer module enablement and manual collection requests.
- Public GitHub update checks with semantic versions, six-hour cache/ETag, technician-approved downloads, SHA-256 and package-manifest validation, separate updater, backups, health checks, rollback and interrupted-update recovery.

Tenant Identity is the only Microsoft data collector. Sign-in, Exchange, SharePoint/OneDrive, Teams and directory-change collectors are marked Coming later. Audit Explorer and Reports are clearly marked future functionality.

## Onboarding prerequisite

Automatic sign-in needs **your organisation's configured Entra public-client application ID** in Administration. No universal application ID or client secret is bundled. Until configured, use Advanced / Manual Setup or Guided PowerShell Setup. See [Entra onboarding](docs/ENTRA-ONBOARDING.md) for registration settings, consent capabilities and cleanup behavior. Microsoft passwords, MFA, passkeys and Conditional Access stay inside Microsoft authentication.

## Build and verify

On Windows with the .NET 10 SDK and PowerShell 7:

```powershell
./scripts/Build-Release.ps1
```

This cleans, restores locked dependencies, builds Release with warnings treated as errors, runs all tests, publishes GUI/Service/Updater as self-contained win-x64 components and writes:

- `dist\M365Collector-0.0.1-win-x64.zip`
- `dist\M365Collector-0.0.1-win-x64.zip.sha256`

Generated binaries, packages, runtime databases, certificates and test output are excluded from Git. `gh` is used only by maintainers to publish releases, never by deployed installations.

Automated tests use isolated local databases and fake Microsoft/GitHub/service operations. They do not require tenant credentials or modify the existing Windows Service. Actual WAM, customer-specific Conditional Access/consent, machine-key service access and end-to-end SCM installation should also be validated in a disposable Windows test environment before production rollout; automated tests are not evidence of a live tenant connection.

See [architecture](docs/ARCHITECTURE.md), [security](docs/SECURITY.md), [updates](docs/UPDATES.md) and [changelog](CHANGELOG.md).
