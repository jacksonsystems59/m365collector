using System.Text.Json;
using M365Collector.Contracts;
using Microsoft.Win32;

namespace M365Collector.Core;

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static void WriteAtomic<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, value, Options); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException("The JSON document is empty.");
}

public sealed class ConfigurationStore(RuntimePaths paths)
{
    public RuntimeConfig Load()
    {
        var config = JsonFiles.Read<RuntimeConfig>(paths.Config);
        Validate(config);
        return config;
    }
    public void Save(RuntimeConfig config) { Validate(config); JsonFiles.WriteAtomic(paths.Config, config); }
    private void Validate(RuntimeConfig config)
    {
        if (config.SchemaVersion != ProductInfo.ConfigSchema) throw new InvalidDataException("Unsupported configuration schema; use the matching application version.");
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.DataRoot)), paths.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Configuration DataRoot does not match the registered runtime.");
        RuntimePaths.Validate(config.DataRoot, config.InstallRoot, AppContext.BaseDirectory);
        if (config.CollectionIntervalMinutes is < 1 or > 1440) throw new InvalidDataException("Collection interval must be 1–1440 minutes.");
    }
}
public enum SetupState { New, Incomplete, Complete, Broken }
public static class SetupDetection
{
    public static SetupState Detect(string? registeredRoot)
    {
        if (registeredRoot is null) return SetupState.New;
        try
        {
            var paths = new RuntimePaths(registeredRoot);
            var config = new ConfigurationStore(paths).Load();
            if (!config.SetupComplete) return SetupState.Incomplete;
            return File.Exists(paths.Database) ? SetupState.Complete : SetupState.Broken;
        }
        catch { return SetupState.Broken; }
    }
}
public static class InstallationRegistry
{
    private const string Key = @"SOFTWARE\SterlingTech\M365Collector";
    public static string? DataRoot
    {
        get { using var key = Registry.LocalMachine.OpenSubKey(Key); return key?.GetValue("DataRoot") as string; }
    }
    public static void Register(RuntimeConfig config)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Key);
        key.SetValue("DataRoot", config.DataRoot);
        key.SetValue("InstallRoot", config.InstallRoot);
        key.SetValue("Version", config.InstalledVersion);
        key.SetValue("SchemaVersion", ProductInfo.ConfigSchema, RegistryValueKind.DWord);
    }
}
