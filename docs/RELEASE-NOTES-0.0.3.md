# M365Collector v0.0.3

- New self-contained **M365Collector-0.0.3-win-x64.exe** download bundles the application, Windows Service and updater. No separately installed .NET runtime is required. The launcher installs or upgrades with the existing backup, health-check and rollback process.
- **Reset local password** is available in the launcher, including for existing v0.0.1/v0.0.2 installations. **Forgot local login?** is also available on the installed application's sign-in/lock screen. Run as a Windows administrator, select your existing username and enter/confirm a new password of 14–1024 characters.
- Local passwords remain salted PBKDF2-SHA256 hashes, not reversible encrypted passwords. They cannot be viewed. Recovery preserves the account's role, clears its lockout and records the Windows SID/account/time without the password. Customers, Microsoft consent, certificates and collection data are preserved.
- Original application icon in multiple Windows sizes, plus 16 matching navigation/action icons. SVG/PNG/ICO files are available in the separate icon-pack ZIP and used in the interface.
- GUI, Service and Updater each publish as a single executable. The compatibility ZIP/checksum remains available so older installed versions can discover and install this release through Settings / Updates.

## Forgotten-login recovery

1. Download and run **M365Collector-0.0.3-win-x64.exe**, accepting Windows administrator elevation.
2. Select **Reset local password**, choose the existing local username and enter/confirm a new password. The reset window lists usernames; no old password is required.
3. Close the reset window. Close any old M365Collector GUI and select **Install / upgrade & open**.
4. Sign in using the username shown in recovery and your new password.

This is one downloadable executable, not a zero-file portable application: it installs separate service/updater executables for independent collection and safe updates, and .NET extracts bundled native libraries at runtime. Runtime data remains in the existing DataRoot. Windows administrators are trusted recovery operators; local application role restrictions still apply after sign-in.

Automated recovery, UI, collector, database and updater tests use synthetic data. Live Microsoft sign-in, elevated installation/service upgrade and recovery against real customer data require local acceptance. Existing v0.0.1 and v0.0.2 releases are retained.
