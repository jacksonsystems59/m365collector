using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Entra;
using M365Collector.Modules.TenantIdentity;
using M365Collector.Security;
using M365Collector.Storage;
using Microsoft.Extensions.Hosting;

namespace M365Collector.Service;

public sealed class CollectorWorker(RuntimePaths paths, RuntimeConfig config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { Environment.ExitCode = 1; throw; }
    }
    private async Task RunAsync(CancellationToken stoppingToken)
    {
        using var lease = new FileStream(Path.Combine(paths.Root, "Service", "runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        paths.RejectReparsePoints();
        foreach (var name in RuntimePaths.Directories)
            if (!Directory.Exists(Path.Combine(paths.Root, name))) throw new DirectoryNotFoundException("A required runtime directory is missing.");
        var db = new CollectorDatabase(paths);
        if (db.SchemaVersion() != ProductInfo.DatabaseSchema) throw new InvalidDataException("Run the explicit migration command before starting this service version.");
        db.RecoverInterruptedJobs();
        var log = new StructuredLog(paths, "service");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        var module = new TenantIdentityModule(new TenantIdentityReader(http, new CertificateStore()));
        var health = new ServiceHealth(1, ProductInfo.Version, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Starting", null);
        log.Write("ServiceStartup", "Success");
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCancellation.IsCancellationRequested)
            {
                var current = health with { HeartbeatAt = DateTimeOffset.UtcNow, Status = "Running" };
                JsonFiles.WriteAtomic(paths.Health, current); db.SaveHealth(current);
                await Task.Delay(TimeSpan.FromSeconds(10), heartbeatCancellation.Token);
            }
        }, heartbeatCancellation.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (heartbeat.IsFaulted) await heartbeat;
                foreach (var customer in db.Customers().Where(c => c.State == CustomerState.Active))
                    if (db.ModuleEnabled(customer.TenantId, module.Manifest.Id) &&
                        DateTimeOffset.UtcNow - (db.LastCollection(customer.TenantId) ?? DateTimeOffset.MinValue) >= TimeSpan.FromMinutes(config.CollectionIntervalMinutes))
                        db.Enqueue(customer.TenantId, "Collect");
                var job = db.Claim();
                if (job is null) { await Task.Delay(1000, stoppingToken); continue; }
                var customerForJob = db.Find(job.TenantId);
                CollectionResult result;
                if (customerForJob is null) result = CollectionResult.Failed("CustomerMissing");
                else if (!db.ModuleEnabled(job.TenantId, module.Manifest.Id)) result = CollectionResult.Failed("ModuleDisabled");
                else if (job.Kind == "Collect" && customerForJob.State != CustomerState.Active) result = CollectionResult.Failed("CustomerNotActive");
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    result = await module.CollectAsync(customerForJob, timeout.Token);
                }
                if (result.Success)
                {
                    paths.CreateCustomer(job.TenantId);
                    JsonFiles.WriteAtomic(Path.Combine(paths.CustomerRoot(job.TenantId), "Data", $"tenant-identity-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{job.Id}.json"),
                        new { schemaVersion = 1, moduleVersion = module.Manifest.Version, identity = result.Identity });
                }
                db.Complete(job, result);
                log.Write(job.Kind == "Validate" ? "TenantValidation" : "ModuleCollection", result.Success ? "Success" : "Failure", job.TenantId, result.Code);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch
        {
            Environment.ExitCode = 1;
            log.Write("ServiceRuntime", "Failure", code: "RuntimeFailure");
            throw;
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
            JsonFiles.WriteAtomic(paths.Health, health with { Status = "Stopped", HeartbeatAt = DateTimeOffset.UtcNow });
            log.Write("ServiceStopped", "Success");
        }
    }
}
