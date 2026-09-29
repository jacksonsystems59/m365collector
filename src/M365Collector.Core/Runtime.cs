using System.Diagnostics;
using System.Text.Json;
namespace M365Collector.Core;
public sealed record Installation(string DataRoot, string AppRoot, bool Completed);
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("Empty configuration.");
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options)); File.Move(temp, path, true);
    }
}
public static class InstallationState
{
    public static string Locator => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "M365Collector.Installation", "installation.json");
    public static bool IsFirstRun(string locator) => !File.Exists(locator) || !JsonFile.Read<Installation>(locator).Completed;
}
public sealed class RuntimePaths
{
    public string Root { get; }
    public string Database => Path.Combine(Root, "Database", "collector.db");
    public string Heartbeat => Path.Combine(Root, "Service", "heartbeat.json");
    public RuntimePaths(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\\\")) throw new IOException("Choose an absolute local drive path.");
        // Validate the supplied spelling before Win32 canonicalizes DOS device names or trailing dots.
        var relative = root[Path.GetPathRoot(root)!.Length..];
        if (relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(p => p.EndsWith('.') || p.EndsWith(' ') || p.Contains(':') || System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) throw new IOException("Data paths cannot contain Windows aliases, device names or alternate streams.");
        Root = Path.GetFullPath(root);
    }
    public static bool Within(string child, string parent) => Path.GetFullPath(child).TrimEnd('\\').Equals(Path.GetFullPath(parent).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(child).StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    public static void RejectReparse(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Junctions and symbolic links are not supported: " + current.FullName);
    }
    public void Validate(string appRoot, long minimumFreeBytes = 512L * 1024 * 1024)
    {
        if (Root.StartsWith("\\\\") || Root.Length <= 3) throw new IOException("Choose a dedicated local data folder.");
        if (Within(Root, appRoot) || Within(appRoot, Root)) throw new IOException("Application and data directories must be separate, not nested.");
        var metadata = Path.GetDirectoryName(InstallationState.Locator)!;
        if (Within(Root, metadata) || Within(metadata, Root)) throw new IOException("DataRoot must be separate from the protected installation metadata directory.");
        RejectReparse(Root);
        if (new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace < minimumFreeBytes) throw new IOException("At least 512 MB of free disk space is required.");
        Directory.CreateDirectory(Root);
        var probe = Path.Combine(Root, Guid.NewGuid() + ".probe");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
    }
    public void Create() { foreach (var folder in new[] { "Config", "Database", "Logs", "Logs/Updates", "Reports", "Exports", "Cache", "Temp", "Service", "Customers" }) Directory.CreateDirectory(Path.Combine(Root, folder)); }
    public string CustomerDirectory(Guid tenant)
    {
        if (tenant == Guid.Empty) throw new ArgumentException("Tenant ID is required.");
        var path = Path.Combine(Root, "Customers", tenant.ToString("D")); RejectReparse(path);
        foreach (var folder in new[] { "Data", "Audit", "Reports", "Exports", "Cache" }) Directory.CreateDirectory(Path.Combine(path, folder)); return path;
    }
    public void Log(string area, string message)
    {
        var folder = Path.Combine(Root, "Logs", area == "Updates" ? "Updates" : ""); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log"), $"{DateTimeOffset.UtcNow:O} {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
    }
}
public static class ProcessRunner
{
    public static async Task<string> RunAsync(string file, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start " + file);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken); var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(deadline.Token); } catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        var result = await output; if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(file)} failed ({process.ExitCode}): {await error} {result}"); return result;
    }
}
