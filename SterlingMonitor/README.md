# SterlingMonitor

Version 0.1.0 — Phase 1: a native Windows system-tray service monitor.

## Run

1. Copy **only `SterlingMonitor.exe`** to a permanent location on your Windows VM and run it. No adjacent DLLs, configuration files, installer, or separate .NET installation are needed. The self-contained x64 executable includes the runtime and application dependencies. Settings and logs are created automatically under the current user's local application data; bundled native runtime files may be extracted automatically to the user's temporary directory. Use a supported Windows x64 system with a desktop session (not Server Core). Configure services and SMTP separately on each machine.
2. Select **Add services**, check one or more services, and choose **Add selected**.
3. Select a service and open **Edit policy** to configure its alert level, failure delay, repeated alerts and optional automatic recovery.
4. Open **SMTP & settings**, enter the server, sender and recipients, then choose **Send test email**. Check the receiving inbox, then **Save settings**. Test sends the values currently entered even when alert emails are disabled; it does not save them.
5. Close or minimize the window to keep it in the tray. Double-click the tray icon to reopen. Use the tray menu's **Exit monitor** to quit.

The executable is an unsigned development build. Validate it on a test VM before production deployment. It has been built and tested on Windows 11 x64; your particular Windows Server version and SMTP provider still need acceptance testing.

## Service policies

| Setting | Behavior |
| --- | --- |
| Enabled | Enables monitoring for this service. |
| LogOnly | Records unhealthy/recovery events without email or tray notifications. |
| Warning / Critical | Labels email and tray alerts with the selected severity. Both trigger on the same unhealthy conditions. |
| Failure delay | Continuous unhealthy time required before declaring an incident; default 30 seconds. |
| Repeat email | Interval between successful incident emails; default 15 minutes. |
| Recovery email | Sends when health returns, only if an incident email was previously accepted by SMTP. |
| Automatic recovery | Off by default. Starts stopped services, continues paused services, or restarts a running service with an unhealthy heartbeat. |
| Maximum attempts | Default 3 attempts; failures count toward the limit. |
| Retry cooldown | Default 5 minutes between attempts. Attempts reset after 5 minutes of continuous health. |
| Heartbeat file | Optional absolute local file path that the monitored service must regularly update. |
| Heartbeat max age | Default 120 seconds. Missing, inaccessible, stale or substantially future-dated files are unhealthy. |

Without a heartbeat, a Running service is considered healthy based only on Windows Service Control Manager status. That does **not** establish application responsiveness. StartPending, StopPending, paused, stopped and inaccessible/missing services are unhealthy after the failure delay. Automatic recovery does not issue competing commands during a pending transition and never kills a process.

Monitoring defaults to a 15-second poll, configurable from 5–300 seconds. Checks do not overlap. The interval begins when the previous cycle finishes; SMTP and service control waits can lengthen it. SMTP has a 30-second timeout per send, and service control waits up to 30 seconds per transition. Large service sets or failing SMTP endpoints can therefore delay later services in a cycle.

Automatic restart attempts, maintenance windows and alert state are held in memory. Restarting SterlingMonitor resets them; repeated app restarts can replenish a restart budget. Phase 1 is not a durable alert queue: an undelivered incident may not be emailed if the service recovers before delivery succeeds. SMTP acceptance is not proof of inbox delivery. Failed sends retry no more than once a minute while applicable; a connection loss after server acceptance can still lead to a duplicate.

## Manual control and maintenance

Select a monitored service and choose **Start**, **Stop**, or **Restart**. Stop and Restart require confirmation. Commands need Windows permissions on the target service, usually achieved by running SterlingMonitor as administrator. Read-only monitoring does not require automatic elevation. The app does not change service ACLs, startup types or Windows recovery settings.

Manual commands open a five-minute maintenance window for that service, including when a command fails. **Maintain 30 min** pauses email alerts and automatic recovery for 30 minutes; **Resume** ends maintenance immediately. Observations remain visible. Running dependent services are never stopped implicitly.

