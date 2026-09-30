# M365Collector v0.0.2

This incremental release adds Entra Sign-in Activity, Directory Changes and Microsoft 365 Unified Audit collectors while preserving existing 0.0.1 customers, local accounts, certificate references and DataRoot. The 0.1.0 abandoned prototype remains a separate, unsupported migration path.

- Customer and module cards show health, schedules, permissions, counts and recent failures. Run Now queues independent service collection.
- Audit Explorer searches local events with filters, event details/raw JSON, sign-in/IP/location views and bounded CSV export.
- Unified Audit includes SharePoint/OneDrive, Exchange and general/Teams activity supplied by Microsoft. Exchange message trace and PDF reports remain future work.
- Additional permissions can be reviewed and granted to the existing customer application through Microsoft sign-in.
- Schema 2 adds transactional event storage, checkpoints, deduplication, processed-feed receipts and run history. New audit modules are disabled until enabled; 90-day retention cleanup is off until explicitly enabled.
- LOCK and Version 0.0.2 are anchored at the bottom-left. Update notices include current/available versions and explicit Install/Later actions.

Install from the existing 0.0.1 Settings / Updates page or use the full self-contained win-x64 ZIP for a new installation. Keep the ZIP and matching SHA-256 companion together. No GitHub login or separately installed .NET runtime is needed on the target machine.

Live tenant consent/feed availability and an installed 0.0.1 → 0.0.2 service upgrade require acceptance testing; automated tests use isolated databases and fake HTTP/service operations. Follow [collector setup and live acceptance](https://github.com/jacksonsystems59/m365collector/blob/main/docs/COLLECTOR-MODULES.md). Entra licensing and enabled Microsoft Purview auditing affect available data. Microsoft commercial cloud endpoints only.
