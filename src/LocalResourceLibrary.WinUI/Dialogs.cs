using LocalResourceLibrary.WinUI.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LocalResourceLibrary.WinUI;

internal static class Dialogs
{
    private static ContentDialog Create(FrameworkElement root, string title, object content, string automationId)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
            Title = title,
            Content = content,
            DefaultButton = ContentDialogButton.Primary
        };
        AutomationProperties.SetAutomationId(dialog, automationId);
        return dialog;
    }

    public static async Task<(string Value, string Description)?> InputAsync(FrameworkElement root, Localizer text,
        string title, string label, string value, string? help = null, string? description = null, string confirm = "Save")
    {
        var isProject = label == text["ProjectName"] || label == "ProjectName";
        var body = new StackPanel { Spacing = 16, MinWidth = 320, MaxWidth = 480 };
        if (!string.IsNullOrEmpty(help))
        {
            var guidance = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
            if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var brush) && brush is Brush muted)
                guidance.Foreground = muted;
            body.Children.Add(guidance);
        }

        var input = new TextBox { Header = label, Text = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(input, label);
        AutomationProperties.SetAutomationId(input, isProject ? "ProjectNameInput" : "ValueInput");
        body.Children.Add(input);

        TextBox? descriptionInput = null;
        if (description != null)
        {
            descriptionInput = new TextBox
            {
                Header = text["ProjectDescription"],
                Text = description,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 104,
                MaxHeight = 208,
                VerticalContentAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(descriptionInput, text["ProjectDescription"]);
            AutomationProperties.SetAutomationId(descriptionInput, isProject ? "ProjectDescriptionInput" : "DescriptionInput");
            ScrollViewer.SetVerticalScrollBarVisibility(descriptionInput, ScrollBarVisibility.Auto);
            body.Children.Add(descriptionInput);
        }

        var dialog = Create(root, title, body, isProject ? "ProjectDialog" : "InputDialog");
        dialog.PrimaryButtonText = text[confirm];
        dialog.CloseButtonText = text["Cancel"];
        dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) args.Cancel = true;
        };
        dialog.Opened += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? (input.Text, descriptionInput?.Text ?? "") : null;
    }

    public static async Task<ContentDialogResult> ConfirmAsync(FrameworkElement root, Localizer text,
        string title, string message, string primary = "Confirm", string? secondary = null)
    {
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MinWidth = 320, MaxWidth = 480, LineHeight = 22 };
        var dialog = Create(root, title, body, primary == "Save" && secondary == "Discard" ? "UnsavedDialog" : "ConfirmationDialog");
        dialog.PrimaryButtonText = text[primary];
        dialog.SecondaryButtonText = secondary == null ? "" : text[secondary];
        dialog.CloseButtonText = text["Cancel"];
        return await dialog.ShowAsync();
    }

    public static async Task MessageAsync(FrameworkElement root, Localizer text, string title, string message)
    {
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MinWidth = 320, MaxWidth = 480, LineHeight = 22 };
        var dialog = Create(root, title, body, "MessageDialog");
        dialog.PrimaryButtonText = text["Confirm"];
        await dialog.ShowAsync();
    }
}
