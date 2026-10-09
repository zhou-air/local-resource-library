using LocalResourceLibrary.WinUI.Localization;
using LocalResourceLibrary.WinUI.Services;
using LocalResourceLibrary.Core;
using LocalResourceLibrary.WinUI.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace LocalResourceLibrary.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly LibraryService _library;
    private readonly Localizer _text;
    private readonly MainViewModel _vm;
    private readonly DispatcherQueueTimer _searchTimer;
    private bool _rendering = true;
    private bool _dialogOpen;
    private bool _allowClose;
    private readonly TrayIcon _tray;

    public MainWindow(LibraryService library, Localizer text, string instanceKey)
    {
        _library = library;
        _text = text;
        _vm = new MainViewModel(text, library.DatabasePath);
        InitializeComponent();
        Root.DataContext = _vm;
        Title = text["Title"] + " · WinUI 3";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1420, 860));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        AppWindow.Move(new PointInt32(area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - 1420) / 2),
            area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - 860) / 2)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1080;
            presenter.PreferredMinimumHeight = 650;
        }
        AppWindow.Closing += Window_Closing;
        Root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        UpdateTitleBar();
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(220);
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await ApplySearchAsync();
        };
        _rendering = false;
        InitializeExplorer();
        InitializeResourceSelection();
        InitializeResourceInteractions();
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Library.ico");
        AppWindow.SetIcon(iconPath);
        _tray = new TrayIcon(instanceKey, iconPath, text["Title"], text["OpenLibrary"], text["ExitLibrary"]);
        _tray.RestoreRequested += () => DispatcherQueue.TryEnqueue(RestoreFromTray);
        _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(async () => await ExitFromTrayAsync());
        Closed += (_, _) => { _searchTimer.Stop(); _tray.Dispose(); };
    }

    private void UpdateTitleBar()
    {
        if (new AccessibilitySettings().HighContrast) return;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Colors.LightGray : Colors.DimGray;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e) => await RunAsync(() => { }, "Ready");

    private void Render(LibrarySnapshot snapshot, string? selectedId, bool preserveSelection = false)
    {
        var previousId = _vm.Selected?.Id;
        var detailsWereOpen = _detailsOpen;
        var selectedIds = preserveSelection ? _vm.SelectedRows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal) : [];
        if (selectedId != null) selectedIds.Add(selectedId);
        _rendering = true;
        try
        {
            _vm.Load(snapshot);
            NavigationList.SelectedItem = _vm.Navigation.FirstOrDefault(entry => entry.Id == _vm.NavigationId);
            _vm.SelectMany(_vm.Rows.Where(row => selectedIds.Contains(row.Id)));
            SyncResourceSelection();
            var selected = _vm.Selected;
            _detailsOpen = selected != null && (selected.Id != previousId || detailsWereOpen);
            ApplyPresentation();
            RefreshCutAppearance();
            UpdateInteractionCommands();
        }
        finally { _rendering = false; }
    }

    private LibrarySnapshot ReadMetadata()
    {
        var previous = _vm.Snapshot.Items.ToDictionary(item => item.Id);
        var snapshot = _library.GetSnapshot(false);
        return snapshot with
        {
            Items = snapshot.Items.Select(item => previous.TryGetValue(item.Id, out var old) && old.Target == item.Target
                ? item with { IsMissing = old.IsMissing } : item).ToArray()
        };
    }

    private void AliasBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Run after the pointer places its caret so entering the field retains selection.
        DispatcherQueue.TryEnqueue(SelectAliasName);
    }

    private void SelectAliasName()
    {
        AliasBox.SelectAll();
    }

    private void SyncDraft()
    {
        if (_vm.Selected == null) return;
        _vm.Alias = AliasBox.Text;
        _vm.Description = DescriptionBox.Text;
        _vm.Note = NoteBox.Text;
        if (_vm.Selected.IsUrl) _vm.UrlTarget = UrlTargetBox.Text;
    }

    private async Task<bool> SaveDetailsAsync()
    {
        if (_vm.IsBusy) return false;
        SyncDraft();
        if (_vm.Selected == null || !_vm.IsDirty) return true;
        var expected = _vm.DraftBaseline!;
        string? url = null;
        byte[]? favicon = null;
        if (_vm.Selected.IsUrl)
        {
            try { url = ResourceUrls.Normalize(_vm.UrlTarget); }
            catch (ArgumentException exception) { await ShowErrorAsync(exception); return false; }
            if (url != expected.Target)
            {
                _vm.IsBusy = true;
                _vm.Status = _text["FetchingWebsite"];
                try { favicon = (await new UrlMetadataFetcher().FetchAsync(url)).Favicon; }
                catch { /* Metadata is optional; saving a valid URL must remain available. */ }
                finally { _vm.IsBusy = false; }
            }
        }
        var edit = _vm.CreateEditDraft(url)!;
        return await RunAsync(() => _library.ApplyResourceEdit(edit.Expected, edit.Metadata,
            edit.AddProjectIds, edit.RemoveProjectIds, edit.Url, favicon), "Saved", expected.Id, false);
    }

    private async Task<bool> EnsureEditsAsync()
    {
        if (_dialogOpen || _vm.IsBusy) return false;
        SyncDraft();
        if (!_vm.IsDirty) return true;
        _searchTimer.Stop();
        ContentDialogResult result;
        _dialogOpen = true;
        try
        {
            result = await Dialogs.ConfirmAsync(Root, _text, _text["Unsaved"],
                _text.IsEnglish ? "Save your changes before continuing?" : "是否保存当前资源的修改后继续？", "Save", "Discard");
        }
        finally { _dialogOpen = false; }
        if (result == ContentDialogResult.None) return false;
        if (result == ContentDialogResult.Primary) return await SaveDetailsAsync();
        _vm.Select(_vm.Selected);
        return true;
    }

    private async Task<bool> RunAsync(Action action, string successKey = "Saved", string? selectedId = null, bool checkPaths = true,
        bool refreshOnFailure = false)
    {
        if (_vm.IsBusy) return false;
        _searchTimer.Stop();
        selectedId ??= _vm.Selected?.Id;
        _vm.IsBusy = true;
        _vm.Status = _text["Working"];
        try
        {
            await Task.Run(action);
            if (checkPaths) _vm.InvalidateFileMetadata();
            var snapshot = await Task.Run(() => checkPaths ? _library.GetSnapshot() : ReadMetadata());
            Render(snapshot, selectedId, preserveSelection: true);
            _vm.Status = _text[successKey];
            return true;
        }
        catch (Exception exception)
        {
            if (refreshOnFailure)
            {
                try
                {
                    _vm.InvalidateFileMetadata();
                    var snapshot = await Task.Run(() => _library.GetSnapshot());
                    Render(snapshot, selectedId, preserveSelection: true);
                }
                catch { /* Keep the original open error if refreshing the library also fails. */ }
            }
            await ShowErrorAsync(exception);
            return false;
        }
        finally { _vm.IsBusy = false; UpdateInteractionCommands(); }
    }

    private async Task ShowErrorAsync(Exception exception)
    {
        _vm.Status = _text["Error"];
        if (_dialogOpen) return;
        _dialogOpen = true;
        try { await Dialogs.MessageAsync(Root, _text, _text["Error"], _text["ErrorHelp"] + ErrorText.Format(exception, _text)); }
        finally { _dialogOpen = false; }
    }

    private async void Resource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;
        await HandleResourceSelectionChangedAsync((ListViewBase)sender);
    }

    private async void Navigation_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering) return;
        var navigation = NavigationList.SelectedItem as NavigationEntry;
        // Up/Down may focus a header on its way to a project. Expansion is an explicit tap or key action.
        if (navigation is { IsHeader: true }) return;
        if (navigation == null || navigation.Id == _vm.NavigationId) return;
        if (!await EnsureEditsAsync())
        {
            _rendering = true;
            NavigationList.SelectedItem = _vm.Navigation.FirstOrDefault(entry => entry.Id == _vm.NavigationId);
            _rendering = false;
            return;
        }
        if (_vm.TrySetNavigation(navigation)) Render(_vm.Snapshot, null);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_rendering) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async Task ApplySearchAsync()
    {
        if (_vm.IsBusy) return;
        if (!await EnsureEditsAsync())
        {
            _rendering = true;
            SearchBox.Text = _vm.Query;
            _rendering = false;
            return;
        }
        _vm.Query = SearchBox.Text;
        Render(_vm.Snapshot, _vm.Selected?.Id, preserveSelection: true);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveDetailsAsync();
    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsBusy) _vm.Select(_vm.Selected);
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // F5 re-reads shared metadata as well as physical availability, after resolving local edits.
        if (await EnsureEditsAsync()) await RunAsync(() => { }, "RefreshDone");
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        var file = new MenuFlyoutItem { Text = _text["AddFile"], Icon = new SymbolIcon(Symbol.Document) };
        var folder = new MenuFlyoutItem { Text = _text["AddFolder"], Icon = new SymbolIcon(Symbol.Folder) };
        var url = new MenuFlyoutItem { Text = _text["AddUrl"], Icon = new FontIcon { Glyph = "\uE774" } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(url, "AddUrlMenu");
        file.Click += AddFile_Click;
        folder.Click += AddFolder_Click;
        url.Click += AddUrl_Click;
        menu.Items.Add(file);
        menu.Items.Add(folder);
        menu.Items.Add(url);
        menu.ShowAt((FrameworkElement)sender);
    }

    private async void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureEditsAsync()) return;
        Dialogs.UrlInput? input;
        _dialogOpen = true;
        try { input = await Dialogs.UrlAsync(Root, _text, _vm.Snapshot, _vm.CurrentProjectId); }
        catch (Exception exception) { _dialogOpen = false; await ShowErrorAsync(exception); return; }
        finally { _dialogOpen = false; }
        if (input == null) return;
        AddUrlResult? result = null;
        if (await RunAsync(() => result = _library.AddUrl(input.Target, input.Alias, input.Description,
                input.Note, input.ProjectIds, input.Favicon), checkPaths: false) && result != null)
        {
            Render(_vm.Snapshot, result.Item.Id);
            _vm.Status = _text.Format("AddResult", result.Added ? 1 : 0, result.Added ? 0 : 1);
        }
    }

    private async void EditUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected?.IsUrl != true || !await EnsureEditsAsync()) return;
        var item = _vm.Selected.Item;
        Dialogs.UrlInput? input;
        _dialogOpen = true;
        try { input = await Dialogs.UrlAsync(Root, _text, _vm.Snapshot, _vm.CurrentProjectId, item); }
        catch (Exception exception) { _dialogOpen = false; await ShowErrorAsync(exception); return; }
        finally { _dialogOpen = false; }
        if (input == null) return;
        var edit = ResourceEditDraft.Create(item, input.Alias, input.Description, input.Note, input.ProjectIds, input.Target);
        if (!await RunAsync(() => _library.ApplyResourceEdit(edit.Expected, edit.Metadata,
                edit.AddProjectIds, edit.RemoveProjectIds, edit.Url, input.Favicon), "Saved", item.Id, false))
        {
            // Keep the user's attempted edit visible when a concurrent write prevents saving.
            _vm.Alias = input.Alias;
            _vm.Description = input.Description;
            _vm.Note = input.Note;
            _vm.UrlTarget = input.Target;
            foreach (var choice in _vm.Memberships) choice.IsSelected = input.ProjectIds.Contains(choice.Id);
        }
    }

    private void EditDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSingleSelection) return;
        _detailsOpen = true;
        UpdatePaneWidths();
        AliasBox.Focus(FocusState.Keyboard);
        SelectAliasName();
    }

    private async void AddFile_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureEditsAsync()) return;
        try
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0) await AddPathsAsync(files.Select(file => file.Path));
        }
        catch (Exception exception) { await ShowErrorAsync(exception); }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureEditsAsync()) return;
        try
        {
            var picker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) await AddPathsAsync([folder.Path]);
        }
        catch (Exception exception) { await ShowErrorAsync(exception); }
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        await ImportReferencesAsync(paths.ToArray(), _vm.CurrentProjectId);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = !_vm.IsBusy && !_dialogOpen && HasResourceData(e.DataView) &&
            !e.DataView.Contains(ProjectDragFormat) ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_vm.IsBusy || !HasResourceData(e.DataView) || e.DataView.Contains(ProjectDragFormat)) return;
        var deferral = e.GetDeferral();
        try
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            await PasteDataAsync(e.DataView, _vm.CurrentProjectId, fromDrag: true, move: IsShiftDrag(e));
        }
        catch (Exception exception) { await ShowErrorAsync(exception); }
        finally { deferral.Complete(); }
    }

    private async void NewProject_Click(SplitButton sender, SplitButtonClickEventArgs e) => await NewProjectAsync();

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var id = _vm.Selected?.Id;
        if (id != null && await EnsureEditsAsync())
            await RunAsync(() => _library.Open(id), "Ready", id, refreshOnFailure: true);
    }

    private async void Location_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected?.IsUrl == true) return;
        var id = _vm.Selected?.Id;
        if (id != null && await EnsureEditsAsync()) await RunAsync(() => _library.OpenLocation(id), "Ready", id);
    }

    private async void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSelection) return;
        try
        {
            var data = new DataPackage();
            data.SetText(string.Join(Environment.NewLine, _vm.SelectedRows.Select(row => row.Target)));
            Clipboard.SetContent(data);
            _vm.Status = _text[_vm.SelectedRows.All(row => row.IsUrl) ? "UrlCopied" :
                _vm.SelectedRows.Any(row => row.IsUrl) ? "TargetsCopied" : "Copied"];
        }
        catch (Exception exception) { await ShowErrorAsync(exception); }
    }

    private void Resource_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var parent = e.OriginalSource as DependencyObject;
        while (parent != null)
        {
            if (parent is ListViewItem or GridViewItem) { Open_Click(sender, e); return; }
            parent = VisualTreeHelper.GetParent(parent);
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_vm.IsBusy || _dialogOpen) return;
        var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        if (control && e.Key == VirtualKey.F) { SearchBox.Focus(FocusState.Keyboard); SearchBox.SelectAll(); e.Handled = true; }
        else if (control && e.Key == VirtualKey.S) { Save_Click(sender, e); e.Handled = true; }
        else if (e.Key == VirtualKey.F5) { Refresh_Click(sender, e); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { CancelResourceMarquee(); CancelCutResources(); if (_vm.HasSelection) ClearSelectionAsync(); e.Handled = true; }
        else if (control && !IsTextEditingFocus() && e.Key == VirtualKey.C && _vm.HasSelection) { CopyResources_Click(sender, e); e.Handled = true; }
        else if (control && !IsTextEditingFocus() && e.Key == VirtualKey.X && _vm.HasSelection) { CutResources_Click(sender, e); e.Handled = true; }
        else if (control && !IsTextEditingFocus() && e.Key == VirtualKey.V) { PasteResources_Click(sender, e); e.Handled = true; }
        else if (control && !IsTextEditingFocus() && e.Key == VirtualKey.Z) { Undo_Click(sender, e); e.Handled = true; }
        else if (control && !IsTextEditingFocus() && e.Key == VirtualKey.Y) { Redo_Click(sender, e); e.Handled = true; }
        else if (control && e.Key == VirtualKey.A && !IsTextEditingFocus()) { SelectAll_Click(sender, e); e.Handled = true; }
        else if (e.Key == VirtualKey.Delete && IsResourceFocus()) { DeleteResources_Click(sender, e); e.Handled = true; }
        else if (e.Key == VirtualKey.Enter && IsResourceFocus()) { Open_Click(sender, e); e.Handled = true; }
        else if (e.Key == VirtualKey.F2 && IsResourceFocus() && _vm.HasSingleSelection) { _detailsOpen = true; UpdatePaneWidths(); AliasBox.Focus(FocusState.Keyboard); SelectAliasName(); e.Handled = true; }
    }

    private void RestoreFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        TrayIcon.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private async Task ExitFromTrayAsync()
    {
        RestoreFromTray();
        if (_vm.IsBusy || _dialogOpen) { _vm.Status = _text["BusyClose"]; return; }
        if (await EnsureEditsAsync()) { _allowClose = true; Close(); }
    }

    private void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        // Hiding keeps the current draft, dialogs, and in-flight operations alive.
        args.Cancel = true;
        SyncDraft();
        sender.Hide();
    }
}
