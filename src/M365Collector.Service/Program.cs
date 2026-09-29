using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Storage;
using M365Collector.Service;

var installation = JsonFile.Read<Installation>(InstallationState.Locator);
var paths = new RuntimePaths(installation.DataRoot);
var store = new CollectorStore(paths.Database);
if (args.Contains("--migrate")) { store.Migrate(); store.Verify(); return; }
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = Product.ServiceName);
builder.Services.AddSingleton(paths); builder.Services.AddSingleton(store); builder.Services.AddHostedService<CollectorWorker>();
await builder.Build().RunAsync();
