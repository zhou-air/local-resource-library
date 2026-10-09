using System.Text.Json;
using LocalResourceLibrary.Core;

namespace LocalResourceLibrary.WinUI.Services;

public sealed record ClipboardReferencePayload(int Version, string LibraryPath, string[] ItemIds,
    string? SourceProjectId, bool IsCut, string Token);

/// <summary>Clipboard references contain stable IDs; external text must contain complete paths or URLs.</summary>
public static class ResourceClipboard
{
    public const string ReferenceFormat = "LocalResourceLibrary.ItemReferences.v1";

    public static string Encode(ClipboardReferencePayload payload) => JsonSerializer.Serialize(payload);

    public static ClipboardReferencePayload Decode(string value, string expectedLibraryPath)
    {
        var payload = JsonSerializer.Deserialize<ClipboardReferencePayload>(value)
            ?? throw new ArgumentException("Invalid resource references.");
        if (payload.Version != 1 || string.IsNullOrWhiteSpace(payload.LibraryPath) ||
            !string.Equals(Path.GetFullPath(payload.LibraryPath), Path.GetFullPath(expectedLibraryPath), StringComparison.OrdinalIgnoreCase) ||
            payload.ItemIds == null || payload.ItemIds.Length == 0 || payload.ItemIds.Length > 10000 ||
            payload.ItemIds.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(payload.Token))
            throw new ArgumentException("These resource references belong to a different library or are invalid.");
        return payload with { ItemIds = payload.ItemIds.Distinct(StringComparer.Ordinal).ToArray() };
    }

    public static string[] ParseText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var targets = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = line.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1].Trim();
            if (value.Length == 0) continue;
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var url = ResourceUrls.Normalize(value);
                if (urls.Add(url)) targets.Add(url);
            }
            else
            {
                if (!Path.IsPathFullyQualified(value) || value.Any(char.IsControl) || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    throw new ArgumentException("Clipboard text must contain absolute paths or valid HTTP/HTTPS URLs, one per line.");
                var path = Path.GetFullPath(value);
                if (paths.Add(path)) targets.Add(path);
            }
        }
        if (targets.Count == 0) throw new ArgumentException("The clipboard contains no resource paths or URLs.");
        return targets.ToArray();
    }
}
