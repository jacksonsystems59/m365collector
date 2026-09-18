using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SterlingMonitor;

public static class SecretStore
{
    public static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}

public sealed class SettingsStore(string directory)
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SterlingMonitor");
    public string DirectoryPath => directory;
    public string FilePath => Path.Combine(directory, "settings.json");
    public MonitorSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        var settings = JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Settings file is empty.");
        settings.Validate();
        return settings;
    }
    public void Save(MonitorSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}

public sealed class ActivityLog(string directory)
{
    private readonly object gate = new();
    private readonly Queue<string> lines = new();
    public string[] Lines { get { lock (gate) return lines.ToArray(); } }
    public void Write(string message)
    {
        // Callers log fixed diagnostics and exception types, never SMTP credentials or protocol transcripts.
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message.Replace('\r', ' ').Replace('\n', ' ')}";
        lock (gate)
        {
            lines.Enqueue(line);
            while (lines.Count > 500) lines.Dequeue();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "activity.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".1", true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException) { lines.Enqueue("Activity log could not be written to disk."); }
            catch (UnauthorizedAccessException) { lines.Enqueue("Activity log directory is not writable."); }
        }
    }
}
