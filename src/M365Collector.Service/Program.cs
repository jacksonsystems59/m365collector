using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Storage;
using M365Collector.Service;

if (args is ["--verify-runtime", var diagnosticDirectory])
{
    if (!Path.IsPathFullyQualified(diagnosticDirectory) || Directory.Exists(diagnosticDirectory)) throw new IOException("Choose a new absolute diagnostic directory.");
    RuntimePaths.RejectReparse(diagnosticDirectory); Directory.CreateDirectory(diagnosticDirectory);
    var diagnosticStore = new CollectorStore(Path.Combine(diagnosticDirectory, "runtime-check.db"));
    diagnosticStore.Migrate(); diagnosticStore.Verify();
    File.WriteAllText(Path.Combine(diagnosticDirectory, "verified.txt"), $"M365Collector {Product.Version}; bundled runtime {Environment.Version}; SQLite schema {CollectorStore.CurrentSchema} verified.");
    return;
}
var installation = JsonFile.Read<Installation>(InstallationState.Locator);
var paths = new RuntimePaths(installation.DataRoot);
var store = new CollectorStore(paths.Database);
if (args.Contains("--migrate")) { store.Migrate(); store.Verify(); return; }
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = Product.ServiceName);
builder.Services.AddSingleton(paths); builder.Services.AddSingleton(store); builder.Services.AddHostedService<CollectorWorker>();
await builder.Build().RunAsync();
