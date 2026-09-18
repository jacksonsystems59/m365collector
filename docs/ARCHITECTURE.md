# M365Collector architecture

The solution depends inward on Contracts. Core references Contracts; Security references Contracts; Storage references Core; Entra references Core/Security; TenantIdentity references Entra. GUI and Service compose those libraries. There are no references to SterlingMonitor and no circular dependencies.

## Runtime and trust boundaries

The GUI runs elevated and uses Windows-authenticated administrator identity. `IAuthorizationPolicy` is separate from authentication for future providers. Every privileged setup/certificate operation explicitly requires elevation as well as the GUI manifest. A normal Windows user cannot read protected runtime directories. SQLite is local IPC for trusted components, not a remotely exposed API. A local administrator is inside the trusted boundary.

The service runs as LocalService and receives a per-service SID. DataRoot grants that SID Modify, Administrators and SYSTEM FullControl. Private keys grant the SID Read. Program Files prevents the service writing application binaries. Service startup acquires a filesystem lease, validates the directory tree, requires an already migrated database and publishes an independent ten-second heartbeat. Collection failures are recorded per job and do not activate drafts. An unhandled runtime failure stops the host; a stale heartbeat is unhealthy.

## Customer lifecycle

Customers are keyed by tenant GUID. Save Draft always invalidates activation. Configuration edits are refused while that tenant has queued/running work. The service atomically claims jobs and validates the returned tenant identity before transactionally updating customer state, metadata and job result. Interrupted jobs are marked Failed at service restart, permitting a deliberate retry. Repeated job requests are deduplicated while work is pending. A service-side validation failure leaves a customer Draft; API permission success is established by the requested organization read, without requesting directory-wide role enumeration permissions.

Only implemented, enabled modules can collect. A module declares schema, ID, display name, description, version, required APIs, permission IDs/reasons and PowerShell dependency requirements. Tenant Identity has no PowerShell dependency and requires only Graph Organization.Read.All application permission. Future module configuration is versioned in `enabled_modules.configuration_schema/configuration_json`.

## Audit and report extension boundary

Future workload-specific raw records should be stored separately under each tenant's Audit folder, with workload/time-oriented SQLite indexes or another appropriate query store, not a single giant table. The future normalized query contract should include timestamp, tenant/customer, user, workload, operation, object/event ID, file name/type/path, folder, SharePoint site, IP address, location and result. Audit Explorer owns high-volume searches; Reports owns aggregate/report presentation.

Future CSV/PDF export envelopes must identify tenant/customer, UTC date range, filters, generated timestamp, module/schema versions and export format. Raw exports and generated reports remain under DataRoot. Neither the full query engine nor export rendering is implemented in 0.1.0.

## Operational limits

Single machine, Windows administrators only, Microsoft public cloud only, one implemented collector, fixed hourly default schedule, operator-managed retention, manual certificate rotation. Future custom identities, role assignment UI, remote management, automatic rotation/updating, report rendering and audit collectors require separate releases. GUI consent opens Entra; it never automatically grants a tenant-wide application role.
