using M365Collector.Core;
using M365Collector.Contracts;
using M365Collector.Security;
using M365Collector.Storage;
namespace M365Collector.GUI;
internal sealed class FirstRunWizard : Form
{
    private static readonly string[] Steps = ["Welcome", "System Requirements", "Dependency Check", "Data Location", "Runtime Structure Creation", "Windows Service Installation", "Local Application Security", "Setup Verification", "Add First Customer"];
    private readonly FlowLayoutPanel page = Ui.Stack();
    private readonly Label status = Ui.Text("");
    private readonly Button next, back, cancel;
    private int step; private bool busy; private string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "M365Collector");
    private TextBox? data, username, password, confirm;
    public FirstRunWizard()
    {
        Ui.Style(this, "M365Collector 0.0.2 • First Run Wizard");
        if (File.Exists(InstallationState.Locator)) dataRoot = JsonFile.Read<Installation>(InstallationState.Locator).DataRoot;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(24, 10, 24, 10), FlowDirection = FlowDirection.RightToLeft };
        next = Ui.Button("Next →", async () => await Advance()); back = Ui.Button("← Back", () => { step--; Render(); }); cancel = Ui.Button("Cancel", () => Close());
        buttons.Controls.Add(next); buttons.Controls.Add(back); buttons.Controls.Add(cancel); Controls.Add(page); Controls.Add(buttons);
        FormClosing += (_, e) => { if (busy) e.Cancel = true; }; Render();
    }
    private void Render()
    {
        page.Controls.Clear(); status.Text = ""; page.Controls.Add(Ui.Text($"{step + 1} / {Steps.Length}   {Steps[step]}", heading: true));
        back.Enabled = step > 0 && step < 5; next.Text = step == 8 ? "Finish & add customer" : "Next →";
        switch (step)
        {
            case 0: page.Controls.Add(Ui.Text("A secure foundation for Microsoft 365 collection. This wizard installs the independent Windows Service, creates protected runtime storage and creates your first local Administrator.\n\nMicrosoft passwords are never requested by M365Collector.")); break;
            case 1: page.Controls.Add(Ui.Text("64-bit Windows 10 1809 / Server 2019 or newer\nAdministrator elevation\nPowerShell 5.1+ for optional guided setup\nHTTPS access to Microsoft login, Graph and GitHub\n512 MB or more free space for runtime data\n\nThe release includes its .NET runtime. Application binaries install under Program Files; runtime data stays separate.")); break;
            case 2: page.Controls.Add(Ui.Text("Next runs the dependency checks. Errors appear here; correct them and retry.")); break;
            case 3:
                data = Ui.Field(page, "Where should M365Collector store runtime data?", value: dataRoot);
                page.Controls.Add(Ui.Button("Browse…", () => { using var dialog = new FolderBrowserDialog { Description = "Choose a dedicated M365Collector data folder", UseDescriptionForTitle = true }; if (dialog.ShowDialog() == DialogResult.OK) data.Text = dialog.SelectedPath; }));
                page.Controls.Add(Ui.Text("Examples: C:\\ProgramData\\M365Collector or D:\\M365CollectorData. Use an empty dedicated local folder. Its permissions will be restricted to administrators and the service.")); break;
            case 4: page.Controls.Add(Ui.Text("Create Config, Database, Logs, Reports, Exports, Cache, Temp, Service and Customers under:\n" + dataRoot)); break;
            case 5: page.Controls.Add(Ui.Text("Install M365CollectorService under LocalService with its own service SID, configure automatic startup, start it and verify database access and a fresh heartbeat. Closing the GUI will not stop collection.\n\nCancelling after this step retains the installation for safe resumption.")); break;
            case 6:
                username = Ui.Field(page, "First local Administrator username"); password = Ui.Field(page, "Local password (14+ characters; not a Microsoft password)", true); confirm = Ui.Field(page, "Confirm local password", true); break;
            case 7: page.Controls.Add(Ui.Text("Verify the SQLite schema, local Administrator, service Running state and current-version heartbeat.")); break;
            case 8: page.Controls.Add(Ui.Text("The platform is ready. Sign in to your local account, then add your first customer. Customer onboarding uses Microsoft-controlled sign-in or guided/manual Entra setup, followed by service-side app-only verification.")); break;
        }
        page.Controls.Add(status);
    }
    private async Task Advance()
    {
        busy = true; next.Enabled = back.Enabled = cancel.Enabled = false;
        try
        {
            status.Text = "Working…";
            switch (step)
            {
                case 2: status.Text = await SetupOperations.CheckAsync(); step++; await Task.Delay(500); Render(); return;
                case 3:
                    dataRoot = new RuntimePaths(data!.Text).Root;
                    var resuming = File.Exists(InstallationState.Locator) && JsonFile.Read<Installation>(InstallationState.Locator).DataRoot.Equals(dataRoot, StringComparison.OrdinalIgnoreCase);
                    if (Directory.Exists(dataRoot) && Directory.EnumerateFileSystemEntries(dataRoot).Any() && !resuming) throw new IOException("Choose an empty dedicated folder. Existing unrelated data must not have its permissions changed.");
                    new RuntimePaths(dataRoot).Validate(SetupOperations.AppRoot); break;
                case 4:
                    WindowsAcl.ProtectDirectory(dataRoot, true); new RuntimePaths(dataRoot).Create();
                    WindowsAcl.ProtectDirectory(Path.GetDirectoryName(InstallationState.Locator)!, false);
                    JsonFile.Write(InstallationState.Locator, new Installation(dataRoot, SetupOperations.AppRoot, false)); break;
                case 5: await SetupOperations.InstallAsync(new(dataRoot, SetupOperations.AppRoot, false)); break;
                case 6:
                    var accounts = new LocalAccounts(new CollectorStore(new RuntimePaths(dataRoot).Database));
                    if (accounts.HasUsers) { if (accounts.Login(username!.Text, password!.Text)?.Role != LocalRole.Administrator) throw new UnauthorizedAccessException("Setup already has an Administrator. Enter that local account to resume."); }
                    else { if (password!.Text != confirm!.Text) throw new ArgumentException("Passwords do not match."); accounts.CreateFirst(username!.Text, password.Text); }
                    password!.Clear(); confirm!.Clear(); break;
                case 7:
                    var paths = new RuntimePaths(dataRoot); var store = new CollectorStore(paths.Database); store.Verify(); if (!new LocalAccounts(store).HasUsers) throw new InvalidOperationException("Create the local Administrator first.");
                    await new WindowsServiceControl().VerifyAsync(paths, Product.Version, DateTimeOffset.UtcNow.AddSeconds(-15), CancellationToken.None); break;
                case 8: JsonFile.Write(InstallationState.Locator, new Installation(dataRoot, SetupOperations.AppRoot, true)); DialogResult = DialogResult.OK; busy = false; Close(); return;
            }
            step++; Render();
        }
        catch (Exception error) { status.Text = error.Message + "\nCorrect the issue, then select Next to retry."; }
        finally { busy = false; next.Enabled = cancel.Enabled = true; back.Enabled = step > 0 && step < 5; }
    }
}
