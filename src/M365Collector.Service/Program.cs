using System.ServiceProcess;
using System.Text.Json;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Security;
using M365Collector.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace M365Collector.Service;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version")) { Console.WriteLine(JsonSerializer.Serialize(ProductInfo.Describe())); return 0; }
        try
        {
            if (args.Contains("--help"))
            {
                Console.WriteLine("M365Collector.Service --version | --verify-package <folder> | [--data-root <folder>] [--health | --backup | --migrate]");
                return 0;
            }
            var packageIndex = Array.IndexOf(args, "--verify-package");
            if (packageIndex >= 0)
            {
                if (packageIndex + 1 >= args.Length) throw new ArgumentException("Package folder required.");
                var manifest = ReleasePackage.Verify(args[packageIndex + 1]);
                Console.WriteLine(JsonSerializer.Serialize(new { version = manifest.Version, verifiedFiles = manifest.Files.Count, status = "Verified" }));
                return 0;
            }
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == "--data-root") { if (++index >= args.Length) throw new ArgumentException("DataRoot required."); }
                else if (args[index] is not ("--health" or "--backup" or "--migrate")) throw new ArgumentException("Unknown command option.");
            }
            var rootIndex = Array.IndexOf(args, "--data-root");
            var root = rootIndex >= 0 && rootIndex + 1 < args.Length ? args[rootIndex + 1] : InstallationRegistry.DataRoot;
            if (root is null) throw new InvalidOperationException("No runtime is configured.");
            var paths = new RuntimePaths(root);
            if (args.Contains("--health"))
            {
                var health = JsonFiles.Read<ServiceHealth>(paths.Health); Console.WriteLine(JsonSerializer.Serialize(health));
                using var service = new ServiceController(ProductInfo.ServiceName);
                return health.IsFresh(DateTimeOffset.UtcNow) && health.Status == "Running" && health.Version == ProductInfo.Version && service.Status == ServiceControllerStatus.Running ? 0 : 2;
            }
            var config = new ConfigurationStore(paths).Load();
            if (args.Contains("--migrate") || args.Contains("--backup"))
            {
                WindowsSecurity.RequireAdministrator();
                using var service = new ServiceController(ProductInfo.ServiceName);
                try { if (service.Status != ServiceControllerStatus.Stopped) throw new InvalidOperationException("Stop M365CollectorService before maintenance."); }
                catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 }) { }
                // A file lock also prevents another maintenance process or service worker opening the runtime.
                using var lease = new FileStream(Path.Combine(paths.Root, "Service", "runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var database = new CollectorDatabase(paths);
                if (args.Contains("--backup"))
                {
                    var destination = Path.Combine(paths.Root, "Service", "Backups", DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(destination); database.Backup(Path.Combine(destination, "collector.db"));
                    File.Copy(paths.Config, Path.Combine(destination, "runtime.json"));
                    JsonFiles.WriteAtomic(Path.Combine(destination, "backup.json"), new { schemaVersion = 1, version = ProductInfo.Version, databaseSchema = database.SchemaVersion(), createdAt = DateTimeOffset.UtcNow });
                    Console.WriteLine(JsonSerializer.Serialize(new { backupPath = destination }));
                }
                if (args.Contains("--migrate")) { database.Migrate(); Console.WriteLine(JsonSerializer.Serialize(new { databaseSchema = database.SchemaVersion() })); }
                return 0;
            }
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = config.InstallRoot });
            builder.Logging.ClearProviders();
            builder.Services.AddWindowsService(options => options.ServiceName = ProductInfo.ServiceName);
            builder.Services.AddSingleton(paths); builder.Services.AddSingleton(config); builder.Services.AddHostedService<CollectorWorker>();
            await builder.Build().RunAsync(); return Environment.ExitCode;
        }
        catch (Exception exception)
        {
            // No raw authentication exception text or response content reaches stdout/event logs.
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "RuntimeCommandFailed", exceptionType = exception.GetType().Name, version = ProductInfo.Version })); return 1;
        }
    }
}