Only enable recovery for services that should be continuously running. Demand-start and trigger-start services often stop normally. Check whether Windows' own recovery configuration also acts on the same service.

## SMTP and credentials

- STARTTLS (normally port 587) and implicit TLS (normally 465) are supported. TLS is required and server certificates use normal trust validation.
- Configure username/password authentication (including provider app passwords), or disable authentication for a compatible TLS-enabled relay.
- Recipients are separated by semicolons. The sender must be permitted by your SMTP provider.
- The password is encrypted with Windows DPAPI for the current account before storage. It is not stored as plaintext or written to logs. Processes running as that same account may still decrypt it; DPAPI is not protection against a compromised account.
- A blank password field preserves the saved password. Enter a new value to replace it, or select **Remove saved password on save**. If authentication is still enabled, a password is required before enabled email settings can be saved.
- DPAPI secrets are account/profile-bound. Configure credentials again after moving the app's settings to another account or VM.
- This release does not implement OAuth-based SMTP or Microsoft 365 tenant authentication. Use a compatible SMTP provider/relay for Phase 1.

Settings and rotating activity logs are stored in `%LOCALAPPDATA%\SterlingMonitor`. The activity view shows the current session's last 500 events, newest first; disk logs rotate at approximately 2 MB and retain one prior file. SMTP failures use safe diagnostic categories rather than logging passwords or raw server exchanges.

## VM session and startup

This phase is a tray application, not a background Windows monitoring service. It runs only while the application remains open in a signed-in session. Disconnecting RDP normally leaves the session running, but signing out or a policy that logs off disconnected sessions stops it. A reboot requires signing in and starting the application again.

To start at sign-in, place a shortcut to the permanent executable in the Windows Startup folder (`shell:startup`) and add `--minimized` to its target arguments. The in-app **Start minimized** setting controls window visibility; it does not register startup. For elevated service control, an administrator can instead configure a scheduled task at logon with **Run only when user is logged on** and **Run with highest privileges**. No startup task is installed automatically.

Monitoring across sign-out/reboot requires a later background worker service. The planned M365 collector will be a separate Windows service and will not depend on this tray UI.

## Build and verify

Requires the .NET 10 SDK on Windows.

```powershell
dotnet restore SterlingMonitor.slnx
dotnet build SterlingMonitor.slnx -c Release
dotnet run --project tests/SterlingMonitor.Tests -c Release
dotnet publish src/SterlingMonitor/SterlingMonitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o release/win-x64
```

The executable test harness exits nonzero on a failed assertion. It uses fake service controls and fake SMTP delivery for incident tests, real DPAPI for credential tests, and read-only access for Windows service enumeration and heartbeat tests. UI rendering uses an off-screen preview with monitoring disabled. It does not control real services or send external email. `dotnet test` is not the runner for this harness.

37 checks cover grace periods, email repetition and recovery, failed delivery retries, bounded restarts, flapping, maintenance, severity, disabled policies, pending service states, heartbeat failure, multiple services, DPAPI, settings corruption, validation, real read-only Windows checks, rendering the four main screens and two dialogs, and preserving the configured alert-level selection.

Before production use, validate real start/stop/restart and recovery against a disposable test service, SMTP inbox delivery and failure handling against your provider, target-VM permissions, tray behavior, RDP disconnect/sign-out behavior, and your chosen startup configuration. These real service-control and SMTP delivery tests have not been run on your infrastructure.

## Next phases

See [ARCHITECTURE.md](ARCHITECTURE.md) for the private GitHub updater and Microsoft 365 collector design. Neither updater polling nor collector functionality is implemented in Phase 1. The About screen reflects this boundary; there are no nonfunctional update controls or stored GitHub credentials.

Third-party runtime/library licenses travel with the published release. Source dependency versions are captured in `packages.lock.json`; NuGet is scoped through the repository's `NuGet.Config`.
