using System.Diagnostics;
using System.Reflection;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.GUI;
using M365Collector.Security;
using M365Collector.Storage;
namespace M365Collector.Launcher;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Credential-free packaging smoke check; never reads installation/customer data.
        if (args is ["--verify-bundle", var directory])
        {
            try { ExtractBundle(directory); Environment.ExitCode = 0; }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (!WindowsAcl.Elevated) { MessageBox.Show("Run M365Collector as a Windows administrator."); return; }
        Application.Run(new LauncherForm());
    }
    internal static string ExtractBundle(string work)
    {
        if (Directory.Exists(work)) throw new IOException("Bundle destination must be a new directory.");
        RuntimePaths.RejectReparse(work); Directory.CreateDirectory(work);
        var zip = Path.Combine(work, $"M365Collector-{Product.Version}-win-x64.zip");
        foreach (var (resource, target) in new[] { ("Bundle.zip", zip), ("Bundle.sha256", zip + ".sha256") })
        {
            using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource) ?? throw new IOException("This development build does not contain the release payload. Use the published executable.");
            using var output = File.Create(target); input.CopyTo(output);
        }
        UpdatePackage.VerifyChecksum(zip, zip + ".sha256");
        UpdatePackage.Extract(zip, Path.Combine(work, "payload"), Product.Version); return zip;
    }
}

internal sealed class LauncherForm : Form
{
    private bool busy;
    public LauncherForm()
    {
        Ui.Style(this, "M365Collector " + Product.Version, 880, 650);
        var page = Ui.Stack(); Controls.Add(page); page.Controls.Add(Ui.Text("M365Collector " + Product.Version, heading: true));
        page.Controls.Add(Ui.Text("One self-contained download: application, background service and updater. Installation stores components under Program Files and keeps customer data separate. Existing installations are backed up before upgrading."));
        var status = Ui.Text("");
        var install = Ui.AsyncButton("Install / upgrade & open", async () =>
        {
            busy = true; try { await Install(status); } finally { busy = false; }
        }, status); page.Controls.Add(install);
        page.Controls.Add(Ui.AsyncButton("Reset local password", () =>
        {
            if (busy) throw new InvalidOperationException("Wait for installation to finish.");
            if (Mutex.TryOpenExisting(@"Global\M365Collector.Update", out var update)) { update.Dispose(); throw new IOException("Wait for the active update to finish before recovery."); }
            if (!File.Exists(InstallationState.Locator)) throw new IOException("No installation found. Install M365Collector first.");
            var installation = JsonFile.Read<Installation>(InstallationState.Locator);
            var store = new CollectorStore(new RuntimePaths(installation.DataRoot).Database); store.VerifyIntegrity();
            using var recovery = new RecoveryForm(store); recovery.ShowDialog(this); return Task.CompletedTask;
        }, status));
        page.Controls.Add(Ui.Text("Forgotten your application login? Reset local password lists your existing usernames and lets a Windows administrator set a new password. It also works with existing v0.0.1/v0.0.2 installations."));
        page.Controls.Add(status); FormClosing += (_, e) => { if (busy) e.Cancel = true; };
    }
    private async Task Install(Label status)
    {
        if (Mutex.TryOpenExisting(@"Global\M365Collector.Update", out var update)) { update.Dispose(); throw new IOException("An update is already in progress."); }
        Installation? installation = File.Exists(InstallationState.Locator) ? JsonFile.Read<Installation>(InstallationState.Locator) : null;
        string? previous = null;
        if (installation?.Completed == true)
        {
            var gui = Path.Combine(installation.AppRoot, "GUI", "M365Collector.GUI.exe");
            previous = FileVersionInfo.GetVersionInfo(gui).ProductVersion ?? throw new IOException("Cannot determine the installed version.");
            if (SemanticVersion.Parse(previous).CompareTo(SemanticVersion.Parse(Product.Version)) >= 0)
            { Process.Start(new ProcessStartInfo(gui) { UseShellExecute = true }); status.Text = "Opened the installed application. Use Reset local password if needed."; return; }
        }
        if (Mutex.TryOpenExisting(@"Global\M365Collector.GUI", out var running)) { running.Dispose(); throw new IOException("Close the existing M365Collector application before installing this version."); }
        var parent = Path.Combine(Path.GetDirectoryName(InstallationState.Locator)!, "Updates");
        RuntimePaths.RejectReparse(parent); WindowsAcl.ProtectDirectory(parent, false);
        var work = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        status.Text = "Unpacking and validating the bundled release…";
        var zip = await Task.Run(() => Program.ExtractBundle(work));
        if (installation?.Completed != true)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(work, "payload", "GUI", "M365Collector.GUI.exe")) { UseShellExecute = true });
            status.Text = "First Run Wizard opened. Its protected installation payload is retained for setup resumption."; return;
        }
        // The existing updater retains backups, migrates, verifies health and rolls back on failure.
        UpdatePackage.CopyTree(Path.Combine(work, "payload", "Updater"), Path.Combine(work, "helper"));
        var request = Path.Combine(work, "request.json"); JsonFile.Write(request, new UpdateRequest(zip, zip + ".sha256", Product.Version, previous!, Environment.ProcessId));
        status.Text = "Installing with backup and service verification. Please wait…";
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(work, "helper", "M365Collector.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { request } }) ?? throw new IOException("Could not start updater.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Update did not complete. The updater attempted recovery; see the installed application's update status and protected update logs.");
        status.Text = "Update complete. The installed application has opened. Use Reset local password if needed.";
    }
}
