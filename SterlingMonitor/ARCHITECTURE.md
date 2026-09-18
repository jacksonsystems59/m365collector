# Architecture and next phases

## Phase 1 implementation

`MainForm` owns the tray icon, configuration screens and a non-overlapping polling timer. `MonitorEngine` owns incident transitions, restart budgets, maintenance and delivery timing. `IServiceAccess` and `IAlertSender` isolate Windows and SMTP operations for deterministic tests. `WindowsServices` uses ServiceController; `SmtpSender` uses MailKit/MimeKit with required TLS. `SettingsStore` writes JSON atomically through a temporary file; credentials use current-user DPAPI. `ActivityLog` stores bounded local history.

The application runs as the invoking user, with no mandatory UAC elevation. Monitoring and commands share an asynchronous gate so manual and automatic service control cannot overlap. Settings are copied at the start of a monitoring cycle; a settings change takes effect on the next cycle. A cycle already in progress finishes under its existing settings snapshot.

No real Windows services are configured by the build/test process. No installer, startup task or updater is silently registered.

## Phase 2: private GitHub release updater

Use a **GitHub App**, installed only on the private collector repository, with repository **Contents: read** and required metadata access. This gives an organisation-owned machine identity with no routine interactive login and avoids a personal token tied to an employee. Initial app registration and installation require the repository owner's administrative setup once.

Proposed setup fields: owner, repository, GitHub App client/app identifier, installation ID, private-key import, collector service name, protected installation directory, release channel and polling interval. Validate repository access before enabling updates.

The worker signs short-lived JWTs with the app private key and exchanges them for installation tokens scoped to the repository. GitHub installation tokens currently expire after one hour; renew on demand and keep them in memory. A PEM key is a long-lived credential: encrypt it under the updater service identity (or use a tightly ACL-restricted non-exportable certificate/key provider where compatible), never bundle it in the executable or source tree, and support rotation. The current-user SMTP secret storage in Phase 1 must not simply be copied to a service account.

Read-only SSH deploy keys can support cloning a single repository, but a GitHub App is the better fit for release API access and installation tokens. Do not pull arbitrary branch contents directly into a live application directory.

Update pipeline:

1. Read a release manifest from the configured repository/channel. Compare explicit package versions and reject unintended downgrades.
2. Download to a staging directory. Verify a publisher signature against a pinned trust key, plus the manifest's file sizes and SHA-256 hashes. Hashes alone do not prove publisher identity.
3. Reject absolute paths, path traversal, links/reparse points, excessive extraction sizes and unexpected files. Restrict destinations to the configured installation directory.
4. Coordinate maintenance with the monitor. Stop only the configured collector service; wait with a bounded timeout and abort safely if it cannot stop.
5. Retain the previous version and atomically switch to the verified package. Restart and check both Windows status and an application heartbeat/readiness signal.
6. Roll back on failed readiness, restart the prior version, record the result and email the outcome. Persist transaction state so interruption/reboot can be recovered.

Start with a **download-and-verify test mode** using harmless versioned text fixtures in a dedicated staging folder. No service stop or executable replacement should occur in that mode. Then exercise tampered package rejection, extraction limits, interrupted updates and rollback using a disposable collector test service. This implementation is intentionally deferred beyond Phase 1.

References: [GitHub installation authentication](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation), [GitHub App best practices](https://docs.github.com/en/apps/creating-github-apps/about-creating-github-apps/best-practices-for-creating-a-github-app), [Windows data protection](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection).

## Phase 3: Microsoft 365 collector

Build a separate Windows Worker Service with its own service identity, tenant configuration, scheduling, durable collection checkpoints, retry/backoff, retention and delivery queue. Keep the UI as a management client. Choose the Microsoft 365 audit/report APIs and application permissions per report type; confirm coverage and licensing before implementation. Use tenant-approved application authentication with certificates where supported, least-privilege permissions, and no interactive login in the collection loop.

A future monitoring worker can share the Phase 1 engine but needs durable incident state, an independently scheduled notification queue, protected machine-wide configuration and an authenticated local IPC channel to the tray. Privileged update/control operations should run in a narrowly scoped broker/service rather than requiring the entire tray application to be elevated.
