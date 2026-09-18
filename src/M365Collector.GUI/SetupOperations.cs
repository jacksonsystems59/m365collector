using System.Diagnostics;
using System.ServiceProcess;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Security;
using M365Collector.Storage;

namespace M365Collector.GUI;

public static class SetupOperations
{
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "M365Collector", ProductInfo.Version);
    public static async Task<IReadOnlyList<DependencyResult>> CheckDependencies(CancellationToken ct)
    {
        var checks = new List<DependencyResult>
        {
            new("Windows", true, SupportedWindows(), "Windows Server 2019+ with Desktop Experience / Windows 11; x64"),
            new("64-bit operating system", true, Environment.Is64BitOperatingSystem, Environment.OSVersion.VersionString),
            new("Administrator / service installation", true, WindowsSecurity.IsAdministrator, "Elevated Windows administrator required; actual SCM install/start verified during setup."),
            new(".NET runtime", true, true, $"Running .NET {Environment.Version}; release package includes its runtime."),
            new("Service package", true, File.Exists(Path.Combine(AppContext.BaseDirectory,"service","M365Collector.Service.exe")), "Requires the complete portable ZIP, including service subfolder."),
            new("PowerShell modules", false, true, "None required for Tenant Identity. Future modules declare their own dependencies.")
        };
        try
        {
            var output = await Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"], ct);
            checks.Add(new("PowerShell", false, true, output.Trim() + " — optional for v0.1.0"));
        }
        catch { checks.Add(new("PowerShell", false, false, "Optional; native Graph collector does not require PowerShell.")); }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        foreach (var (name, url) in new[] { ("Internet / Microsoft authentication", "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration"), ("Microsoft Graph", "https://graph.microsoft.com/v1.0/$metadata") })
        {
            try { using var result = await http.GetAsync(url, ct); checks.Add(new(name, true, result.IsSuccessStatusCode, $"HTTPS response {(int)result.StatusCode}")); }
            catch { checks.Add(new(name, true, false, "HTTPS connection failed; check network/proxy/firewall.")); }
        }
        return checks;
    }
    private static bool SupportedWindows()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return key?.GetValue("InstallationType") as string == "Server"
            ? OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
            : key?.GetValue("InstallationType") as string == "Client" && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
    }
    public static RuntimeConfig CreateRuntime(string root)
    {
        WindowsSecurity.RequireAdministrator();
        root = RuntimePaths.Validate(root, AppContext.BaseDirectory, InstallRoot);
        var paths = new RuntimePaths(root);
        if (InstallationRegistry.DataRoot is string existing && !string.Equals(Path.GetFullPath(existing), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A different DataRoot is already registered. Repair it instead of replacing it.");
        if (!File.Exists(paths.Config) && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Select an empty dedicated data folder.");
        RuntimeConfig config;
        var resuming = File.Exists(paths.Config);
        if (resuming)
        {
            config = new ConfigurationStore(paths).Load();
            if (config.SetupComplete) throw new InvalidOperationException("This runtime is already configured.");
            if (config.InstalledVersion != ProductInfo.Version) throw new InvalidOperationException("Use the version matching this incomplete installation.");
        }
        else config = new(ProductInfo.ConfigSchema, root, InstallRoot, ProductInfo.Version, false, WindowsSecurity.CurrentSid);
        Directory.CreateDirectory(root);
        if (!resuming) WindowsSecurity.ProtectDirectory(root, false);
        paths.RejectReparsePoints(); paths.Create();
        new ConfigurationStore(paths).Save(config);
        new CollectorDatabase(paths).Migrate(); InstallationRegistry.Register(config);
        new StructuredLog(paths, "gui").Write("RuntimeCreated", "Success");
        return config;
    }
    public static async Task InstallAndStart(RuntimeConfig config, IProgress<string> progress, CancellationToken ct)
    {
        WindowsSecurity.RequireAdministrator();
        var source = AppContext.BaseDirectory;
        var manifest = ReleasePackage.Verify(source);
        var executable = Path.Combine(config.InstallRoot, "service", "M365Collector.Service.exe");
        if (!File.Exists(Path.Combine(source, "service", "M365Collector.Service.exe"))) throw new FileNotFoundException("Use the complete release package.");
        var serviceExists = ServiceController.GetServices().Any(s => { using (s) return s.ServiceName == ProductInfo.ServiceName; });
        if (serviceExists)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ProductInfo.ServiceName);
            var expected = $"\"{executable}\" --data-root \"{config.DataRoot}\"";
            if (!string.Equals(key?.GetValue("ImagePath") as string, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A service with this name already points to another installation. It will not be overwritten.");
            using var controller = new ServiceController(ProductInfo.ServiceName);
            if (controller.Status != ServiceControllerStatus.Stopped)
                throw new InvalidOperationException("The service is already running. Use Verify, or stop it before repairing incomplete setup.");
        }
        progress.Report("Copying application binaries to " + config.InstallRoot);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(source), config.InstallRoot, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(config.InstallRoot);
            WindowsSecurity.ProtectBinaryDirectory(config.InstallRoot);
            foreach (var relative in manifest.Files.Select(file => file.Path).Append("release.json"))
            {
                var destination = Path.Combine(config.InstallRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(Path.Combine(source, relative), destination, true);
            }
        }
        var sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        if (!serviceExists)
        {
            progress.Report("Registering M365CollectorService as LocalService with an isolated service SID.");
            await Run(sc, ["create", ProductInfo.ServiceName, "binPath=", $"\"{executable}\" --data-root \"{config.DataRoot}\"", "start=", "auto", "obj=", @"NT AUTHORITY\LocalService", "DisplayName=", "M365Collector Service"], ct);
        }
        await Run(sc, ["sidtype", ProductInfo.ServiceName, "unrestricted"], ct);
        await Run(sc, ["description", ProductInfo.ServiceName, "M365Collector unattended tenant collection runtime " + ProductInfo.Version], ct);
        await Run(sc, ["failure", ProductInfo.ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000"], ct);
        await Run(sc, ["failureflag", ProductInfo.ServiceName, "1"], ct);
        WindowsSecurity.ProtectDirectory(config.DataRoot, true);
        progress.Report("Starting service and waiting for fresh health with matching version.");
        await Run(sc, ["start", ProductInfo.ServiceName], ct);
        for (var i = 0; i < 30; i++) { if (Healthy(new RuntimePaths(config.DataRoot))) return; await Task.Delay(1000, ct); }
        throw new System.TimeoutException("Service did not publish healthy status within 30 seconds. Setup remains incomplete; correct the problem and retry.");
    }
    public static bool Healthy(RuntimePaths paths)
    {
        try
        {
            using var controller = new ServiceController(ProductInfo.ServiceName);
            var health = JsonFiles.Read<ServiceHealth>(paths.Health);
            return controller.Status == ServiceControllerStatus.Running && health.Version == ProductInfo.Version && health.Status == "Running" && health.IsFresh(DateTimeOffset.UtcNow);
        }
        catch { return false; }
    }
    public static async Task<string> Run(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start system command.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { try { process.Kill(true); } catch { } throw; }
        var output = await stdout; await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed (exit {process.ExitCode}). Verify permissions and service configuration.");
        return output;
    }
}
