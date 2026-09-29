using System.Diagnostics;
using M365Collector.Core;
using M365Collector.Security;
using M365Collector.Updater;

if (!WindowsAcl.Elevated || args.Length != 1) return 2;
using var mutex = new Mutex(true, @"Global\M365Collector.Update", out var owned);
if (!owned) return 3;
var mutexReleased = false;
void ReleaseUpdateLock() { if (!mutexReleased) { mutex.ReleaseMutex(); mutex.Dispose(); mutexReleased = true; } }
RuntimePaths? paths = null;
try
{
    var installation = JsonFile.Read<Installation>(InstallationState.Locator); paths = new(installation.DataRoot);
    var requestPath = Path.GetFullPath(args[0]); var work = Path.GetDirectoryName(requestPath)!;
    var allowed = Path.Combine(Path.GetDirectoryName(InstallationState.Locator)!, "Updates");
    if (!RuntimePaths.Within(work, allowed) || string.Equals(work, allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid update request location.");
    RuntimePaths.RejectReparse(work);
    var request = JsonFile.Read<UpdateRequest>(requestPath);
    if (!RuntimePaths.Within(request.Zip, work) || !RuntimePaths.Within(request.Checksum, work)) throw new IOException("Package is outside secure staging.");
    if (SemanticVersion.Parse(request.TargetVersion).CompareTo(SemanticVersion.Parse(request.PreviousVersion)) <= 0) throw new IOException("Updates must increase the version.");
    try
    {
        using var gui = Process.GetProcessById(request.GuiProcessId);
        if (gui.ProcessName == "M365Collector.GUI") { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); await gui.WaitForExitAsync(timeout.Token); }
    }
    catch (ArgumentException) { }
    var transaction = new UpdateTransaction(installation, work, new WindowsServiceControl(), async ct => { await ProcessRunner.RunAsync(Path.Combine(installation.AppRoot, "Service", "M365Collector.Service.exe"), ["--migrate"], ct); });
    try { await transaction.ApplyAsync(request, CancellationToken.None); }
    finally { JsonFile.Write(Path.Combine(paths.Root, "Config", "last-update.json"), new { Time = DateTimeOffset.UtcNow, Work = work, Journal = File.Exists(Path.Combine(work, "journal.json")) ? JsonFile.Read<UpdateJournal>(Path.Combine(work, "journal.json")) : null }); }
    ReleaseUpdateLock();
    Process.Start(new ProcessStartInfo(Path.Combine(installation.AppRoot, "GUI", "M365Collector.GUI.exe")) { UseShellExecute = true }); return 0;
}
catch (Exception error)
{
    paths?.Log("Updates", "Updater stopped: " + error.Message);
    Console.Error.WriteLine(error.Message);
    if (paths != null)
    {
        var installation = JsonFile.Read<Installation>(InstallationState.Locator);
        ReleaseUpdateLock();
        if (File.Exists(Path.Combine(installation.AppRoot, "GUI", "M365Collector.GUI.exe"))) Process.Start(new ProcessStartInfo(Path.Combine(installation.AppRoot, "GUI", "M365Collector.GUI.exe")) { UseShellExecute = true });
    }
    return 1;
}
