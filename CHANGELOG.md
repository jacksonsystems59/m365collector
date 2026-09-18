# Changelog

## 0.1.0 — foundation implementation (unreleased)

- Added a separate M365Collector solution; existing SterlingMonitor sources are unchanged.
- Added portable self-contained bootstrap and multi-stage Windows first-run wizard.
- Added configurable protected DataRoot, registry-based setup discovery and independent Windows Service installation.
- Added SQLite repositories, transactional schema migration ledger, service job queue and tenant-separated identity snapshots.
- Added customer Draft/Active lifecycle, manual and guided Entra onboarding, machine certificates and service-side connection validation.
- Added least-permission Tenant Identity collector, per-customer module selection and explicit future collector/navigation placeholders.
- Added Windows administrator authentication, separate authorisation policy and structured safe-code logging.
- Made version 0.1.0 visible in the UI and discoverable through metadata, release manifest, registry and service CLI/health.
- Added upgrade-ready binary/data separation, explicit backup/migration/health commands and deterministic asset naming. No automatic update/download/install workflow.
- Added credential-free automated tests, release packaging and manual Windows/Entra acceptance checklist.

Release remains gated on successful elevated Windows service/certificate and live test-tenant acceptance, source review/commit/push and publishing confirmation.
