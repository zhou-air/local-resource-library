using LocalResourceLibrary.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace LocalResourceLibrary.WinUI.Services;

/// <summary>Only project folder glyphs use the palette; names, counts and selection retain theme brushes.</summary>
public static class ProjectIconColor
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(ProjectIconColor), new PropertyMetadata(null, OnKeyChanged));

    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);
    public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);

    private static void OnKeyChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FontIcon icon) return;
        if (args.OldValue == null)
        {
            icon.Loaded += (_, _) => Apply(icon);
            icon.ActualThemeChanged += (_, _) => Apply(icon);
        }
        Apply(icon);
    }

    private static void Apply(FontIcon icon)
    {
        if (new AccessibilitySettings().HighContrast)
        {
            // A fresh theme brush keeps the folder accessible in Windows contrast themes.
            if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var resource) && resource is Brush brush)
                icon.Foreground = brush;
            return;
        }
        var color = ProjectColors.Presets.FirstOrDefault(preset => preset.Key == GetKey(icon))
            ?? ProjectColors.Presets.First(preset => preset.Key == "default");
        var hex = icon.ActualTheme == ElementTheme.Dark ? color.DarkHex : color.LightHex;
        var rgb = Convert.ToUInt32(hex.TrimStart('#'), 16);
        icon.Foreground = new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }
}
