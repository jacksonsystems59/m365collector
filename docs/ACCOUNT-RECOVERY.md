# Local account recovery

Run the v0.0.3 single executable as a Windows administrator, select **Reset local password**, select your existing account and enter the new password twice. You can reset an older v0.0.1/v0.0.2 installation before upgrading. On the installed v0.0.3 login or lock screen, use **Forgot local login?** instead.

Recovery lists local usernames and roles only. It does not expose password hashes or Microsoft credentials. Passwords use random salts and PBKDF2-SHA256 (600,000 iterations). Their original text cannot be decrypted or recovered. Choose a new password of 14–1024 characters and keep it in your password manager.

The Windows administrator check runs again at the storage operation, not just in the UI. Reset changes only the selected account's password hash, failed-attempt counter and lockout time. The role remains unchanged. A transaction records the action, local account name, Windows SID and UTC time in Settings under a unique `SecurityRecovery:` key; it does not record the new password. Existing customer data, certificates, consent, DataRoot and other local accounts remain untouched.

Windows administrators already control the protected database and binaries and are trusted recovery operators. This is not a way for an ordinary Windows user to bypass local roles. Anyone without Windows administrator access must ask the machine administrator to perform recovery.

If no installation is found, run setup first. If no accounts are listed, complete the first local Administrator step in setup; recovery does not create accounts. If an update is active, finish or recover that update before resetting a password. A reset does not terminate already-open GUI sessions; close other application windows when recovering a potentially compromised password.

## Manual acceptance

1. On a disposable existing installation, note customer count, DataRoot, client IDs and certificate thumbprints without exporting secrets.
2. Run the new executable elevated and reset an existing account. Verify the displayed username is correct; old password fails, new password succeeds and the original role is unchanged.
3. Close the old application and select Install / upgrade & open. Confirm version 0.0.3, the new icon and service heartbeat, and that the same customers/certificates/DataRoot remain.
4. Lock the application and exercise Forgot local login? again. Confirm confirmation mismatch and short passwords do not reset the account.
5. On a clean Windows x64 VM without .NET installed, run the one-file download and complete setup; verify background collection continues after closing the GUI. Test Microsoft WAM/system-browser authentication against a disposable tenant.
6. Verify Settings / Updates from v0.0.1 or v0.0.2 detects the release and its compatibility ZIP. Exercise failed upgrade rollback only on a disposable copy.
