# Upgrade contract — schema 1, application 0.1.0

This document defines a future update helper's integration points. There is **no updater** in v0.1.0.

## Stable layout

- Binaries: `%ProgramFiles%\M365Collector\<version>\`; GUI at root, service in `service/`.
- Runtime: technician-selected DataRoot; locator at 64-bit HKLM `SOFTWARE\SterlingTech\M365Collector`.
- Database: `<DataRoot>\Database\collector.db`, WAL enabled. Never copy only a live `.db` file.
- Configuration: `<DataRoot>\Config\runtime.json`, `SchemaVersion=1`.
- Health: `<DataRoot>\Service\health.json`, schema 1, semantic version, process instance ID, startup and heartbeat UTC timestamps.
- Backup: `<DataRoot>\Service\Backups\<timestamp>-<unique-id>\`, SQLite online backup API snapshot plus runtime configuration and metadata.
- Certificates: Windows LocalMachine/My and OS-managed non-exportable private keys. Preserve certificate objects, key ACLs and safe configuration references. They are never part of an application ZIP or binary replacement.
- Release: `M365Collector-<semver>-win-x64.zip`, adjacent `.sha256`, root `release.json` with schema, product, version, RID, component executable paths, database/config schemas and per-file hashes.

## Discoverable commands

```powershell
& 'C:\Program Files\M365Collector\0.1.0\service\M365Collector.Service.exe' --version
& 'C:\Program Files\M365Collector\0.1.0\service\M365Collector.Service.exe' --verify-package 'C:\Staging\M365Collector-0.1.0-win-x64'
& 'C:\Program Files\M365Collector\0.1.0\service\M365Collector.Service.exe' --health
# Run elevated, with GUI closed and service stopped:
& 'C:\Program Files\M365Collector\0.1.0\service\M365Collector.Service.exe' --backup
& 'C:\Program Files\M365Collector\0.1.0\service\M365Collector.Service.exe' --migrate
```

Commands accept `--data-root <absolute-path>` or use the registry locator. `--version` does not require configured state. Exit codes: 0 success, 1 command/configuration failure, 2 unhealthy/stale/version mismatch for health. A missing or unreadable health document returns command failure. Health requires SCM Running **and** a matching version/fresh (<45 seconds) Running heartbeat. An updater must also ensure the returned InstanceId differs from the pre-stop instance.

Maintenance refuses a running service and uses an exclusive runtime lease. Migrations do not run implicitly during ordinary service startup. First-run creation explicitly initializes schema 1. Subsequent migration implementations must append ordered transactional migrations, retain prior ledger rows, reject unsupported/newer schemas and preserve data. Configuration uses atomic same-directory replacement. A future config upgrader must explicitly transform versions; unknown versions currently fail closed.

## Future helper sequence (not implemented)

1. Resolve registered paths; read installed semantic version and schema. Compare semantic versions against the chosen GitHub Release version, normalising the `v` tag prefix. Validate publisher authenticity/checksum/product/RID; do not trust lexicographic version ordering.
2. Stage new binaries in a **new version directory outside DataRoot**. Preserve the previous directory. Close management GUIs to prevent edits, stop `M365CollectorService` and verify Stopped. Suspend recovery during maintenance if necessary. Record the old service ImagePath, identity/startup/recovery settings, locator and health instance ID in protected upgrade metadata under DataRoot.
3. Run the old version's `--backup`. Inventory all runtime files and certificate references. If a future migration modifies raw files or configuration beyond this snapshot, extend the backup to cover those files before mutating them. Non-exportable keys remain in the machine certificate store; routine binary upgrades do not remove/recreate them. Full machine recovery is a separate host backup concern.
4. Run supported new-version configuration/database migrations against the existing DataRoot. Update the registry/config installed version and InstallRoot only as part of the controlled upgrade. Preserve customers, tenant IDs, authentication references, enabled modules, snapshots, audit data, reports, exports and logs.
5. Repoint the existing service to the new binary directory, retaining the service name and SID; do not recreate it under an unrelated identity. Start it, verify SCM state and new matching-version heartbeat/instance, then perform appropriate read-only application health checks.
6. On failure, stop the new service, retain diagnostic logs, restore the pre-upgrade configuration and consistent database snapshot if any migration was applied, restore the old service path/locator/settings, restart and verify the prior version. **Never run old binaries against a newer database schema.** Report failures precisely; if restoration cannot be verified, leave the service stopped and require operator recovery.

The updater must never delete DataRoot or put customer files into a binary directory. Replacing an installation package is not a licence to remove certificates, configurations, data, reports or logs. Automatic rollback machinery, network release comparison and GitHub download/install are intentionally future work.
