using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace M365Collector.Core;
public sealed record SemanticVersion(int Major, int Minor, int Patch) : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string text)
    {
        var match = Regex.Match(text, @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new FormatException("Expected a stable semantic version (major.minor.patch).");
        return new(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }
    public int CompareTo(SemanticVersion? other) => other == null ? 1 : Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
public sealed record ReleaseInfo(string Version, string Notes, string Page, DateTimeOffset Published, string ZipUrl, string ChecksumUrl);
public sealed record ReleaseCache(DateTimeOffset Checked, string? ETag, ReleaseInfo? Release);
public sealed class ReleaseClient(HttpClient http, string cachePath)
{
    public const string Api = "https://api.github.com/repos/jacksonsystems59/m365collector/releases/latest";
    public static bool IsUpdate(string current, ReleaseInfo? release) => release != null && SemanticVersion.Parse(release.Version).CompareTo(SemanticVersion.Parse(current)) > 0;
    public async Task<ReleaseCache> CheckAsync(bool force, CancellationToken ct)
    {
        ReleaseCache? cached = null;
        try { if (File.Exists(cachePath)) cached = JsonFile.Read<ReleaseCache>(cachePath); } catch (JsonException) { }
        if (!force && cached != null && cached.Checked > DateTimeOffset.UtcNow.AddHours(-6)) return cached;
        using var request = new HttpRequestMessage(HttpMethod.Get, Api); request.Headers.UserAgent.ParseAdd("M365Collector/"+M365Collector.Contracts.Product.Version); request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (cached?.ETag != null) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new HttpRequestException("GitHub rate limit or access restriction. Try again later; no credentials are required.");
        ReleaseInfo? release;
        if (response.StatusCode == HttpStatusCode.NotModified && cached != null) release = cached.Release;
        else if (response.StatusCode == HttpStatusCode.NotFound) release = null;
        else { response.EnsureSuccessStatusCode(); release = Parse(await response.Content.ReadAsStringAsync(ct)); }
        var state = new ReleaseCache(DateTimeOffset.UtcNow, response.Headers.ETag?.ToString() ?? cached?.ETag, release); JsonFile.Write(cachePath, state); return state;
    }
    public static ReleaseInfo? Parse(string json)
    {
        if (json.Length > 2_000_000) throw new InvalidDataException("Release response is too large.");
        using var doc = JsonDocument.Parse(json); var release = doc.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var version = SemanticVersion.Parse(release.GetProperty("tag_name").GetString()!).ToString();
        var name = $"M365Collector-{version}-win-x64.zip";
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        string Asset(string expected)
        {
            var matches = assets.Where(a => a.GetProperty("name").GetString() == expected).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Release is missing a unique asset: " + expected);
            var url = matches[0].GetProperty("browser_download_url").GetString()!; ValidateAssetUrl(url); return url;
        }
        var page = release.GetProperty("html_url").GetString()!;
        if (!Uri.TryCreate(page, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/jacksonsystems59/m365collector/releases/tag/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid release page.");
        return new(version, release.GetProperty("body").GetString() ?? "", page, release.GetProperty("published_at").GetDateTimeOffset(), Asset(name), Asset(name + ".sha256"));
    }
    public static void ValidateAssetUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/jacksonsystems59/m365collector/releases/download/", StringComparison.Ordinal)) throw new InvalidDataException("Untrusted release asset URL.");
    }
}
