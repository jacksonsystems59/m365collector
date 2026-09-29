# M365Collector v0.0.1

Clean rebuild of the platform foundation, with Tenant Identity as its only Microsoft 365 data collector.

- Portable self-contained win-x64 package and professional First Run Wizard with dependency checks, configurable DataRoot, protected runtime folders and setup verification.
- Independent automatic Windows Service, SQLite storage and transactional versioned migrations.
- Secure local application access with Administrator, Operator and Read Only roles, salted password hashing, lockout and idle locking.
- Simplified customer onboarding and Microsoft-controlled authentication through WAM or a system browser. Automatic Graph provisioning uses an organisation-configured bootstrap public client; guided/manual paths are included.
- Dedicated customer app identities, non-exportable machine certificates, public CER export and certificate-based app-only runtime authentication. Customer connection is accepted only after the service verifies Tenant Identity.
- Guided `Connect-MgGraph` setup with process-scoped authentication and delegated-session cleanup.
- Modular collector manifests, hourly Tenant Identity collection and clear placeholders for future collectors and Audit Explorer.
- Public GitHub Releases update checker with stable semantic versions, release notes and conditional caching; no deployed GitHub CLI, PAT or account required.
- Explicitly approved updates through a separate helper, SHA-256 and manifest verification, safe extraction, service-aware replacement, database/config backups, health checks, rollback and interrupted-update recovery.

## Installation notes

Extract the complete ZIP and run `GUI\M365Collector.GUI.exe` elevated. Applications install under Program Files; runtime data stays in the chosen separate DataRoot. The checksum asset contains the ZIP's SHA-256.

Automatic Microsoft sign-in needs your configured public-client ID; no universal client identity is bundled. Use Advanced / Manual Setup or Guided PowerShell Setup until configured. See the onboarding documentation.

This is not a migration of abandoned prototype databases. An existing service with a different binary path must be retired deliberately before fresh setup; old data is not deleted. Windows administrators remain trusted and the GUI requires elevation in this foundation release.

Validation: clean Release build with zero warnings and zero errors; all 64 automated tests passed with no skips. Automated tests use fakes for Microsoft, GitHub and service failures. Live tenant consent, WAM/Conditional Access and fresh-machine Windows Service acceptance are not represented as completed live tests. Validate those in an authorised test environment before production rollout.
