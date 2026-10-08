using System.Globalization;

namespace LocalResourceLibrary.WinUI.Services;

public static class SettingsStore
{
    // Windows display language, independent of regional date/number formats.
    public static string GetSystemLanguage() => ResolveLanguage(CultureInfo.CurrentUICulture);

    public static string ResolveLanguage(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
}
