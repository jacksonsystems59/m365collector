# Changelog

## 0.0.3 — 2026-10-02

- Added Windows-administrator local account recovery to the launcher and login/lock screen, preserving roles and customer data with atomic audit recording.
- Added a self-contained single-executable installer/launcher and single-file GUI, Service and Updater components. Retained the compatible updater ZIP and rollback flow.
- Added original multi-size application ICO and 16 SVG/PNG navigation/action icons, integrated into the interface.
- Moved updater request models into Core without changing their serialized format, removing executable dependencies from GUI/launcher packaging.
- Added recovery authorization, lockout, validation and audit tests plus native SQLite single-file runtime diagnostics.

## 0.0.2 — 2026-09-30

- Added Sign-in Activity, Directory Changes and Microsoft 365 Unified Audit with pagination, bounded retries, checkpoints and tenant-scoped deduplication.
- Added module/customer health cards, configurable schedules, independent service jobs, local Audit Explorer, raw event details, grouping and filtered CSV export.
- Added reviewed permission updates for existing customer apps and explicit opt-in retention.
- Migrated schema 1 transactionally to schema 2 while preserving existing users/customer references; added rollback-compatible integrity checks.
- Anchored LOCK and version at bottom-left and added explicit update notification actions.
- Added synthetic collector/storage/migration tests and live acceptance documentation. Live tenant and installed-service upgrade acceptance remains separate.

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
