using System.Security.Principal;

namespace SterlingMonitor;

internal sealed class MainForm : Form
{
    private readonly SettingsStore store;
    private MonitorSettings settings;
    private readonly ActivityLog log;
    private readonly MonitorEngine engine;
    private readonly SmtpSender sender = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly Label summary = Ui.Label("Choose services to begin monitoring", 18, true);
    private readonly Label status = Ui.Label("Waiting for first check…");
    private readonly Panel content = new() { Dock = DockStyle.Fill, BackColor = Ui.Canvas };
    private readonly Panel dashboard;
    private DateTimeOffset nextPoll = DateTimeOffset.MinValue;
    private bool polling, quitting, smtpDirty;
    private readonly bool initiallyMinimized;

    public MainForm(SettingsStore store, MonitorSettings settings, ActivityLog log, bool minimized, bool previewMode = false)
    {
        this.store = store; this.settings = settings; this.log = log; initiallyMinimized = minimized || settings.StartMinimized;
        engine = new(new WindowsServices(), sender, log);
        Text = "SterlingMonitor"; Size = new(1180, 800); MinimumSize = new(1020, 680); StartPosition = FormStartPosition.CenterScreen; Font = new("Segoe UI", 10); BackColor = Ui.Canvas;
        Icon = SystemIcons.Shield;
        var sidebar = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 210, BackColor = Ui.Ink, Padding = new(18, 28, 12, 12), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var brand = Ui.Label("STERLING\nMONITOR", 19, true); brand.ForeColor = Color.White; brand.Margin = new(0, 0, 0, 32); sidebar.Controls.Add(brand);
        sidebar.Controls.Add(Nav("Services", () => ShowPage(dashboard!)));
        sidebar.Controls.Add(Nav("SMTP & settings", ShowSettings));
        sidebar.Controls.Add(Nav("Activity", ShowActivity));
        sidebar.Controls.Add(Nav("About & roadmap", ShowAbout));
        var phase = Ui.Label("PHASE 01\nWindows service monitor", 9); phase.ForeColor = Color.FromArgb(174, 197, 210); phase.MaximumSize = new(175, 0); phase.Margin = new(0, 40, 0, 0); sidebar.Controls.Add(phase);
        Controls.Add(content); Controls.Add(sidebar);
        dashboard = BuildDashboard(); content.Controls.Add(dashboard);
        var menu = new ContextMenuStrip(); menu.Items.Add("Open SterlingMonitor", null, (_, _) => Restore()); menu.Items.Add("Check now", null, async (_, _) => await CheckAsync(true)); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Exit monitor", null, (_, _) => ExitMonitor());
        tray = new NotifyIcon { Icon = SystemIcons.Shield, Text = "SterlingMonitor", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Restore();
        engine.IncidentChanged += message => {
            if (message.Level == AlertLevel.LogOnly) return;
            tray.ShowBalloonTip(5000, message.Recovery ? "Service recovered" : $"{message.Level}: service unhealthy", $"{message.ServiceName}: {message.Detail}", message.Recovery ? ToolTipIcon.Info : message.Level == AlertLevel.Critical ? ToolTipIcon.Error : ToolTipIcon.Warning);
        };
        timer.Tick += async (_, _) => await CheckAsync(false);
        Shown += async (_, _) => { if (previewMode) return; if (initiallyMinimized) Hide(); timer.Start(); await CheckAsync(true); };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += (_, e) => { if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); tray.ShowBalloonTip(2000, "SterlingMonitor is still monitoring", "Open the tray menu to return or exit.", ToolTipIcon.Info); } };
        FormClosed += (_, _) => { timer.Stop(); lifetime.Cancel(); tray.Visible = false; tray.Dispose(); timer.Dispose(); log.Write("Monitor exited."); };
        log.Write("SterlingMonitor started. Monitoring is active only while this application is running.");
    }
    private Button Nav(string text, Action action)
    {
        var button = Ui.Button(text, (_, _) => { if (CanNavigate()) action(); }); button.AutoSize = false; button.Width = 174; button.Height = 43; button.BackColor = Ui.Ink; button.ForeColor = Color.White; button.FlatAppearance.BorderSize = 0; button.TextAlign = ContentAlignment.MiddleLeft; return button;
    }
    private bool CanNavigate()
    {
        if (!smtpDirty) return true;
        if (MessageBox.Show(this, "Discard the unsaved settings on this page?", "Unsaved settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        smtpDirty = false; return true;
    }
    private void ShowPage(Control page)
    {
        foreach (Control old in content.Controls.Cast<Control>().ToArray()) { content.Controls.Remove(old); if (old != dashboard) old.Dispose(); }
        page.Dock = DockStyle.Fill; content.Controls.Add(page);
    }
    private Panel BuildDashboard()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new(24) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(summary); layout.Controls.Add(Ui.Label("Local services  /  Continuous checks  /  SMTP notifications"));
        var bar = Ui.Bar(); bar.Controls.Add(Ui.Button("+ Add services", async (_, _) => await AddServicesAsync(), true)); bar.Controls.Add(Ui.Button("Edit policy", (_, _) => EditPolicy())); bar.Controls.Add(Ui.Button("Remove", (_, _) => RemoveService())); bar.Controls.Add(Ui.Button("Check now", async (_, _) => await CheckAsync(true))); layout.Controls.Add(bar);
        foreach (var (property, title, weight) in new[] { ("DisplayName", "SERVICE", 180), ("State", "WINDOWS STATE", 95), ("Health", "HEALTH", 90), ("Level", "ALERT", 65), ("Recovery", "RECOVERY", 95) }) grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title, FillWeight = weight });
        grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 238, 243); grid.ColumnHeadersDefaultCellStyle.ForeColor = Ui.Ink; grid.ColumnHeadersHeight = 38; grid.RowTemplate.Height = 42; grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(215, 239, 235); grid.DefaultCellStyle.SelectionForeColor = Ui.Ink;
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditPolicy(); };
        grid.CellToolTipTextNeeded += (_, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is ServiceView view) e.ToolTipText = view.Detail; };
        layout.Controls.Add(grid);
        var commands = Ui.Bar();
        foreach (var command in Enum.GetValues<ServiceAction>()) commands.Controls.Add(Ui.Button(command.ToString(), async (_, _) => await ManualAsync(command)));
        commands.Controls.Add(Ui.Button("Maintain 30 min", async (_, _) => await MaintenanceAsync(true))); commands.Controls.Add(Ui.Button("Resume", async (_, _) => await MaintenanceAsync(false))); layout.Controls.Add(commands);
        status.MaximumSize = new(880, 0); layout.Controls.Add(status); panel.Controls.Add(layout); return panel;
    }
    private string? SelectedName => (grid.CurrentRow?.DataBoundItem as ServiceView)?.Name;
    private void Persist(MonitorSettings updated) { store.Save(updated); settings = updated; nextPoll = DateTimeOffset.MinValue; }
    private async Task AddServicesAsync()
    {
        try
        {
            var list = await Task.Run(WindowsServices.List);
            using var picker = new ServicePicker(list.Where(s => !settings.Services.Any(p => p.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))).ToList());
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var updated = settings.Copy(); foreach (var service in picker.Selected) updated.Services.Add(new() { Name = service.Name, DisplayName = service.DisplayName });
            Persist(updated); log.Write($"Added {picker.Selected.Count} service(s), automatic recovery off by default."); await CheckAsync(true);
        } catch (Exception ex) { Ui.Error(this, ex); }
    }
    private void EditPolicy()
    {
        if (SelectedName is not { } name) return;
        var updated = settings.Copy(); var index = updated.Services.FindIndex(p => p.Name == name);
        if (index < 0) return;
        using var dialog = new PolicyDialog(updated.Services[index]);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { updated.Services[index] = dialog.Policy; Persist(updated); log.Write($"Policy saved for {name}."); } catch (Exception ex) { Ui.Error(this, ex); }
    }
    private void RemoveService()
    {
        if (SelectedName is not { } name) return;
        try { var updated = settings.Copy(); updated.Services.RemoveAll(p => p.Name == name); Persist(updated); log.Write($"Removed {name} from monitoring."); } catch (Exception ex) { Ui.Error(this, ex); }
    }
    private async Task ManualAsync(ServiceAction action)
    {
        if (SelectedName is not { } name) return;
        if (action != ServiceAction.Start && MessageBox.Show(this, $"{action} {name}? This may interrupt its work. Alerts and automatic recovery will pause for 5 minutes.", "Service control", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { status.Text = $"{action} in progress for {name}…"; await engine.ManualAsync(name, action, lifetime.Token); await CheckAsync(true); }
        catch (Exception ex) { log.Write($"MANUAL FAILED {name}: {ex.GetType().Name}."); MessageBox.Show(this, $"The command failed ({ex.GetType().Name}).\n\nService control requires permission on the service, usually an administrator account. Check service dependencies and its configuration.\n\nThe five-minute maintenance window remains active; use Resume to end it.", "Service control", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private async Task MaintenanceAsync(bool enable)
    {
        if (SelectedName is not { } name) return;
        await engine.SetMaintenanceAsync(name, enable, lifetime.Token); await CheckAsync(true);
    }
    private async Task CheckAsync(bool force)
    {
        if (quitting || polling || (!force && DateTimeOffset.UtcNow < nextPoll)) return;
        polling = true;
        try
        {
            await engine.TickAsync(settings.Copy(), DateTimeOffset.UtcNow, lifetime.Token);
            if (quitting) return;
            var selected = SelectedName; grid.DataSource = engine.Views.ToList();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.DataBoundItem is not ServiceView view) continue;
                row.DefaultCellStyle.ForeColor = view.Health == "Unhealthy" ? Color.Firebrick : view.Health == "Healthy" ? Ui.Accent : Ui.Ink;
                if (view.Name == selected) grid.CurrentCell = row.Cells[0];
            }
            int unhealthy = engine.Views.Count(v => v.Health == "Unhealthy");
            summary.Text = settings.Services.Count == 0 ? "Choose services to begin monitoring" : $"{engine.Views.Count(v => v.Health == "Healthy")} healthy   ·   {unhealthy} need attention   ·   {settings.Services.Count} configured";
            using var identity = WindowsIdentity.GetCurrent(); var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            status.Text = $"Last check {DateTime.Now:T}  ·  Every {settings.PollSeconds}s  ·  Email {(settings.Smtp.Enabled ? "enabled" : "disabled")}  ·  {(admin ? "Administrator" : "Standard permissions")}\nClose or minimize to keep monitoring in the tray. Exit or sign-out stops monitoring. Hover over a service for details.";
            tray.Text = unhealthy > 0 ? $"SterlingMonitor · {unhealthy} unhealthy" : "SterlingMonitor · Monitoring"; tray.Icon = unhealthy > 0 ? SystemIcons.Warning : SystemIcons.Shield;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { log.Write($"MONITOR CHECK FAILED: {ex.GetType().Name}."); status.Text = "Check failed. See Activity."; }
        finally { nextPoll = DateTimeOffset.UtcNow.AddSeconds(settings.PollSeconds); polling = false; }
    }
    private void ShowActivity()
    {
        var page = Ui.Flow(); page.Controls.Add(Ui.Label("Activity", 23, true)); page.Controls.Add(Ui.Label("Newest first · Last 500 events · Rotating local log"));
        var box = new TextBox { Multiline = true, ReadOnly = true, Width = 870, Height = 510, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 9), Text = string.Join(Environment.NewLine, log.Lines.Reverse()) };
        page.Controls.Add(box); page.Controls.Add(Ui.Button("Refresh", (_, _) => box.Text = string.Join(Environment.NewLine, log.Lines.Reverse()))); page.Controls.Add(Ui.Label($"Stored in {store.DirectoryPath}")); ShowPage(page);
    }
    private void ShowSettings()
    {
        var page = Ui.Flow(); page.Controls.Add(Ui.Label("SMTP & settings", 23, true)); page.Controls.Add(Ui.Label("Configure delivery, then send a test email to verify the connection."));
        var fields = Ui.Fields(); page.Controls.Add(fields); var s = settings.Smtp;
        var enabled = new CheckBox { Text = "Enable service alert emails", Checked = s.Enabled, AutoSize = true };
        var host = Ui.Text(s.Host); var port = Ui.Number(1, 65535, s.Port); var tls = Ui.Choice(s.Tls);
        var auth = new CheckBox { Text = "Authenticate with username and password", Checked = s.Authenticate, AutoSize = true };
        var user = Ui.Text(s.Username); var password = Ui.Text(); password.UseSystemPasswordChar = true; password.PlaceholderText = s.ProtectedPassword.Length > 0 ? "Saved securely · leave blank to keep" : "SMTP password or provider app password";
        var clearPassword = new CheckBox { Text = "Remove saved password on save", AutoSize = true };
        var from = Ui.Text(s.From); var to = Ui.Text(s.Recipients); var prefix = Ui.Text(s.SubjectPrefix);
        var poll = Ui.Number(5, 300, settings.PollSeconds); var minimized = new CheckBox { Text = "Start minimized to the tray", Checked = settings.StartMinimized, AutoSize = true };
        Ui.Field(fields, "Email alerts", enabled); Ui.Field(fields, "SMTP hostname", host); Ui.Field(fields, "Port", port); Ui.Field(fields, "TLS mode", tls); Ui.Field(fields, "Authentication", auth); Ui.Field(fields, "Username", user); Ui.Field(fields, "Password", password); Ui.Field(fields, "Stored credential", clearPassword); Ui.Field(fields, "From address", from); Ui.Field(fields, "Recipients (; separated)", to); Ui.Field(fields, "Subject prefix", prefix); Ui.Field(fields, "Poll interval (seconds)", poll); Ui.Field(fields, "Window", minimized);
        var note = Ui.Label("StartTls usually uses port 587; ImplicitTls usually uses 465. TLS and certificate validation are always required. Passwords are encrypted for this Windows account. Use a compatible SMTP provider or relay; Microsoft 365 OAuth is a later phase."); note.MaximumSize = new(810, 0); page.Controls.Add(note);
        MonitorSettings Read()
        {
            var updated = settings.Copy(); updated.PollSeconds = (int)poll.Value; updated.StartMinimized = minimized.Checked;
            updated.Smtp = new() { Enabled = enabled.Checked, Host = host.Text.Trim(), Port = (int)port.Value, Tls = (TlsMode)tls.SelectedItem!, Authenticate = auth.Checked, Username = user.Text.Trim(), ProtectedPassword = clearPassword.Checked ? "" : password.Text.Length > 0 ? SecretStore.Protect(password.Text) : settings.Smtp.ProtectedPassword, From = from.Text.Trim(), Recipients = to.Text.Trim(), SubjectPrefix = prefix.Text.Trim() };
            return updated;
        }
        var result = Ui.Label(""); result.MaximumSize = new(810, 0);
        var bar = Ui.Bar(); page.Controls.Add(bar);
        bar.Controls.Add(Ui.Button("Save settings", (_, _) => { try { var updated = Read(); Persist(updated); smtpDirty = false; password.Clear(); password.PlaceholderText = updated.Smtp.ProtectedPassword.Length > 0 ? "Saved securely · leave blank to keep" : "SMTP password"; clearPassword.Checked = false; smtpDirty = false; result.Text = "Settings saved."; log.Write("Settings saved."); } catch (Exception ex) { Ui.Error(this, ex); } }, true));
        var test = Ui.Button("Send test email", async (button, _) => {
            try
            {
                var draft = Read(); draft.Smtp.Validate(); ((Button)button!).Enabled = false; result.Text = "Connecting securely and sending…";
                await sender.SendTextAsync(draft.Smtp, $"Test email from {Environment.MachineName}", $"SterlingMonitor SMTP test\n\nMachine: {Environment.MachineName}\nUTC: {DateTimeOffset.UtcNow:O}\n\nYour SMTP configuration successfully submitted this message.", lifetime.Token);
                result.Text = "Test accepted by the SMTP server. Check the recipient inbox. Save settings to use this configuration for alerts."; log.Write("SMTP test accepted by server.");
            }
            catch (ArgumentException ex) { result.Text = ex.Message; }
            catch (Exception ex) { result.Text = SmtpSender.FailureHint(ex); log.Write($"SMTP TEST FAILED: {SmtpSender.FailureHint(ex)}"); }
            finally { if (!((Button)button!).IsDisposed) ((Button)button!).Enabled = true; }
        }); bar.Controls.Add(test); page.Controls.Add(result);
        foreach (Control c in fields.Controls)
        {
            if (c is TextBox text) text.TextChanged += (_, _) => smtpDirty = true;
            if (c is CheckBox check) check.CheckedChanged += (_, _) => smtpDirty = true;
            if (c is NumericUpDown number) number.ValueChanged += (_, _) => smtpDirty = true;
            if (c is ComboBox combo) combo.SelectedIndexChanged += (_, _) => smtpDirty = true;
        }
        ShowPage(page); smtpDirty = false;
    }
    private void ShowAbout()
    {
        var page = Ui.Flow(); page.Controls.Add(Ui.Label("SterlingMonitor", 26, true)); page.Controls.Add(Ui.Label("Version 0.1.0 · Phase 1", 13));
        page.Controls.Add(Ui.Label("Service visibility, from the system tray.", 18, true));
        page.Controls.Add(Ui.Label("Monitor local Windows services, receive SMTP alerts and perform controlled recovery. Settings belong to the current Windows account. The tray application must remain running in a signed-in session."));
        page.Controls.Add(Ui.Label("Planned: private GitHub updates", 16, true));
        page.Controls.Add(Ui.Label("The next phase will use a GitHub App installed on the selected repository with read-only Contents permission. Its private key will mint short-lived installation tokens without interactive user login. Updates should use signed release packages, staging, service health checks and rollback. Repository polling and installation are not implemented in this version."));
        page.Controls.Add(Ui.Label("Planned: Microsoft 365 collector", 16, true));
        page.Controls.Add(Ui.Label("The collector will run as a separate Windows service. Collection, scheduling and tenant authentication will work independently of the tray interface. A background monitoring worker can also be added for monitoring across sign-out and reboot.")); ShowPage(page);
    }
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void ExitMonitor()
    {
        if (MessageBox.Show(this, "Exit SterlingMonitor? Service monitoring and email alerts will stop.", "Exit monitor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!CanNavigate()) return; quitting = true; Close();
    }
}
