using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Security;
using M365Collector.Storage;
using System.ServiceProcess;
using Microsoft.Win32;
namespace M365Collector.GUI;
internal static class SetupOperations
{
    public static string AppRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "M365Collector");
    public static async Task<string> CheckAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) || !Environment.Is64BitOperatingSystem || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Windows 10 1809 / Server 2019 or newer, 64-bit, is required.");
        if (!WindowsAcl.Elevated) throw new UnauthorizedAccessException("Run M365Collector with administrator elevation to install its service.");
        var ps = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"]);
        if (Version.Parse(ps.Trim()).Major < 5) throw new InvalidOperationException("PowerShell 5.1 or newer is required for guided setup.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        foreach (var url in new[] { "https://login.microsoftonline.com/organizations/v2.0/.well-known/openid-configuration", "https://graph.microsoft.com/v1.0/$metadata" }) { using var result = await http.GetAsync(url); result.EnsureSuccessStatusCode(); }
        _ = ServiceController.GetServices();
        return $"✓ Supported x64 Windows\n✓ Administrator elevation\n✓ Bundled .NET runtime {Environment.Version}\n✓ PowerShell {ps.Trim()}\n✓ Microsoft login and Graph reachable\n✓ Windows Service manager available";
    }
    public static async Task InstallAsync(Installation installation)
    {
        var source = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        if (!File.Exists(Path.Combine(source, "package.json"))) throw new IOException("Run the GUI from the extracted release ZIP. Its package.json, GUI, Service and Updater folders must remain together.");
        var manifest = JsonFile.Read<PackageManifest>(Path.Combine(source, "package.json"));
        if (manifest.Version != Product.Version) throw new IOException("Release package version does not match the GUI.");
        foreach (var (relative, expectedHash) in manifest.Files)
        {
            if (!UpdatePackage.ValidPath(relative)) throw new IOException("Invalid installation package path.");
            var file = Path.Combine(source, relative); RuntimePaths.RejectReparse(Path.GetDirectoryName(file)!);
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked package files are unsupported.");
            using var stream = File.OpenRead(file);
            if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Release package file validation failed: " + relative);
        }
        var executable = Path.Combine(installation.AppRoot, "Service", "M365Collector.Service.exe");
        var services = ServiceController.GetServices(); var existing = services.Any(s => s.ServiceName == Product.ServiceName); foreach (var item in services) item.Dispose();
        if (existing)
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Product.ServiceName);
            if (!string.Equals((key?.GetValue("ImagePath") as string)?.Trim('"'), executable, StringComparison.OrdinalIgnoreCase)) throw new IOException("An existing service has a different binary path. Resolve that installation before continuing.");
            await new WindowsServiceControl().StopAsync(CancellationToken.None);
        }
        else await ProcessRunner.RunAsync("sc.exe", ["create", Product.ServiceName, "binPath=", "\"" + executable + "\"", "start=", "auto", "obj=", @"NT AUTHORITY\LocalService", "DisplayName=", "M365Collector Service"]);
        await ProcessRunner.RunAsync("sc.exe", ["sidtype", Product.ServiceName, "unrestricted"]);
        await ProcessRunner.RunAsync("sc.exe", ["config", Product.ServiceName, "start=", "auto", "obj=", @"NT AUTHORITY\LocalService"]);
        RuntimePaths.RejectReparse(installation.AppRoot);
        WindowsAcl.ProtectDirectory(installation.AppRoot, false);
        if (!string.Equals(source, installation.AppRoot, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var relative in manifest.Files.Keys.Append("package.json")) { var target = Path.Combine(installation.AppRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(Path.Combine(source, relative), target, true); }
        }
        WindowsAcl.ProtectDirectory(installation.DataRoot, true);
        WindowsAcl.ProtectDirectory(Path.GetDirectoryName(InstallationState.Locator)!, false);
        JsonFile.Write(InstallationState.Locator, installation);
        var paths = new RuntimePaths(installation.DataRoot); paths.Create(); new CollectorStore(paths.Database).Migrate();
        await ProcessRunner.RunAsync("sc.exe", ["failure", Product.ServiceName, "reset=", "86400", "actions=", "restart/10000/restart/30000/restart/60000"]);
        var since = DateTimeOffset.UtcNow; var service = new WindowsServiceControl(); await service.StartAsync(CancellationToken.None); await service.VerifyAsync(paths, Product.Version, since, CancellationToken.None);
    }
}
