using System.Text.Json;
using M365Collector.Contracts;

namespace M365Collector.Core;

// Deliberately excludes arbitrary messages, exception text, tokens and response bodies.
public sealed class StructuredLog(RuntimePaths paths, string component)
{
    private readonly object gate = new();
    public void Write(string eventName, string outcome, Guid? tenantId = null, string? code = null)
    {
        if (!Safe(eventName) || !Safe(outcome) || (code != null && !Safe(code)))
            throw new ArgumentException("Log fields must be short event identifiers, never untrusted text.");
        var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, version = ProductInfo.Version,
            component, eventName, outcome, tenantId, code });
        lock (gate) File.AppendAllText(Path.Combine(paths.Root, "Logs", $"{component}-{DateTime.UtcNow:yyyyMMdd}.jsonl"), line + Environment.NewLine);
    }
    private static bool Safe(string text) => text.Length is > 0 and <= 80 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
