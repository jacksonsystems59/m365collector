using System.Diagnostics;

namespace M365Collector.GUI;

public static class Ui
{
    public static readonly Color Navy = Color.FromArgb(20, 37, 57);
    public static readonly Color Teal = Color.FromArgb(0, 112, 118);
    public static readonly Color Muted = Color.FromArgb(83, 100, 118);
    public static readonly Color Canvas = Color.FromArgb(242, 246, 250);
    public static Label Text(string text, int size = 11, bool bold = false) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(840, 0), ForeColor = size >= 18 ? Navy : Muted,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 0, 0, 14)
    };
    public static Button Button(string text, EventHandler? handler = null, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(120, 38), Padding = new Padding(12, 5, 12, 5),
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Teal : Color.White, ForeColor = primary ? Color.White : Navy, Margin = new Padding(0, 0, 10, 10), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = primary ? Teal : Color.FromArgb(201, 213, 224);
        if (handler != null) button.Click += handler;
        return button;
    }
    public static FlowLayoutPanel Page(string title, string subtitle)
    {
        var page = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(30), BackColor = Canvas };
        page.Controls.Add(Text(title, 24, true)); page.Controls.Add(Text(subtitle)); return page;
    }
    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(880, 0), Margin = new Padding(0, 0, 0, 10) };
        row.Controls.AddRange(controls); return row;
    }
    public static TextBox Field(FlowLayoutPanel page, string label, string value = "", bool readOnly = false)
    {
        page.Controls.Add(Text(label, 10, true));
        var box = new TextBox { Text = value, Width = 680, ReadOnly = readOnly, Margin = new Padding(0, 0, 0, 16), Font = new Font("Segoe UI", 11) };
        page.Controls.Add(box); return box;
    }
    public static CheckBox Check(string text, bool value = false) => new() { Text = text, AutoSize = true, Checked = value, MaximumSize = new Size(820, 0), Margin = new Padding(0, 0, 0, 15) };
    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
