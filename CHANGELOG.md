# Changelog

## 0.0.1 — 2026-09-29

- Clean rebuild replacing the abandoned prototype, preserving repository identity and history.
- Self-contained First Run Wizard, dependency checks, configurable protected DataRoot and independent Windows Service.
- SQLite repositories, versioned migrations, local password authentication and Administrator/Operator/Read Only roles.
- Simplified customer onboarding with Microsoft-controlled WAM/system-browser authentication, configurable bootstrap public client, Graph provisioning, guided Connect-MgGraph and manual setup.
- Per-tenant non-exportable machine certificates, public CER export and service-side app-only verification.
- Tenant Identity collector, hourly service scheduling, modular manifests and honest placeholders for future collectors/Audit Explorer/Reports.
- Public GitHub release checks, semantic versions, conditional caching, explicit update approval, secure staging, SHA-256 and manifest checks, separate service-aware updater, rollback and interrupted-update recovery.
- Credential-free automated tests and Windows release packaging.

This release does not migrate prototype databases or implement large Microsoft 365 workload collectors. Automatic Microsoft sign-in requires an organisation-configured bootstrap application. Live tenant/SCM acceptance is separate from automated tests.
