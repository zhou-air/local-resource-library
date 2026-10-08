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
    }

    private async Task<bool> SaveDetailsAsync()
    {
        SyncDraft();
        if (_vm.Selected == null || !_vm.IsDirty) return true;
        var id = _vm.Selected.Id;
        var alias = _vm.AliasForSave;
        var description = _vm.Description;
        var note = _vm.Note;
        var projects = _vm.Memberships.Where(choice => choice.IsSelected).Select(choice => choice.Id).ToArray();
        return await RunAsync(() => _library.UpdateItem(id, alias, description, note, projects), "Saved", id, false);
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
        finally { _vm.IsBusy = false; }
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
        await ChangeResourceSelectionAsync(((ListViewBase)sender).SelectedItems.OfType<ResourceRow>().Select(row => row.Id).ToArray());
    }

    private async void Navigation_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering) return;
        var navigation = NavigationList.SelectedItem as NavigationEntry;
        if (navigation == null || navigation.Id == _vm.NavigationId) return;
        if (!await EnsureEditsAsync())
        {
            _rendering = true;
            NavigationList.SelectedItem = _vm.Navigation.FirstOrDefault(entry => entry.Id == _vm.NavigationId);
            _rendering = false;
            return;
        }
        _vm.NavigationId = navigation.Id;
        Render(_vm.Snapshot, null);
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
    private void Discard_Click(object sender, RoutedEventArgs e) => _vm.Select(_vm.Selected);
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (await EnsureEditsAsync()) await RunAsync(() => { }, "RefreshDone");
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        var file = new MenuFlyoutItem { Text = _text["AddFile"], Icon = new SymbolIcon(Symbol.Document) };
        var folder = new MenuFlyoutItem { Text = _text["AddFolder"], Icon = new SymbolIcon(Symbol.Folder) };
        file.Click += AddFile_Click;
        folder.Click += AddFolder_Click;
        menu.Items.Add(file);
        menu.Items.Add(folder);
        menu.ShowAt((FrameworkElement)sender);
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
        AddResourcesResult? result = null;
        var projectId = _vm.CurrentProjectId;
        var values = paths.ToArray();
        if (!await RunAsync(() => result = _library.AddPaths(values, projectId)) || result == null) return;
        _vm.Status = _text.Format("AddResult", result.Added, result.Existing);
        if (result.Errors.Count > 0)
        {
            _dialogOpen = true;
            try { await Dialogs.MessageAsync(Root, _text, _text["AddErrors"], string.Join("\n", result.Errors.Take(12).Select(error => ErrorText.FormatMessage(error, _text)))); }
            finally { _dialogOpen = false; }
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = !_vm.IsBusy && e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Link : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_vm.IsBusy || !e.DataView.Contains(StandardDataFormats.StorageItems) || !await EnsureEditsAsync()) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            await AddPathsAsync(items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)));
        }
        catch (Exception exception) { await ShowErrorAsync(exception); }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureEditsAsync()) return;
        _dialogOpen = true;
        (string Value, string Description)? result;
        try { result = await Dialogs.InputAsync(Root, _text, _text["NewProject"], _text["ProjectName"], "", description: ""); }
        finally { _dialogOpen = false; }
        if (result == null) return;
        Project? project = null;
        if (await RunAsync(() => project = _library.CreateProject(result.Value.Value, result.Value.Description)) && project != null)
        {
            _vm.NavigationId = project.Id;
            Render(_vm.Snapshot, null);
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var id = _vm.Selected?.Id;
        if (id != null && await EnsureEditsAsync())
            await RunAsync(() => _library.Open(id), "Ready", id, refreshOnFailure: true);
    }

    private async void Location_Click(object sender, RoutedEventArgs e)
    {
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
            _vm.Status = _text["Copied"];
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
        else if (e.Key == VirtualKey.Escape && _vm.HasSelection) { ClearSelectionAsync(); e.Handled = true; }
        else if (control && e.Key == VirtualKey.A && IsResourceFocus()) { SelectAll_Click(sender, e); e.Handled = true; }
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
