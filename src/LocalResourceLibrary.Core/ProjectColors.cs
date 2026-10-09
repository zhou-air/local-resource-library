namespace LocalResourceLibrary.Core;

public sealed record ProjectColor(string Key, string LightHex, string DarkHex);

/// <summary>Stable color keys shared by the database, sidebar and MCP.</summary>
public static class ProjectColors
{
    public const string Default = "default";

    public static IReadOnlyList<ProjectColor> Presets { get; } = Array.AsReadOnly<ProjectColor>(
    [
        new(Default, "#64748B", "#AAB8CB"),
        new("blue", "#4F80BA", "#7FAAE0"),
        new("teal", "#338F7C", "#71BCA7"),
        new("purple", "#906DB4", "#B69AD3"),
        new("amber", "#A2752F", "#D7B467"),
        new("cyan", "#358BA4", "#79BED1"),
        new("rose", "#B56889", "#D799B2"),
        new("slate", "#6F7D89", "#ABB6C2")
    ]);

    public static string Normalize(string color)
    {
        ArgumentNullException.ThrowIfNull(color);
        var key = color.Trim().ToLowerInvariant();
        if (!Presets.Any(preset => preset.Key == key))
            throw new ArgumentException("项目颜色必须使用预设颜色。", nameof(color));
        return key;
    }
}
