using System.IO;
using System.Text.Json;

namespace LocalResourceLibrary.App.Services;

public sealed class SettingsStore
{
    private readonly string _path;
    public string Language { get; private set; } = "zh-CN";
    public SettingsStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "settings.json");
        try
        {
            if (File.Exists(_path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("language", out var language) &&
                    language.ValueKind == JsonValueKind.String && language.GetString() == "en") Language = "en";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public void SaveLanguage(string language)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { language }));
        File.Move(temp, _path, true);
        Language = language;
    }
}
