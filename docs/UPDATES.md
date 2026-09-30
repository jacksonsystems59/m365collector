# Built-in updates

## Checking

Settings → Updates shows current version, stable channel, latest version, last checked time, publication date and release notes. It uses `https://api.github.com/repos/jacksonsystems59/m365collector/releases/latest` with a product User-Agent and GitHub JSON Accept header. Drafts/prereleases are ignored. Stable semantic versions compare numeric major/minor/patch components, not strings. Stable tags with optional `v` prefix are supported; prerelease/build-metadata versions are not an enabled channel.

The GUI checks in the background while open, caches successful checks for six hours and uses ETags for conditional requests. Background failures back off for 15 minutes. Explicit checks show offline, rate-limit, malformed-response and missing-asset errors. A 404 is treated as no published release. There is no GitHub login, PAT, credential storage or dependency on `gh` on deployed machines. Checks never install updates.

## Release contract

Publish a normal stable release containing exactly named assets:

```text
M365Collector-<version>-win-x64.zip
M365Collector-<version>-win-x64.zip.sha256
```

The SHA file contains hexadecimal SHA-256 and the ZIP filename. The ZIP root contains `package.json`, `GUI/`, `Service/` and `Updater/`. The manifest records the exact version and SHA-256 of every file. Required component executables must exist. No scripts are permitted in a package. Release builds generate both ZIP and checksum; files stay in ignored `dist`, never normal Git history.

## Transaction

1. An application Administrator selects Download and Install.
2. The GUI downloads ZIP and SHA into `%ProgramData%\M365Collector.Installation\Updates\<random-id>`, with restricted ACLs, HTTPS and size limits. A checksum failure deletes the staged downloads, logs failure and stops.
3. The currently installed updater component is copied to a helper directory there. A structured request includes package paths, source/target versions and the GUI PID. The helper starts and the GUI exits.
4. The helper takes the global update mutex, validates the request location against the protected locator, waits for GUI exit, rechecks SHA-256 and extracts the validated package to a fresh staging folder.
5. It stops the service, copies old binaries/config and takes a SQLite backup. A durable journal records the transition before replacement.
6. It replaces only the configured application directory and runs the new `Service\M365Collector.Service.exe --migrate`.
7. It starts the service and requires Running state, a heartbeat newer than startup with the target version/DataRoot, and database integrity. It records update history, logs success and relaunches the installed GUI.

DataRoot and AppRoot cannot overlap. Customers, Reports, Exports, Logs, keys and certificates are never part of binary replacement. Backups remain in the restricted update working directory for recovery; automatic backup retention/cleanup is future work. Ensure enough space for the ZIP, extracted release, helper, previous binaries and database/config backups.

## Rollback and interruption

Failure during replacement, migration, startup or health verification stops the failed service, restores the previous application, SQLite snapshot and configuration, restarts the previous service, validates health and reports a rollback. An extraction/checksum failure before replacement leaves the running installation intact. A backup failure after stopping the service restarts/verifies the old service. A failed rollback retains its journal/backups and reports that recovery is needed; it cannot promise success if the disk or SCM is unavailable.

On interruption, Settings → Updates exposes Recover interrupted update. The staged helper reads its journal and restores the previous version for Replacing/Migrating/Verifying/RollingBack states. Preparing restarts/verifies the old version. Recovery requires operator action after power loss; it is not a boot-time recovery service. If the GUI cannot start, run the retained `helper\M365Collector.Updater.exe` elevated with the full `request.json` path. The helper only waits on a recorded process that still has the GUI process name; unrelated reused process IDs are ignored.

Update logs are under `<DataRoot>\Logs\Updates`. `Config\last-update.json` and the journal identify the last transaction; UpdateHistory records successful installs and verified rollback outcomes.

Tests inject network/service/migration failures and verify database/config restoration and preservation of reports. They also reject bad checksums, unsafe ZIP paths and unlisted payloads. Actual service-control and live-tenant acceptance are separately documented deployment checks.

## 0.0.1 to 0.0.2

The release retains the GUI/Service/Updater package layout and checksum/manifest formats understood by 0.0.1. The service migrates schema 1 to 2 transactionally; updater backup/recovery preserves the prior database for rollback. New collector permissions are not granted by installing an update. The installed GUI shows LOCK and its version at bottom-left. See [live upgrade acceptance](COLLECTOR-MODULES.md#live-acceptance-checklist); automated package checks do not replace installed-service testing.
