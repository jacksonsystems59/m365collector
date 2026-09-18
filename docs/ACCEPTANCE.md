# v0.1.0 release acceptance

Do not publish until these checks pass on a disposable Windows Server/VM and approved non-production tenant. Automated tests do not install a service or change the real machine certificate store, registry or production tenant.

## Windows first run (requires elevation)

- [ ] On a clean Windows Server 2019+ x64 VM without separately installed .NET, extract ZIP and run GUI. Confirm displayed version 0.1.0 and UAC requirement.
- [ ] Check dependency statuses, PowerShell version/optional state, offline/blocked Microsoft endpoint failure and retry. No PowerShell module bulk install occurs.
- [ ] Validate a non-C: NTFS DataRoot, free-space display, denied write location, binary-folder overlap, junction and UNC rejection. Select an empty dedicated data folder.
- [ ] Complete wizard. Confirm persistent binaries in Program Files, runtime exclusively in DataRoot, HKLM locator, schema 1 SQLite, restricted ACLs and automatic LocalService service.
- [ ] Confirm installer does not overwrite an unrelated service with the same name. Test failed install/start and resume; setup must remain incomplete until health succeeds.
- [ ] Close GUI, reboot VM and confirm service heartbeat and matching version. Relaunch GUI from another extracted folder and confirm setup is not repeated.
- [ ] A standard Windows user cannot open runtime files or manage the application. The service cannot modify its Program Files binaries. Unrelated LocalService processes cannot use the collector private keys through the per-service ACL.
- [ ] Delete/move a *disposable* registered config/database and confirm startup fails closed; restore it. Corrupt config and newer schema are rejected.

## Certificate and Entra (requires approved tenant/admin)

- [ ] Create a machine certificate, check non-exportable RSA private key and administrator/SYSTEM/service-only ACL. Exported .cer has no private key and stays under tenant Exports.
- [ ] Guided setup: approved setup public-client ID, review Application.ReadWrite.All delegation, Microsoft browser sign-in/MFA, tenant match, dedicated app, public certificate and service principal creation, explicit Entra consent, session-ended state.
- [ ] Interrupt guided setup after app creation; verify recorded checkpoint resumes. Inspect any ambiguous timed-out create in Entra before retrying. No application is deleted automatically.
- [ ] Manual setup: complete entirely from in-app instructions, grant only Organization.Read.All application permission and validate service-side access.
- [ ] Incorrect tenant/client, wrong/missing/expired certificate, denied consent, inaccessible service key, network outage and tenant mismatch do not activate a Draft.
- [ ] Successful service-side Test Connection retrieves identity/verified/default domains, activates customer and allows Collection Modules navigation.
- [ ] Add a second tenant with its own app/certificate. Confirm separate directories, metadata, jobs and module choices. Reusing an existing customer's app/certificate is rejected.
- [ ] Close GUI and collect again via scheduled service work. Disable Tenant Identity and confirm no further collection. Re-enable and Collect now.
- [ ] Inspect logs/config/database for accidental passwords, private keys or authentication tokens; none should be present.
- [ ] Rotate a certificate through a draft configuration, successfully validate new key, then remove old Entra credential manually.

## Upgrade preparedness and release

- [ ] CLI --version, assembly/file version, release manifest, registry and UI all identify 0.1.0.
- [ ] --health fails if stopped, heartbeat stale or version mismatched.
- [ ] With GUI closed and service stopped, --backup produces a restorable consistent database/config snapshot; --migrate is idempotent and refuses newer schemas.
- [ ] Maintenance refuses running service; binary replacement leaves all DataRoot content and machine certificates intact.
- [ ] Execute documented backup/restore rehearsal on the disposable VM; confirm customer/module state and collected data are preserved.
- [ ] Clean Release build, full tests and locked restore pass; package SHA-256/manifest verify; generated binaries ignored by Git.
- [ ] Exact source committed and pushed to jacksonsystems59/m365collector; technician authorises publication. Check gh auth, repo and tag absence before release creation.

Record OS/build, package SHA-256, source commit, tester, UTC date and pass/fail evidence. A checklist without recorded execution is not acceptance evidence.
