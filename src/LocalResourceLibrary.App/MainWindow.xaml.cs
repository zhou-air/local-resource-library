using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LocalResourceLibrary.App.Localization;
using LocalResourceLibrary.App.Services;
using LocalResourceLibrary.App.ViewModels;
using LocalResourceLibrary.Core;
using Microsoft.Win32;

namespace LocalResourceLibrary.App;

public partial class MainWindow : Window
{
    private readonly LibraryService _library;
    private readonly Localizer _text;
    private readonly SettingsStore _settings;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private bool _rendering = true;
    private bool _promptingForEdits;
    public MainWindow(LibraryService library, Localizer text, SettingsStore settings)
    {
        _library = library; _text = text; _settings = settings;
        _vm = new MainViewModel(text, library.DatabasePath);
        InitializeComponent(); DataContext = _vm;
        LanguageBox.SelectedIndex = text.IsEnglish ? 1 : 0;
        UpdateColumnHeaders();
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplySearch(); };
        _rendering = false;
        Loaded += async (_, _) => await RunAsync(() => { }, "Ready");
    }
    private void UpdateColumnHeaders()
    {
        ResourceGrid.Columns[0].Header = _text["Resource"];
        ResourceGrid.Columns[1].Header = _text["Projects"];
        ResourceGrid.Columns[2].Header = _text["State"];
    }
    private void Render(LibrarySnapshot snapshot, string? selectedId)
    {
        _rendering = true;
        try
        {
            _vm.Load(snapshot);
            NavigationList.SelectedItem = _vm.Navigation.FirstOrDefault(p => p.Id == _vm.NavigationId);
            var selected = _vm.Rows.FirstOrDefault(r => r.Id == selectedId);
            _vm.Select(selected); ResourceGrid.SelectedItem = selected;
        }
        finally { _rendering = false; }
    }
    private LibrarySnapshot ReadMetadata()
    {
        var old = _vm.Snapshot.Items.ToDictionary(i => i.Id);
        var snapshot = _library.GetSnapshot(false);
        return snapshot with { Items = snapshot.Items.Select(i => old.TryGetValue(i.Id, out var previous) && previous.Target == i.Target ? i with { IsMissing = previous.IsMissing } : i).ToArray() };
    }
    private bool SaveDetails()
    {
        if (_vm.Selected == null || !_vm.IsDirty) return true;
        try
        {
            var id = _vm.Selected.Id;
            _library.UpdateItem(id, _vm.Alias, _vm.Description, _vm.Note, _vm.Memberships.Where(p => p.IsSelected).Select(p => p.Id));
            Render(ReadMetadata(), id); _vm.Status = _text["Saved"]; return true;
        }
        catch (Exception ex) { ShowError(ex); return false; }
    }
    private bool EnsureEdits()
    {
        if (!_vm.IsDirty) return true;
        if (_promptingForEdits) return false;
        _searchTimer.Stop();
        _promptingForEdits = true;
        try
        {
            var result = MessageBox.Show(this, _text["SavePrompt"], _text["Title"], MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel)
            {
                _rendering = true; SearchBox.Text = _vm.Query; _rendering = false;
                return false;
            }
            if (result == MessageBoxResult.Yes && !SaveDetails()) return false;
            if (result == MessageBoxResult.No) _vm.Select(_vm.Selected);
            if (SearchBox.Text != _vm.Query) _searchTimer.Start();
            return true;
        }
        finally { _promptingForEdits = false; }
    }
    private async Task RunAsync(Action action, string successKey = "Saved", string? selectedId = null)
    {
        if (_vm.IsBusy) return;
        _searchTimer.Stop();
        _vm.Query = SearchBox.Text;
        selectedId ??= _vm.Selected?.Id;
        _vm.IsBusy = true; _vm.Status = _text["Working"];
        try
        {
            await Task.Run(action);
            var snapshot = await Task.Run(() => _library.GetSnapshot());
            Render(snapshot, selectedId); _vm.Status = _text[successKey];
        }
        catch (Exception ex)
        {
            ShowError(ex);
            try { Render(await Task.Run(() => _library.GetSnapshot()), selectedId); } catch { /* Keep current metadata if refresh also fails. */ }
        }
        finally { _vm.IsBusy = false; }
    }
    private void ShowError(Exception ex)
    {
        _vm.Status = _text["Error"];
        MessageBox.Show(this, _text["ErrorHelp"] + ErrorText.Format(ex, _text), _text["Error"], MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void Resource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;
        var newId = (ResourceGrid.SelectedItem as ResourceRow)?.Id;
        if (_vm.Selected?.Id == newId) return;
        if (!EnsureEdits())
        {
            _rendering = true; ResourceGrid.SelectedItem = _vm.Selected; _rendering = false; return;
        }
        _rendering = true;
        var row = _vm.Rows.FirstOrDefault(r => r.Id == newId);
        _vm.Select(row); ResourceGrid.SelectedItem = row;
        _rendering = false;
    }
    private void Navigation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || NavigationList.SelectedItem is not NavigationEntry nav) return;
        if (!EnsureEdits())
        {
            _rendering = true; NavigationList.SelectedItem = _vm.Navigation.FirstOrDefault(p => p.Id == _vm.NavigationId); _rendering = false; return;
        }
        _vm.NavigationId = nav.Id;
        Render(_vm.Snapshot, _vm.Selected?.Id);
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_rendering) return;
        _searchTimer.Stop(); _searchTimer.Start();
    }
    private void ApplySearch()
    {
        if (_vm.IsBusy) return;
        if (!EnsureEdits()) { _rendering = true; SearchBox.Text = _vm.Query; _rendering = false; return; }
        _vm.Query = SearchBox.Text;
        Render(_vm.Snapshot, _vm.Selected?.Id);
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Clear();
    private void Save_Click(object sender, RoutedEventArgs e) => SaveDetails();
    private void Discard_Click(object sender, RoutedEventArgs e) => _vm.Select(_vm.Selected);
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (EnsureEdits()) await RunAsync(() => { }, "RefreshDone"); }
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || LanguageBox.SelectedItem is not ComboBoxItem choice) return;
        var language = (string)choice.Tag;
        try { _settings.SaveLanguage(language); }
        catch (Exception ex) { ShowError(ex); _rendering = true; LanguageBox.SelectedIndex = _text.IsEnglish ? 1 : 0; _rendering = false; return; }
        // Preserve unsaved edit fields while recreating translated rows and navigation labels.
        var selectedId = _vm.Selected?.Id; var alias = _vm.Alias; var description = _vm.Description; var note = _vm.Note;
        var memberships = _vm.Memberships.Where(p => p.IsSelected).Select(p => p.Id).ToHashSet();
        _text.ChangeLanguage(language); UpdateColumnHeaders(); Render(_vm.Snapshot, selectedId);
        _vm.Alias = alias; _vm.Description = description; _vm.Note = note;
        foreach (var membership in _vm.Memberships) membership.IsSelected = memberships.Contains(membership.Id);
        _vm.Status = _text["Ready"];
    }
    private MenuItem Menu(string key, RoutedEventHandler action)
    {
        var entry = new MenuItem { Header = _text[key] }; entry.Click += action; return entry;
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Menu("AddFile", AddFile_Click)); menu.Items.Add(Menu("AddFolder", AddFolder_Click));
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
    private async void AddFile_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEdits()) return;
        var dialog = new OpenFileDialog { Multiselect = true, Title = _text["AddFile"], CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FileNames);
    }
    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEdits()) return;
        var dialog = new OpenFolderDialog { Multiselect = true, Title = _text["AddFolder"] };
        if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FolderNames);
    }
    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        AddResourcesResult? result = null;
        var projectId = _vm.CurrentProjectId;
        await RunAsync(() => result = _library.AddPaths(paths, projectId));
        if (result == null) return;
        _vm.Status = _text.Format("AddResult", result.Added, result.Existing);
        if (result.Errors.Count > 0) MessageBox.Show(this, string.Join("\n", result.Errors.Take(12).Select(message => ErrorText.FormatMessage(message, _text))) + (result.Errors.Count > 12 ? "\n…" : ""), _text["AddErrors"], MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_vm.IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_vm.IsBusy || !e.Data.GetDataPresent(DataFormats.FileDrop) || !EnsureEdits()) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddPathsAsync(paths);
    }
    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEdits()) return;
        var value = Dialogs.Input(this, _text, _text["NewProject"], _text["ProjectName"], "", description: "");
        if (value == null) return;
        Project? project = null;
        await RunAsync(() => project = _library.CreateProject(value.Value.Value, value.Value.Description));
        if (project != null) { _vm.NavigationId = project.Id; Render(_vm.Snapshot, _vm.Selected?.Id); }
    }
    private async void EditProject_Click(object sender, RoutedEventArgs e)
    {
        var project = _vm.Snapshot.Projects.FirstOrDefault(p => p.Id == _vm.CurrentProjectId);
        if (project == null || !EnsureEdits()) return;
        var value = Dialogs.Input(this, _text, _text["EditProject"], _text["ProjectName"], project.Name, description: project.Description);
        if (value != null) await RunAsync(() => _library.UpdateProject(project.Id, value.Value.Value, value.Value.Description));
    }
    private async void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        var project = _vm.Snapshot.Projects.FirstOrDefault(p => p.Id == _vm.CurrentProjectId);
        if (project == null || !EnsureEdits()) return;
        if (MessageBox.Show(this, _text.Format("DeleteProjectPrompt", project.Name), _text["DeleteProject"], MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await RunAsync(() => _library.DeleteProject(project.Id));
    }
    private void Navigation_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var container = FindParent<ListBoxItem>(Mouse.DirectlyOver as DependencyObject);
        if (container?.DataContext is NavigationEntry nav)
        {
            NavigationList.SelectedItem = nav;
            if (_vm.NavigationId != nav.Id) { e.Handled = true; return; }
        }
        if (_vm.CurrentProjectId == null) { e.Handled = true; return; }
        NavigationList.ContextMenu.Items.Clear();
        NavigationList.ContextMenu.Items.Add(Menu("EditProject", EditProject_Click));
        NavigationList.ContextMenu.Items.Add(Menu("DeleteProject", DeleteProject_Click));
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var id = _vm.Selected?.Id;
        if (id == null || !EnsureEdits()) return;
        await RunAsync(() => _library.Open(id), "Ready", id);
    }
    private async void Location_Click(object sender, RoutedEventArgs e)
    {
        var id = _vm.Selected?.Id;
        if (id == null || !EnsureEdits()) return;
        await RunAsync(() => _library.OpenLocation(id), "Ready", id);
    }
    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected == null) return;
        try { Clipboard.SetText(_vm.Selected.Target); _vm.Status = _text["Copied"]; } catch (Exception ex) { ShowError(ex); }
    }
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.Selected;
        if (selected == null || !EnsureEdits()) return;
        var value = Dialogs.Input(this, _text, _text["RenameTitle"], _text["NewName"], selected.RealName, _text["RenameHelp"], confirm: "Rename");
        if (value != null && value.Value.Value != selected.RealName) await RunAsync(() => _library.RenamePhysical(selected.Id, value.Value.Value));
    }
    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.Selected;
        if (selected == null || !EnsureEdits()) return;
        string? target = null;
        if (selected.Item.Type == "folder")
        {
            var dialog = new OpenFolderDialog { Title = _text["Repair"] };
            if (dialog.ShowDialog(this) == true) target = dialog.FolderName;
        }
        else
        {
            var dialog = new OpenFileDialog { Title = _text["Repair"], CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) target = dialog.FileName;
        }
        if (target != null) await RunAsync(() => _library.RepairPath(selected.Id, target));
    }
    private async void RemoveLibrary_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.Selected;
        if (selected == null || !EnsureEdits()) return;
        if (MessageBox.Show(this, _text.Format("RemovePrompt", selected.Name), _text["RemoveLibrary"], MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await RunAsync(() => _library.RemoveFromLibrary(selected.Id));
    }
    private async void AddToProject_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.Selected;
        if (selected == null || !EnsureEdits()) return;
        selected = new ResourceRow(_vm.Snapshot.Items.Single(i => i.Id == selected.Id), _text);
        var available = _vm.Snapshot.Projects.Where(p => !selected.Item.Projects.Any(q => q.Id == p.Id)).ToArray();
        if (available.Length == 0) { MessageBox.Show(this, _text["NoProjectToAdd"], _text["Title"]); return; }
        var project = Dialogs.ChooseProject(this, _text, _text["AddToProject"], available);
        if (project != null) await RunAsync(() => _library.AddToProject(selected.Id, project.Id));
    }
    private async void RemoveFromProject_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.Selected;
        if (selected == null || !EnsureEdits()) return;
        selected = new ResourceRow(_vm.Snapshot.Items.Single(i => i.Id == selected.Id), _text);
        if (selected.Item.Projects.Count == 0) { MessageBox.Show(this, _text["NothingToRemove"], _text["Title"]); return; }
        var project = Dialogs.ChooseProject(this, _text, _text["RemoveFromProject"], selected.Item.Projects);
        if (project != null) await RunAsync(() => _library.RemoveFromProject(selected.Id, project.Id));
    }
    private void Resource_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_vm.Selected == null) { e.Handled = true; return; }
        var menu = ResourceGrid.ContextMenu; menu.Items.Clear();
        menu.Items.Add(Menu("Open", Open_Click)); menu.Items.Add(Menu("OpenLocation", Location_Click)); menu.Items.Add(Menu("CopyPath", CopyPath_Click));
        menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = _text["LibraryOperations"], IsEnabled = false });
        menu.Items.Add(Menu("EditAlias", (_, _) => { AliasBox.Focus(); AliasBox.SelectAll(); }));
        menu.Items.Add(Menu("EditDescription", (_, _) => DescriptionBox.Focus())); menu.Items.Add(Menu("EditNote", (_, _) => NoteBox.Focus()));
        menu.Items.Add(Menu("AddToProject", AddToProject_Click)); menu.Items.Add(Menu("RemoveFromProject", RemoveFromProject_Click));
        menu.Items.Add(Menu("Repair", Repair_Click)); menu.Items.Add(Menu("RemoveLibrary", RemoveLibrary_Click));
        menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = _text["FileOperations"], IsEnabled = false });
        menu.Items.Add(Menu("RenamePhysical", Rename_Click));
    }
    private void Resource_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row != null)
        {
            ResourceGrid.SelectedItem = row.Item;
            if (ResourceGrid.SelectedItem != row.Item) { e.Handled = true; return; }
            row.Focus();
        }
    }
    private void Resource_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject) != null) Open_Click(sender, e);
    }
    private static T? FindParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null) { if (current is T match) return match; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current); }
        return null;
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.IsBusy) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { SaveDetails(); e.Handled = true; }
        else if (e.Key == Key.F5) { Refresh_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && ResourceGrid.IsKeyboardFocusWithin) { Open_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F2 && ResourceGrid.IsKeyboardFocusWithin) { AliasBox.Focus(); AliasBox.SelectAll(); e.Handled = true; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _searchTimer.Stop();
        if (_vm.IsBusy) { e.Cancel = true; _vm.Status = _text["BusyClose"]; return; }
        if (!EnsureEdits()) e.Cancel = true;
    }
}
