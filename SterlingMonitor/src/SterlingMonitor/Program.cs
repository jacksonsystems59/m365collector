namespace SterlingMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\SterlingMonitor.Tray", out var first);
        if (!first) { MessageBox.Show("SterlingMonitor is already running. Open it from the system tray.", "SterlingMonitor"); return; }
        ApplicationConfiguration.Initialize();
        var store = new SettingsStore(SettingsStore.DefaultDirectory);
        MonitorSettings settings;
        try { settings = store.Load(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Settings could not be loaded ({ex.GetType().Name}).\n\nYour file has not been overwritten. Repair or rename:\n{store.FilePath}\n\nSterlingMonitor will exit to avoid monitoring with incorrect settings.", "SterlingMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var log = new ActivityLog(store.DirectoryPath);
        Application.ThreadException += (_, e) => { log.Write($"UI ERROR: {e.Exception.GetType().Name}."); MessageBox.Show("An operation failed. See Activity for details.", "SterlingMonitor"); };
        Application.Run(new MainForm(store, settings, log, args.Contains("--minimized")));
    }
}
