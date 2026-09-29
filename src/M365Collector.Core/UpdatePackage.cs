using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace M365Collector.Core;
public sealed record PackageManifest(string Version, Dictionary<string, string> Files);
public static class UpdatePackage
{
    public static async Task DownloadAsync(HttpClient http, string url, string target, long maxBytes, CancellationToken ct)
    {
        ReleaseClient.ValidateAssetUrl(url);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri;
        if (final?.Scheme != "https" || !(final.Host == "github.com" || final.Host == "release-assets.githubusercontent.com" || final.Host == "objects.githubusercontent.com")) throw new InvalidDataException("Unexpected asset redirect.");
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Release asset exceeds size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0) { total += count; if (total > maxBytes) throw new InvalidDataException("Release asset exceeds size limit."); await output.WriteAsync(buffer.AsMemory(0, count), ct); }
    }
    public static void VerifyChecksum(string zip, string checksum)
    {
        var text = File.ReadAllText(checksum).Trim(); var parts = Regex.Split(text, @"\s+");
        if (parts.Length is < 1 or > 2 || !Regex.IsMatch(parts[0], "^[0-9a-fA-F]{64}$")) throw new InvalidDataException("Invalid SHA-256 file.");
        if (parts.Length == 2 && parts[1].TrimStart('*') != Path.GetFileName(zip)) throw new InvalidDataException("Checksum filename does not match.");
        using var file = File.OpenRead(zip);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), Convert.FromHexString(parts[0]))) throw new InvalidDataException("Downloaded package failed SHA-256 validation.");
    }
    public static PackageManifest Extract(string zip, string destination, string expectedVersion)
    {
        if (Directory.Exists(destination)) throw new IOException("Extraction destination already exists.");
        RuntimePaths.RejectReparse(destination);
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 10_000 || archive.Entries.Sum(e => e.Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Package size limits exceeded.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName;
            if (!ValidPath(path) || !paths.Add(path) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Unsafe package entry: " + path);
        }
        var manifestEntry = archive.GetEntry("package.json") ?? throw new InvalidDataException("Missing package manifest.");
        if (manifestEntry.Length > 2_000_000) throw new InvalidDataException("Oversized package manifest.");
        using var manifestStream = manifestEntry.Open(); var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestStream, JsonFile.Options) ?? throw new InvalidDataException("Invalid manifest.");
        if (manifest.Version != expectedVersion || manifest.Files.Count != archive.Entries.Count - 1 || !manifest.Files.Keys.All(ValidPath) || manifest.Files.Keys.Any(k => k == "package.json")) throw new InvalidDataException("Package version or file manifest mismatch.");
        foreach (var required in new[] { "GUI/M365Collector.GUI.exe", "Service/M365Collector.Service.exe", "Updater/M365Collector.Updater.exe" }) if (!manifest.Files.ContainsKey(required)) throw new InvalidDataException("Missing component: " + required);
        Directory.CreateDirectory(destination);
        try
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName != "package.json")
                {
                    if (!manifest.Files.TryGetValue(entry.FullName, out var hash)) throw new InvalidDataException("Unlisted file.");
                    using var source = entry.Open(); if (!Convert.ToHexString(SHA256.HashData(source)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Manifest hash mismatch: " + entry.FullName);
                }
                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!RuntimePaths.Within(target, destination)) throw new InvalidDataException("Entry escapes staging.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, false);
            }
            return manifest;
        }
        catch { Directory.Delete(destination, true); throw; }
    }
    public static bool ValidPath(string path)
    {
        if (path == "package.json") return true;
        if (path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.EndsWith('/')) return false;
        var segments = path.Split('/');
        if (segments.Length < 2 || !new[] { "GUI", "Service", "Updater" }.Contains(segments[0])) return false;
        if (segments.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase))) return false;
        return new[] { ".exe", ".dll", ".json", ".config", ".xml", ".pri", ".dat" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
    public static void CopyTree(string source, string target)
    {
        RuntimePaths.RejectReparse(source); RuntimePaths.RejectReparse(target); Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked files are not supported."); File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true); }
        foreach (var directory in Directory.GetDirectories(source)) CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
