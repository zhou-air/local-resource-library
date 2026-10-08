using LocalResourceLibrary.WinUI.Localization;
using LocalResourceLibrary.Core;
using LocalResourceLibrary.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LocalResourceLibrary.WinUI;

internal static class Dialogs
{
    internal sealed record UrlInput(string Target, string Alias, string Description, string Note,
        string[] ProjectIds, byte[]? Favicon);

    public static async Task<UrlInput?> UrlAsync(FrameworkElement root, Localizer text,
        LibrarySnapshot snapshot, string? currentProjectId, ResourceItem? item = null)
    {
        var draft = new UrlEditorDraft(item);
        var body = new StackPanel { Spacing = 12, MinWidth = 340, MaxWidth = 520 };
        var target = new TextBox { Header = text["UrlTarget"], Text = draft.Target,
            PlaceholderText = "https://", TextWrapping = TextWrapping.Wrap };
        var alias = new TextBox { Header = text["Alias"], Text = draft.Alias };
        var description = new TextBox { Header = text["Description"], Text = draft.Description,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 140 };
        var note = new TextBox { Header = text["Note"], Text = item?.Note ?? "",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 120 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var retry = new Button { Content = text["FetchWebsite"] };
        AutomationProperties.SetAutomationId(target, "UrlInput");
        AutomationProperties.SetAutomationId(alias, "UrlAliasInput");
        AutomationProperties.SetAutomationId(description, "UrlDescriptionInput");
        AutomationProperties.SetAutomationId(note, "UrlNoteInput");
        AutomationProperties.SetAutomationId(status, "UrlFetchStatus");
        AutomationProperties.SetAutomationId(retry, "UrlFetchButton");
        body.Children.Add(target);
        body.Children.Add(status);
        body.Children.Add(retry);
        body.Children.Add(alias);
        body.Children.Add(description);
        body.Children.Add(note);
        body.Children.Add(new TextBlock { Text = text["Membership"], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var choices = snapshot.Projects.Select(project => new CheckBox { Content = project.Name, Tag = project.Id,
            IsChecked = item?.Projects.Any(member => member.Id == project.Id) == true ||
                (item == null && project.Id == currentProjectId) }).ToArray();
        foreach (var choice in choices) body.Children.Add(choice);
        if (choices.Length == 0) body.Children.Add(new TextBlock { Text = text["NoProjects"], TextWrapping = TextWrapping.Wrap });
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 580 };
        var dialog = Create(root, text[item == null ? "AddUrlTitle" : "EditUrl"], scroll, "UrlDialog");
        dialog.PrimaryButtonText = text["Save"];
        dialog.CloseButtonText = text["Cancel"];
        var timer = root.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(450);
        var fetcher = new UrlMetadataFetcher();
        CancellationTokenSource? request = null;
        var closed = false;
        var assigning = false;

        void SyncAutomaticFields()
        {
            assigning = true;
            try { alias.Text = draft.Alias; description.Text = draft.Description; }
            finally { assigning = false; }
        }

        bool Validate()
        {
            try
            {
                var normalized = ResourceUrls.Normalize(target.Text);
                var duplicate = snapshot.Items.FirstOrDefault(resource => resource.IsUrl && resource.Id != item?.Id && resource.Target == normalized);
                dialog.IsPrimaryButtonEnabled = item == null || duplicate == null;
                retry.IsEnabled = true;
                if (duplicate != null) status.Text = text[item == null ? "UrlAlreadySaved" : "UrlEditDuplicate"];
                return duplicate == null;
            }
            catch (ArgumentException)
            {
                dialog.IsPrimaryButtonEnabled = retry.IsEnabled = false;
                status.Text = string.IsNullOrWhiteSpace(target.Text) ? text["UrlHelp"] : text["UrlInvalid"];
                return false;
            }
        }

        async Task FetchAsync()
        {
            timer.Stop();
            request?.Cancel();
            if (closed || !Validate()) return;
            var pending = new CancellationTokenSource();
            request = pending;
            var revision = draft.Revision;
            var requestedTarget = draft.Target;
            status.Text = text["FetchingWebsite"];
            try
            {
                var metadata = await fetcher.FetchAsync(requestedTarget, pending.Token);
                if (closed || pending.IsCancellationRequested || !draft.Apply(revision, requestedTarget, metadata)) return;
                SyncAutomaticFields();
                var hasInfo = metadata.Title != null || metadata.Description != null || metadata.Favicon != null;
                status.Text = text[hasInfo ? "WebsiteFetched" : "WebsiteFetchFailed"];
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!closed && !pending.IsCancellationRequested && revision == draft.Revision)
                    status.Text = text["WebsiteFetchFailed"];
            }
            finally
            {
                if (ReferenceEquals(request, pending)) request = null;
                pending.Dispose();
            }
        }

        alias.TextChanged += (_, _) => { if (!assigning) draft.EditAlias(alias.Text); };
        description.TextChanged += (_, _) => { if (!assigning) draft.EditDescription(description.Text); };
        target.TextChanged += (_, _) =>
        {
            request?.Cancel();
            timer.Stop();
            draft.SetTarget(target.Text);
            SyncAutomaticFields();
            if (Validate()) { status.Text = text["UrlHelp"]; timer.Start(); }
        };
        timer.Tick += async (_, _) => await FetchAsync();
        retry.Click += async (_, _) => await FetchAsync();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            // Validate again at the write boundary; a duplicate edit cannot replace another resource.
            Validate();
            args.Cancel = !dialog.IsPrimaryButtonEnabled;
        };
        dialog.Opened += (_, _) =>
        {
            target.Focus(FocusState.Programmatic);
            target.SelectAll();
            if (Validate()) timer.Start();
        };
        Validate();
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            return new UrlInput(ResourceUrls.Normalize(target.Text), alias.Text, description.Text, note.Text,
                choices.Where(choice => choice.IsChecked == true).Select(choice => (string)choice.Tag).ToArray(), draft.Favicon);
        }
        finally
        {
            closed = true;
            timer.Stop();
            request?.Cancel();
        }
    }

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
