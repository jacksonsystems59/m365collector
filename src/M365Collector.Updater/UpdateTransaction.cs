using M365Collector.Core;
using M365Collector.Storage;
namespace M365Collector.Updater;
public sealed record UpdateRequest(string Zip, string Checksum, string TargetVersion, string PreviousVersion, int GuiProcessId);
public sealed record UpdateJournal(string State, string TargetVersion, string PreviousVersion);
public sealed class UpdateTransaction(Installation installation, string work, IServiceControl service, Func<CancellationToken, Task> migrate)
{
    private readonly RuntimePaths paths = new(installation.DataRoot);
    private string Journal => Path.Combine(work, "journal.json");
    private string OldApp => Path.Combine(work, "previous-binaries");
    private string OldDb => Path.Combine(work, "previous.db");
    private string OldConfig => Path.Combine(work, "previous-config");
    private void Log(string message) => paths.Log("Updates", message);
    private void State(string state, UpdateRequest request) => JsonFile.Write(Journal, new UpdateJournal(state, request.TargetVersion, request.PreviousVersion));
    public async Task ApplyAsync(UpdateRequest request, CancellationToken ct)
    {
        paths.Validate(installation.AppRoot, 0); RuntimePaths.RejectReparse(installation.AppRoot); RuntimePaths.RejectReparse(work);
        if (RuntimePaths.Within(work, installation.AppRoot) || RuntimePaths.Within(installation.AppRoot, work)) throw new IOException("Update working directory overlaps application binaries.");
        if (File.Exists(Journal))
        {
            var previous = JsonFile.Read<UpdateJournal>(Journal);
            if (previous.State is "Replacing" or "Migrating" or "Verifying" or "RollingBack") { await RollbackAsync(request, CancellationToken.None); throw new IOException("Interrupted update rolled back. Download the update again to retry."); }
            if (previous.State == "Preparing") { var since = DateTimeOffset.UtcNow; await service.StartAsync(ct); await service.VerifyAsync(paths, request.PreviousVersion, since, ct); State("Aborted", request); throw new IOException("Interrupted preparation recovered. Download the update again to retry."); }
            throw new IOException("This update transaction was already used. Download again to retry.");
        }
        Log($"Update {request.PreviousVersion} -> {request.TargetVersion}: validating package");
        UpdatePackage.VerifyChecksum(request.Zip, request.Checksum); Log("SHA-256 verified");
        var newApp = Path.Combine(work, "new-binaries");
        UpdatePackage.Extract(request.Zip, newApp, request.TargetVersion); Log("Package structure and file hashes verified");
        var stopped = false; var replacing = false;
        try
        {
            State("Preparing", request);
            await service.StopAsync(ct); stopped = true; Log("Service stopped");
            UpdatePackage.CopyTree(installation.AppRoot, OldApp);
            new CollectorStore(paths.Database).Backup(OldDb);
            UpdatePackage.CopyTree(Path.Combine(paths.Root, "Config"), OldConfig); Log("Binaries, database and configuration backed up");
            State("Replacing", request); replacing = true;
            Directory.Delete(installation.AppRoot, true); UpdatePackage.CopyTree(newApp, installation.AppRoot);
            State("Migrating", request); Log("Running new version database migrations"); await migrate(ct);
            State("Verifying", request); var since = DateTimeOffset.UtcNow; await service.StartAsync(ct); Log("Service started");
            await service.VerifyAsync(paths, request.TargetVersion, since, ct); new CollectorStore(paths.Database).Verify(allowNewerSchema: true);
            new CollectorStore(paths.Database).UpdateHistory(request.TargetVersion, "Succeeded"); State("Complete", request); Log("Update succeeded; heartbeat and database verified");
        }
        catch (Exception error)
        {
            Log("Update failed: " + error.GetType().Name);
            if (replacing) await RollbackAsync(request, CancellationToken.None);
            else if (stopped) { var since = DateTimeOffset.UtcNow; await service.StartAsync(CancellationToken.None); await service.VerifyAsync(paths, request.PreviousVersion, since, CancellationToken.None); State("Aborted", request); }
            throw;
        }
    }
    private async Task RollbackAsync(UpdateRequest request, CancellationToken ct)
    {
        State("RollingBack", request); Log("Rollback started");
        if (!Directory.Exists(OldApp) || !File.Exists(OldDb) || !Directory.Exists(OldConfig)) throw new IOException("Recovery backup is incomplete. Manual recovery is required; backups have been retained.");
        await service.StopAsync(ct);
        if (Directory.Exists(installation.AppRoot)) Directory.Delete(installation.AppRoot, true);
        UpdatePackage.CopyTree(OldApp, installation.AppRoot);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) if (File.Exists(paths.Database + suffix)) File.Delete(paths.Database + suffix);
        File.Copy(OldDb, paths.Database, true);
        var config = Path.Combine(paths.Root, "Config"); if (Directory.Exists(config)) Directory.Delete(config, true); UpdatePackage.CopyTree(OldConfig, config);
        var since = DateTimeOffset.UtcNow; await service.StartAsync(ct); await service.VerifyAsync(paths, request.PreviousVersion, since, ct); new CollectorStore(paths.Database).VerifyIntegrity();
        new CollectorStore(paths.Database).UpdateHistory(request.TargetVersion, "Rolled back"); State("RolledBack", request); Log("Rollback verified; previous version restored");
    }
}
