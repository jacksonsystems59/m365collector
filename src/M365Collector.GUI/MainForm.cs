using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Modules.TenantIdentity;
using M365Collector.Security;
using M365Collector.Storage;
using M365Collector.Updater;
using System.Diagnostics;
namespace M365Collector.GUI;
internal sealed class MainForm : Form
{
    private readonly Installation installation; private readonly CollectorStore store; private LocalUser user; private readonly RuntimePaths paths;
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly Label banner = new() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(20, 10, 0, 0), BackColor = Color.FromArgb(230, 243, 247) };
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 60_000 };
    private readonly ActivityFilter activity = new(); private bool locking; private DateTimeOffset nextBackgroundCheck;
    public MainForm(Installation installation, CollectorStore store, LocalUser user)
    {
        this.installation = installation; this.store = store; this.user = user; paths = new(installation.DataRoot);
        Ui.Style(this, "M365Collector 0.0.1", 1240, 850);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 225, Padding = new Padding(18, 24, 12, 0), FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Ink };
        navigation.Controls.Add(new Label { Text = "M365Collector\n0.0.1", ForeColor = Color.White, Font = new Font("Segoe UI", 18, FontStyle.Bold), Width = 200, Height = 85 });
        foreach (var (label, action) in new (string, Action)[] { ("Dashboard", Dashboard), ("All Customers", Customers), ("Add Customer", AddCustomer), ("Collection Modules", Modules), ("Audit Explorer", () => Placeholder("Audit Explorer", "Future filters: customer, timestamp, user, workload, action, file, folder, site, IP, location and result.")), ("Reports", () => Placeholder("Reports", "Tenant Identity data is currently available under All Customers and in the customer Data folder.")), ("Administration", Administration), ("Settings · Updates", Updates), ("Logs", Logs), ("Lock application", Lock) })
        {
            var button = Ui.Button(label, action); button.Width = 188; navigation.Controls.Add(button);
        }
        Controls.Add(content); Controls.Add(banner); Controls.Add(navigation);
        banner.Text = $"{user.Name} • {user.Role}   |   Data: {paths.Root}";
        Shown += async (_, _) => { if (store.GetCustomers().Count == 0 && Authorization.Allows(user, Capability.ManageCustomers)) AddCustomer(); else Dashboard(); await CheckNotice(); };
        Application.AddMessageFilter(activity);
        timer.Tick += async (_, _) => { if (DateTimeOffset.UtcNow - activity.Last > TimeSpan.FromMinutes(15)) Lock(); else await CheckNotice(); }; timer.Start();
    }
    private FlowLayoutPanel Page(string title)
    {
        foreach (Control control in content.Controls.Cast<Control>().ToArray()) control.Dispose(); content.Controls.Clear();
        var page = Ui.Stack(); content.Controls.Add(page); page.Controls.Add(Ui.Text(title, heading: true)); return page;
    }
    private void Placeholder(string title, string text) { var page = Page(title); page.Controls.Add(Ui.Text("Coming in a future version")); page.Controls.Add(Ui.Text(text)); }
    private void Dashboard()
    {
        var page = Page("Dashboard"); page.Controls.Add(Ui.Text($"{store.GetCustomers().Count} connected customers\nTenant Identity is the only active collector in 0.0.1."));
        try { var heartbeat = JsonFile.Read<Heartbeat>(paths.Heartbeat); page.Controls.Add(Ui.Text($"Service heartbeat: {heartbeat.Time.LocalDateTime:g}\nVersion: {heartbeat.Version}\nHealth: {(heartbeat.Time > DateTimeOffset.UtcNow.AddSeconds(-30) ? "Recent heartbeat" : "Stale — check the Windows Service")}")); } catch (IOException) { page.Controls.Add(Ui.Text("Service heartbeat unavailable. Check M365CollectorService.")); }
        var last = Path.Combine(paths.Root, "Config", "last-update.json"); if (File.Exists(last)) page.Controls.Add(Ui.Text("Last update transaction:\n" + File.ReadAllText(last)));
        page.Controls.Add(Ui.Button("Refresh", Dashboard));
    }
    private void Customers()
    {
        var page = Page("All Customers"); var status = Ui.Text("");
        foreach (var customer in store.GetCustomers())
        {
            page.Controls.Add(Ui.Text(customer.Name, heading: true));
            page.Controls.Add(Ui.Text($"{customer.Identity?.DisplayName}\nTenant: {customer.TenantId}\nCollected: {customer.Identity?.CollectedAt.LocalDateTime:g}\nDomains: {string.Join(", ", customer.Identity?.Domains.Select(d => d.Name + (d.IsDefault ? " (default)" : "") + (d.IsInitial ? " (initial)" : "")) ?? [])}"));
            if (Authorization.Allows(user, Capability.Collect)) page.Controls.Add(Ui.AsyncButton("Collect Tenant Identity", async () =>
            {
                Authorization.Require(user, Capability.Collect); var id = store.RequestConnection(customer); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (true) { var request = store.Requests().Single(r => r.Id == id); if (request.State == "Failed") throw new InvalidOperationException(request.Error); if (request.State == "Verified") { store.SaveCustomer(request.Customer); status.Text = "Collection complete. Refresh to view."; return; } await Task.Delay(1000, timeout.Token); }
            }, status));
        }
        page.Controls.Add(Ui.Button("Refresh", Customers)); page.Controls.Add(status);
    }
    private void AddCustomer()
    {
        if (!Authorization.Allows(user, Capability.ManageCustomers)) { Placeholder("Add Customer", "An application Administrator must onboard customers."); return; }
        Page("Add Customer"); content.Controls.Clear(); content.Controls.Add(new CustomerPage(store, user, paths));
    }
    private void Modules()
    {
        var page = Page("Collection Modules"); page.Controls.Add(Ui.Text("Tenant Identity • Enabled • v0.0.1\nAPI: Microsoft Graph v1.0\nApplication permission: Organization.Read.All\nWhy required? Read tenant name and verified domains.\nPowerShell dependencies: none\nRuns hourly inside the service."));
        foreach (var customer in store.GetCustomers())
        {
            var check = new CheckBox { Text = customer.Name + " — Tenant Identity", AutoSize = true, Checked = store.Read("SELECT Enabled FROM ModuleConfiguration WHERE TenantId=$id", r => r.GetBoolean(0), ("$id", customer.TenantId.ToString())).SingleOrDefault(), Enabled = Authorization.Allows(user, Capability.Collect) };
            check.CheckedChanged += (_, _) => { Authorization.Require(user, Capability.Collect); store.Write("UPDATE ModuleConfiguration SET Enabled=$value WHERE TenantId=$id AND ModuleId='tenant-identity'", ("$value", check.Checked), ("$id", customer.TenantId.ToString())); }; page.Controls.Add(check);
        }
        foreach (var module in TenantIdentityModule.FutureModules) page.Controls.Add(Ui.Text(module + " — Coming later"));
    }
    private void Administration()
    {
        var page = Page("Administration"); if (!Authorization.Allows(user, Capability.ManageUsers)) { page.Controls.Add(Ui.Text("Administrator role required.")); return; }
        var status = Ui.Text(""); page.Controls.Add(Ui.Text("Microsoft bootstrap sign-in configuration"));
        page.Controls.Add(Ui.Text("Supply your organisation's multitenant public-client application ID, configured for WAM and/or a system browser. No client secret is used. Delegated scopes: Application.ReadWrite.All, AppRoleAssignment.ReadWrite.All and Organization.Read.All. See docs/ENTRA-ONBOARDING.md."));
        var client = Ui.Field(page, "Bootstrap public-client ID", value: store.Setting("BootstrapClientId") ?? "");
        page.Controls.Add(Ui.AsyncButton("Save bootstrap configuration", () => { Authorization.Require(user, Capability.ManageUsers); store.Setting("BootstrapClientId", Guid.Parse(client.Text).ToString()); status.Text = "Bootstrap configuration saved."; return Task.CompletedTask; }, status));
        page.Controls.Add(Ui.Text("Create local account")); var name = Ui.Field(page, "Username"); var password = Ui.Field(page, "Local password", true);
        var roles = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, DataSource = Enum.GetValues<LocalRole>() }; page.Controls.Add(roles);
        page.Controls.Add(Ui.AsyncButton("Create account", () => { new LocalAccounts(store).Add(user, name.Text, password.Text, (LocalRole)roles.SelectedItem!); password.Clear(); status.Text = "Local account created."; return Task.CompletedTask; }, status));
        page.Controls.Add(Ui.Text("Local roles control actions inside this application. Windows administrators remain trusted machine administrators. This release's GUI requires elevation.")); page.Controls.Add(status);
    }
    private ReleaseClient ReleaseClient() => new(http, Path.Combine(paths.Root, "Cache", "release-check.json"));
    private async Task CheckNotice()
    {
        if (DateTimeOffset.UtcNow < nextBackgroundCheck) return;
        nextBackgroundCheck = DateTimeOffset.UtcNow.AddMinutes(15);
        try { var state = await ReleaseClient().CheckAsync(false, CancellationToken.None); nextBackgroundCheck = DateTimeOffset.UtcNow.AddHours(6); if (Core.ReleaseClient.IsUpdate(Product.Version, state.Release)) banner.Text = $"M365Collector {state.Release!.Version} is available. Open Settings · Updates to review and install."; }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException or TaskCanceledException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException) { /* Explicit checks show errors; background checks retry after 15 minutes. */ }
    }
    private void Updates()
    {
        var page = Page("Settings · Updates"); var status = Ui.Text("");
        page.Controls.Add(Ui.Text($"Current Version: {Product.Version}\nUpdate Channel: Stable\nUpdates require an explicit Download and Install action."));
        page.Controls.Add(Ui.AsyncButton("Check for Updates", async () =>
        {
            paths.Log("Updates", "GitHub update check requested");
            var state = await ReleaseClient().CheckAsync(true, CancellationToken.None);
            paths.Log("Updates", "GitHub release detected: " + (state.Release?.Version ?? "none"));
            status.Text = $"Last checked: {state.Checked.LocalDateTime:g}\nLatest version: {state.Release?.Version ?? "No releases yet"}";
            if (state.Release == null) return;
            page.Controls.Add(Ui.Text($"Released: {state.Release.Published.LocalDateTime:g}\nWhat's new:\n{state.Release.Notes}"));
            page.Controls.Add(Ui.Button("View Full Release Notes", () => Ui.Open(state.Release.Page)));
            if (Core.ReleaseClient.IsUpdate(Product.Version, state.Release))
            {
                if (Authorization.Allows(user, Capability.InstallUpdates)) page.Controls.Add(Ui.AsyncButton("Download and Install", () => InstallUpdate(state.Release, status), status));
                else page.Controls.Add(Ui.Text("An application Administrator must install this update."));
                page.Controls.Add(Ui.Button("Later", Dashboard));
            }
            else page.Controls.Add(Ui.Text("You are up to date."));
        }, status));
        var updates = Path.Combine(Path.GetDirectoryName(InstallationState.Locator)!, "Updates");
        if (Directory.Exists(updates) && Authorization.Allows(user, Capability.InstallUpdates))
        {
            foreach (var journal in Directory.GetFiles(updates, "journal.json", SearchOption.AllDirectories))
            {
                var entry = JsonFile.Read<UpdateJournal>(journal);
                if (entry.State is "Preparing" or "Replacing" or "Migrating" or "Verifying" or "RollingBack")
                    page.Controls.Add(Ui.AsyncButton("Recover interrupted update " + entry.TargetVersion, () => { Authorization.Require(user, Capability.InstallUpdates); var requestPath = Path.Combine(Path.GetDirectoryName(journal)!, "request.json"); var request = JsonFile.Read<UpdateRequest>(requestPath); JsonFile.Write(requestPath, request with { GuiProcessId = Environment.ProcessId }); LaunchUpdater(requestPath); return Task.CompletedTask; }, status));
            }
        }
        page.Controls.Add(status);
    }
    private async Task InstallUpdate(ReleaseInfo release, Label status)
    {
        Authorization.Require(user, Capability.InstallUpdates);
        var work = Path.Combine(Path.GetDirectoryName(InstallationState.Locator)!, "Updates", Guid.NewGuid().ToString("N")); WindowsAcl.ProtectDirectory(work, false);
        var zip = Path.Combine(work, $"M365Collector-{release.Version}-win-x64.zip"); var checksum = zip + ".sha256";
        try
        {
            status.Text = "Downloading release package…";
            await UpdatePackage.DownloadAsync(http, release.ZipUrl, zip, 1024L * 1024 * 1024, CancellationToken.None);
            await UpdatePackage.DownloadAsync(http, release.ChecksumUrl, checksum, 4096, CancellationToken.None); paths.Log("Updates", "Downloaded " + release.Version);
            UpdatePackage.VerifyChecksum(zip, checksum); paths.Log("Updates", "SHA-256 verified " + release.Version);
            UpdatePackage.CopyTree(Path.Combine(installation.AppRoot, "Updater"), Path.Combine(work, "helper"));
            var requestPath = Path.Combine(work, "request.json"); JsonFile.Write(requestPath, new UpdateRequest(zip, checksum, release.Version, Product.Version, Environment.ProcessId));
            LaunchUpdater(requestPath);
        }
        catch (Exception error) { paths.Log("Updates", "Download/staging failed: " + error.GetType().Name); if (File.Exists(zip)) File.Delete(zip); if (File.Exists(checksum)) File.Delete(checksum); throw; }
    }
    private void LaunchUpdater(string requestPath)
    {
        var info = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(requestPath)!, "helper", "M365Collector.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true }; info.ArgumentList.Add(requestPath); Process.Start(info); Close();
    }
    private void Logs()
    {
        var page = Page("Logs"); page.Controls.Add(Ui.Text("Service and update logs contain operation status, never Microsoft tokens or passwords."));
        var text = new TextBox { Width = 800, Height = 450, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both };
        var logs = Directory.GetFiles(Path.Combine(paths.Root, "Logs"), "*.log", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(5);
        text.Text = string.Join(Environment.NewLine, logs.SelectMany(f => File.ReadLines(f).TakeLast(100))); page.Controls.Add(text);
    }
    private void Lock()
    {
        if (locking) return; locking = true; timer.Stop(); content.Visible = false;
        using var login = new LoginForm(store);
        if (login.ShowDialog(this) != DialogResult.OK || login.User == null) { Close(); return; }
        user = login.User; activity.Last = DateTimeOffset.UtcNow; content.Visible = true; locking = false; Dashboard(); timer.Start();
    }
    protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); http.Dispose(); Application.RemoveMessageFilter(activity); } base.Dispose(disposing); }
    private sealed class ActivityFilter : IMessageFilter
    {
        public DateTimeOffset Last = DateTimeOffset.UtcNow;
        public bool PreFilterMessage(ref Message m) { if (m.Msg is 0x100 or 0x201 or 0x200) Last = DateTimeOffset.UtcNow; return false; }
    }
}
