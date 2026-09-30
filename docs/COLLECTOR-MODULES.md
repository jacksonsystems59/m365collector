# Collector modules — 0.0.2

Select a customer in **Collection Modules**. New audit modules start disabled, including after an upgrade. Enable queues collection by the Windows Service. Run Now queues an enabled module; it does not launch a second concurrent run. Configure selects 5, 15, 30, 60, 360, 720 or 1440 minutes. Audit modules default to 15 minutes; Tenant Identity retains 60 minutes.

| Module | Microsoft API | Application permission |
| --- | --- | --- |
| Tenant Identity | Graph organization | Organization.Read.All |
| Entra Sign-in Activity | Graph v1.0 auditLogs/signIns | AuditLog.Read.All |
| Directory Changes | Graph v1.0 auditLogs/directoryAudits | AuditLog.Read.All |
| Microsoft 365 Unified Audit | Management Activity API | ActivityFeed.Read |

Failed Sign-ins and Location / IP Activity query the same sign-in records. Location is supplied by Microsoft; no external geolocation service is called. Interrupted is a local classification of selected Microsoft authentication/MFA/consent error codes. Inspect the original status/error code when investigating. Directory events retain target resources and modified properties in raw JSON.

Unified Audit starts and verifies Audit.SharePoint, Audit.Exchange, Audit.AzureActiveDirectory and Audit.General subscriptions. SharePoint/OneDrive, Exchange, Teams and general views filter this shared dataset. Teams appears when Microsoft identifies its workload. DLP subscriptions and Exchange message trace are not implemented. No Exchange PowerShell module is needed.

## Consent and prerequisites

An application Administrator can select **Update Tenant Permissions**, review the customer, existing application ID, resource and permission, then use Microsoft sign-in or the explicit browser alternative. The additional app role is added to the existing customer application/service principal without replacing its certificate or existing permissions. The signed-in administrator must belong to the selected tenant and have authority to grant consent. Temporary delegated sign-in is disposed after the operation. Alternatively, add the displayed application permission and grant tenant administrator consent in Entra, then Run Now.

Sign-in API availability depends on Entra licensing, including P1/P2. Unified auditing must be available and enabled in Microsoft Purview; newly started subscriptions may take time to supply content. Empty feeds are shown as Attention rather than claiming useful audit data was collected. These collectors support Microsoft's commercial cloud endpoints.

## Collection and health

Cards show Disabled, NotConfigured, Ready, Collecting, Healthy, Attention or Failed, plus schedule, timestamps, record counts, permission/dependency state and last error. Healthy means that the last run completed; it does not certify completeness of Microsoft's upstream feed. Missing permission is NotConfigured; no feed, interruption or partial feed errors produce Attention. Dashboard and customer cards summarize these states and recent runs.

The service runs at most four jobs at once and isolates failures by customer/module. Each customer/module has one active run. Shutdown cancels requests; completed pages remain. On restart, interrupted run history is marked Attention. Scheduling uses the last attempt to avoid tight retry loops; Run Now requests an earlier retry.

First collection requests the preceding 24 hours. Graph uses bounded daily slices with ten-minute overlap. Unified Audit uses slices no longer than 23 hours and one-hour overlap, constrained by Microsoft's seven-day content availability. A gap beyond that window is reported. HTTP 429 Retry-After, transient server/network errors and one forced authentication refresh have bounded retries. Continuation and blob URLs must remain on the expected Microsoft resource and customer feed path. Responses are limited to 64 MB and pagination to 10,000 pages per slice.

Events are unique by tenant/module/provider ID; a raw-content hash is used if a unified event lacks an ID. Pages commit before advancing slice checkpoints. Blob events and processed-content receipts commit together. A failed slice is retried with deduplication. Raw Microsoft JSON is retained alongside normalized search fields.

## Explore, export and retention

Audit Explorer searches the local SQLite database. Select a customer, UTC start/end (end exclusive), data type and optional user, action, file/object, site/folder, IP, location, result, application or category filters. Search applies filters. Event pages contain 250 rows; grouping returns up to 500 groups across the filtered data. Double-click an event for details and raw JSON displayed as text.

Export filtered CSV uses the last applied filters, even if controls have subsequently changed. Export runs off the UI thread using a SQLite read snapshot, includes customer/date metadata, escapes cells and neutralizes spreadsheet formulas. Narrow the selection when it exceeds 100,000 events. Exported files contain customer audit data and must be protected accordingly.

Retention defaults to 90 days with cleanup **off**. Only an Administrator can enable deletion or change retention days. Enabled cleanup removes at most 5,000 old events per successful/partial collection run; it is not a promise of immediate deletion. Raw JSON is removed with its event. Processed feed receipts retain at least 14 days. Backups and CSV exports have separate lifecycles and are not purged by this setting.

## Live acceptance checklist

Automated tests use synthetic data and fake HTTP/services. They do not establish live consent, actual tenant licensing, completeness of Microsoft feeds or an installed-service upgrade.

1. In a disposable Windows environment, install stable 0.0.1 and complete its wizard. Create a local administrator and onboard a test customer. Record DataRoot, tenant/client IDs and public certificate thumbprint, without copying secrets.
2. From 0.0.1 Settings / Updates, check for 0.0.2. Confirm current/available versions and notes, approve installation, and verify ZIP/checksum download and updater completion.
3. Reopen the installed GUI. Verify bottom-left LOCK and Version 0.0.2, old local login, same customer/client/certificate/DataRoot, service heartbeat 0.0.2, schema 2 and preserved Tenant Identity history. New audit modules must be disabled. Do not rerun onboarding to recreate a customer.
4. Review and grant AuditLog.Read.All and ActivityFeed.Read for that existing app. Confirm no extra certificate or replacement app was created. Enable modules and Run Now.
5. Compare sample events with Microsoft portals/API data. Verify failed/interrupted sign-ins, IP/location groups, directory modified properties, and SharePoint/file operations. Expect initial feed delay. Teams/Exchange rows require actual upstream activity.
6. Repeat a run and restart the service during a test collection. Verify event IDs do not duplicate and retries resume. Exercise permission denial on a disposable app and verify other modules/customers continue.
7. Filter, inspect raw data and export CSV; compare counts with the applied customer/date filters. Confirm Read Only users cannot collect or grant consent. Test retention only with disposable synthetic/obsolete data after explicit opt-in.
8. In a disposable copy, exercise a failed update/health check and recovery; verify the backed-up v1 database, binaries and local login return. Never simulate failure against production data.

API references: [Graph sign-ins](https://learn.microsoft.com/en-us/graph/api/signin-list?view=graph-rest-1.0), [directory audits](https://learn.microsoft.com/en-us/graph/api/directoryaudit-list?view=graph-rest-1.0), [Management Activity API](https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-reference).
