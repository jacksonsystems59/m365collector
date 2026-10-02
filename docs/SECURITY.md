# Security — 0.0.2

## Trust boundaries

M365Collector never asks for, captures or stores a Microsoft administrator password. All Microsoft interactive authentication occurs in MSAL WAM or the system browser. The service uses tenant ID, client ID and a certificate; it does not use an administrator identity, username/password grant, persisted delegated session or client secret. Graph/MSAL HTTP bodies and access tokens are not written to logs.

The local M365Collector password is separate from Microsoft. Passwords are 14–1024 characters, salted independently with 128 random bits and hashed using PBKDF2-SHA256 with 600,000 iterations and a 256-bit output. Verification uses constant-time comparison. Five failures lock an account for 15 minutes; successful authentication clears failures. There is no default account or password. The first-Administrator insertion is conditional at the database level to prevent a second bootstrap Administrator. GUI sessions lock after 15 minutes of inactivity.

Authentication (`LocalAccounts`) and authorization (`Authorization`) are separate. Administrators manage customers, local users, configuration and updates. Operators view and collect/manage collection enablement. Read Only accounts view data. Handler-level capability checks accompany UI restrictions.

This is a local technician application. Its GUI requires Windows elevation. **Windows administrators can override local files, database role records and process memory and remain fully trusted.** App roles are not a sandbox against hostile Windows administrators. Ordinary Windows users do not receive access to DataRoot or private keys. Use BitLocker, protected backups, supported OS security patches and a dedicated managed collection host as appropriate; SQLite itself is not encrypted by this application.

## Files and keys

DataRoot ACL inheritance is replaced with Administrators/SYSTEM full control and the M365CollectorService SID Modify access. Application binaries and the machine locator allow service read/execute but no service write. Update working directories inherit the protected machine-installation policy. The service runs as LocalService with its own SID. Reparse-point directory ancestors and network data roots are rejected. Customer output directories are derived from validated GUIDs.

Generated certificates use persistent machine CNG keys, RSA-3072, SHA-256 signatures, digital-signature usage and one-year validity. Keys have no export permission. The private key file grants Administrators/SYSTEM full control and the service SID read. The service proves private-key access during connection verification. Public export uses only `X509ContentType.Cert`; no PFX is created. External certificates may have different export policy and require explicit service key access. Renewal and abandoned-certificate cleanup are currently manual.

## Updates

Updates are explicit administrator actions. The public GitHub API requires no credentials. HTTPS, asset-origin restrictions, SHA-256, strict ZIP paths and a per-file manifest protect download integrity and package placement. Executable updates inherently trust this repository's release publisher. A checksum fetched from the same GitHub release does **not** provide independent publisher authenticity against compromise of that release account. Authenticode/package signing is not implemented in 0.0.2; protect repository/release access accordingly.

The extractor rejects paths outside GUI/Service/Updater, traversal, drive paths, device names, alternate streams, links, duplicate names, unlisted files, scripts and oversized archives. It never executes package scripts. The updater runs only the expected service binary for migrations and relaunches the expected GUI. Protected journals/backups support rollback and recovery. Logs, reports, exports, certificates and customer directories are not replaced by updates.

## Reporting and verification

Do not put credentials, private keys or customer data in public GitHub issues. Automated tests use fictional tenants and temporary storage. Release publication scans staged source for secrets and excludes runtime/build outputs. See README for the distinction between automated test evidence and live Windows/Entra acceptance.

## Audit data in 0.0.2

Audit records include identity, IP, location and original Microsoft JSON. Existing DataRoot ACLs protect SQLite and backups; exports are technician-selected files requiring their own protection. JSON is displayed as text. CSV neutralizes spreadsheet formulas. API continuation URLs are restricted to expected Microsoft endpoints and customer feed paths. Additional application consent is explicitly reviewed and uses the existing customer app/certificate. Retention deletion is off by default and requires Administrator opt-in.

## 0.0.3 account recovery

A Windows administrator can list local usernames and reset an existing password through the launcher or login screen. Both list/reset operations enforce Windows elevation. Reset preserves role and records the actor SID/account/time transactionally without password text. Windows administrators are trusted machine owners; ordinary Windows users are not given a recovery bypass. See [account recovery](ACCOUNT-RECOVERY.md).
