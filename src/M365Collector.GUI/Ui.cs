using M365Collector.Contracts;
using M365Collector.Security;
using M365Collector.Storage;
namespace M365Collector.GUI;
internal static class Ui
{
    public static readonly Color Ink = Color.FromArgb(27, 42, 63), Accent = Color.FromArgb(0, 103, 145);
    public static void Style(Form form, string title, int width = 1060, int height = 760)
    {
        form.Text = title; form.Font = new Font("Segoe UI", 10); form.BackColor = Color.White; form.ForeColor = Ink; form.Size = new Size(width, height); form.MinimumSize = new Size(760, 600); form.StartPosition = FormStartPosition.CenterScreen; form.AutoScaleMode = AutoScaleMode.Dpi;
    }
    public static FlowLayoutPanel Stack() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(24) };
    public static Label Text(string text, int width = 700, bool heading = false) => new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, 0, 0, 14), Font = new Font("Segoe UI", heading ? 21 : 10, heading ? FontStyle.Bold : FontStyle.Regular) };
    public static TextBox Field(FlowLayoutPanel panel, string label, bool password = false, string value = "")
    {
        panel.Controls.Add(Text(label)); var input = new TextBox { Width = 560, UseSystemPasswordChar = password, Text = value, Margin = new Padding(0, 0, 0, 18) }; panel.Controls.Add(input); return input;
    }
    public static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(130, 38), FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 12, 12) }; button.Click += (_, _) => action(); return button;
    }
    public static Button AsyncButton(string text, Func<Task> action, Label status)
    {
        var button = Button(text, () => { }); button.Click += async (_, _) => { button.Enabled = false; try { status.Text = "Working…"; await action(); } catch (Exception e) { status.Text = e.Message; } finally { if (!button.IsDisposed) button.Enabled = true; } }; return button;
    }
    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !(uri.Host == "entra.microsoft.com" || uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/jacksonsystems59/m365collector/releases/tag/", StringComparison.Ordinal))) throw new InvalidDataException("Only the expected Entra and GitHub release pages may be opened.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
internal sealed class LoginForm : Form
{
    public LocalUser? User { get; private set; }
    public LoginForm(CollectorStore store)
    {
        Ui.Style(this, "M365Collector • Local sign-in", 780, 600); var panel = Ui.Stack(); Controls.Add(panel);
        panel.Controls.Add(Ui.Text("Welcome to M365Collector", heading: true)); panel.Controls.Add(Ui.Text("Sign in with your local M365Collector account. This is separate from Microsoft 365 authentication."));
        var name = Ui.Field(panel, "Local username"); var password = Ui.Field(panel, "Local password", true); var status = Ui.Text("");
        var login = Ui.AsyncButton("Sign in", async () => { User = await Task.Run(() => new LocalAccounts(store).Login(name.Text, password.Text)); password.Clear(); if (User == null) { status.Text = "Sign-in failed or account is temporarily locked. After five failures, wait 15 minutes."; return; } DialogResult = DialogResult.OK; Close(); }, status);
        panel.Controls.Add(login); panel.Controls.Add(status); AcceptButton = login;
    }
}
