using System.Text.Json;
using System.Text.Json.Serialization;
using LocalResourceLibrary.WinUI.ViewModels;

namespace LocalResourceLibrary.WinUI.Services;

public sealed record ExplorerPreferences
{
    public double NavigationWidth { get; init; } = 226;
    public double DetailsWidth { get; init; } = 340;
    public ResourceViewMode ViewMode { get; init; } = ResourceViewMode.Details;
    public ResourceSortKey SortKey { get; init; } = ResourceSortKey.Name;
    public bool SortDescending { get; init; }
    public bool FoldersFirst { get; init; } = true;
}

/// <summary>The WinUI Explorer options are independent of the shared language settings.</summary>
public sealed class ExplorerPreferencesStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ExplorerPreferencesStore(string databasePath) =>
        _path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "explorer-settings.json");

    public ExplorerPreferences Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<ExplorerPreferences>(File.ReadAllText(_path), JsonOptions) is { } saved)
                return saved with
                {
                    NavigationWidth = ValidWidth(saved.NavigationWidth, 226, 160, 480),
                    DetailsWidth = ValidWidth(saved.DetailsWidth, 340, 280, 620),
                    ViewMode = Enum.IsDefined(saved.ViewMode) ? saved.ViewMode : ResourceViewMode.Details,
                    SortKey = Enum.IsDefined(saved.SortKey) ? saved.SortKey : ResourceSortKey.Name
                };
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { }
        return new();
    }

    public bool Save(ExplorerPreferences preferences)
    {
        var temporaryPath = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporaryPath, _path, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    internal static double ValidWidth(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
