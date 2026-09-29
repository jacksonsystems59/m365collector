using M365Collector.Core;
using M365Collector.Storage;
namespace M365Collector.GUI;
internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (Mutex.TryOpenExisting(@"Global\M365Collector.Update", out var update)) { update.Dispose(); MessageBox.Show("An update or recovery is in progress. Reopen M365Collector when it finishes."); return; }
            using var single = new Mutex(true, @"Global\M365Collector.GUI", out var owns); if (!owns) return;
            if (InstallationState.IsFirstRun(InstallationState.Locator))
            {
                using var wizard = new FirstRunWizard(); if (wizard.ShowDialog() != DialogResult.OK) return;
            }
            var installation = JsonFile.Read<Installation>(InstallationState.Locator);
            var store = new CollectorStore(new RuntimePaths(installation.DataRoot).Database); store.Verify();
            using var login = new LoginForm(store); if (login.ShowDialog() != DialogResult.OK || login.User == null) return;
            Application.Run(new MainForm(installation, store, login.User));
        }
        catch (Exception error) { MessageBox.Show(error.Message, "M365Collector could not start", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
