namespace SterlingMonitor;

internal static class Ui
{
    public static readonly Color Ink = Color.FromArgb(28, 43, 60);
    public static readonly Color Accent = Color.FromArgb(0, 113, 104);
    public static readonly Color Canvas = Color.FromArgb(244, 247, 250);
    public static Button Button(string text, EventHandler handler, bool primary = false)
    {
        var b = new Button { Text = text, UseMnemonic = false, AutoSize = true, Height = 36, MinimumSize = new(85, 36), Padding = new(10, 3, 10, 3), FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, Margin = new(0, 0, 8, 8) };
        b.FlatAppearance.BorderColor = Color.FromArgb(210, 220, 228);
        b.Click += handler;
        return b;
    }
    public static Label Label(string text, float size = 10, bool bold = false) => new() { Text = text, UseMnemonic = false, AutoSize = true, ForeColor = Ink, Font = new("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new(0, 4, 0, 8), MaximumSize = new(850, 0) };
    public static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(24), BackColor = Canvas };
    public static FlowLayoutPanel Bar() => new() { AutoSize = true, WrapContents = true, Margin = new(0, 12, 0, 8) };
    public static TableLayoutPanel Fields() => new() { AutoSize = true, ColumnCount = 2, Padding = new(0, 8, 0, 0), Margin = new(0), BackColor = Canvas };
    public static void Field(TableLayoutPanel table, string caption, Control control)
    {
        int row = table.RowCount++;
        table.Controls.Add(new Label { Text = caption, Width = 205, AutoSize = false, Height = 34, TextAlign = ContentAlignment.MiddleLeft, Margin = new(0, 2, 12, 5) }, 0, row);
        control.Margin = new(0, 4, 0, 5);
        table.Controls.Add(control, 1, row);
    }
    public static NumericUpDown Number(int min, int max, int value) => new() { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 130 };
    public static TextBox Text(string value = "", int width = 390) => new() { Text = value, Width = width };
    public static ComboBox Choice<T>(T value) where T : struct, Enum
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        combo.Items.AddRange(Enum.GetValues<T>().Cast<object>().ToArray());
        combo.SelectedItem = value;
        return combo;
    }
    public static void Error(IWin32Window owner, Exception ex) => MessageBox.Show(owner, ex.Message, "SterlingMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}

internal sealed class ServicePicker : Form
{
    private readonly CheckedListBox list = new() { Dock = DockStyle.Fill, CheckOnClick = true, DisplayMember = "DisplayName", IntegralHeight = false };
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ServiceInfo> services;
    public List<ServiceInfo> Selected => services.Where(s => selected.Contains(s.Name)).ToList();
    public ServicePicker(List<ServiceInfo> available)
    {
        services = available;
        Text = "Add services · SterlingMonitor"; Size = new(650, 620); MinimumSize = new(500, 420); StartPosition = FormStartPosition.CenterParent; Font = new("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new(20) };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var search = Ui.Text("", 560); search.Dock = DockStyle.Top; search.PlaceholderText = "Filter by display name or service name…";
        layout.Controls.Add(Ui.Label("Choose one or more Windows services", 15, true)); layout.Controls.Add(search); layout.Controls.Add(list);
        var bar = Ui.Bar();
        bar.Controls.Add(Ui.Button("Add selected", (_, _) => { if (Selected.Count > 0) DialogResult = DialogResult.OK; }, true));
        bar.Controls.Add(Ui.Button("Cancel", (_, _) => DialogResult = DialogResult.Cancel)); layout.Controls.Add(bar); Controls.Add(layout);
        void Populate()
        {
            list.Items.Clear();
            foreach (var s in services.Where(s => s.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || s.DisplayName.Contains(search.Text, StringComparison.OrdinalIgnoreCase))) list.Items.Add(s, selected.Contains(s.Name));
        }
        list.ItemCheck += (_, e) => { var s = (ServiceInfo)list.Items[e.Index]; if (e.NewValue == CheckState.Checked) selected.Add(s.Name); else selected.Remove(s.Name); };
        search.TextChanged += (_, _) => Populate(); Populate();
    }
}

internal sealed class PolicyDialog : Form
{
    public ServicePolicy Policy { get; }
    public PolicyDialog(ServicePolicy source)
    {
        Policy = System.Text.Json.JsonSerializer.Deserialize<ServicePolicy>(System.Text.Json.JsonSerializer.Serialize(source))!;
        Text = "Service policy · SterlingMonitor"; Size = new(750, 760); MinimumSize = new(670, 600); StartPosition = FormStartPosition.CenterParent; Font = new("Segoe UI", 10);
        var flow = Ui.Flow(); Controls.Add(flow);
        flow.Controls.Add(Ui.Label(source.DisplayName, 18, true)); flow.Controls.Add(Ui.Label(source.Name));
        var fields = Ui.Fields(); flow.Controls.Add(fields);
        var enabled = new CheckBox { Checked = source.Enabled, Text = "Monitor this service", AutoSize = true };
        var level = Ui.Choice(source.Level); var grace = Ui.Number(0, 3600, source.FailureSeconds); var repeat = Ui.Number(1, 1440, source.RepeatMinutes);
        var recovery = new CheckBox { Checked = source.EmailRecovery, Text = "Send recovery email", AutoSize = true };
        var restart = new CheckBox { Checked = source.AutoRestart, Text = "Attempt automatic recovery", AutoSize = true };
        var attempts = Ui.Number(1, 10, source.MaxRestarts); var cooldown = Ui.Number(1, 1440, source.RestartCooldownMinutes);
        var heartbeat = Ui.Text(source.HeartbeatPath); var age = Ui.Number(10, 86400, source.HeartbeatMaxAgeSeconds);
        Ui.Field(fields, "Monitoring", enabled); Ui.Field(fields, "Alert level", level); Ui.Field(fields, "Failure delay (seconds)", grace); Ui.Field(fields, "Repeat email (minutes)", repeat); Ui.Field(fields, "Recovery notification", recovery);
        Ui.Field(fields, "Automatic recovery", restart); Ui.Field(fields, "Maximum attempts", attempts); Ui.Field(fields, "Retry cooldown (minutes)", cooldown); Ui.Field(fields, "Heartbeat file (optional)", heartbeat); Ui.Field(fields, "Heartbeat max age (sec)", age);
        var note = Ui.Label("LogOnly records incidents without email. Warning and Critical label emails and tray alerts. Automatic recovery is independent of alert level.\n\nHeartbeat: the service must regularly update a local file. Without one, monitoring checks Windows service status only. Restart attempts reset after 5 minutes of continuous health, or when the app restarts."); note.MaximumSize = new(640, 0); flow.Controls.Add(note);
        var bar = Ui.Bar(); flow.Controls.Add(bar);
        bar.Controls.Add(Ui.Button("Save policy", (_, _) => {
            try
            {
                Policy.Enabled = enabled.Checked; Policy.Level = (AlertLevel)level.SelectedItem!; Policy.FailureSeconds = (int)grace.Value; Policy.RepeatMinutes = (int)repeat.Value;
                Policy.EmailRecovery = recovery.Checked; Policy.AutoRestart = restart.Checked; Policy.MaxRestarts = (int)attempts.Value; Policy.RestartCooldownMinutes = (int)cooldown.Value;
                Policy.HeartbeatPath = heartbeat.Text.Trim(); Policy.HeartbeatMaxAgeSeconds = (int)age.Value; Policy.Validate(); DialogResult = DialogResult.OK;
            } catch (Exception ex) { Ui.Error(this, ex); }
        }, true));
        bar.Controls.Add(Ui.Button("Cancel", (_, _) => DialogResult = DialogResult.Cancel));
    }
}
