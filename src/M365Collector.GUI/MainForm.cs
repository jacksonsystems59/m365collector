using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Modules.TenantIdentity;
using M365Collector.Security;
using M365Collector.Storage;
using System.Diagnostics;
namespace M365Collector.GUI;
internal sealed partial class MainForm : Form
{
    private readonly Installation installation; private readonly CollectorStore store; private LocalUser user; private readonly RuntimePaths paths;
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly Label banner = new() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(20, 10, 0, 0), BackColor = Color.FromArgb(230, 243, 247) };
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 60_000 };
    private readonly ActivityFilter activity = new(); private bool locking; private DateTimeOffset nextBackgroundCheck;
    private readonly FlowLayoutPanel updateNotice=new(){Dock=DockStyle.Top,Height=110,Padding=new Padding(12),Visible=false,BackColor=Color.FromArgb(229,244,253)};
    public MainForm(Installation installation, CollectorStore store, LocalUser user)
    {
        this.installation = installation; this.store = store; this.user = user; paths = new(installation.DataRoot);
        Ui.Style(this, "M365Collector " + Product.Version, 1340, 900);
        var sidebar=new Panel{Dock=DockStyle.Left,Width=235,BackColor=Ui.Ink};
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 24, 8, 0), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll=true, BackColor = Ui.Ink };
        navigation.Controls.Add(new Label { Text = "M365Collector", ForeColor = Color.White, Font = new Font("Segoe UI", 17, FontStyle.Bold), Width = 205, Height = 60 });
        foreach (var (label, action) in new (string, Action)[] { ("Dashboard", Dashboard), ("All Customers", Customers), ("Add Customer", AddCustomer), ("Collection Modules", Modules), ("Audit Explorer", () => Explorer()), ("Failed Sign-ins",()=>Explorer(module:"sign-ins",result:"Failed")), ("Location / IP Activity",()=>Explorer(module:"sign-ins",group:"IP / location")), ("Reports", () => Placeholder("Reports", "CSV export is available in Audit Explorer. PDF reporting is coming in a future version.")), ("Administration", Administration), ("Settings · Updates", Updates), ("Logs", Logs) })
        {
            var button = Ui.Button(label, action); button.Width = 188; navigation.Controls.Add(button);
        }
        var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=105,Padding=new Padding(18,10,12,12),FlowDirection=FlowDirection.TopDown,WrapContents=false,BackColor=Ui.Ink};
        var lockButton=Ui.Button("LOCK",Lock);lockButton.Width=188;lockButton.Name="SidebarLock";footer.Controls.Add(lockButton);
        footer.Controls.Add(new Label{Text="Version "+Product.Version,AutoSize=true,ForeColor=Color.LightSteelBlue,Name="SidebarVersion"});
        sidebar.Controls.Add(navigation);sidebar.Controls.Add(footer);
        Controls.Add(content); Controls.Add(updateNotice); Controls.Add(banner); Controls.Add(sidebar);
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
    private void AddCustomer()
    {
        if (!Authorization.Allows(user, Capability.ManageCustomers)) { Placeholder("Add Customer", "An application Administrator must onboard customers."); return; }
        Page("Add Customer"); content.Controls.Clear(); content.Controls.Add(new CustomerPage(store, user, paths));
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
        try { var state = await ReleaseClient().CheckAsync(false, CancellationToken.None); nextBackgroundCheck = DateTimeOffset.UtcNow.AddHours(6); if (Core.ReleaseClient.IsUpdate(Product.Version, state.Release)&&store.Setting("DismissedUpdate")!=state.Release!.Version) ShowUpdateNotice(state.Release!); }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException or TaskCanceledException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException) { /* Explicit checks show errors; background checks retry after 15 minutes. */ }
    }
    private void ShowUpdateNotice(ReleaseInfo release)
    {
        updateNotice.Controls.Clear();var message=Ui.Text($"M365Collector Update Available\nCurrent: {Product.Version} • Available: {release.Version}",350);updateNotice.Controls.Add(message);
        updateNotice.Controls.Add(Ui.Button("View What's New",Updates));
        if(Authorization.Allows(user,Capability.InstallUpdates))updateNotice.Controls.Add(Ui.AsyncButton("Install Update",()=>InstallUpdate(release,message),message));
        updateNotice.Controls.Add(Ui.Button("Later",()=>{store.Setting("DismissedUpdate",release.Version);updateNotice.Visible=false;}));updateNotice.Visible=true;
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
