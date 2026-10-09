using LocalResourceLibrary.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LocalResourceLibrary.WinUI;

public sealed partial class MainWindow
{
    private bool _selectionChanging;

    private void SyncResourceSelection()
    {
        var selected = _vm.SelectedRows.ToHashSet();
        foreach (var control in new ListViewBase[] { ResourceList, ResourceGrid })
        {
            // Keep the native selection anchor for subsequent Shift-click and keyboard ranges.
            foreach (var row in control.SelectedItems.OfType<ResourceRow>().ToArray())
                if (!selected.Contains(row)) control.SelectedItems.Remove(row);
            var present = control.SelectedItems.OfType<ResourceRow>().ToHashSet();
            foreach (var row in _vm.SelectedRows)
                if (!present.Contains(row)) control.SelectedItems.Add(row);
        }
    }

    private async Task<bool> ChangeResourceSelectionAsync(string[] ids)
    {
        if (_selectionChanging) return false;
        _selectionChanging = true;
        try
        {
            // Editing applies to a single resource. Retain its draft if the selection is unchanged.
            var sameSingle = ids.Length == 1 && ids[0] == _vm.Selected?.Id;
            if (_vm.IsBusy || _dialogOpen || (!sameSingle && !await EnsureEditsAsync()))
            {
                _rendering = true;
                try { SyncResourceSelection(); }
                finally { _rendering = false; }
                return false;
            }
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            _rendering = true;
            try
            {
                _vm.SelectMany(_vm.Rows.Where(row => wanted.Contains(row.Id)), preserveDraft: true);
                SyncResourceSelection();
                _detailsOpen = _vm.HasSingleSelection;
                UpdatePaneWidths();
            }
            finally { _rendering = false; }
            return true;
        }
        finally { _selectionChanging = false; }
    }

    private async void SelectAll_Click(object sender, RoutedEventArgs e) =>
        await ChangeResourceSelectionAsync(_vm.Rows.Select(row => row.Id).ToArray());

    private async void DeleteResources_Click(object sender, RoutedEventArgs e) =>
        await DeleteResourcesAsync(_vm.SelectedRows.Select(row => row.Id).ToArray());

    private async Task DeleteResourcesAsync(string[] ids)
    {
        if (ids.Length == 0 || !await EnsureEditsAsync()) return;
        var selected = _vm.Snapshot.Items.Where(item => ids.Contains(item.Id)).ToArray();
        if (selected.Length == 0) return;
        var message = selected.Length == 1 ? _text.Format("RemovePrompt", selected[0].DisplayName)
            : _text.Format("RemoveManyPrompt", selected.Length);
        if (!await ConfirmDeletionAsync(_text["DeleteResources"], message)) return;
        await RunAsync(() => _library.RemoveItemsFromLibrary(selected.Select(item => item.Id)), "Deleted", checkPaths: false);
    }

    private async Task DeleteProjectAsync(string id)
    {
        if (!await EnsureEditsAsync()) return;
        var project = _vm.Snapshot.Projects.FirstOrDefault(project => project.Id == id);
        if (project == null || !await ConfirmDeletionAsync(_text["DeleteProject"], _text.Format("DeleteProjectPrompt", project.Name))) return;
        await RunAsync(() => _library.DeleteProject(id), "Deleted", checkPaths: false);
    }

    private async Task RemoveSelectionFromProjectAsync(string[] ids, string projectId)
    {
        if (ids.Length == 0 || !await EnsureEditsAsync()) return;
        var project = _vm.Snapshot.Projects.FirstOrDefault(project => project.Id == projectId);
        if (project == null || !await ConfirmDeletionAsync(_text["RemoveFromProject"],
            _text.Format("RemoveFromProjectPrompt", ids.Length, project.Name), "Confirm")) return;
        await RunAsync(() => _library.RemoveItemsFromProject(ids, projectId), "RemovedFromProject", checkPaths: false);
    }

    private async Task<bool> ConfirmDeletionAsync(string title, string message, string primary = "Delete")
    {
        _searchTimer.Stop();
        _dialogOpen = true;
        try { return await Dialogs.ConfirmAsync(Root, _text, title, message, primary) == ContentDialogResult.Primary; }
        finally { _dialogOpen = false; }
    }

    private static object? ItemAtSource(ListViewBase control, object source)
    {
        var parent = source as DependencyObject;
        while (parent != null && !ReferenceEquals(parent, control))
        {
            if (parent is ListViewItem or GridViewItem) return control.ItemFromContainer(parent);
            if (parent is ScrollBar or Thumb) return null;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private MenuFlyoutItem MenuItem(string text, string automationId, RoutedEventHandler action, Symbol? icon = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (icon is { } symbol) item.Icon = new SymbolIcon(symbol);
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += action;
        return item;
    }

    private async void Resource_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_vm.IsBusy || _dialogOpen || _selectionChanging || sender is not ListViewBase control) return;
        var row = ItemAtSource(control, e.OriginalSource) as ResourceRow;
        var position = e.GetPosition(control);
        e.Handled = true;
        if (row != null && !_vm.SelectedRows.Any(selected => selected.Id == row.Id) &&
            !await ChangeResourceSelectionAsync([row.Id])) return;
        var ids = _vm.SelectedRows.Select(selected => selected.Id).ToArray();
        var menu = new MenuFlyout();
        if (row != null && _vm.HasSingleSelection)
        {
            menu.Items.Add(MenuItem(_text["Open"], "ResourceOpenMenu", Open_Click, Symbol.OpenFile));
            if (row.IsUrl)
                menu.Items.Add(MenuItem(_text["EditUrl"], "ResourceEditUrlMenu", EditUrl_Click, Symbol.Edit));
            else
            {
                menu.Items.Add(MenuItem(_text["OpenLocation"], "ResourceLocationMenu", Location_Click, Symbol.Folder));
                menu.Items.Add(MenuItem(_text["EditDetails"], "ResourceEditDetailsMenu", EditDetails_Click, Symbol.Edit));
            }
        }
        if (row != null && ids.Length > 0)
        {
            menu.Items.Add(MenuItem(Label("复制", "Copy"), "ResourceCopyMenu", CopyResources_Click, Symbol.Copy));
            menu.Items.Add(MenuItem(Label("剪切", "Cut"), "ResourceCutMenu", CutResources_Click, Symbol.Cut));
            var copyKey = _vm.SelectedRows.All(selected => selected.IsUrl) ? "CopyUrl" :
                _vm.SelectedRows.Any(selected => selected.IsUrl) ? "CopyTargets" : "CopyPath";
            menu.Items.Add(MenuItem(_text[copyKey], "ResourceCopyPathsMenu", CopyPath_Click, Symbol.Copy));
            menu.Items.Add(new MenuFlyoutSeparator());
            if (_vm.CurrentProjectId is { } projectId)
                menu.Items.Add(MenuItem(_text["RemoveFromProject"], "ResourceRemoveProjectMenu",
                    async (_, _) => await RemoveSelectionFromProjectAsync(ids, projectId)));
            menu.Items.Add(MenuItem(_text["DeleteResources"], "ResourceDeleteMenu",
                async (_, _) => await DeleteResourcesAsync(ids), Symbol.Delete));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        var paste = MenuItem(Label("粘贴", "Paste"), "ResourcePasteMenu", PasteResources_Click, Symbol.Paste);
        paste.IsEnabled = CanPasteResources();
        menu.Items.Add(paste);
        menu.Items.Add(MenuItem(_text["SelectAll"], "ResourceSelectAllMenu", SelectAll_Click, Symbol.SelectAll));
        AddUndoRedoMenuItems(menu);
        menu.ShowAt(control, new FlyoutShowOptions { Position = position });
    }

    private void AddUndoRedoMenuItems(MenuFlyout menu)
    {
        menu.Items.Add(new MenuFlyoutSeparator());
        var undo = MenuItem(Label("撤销", "Undo"), "LibraryUndoMenu", Undo_Click, Symbol.Undo);
        undo.IsEnabled = _library.UndoState.CanUndo;
        menu.Items.Add(undo);
        var redo = MenuItem(Label("重做", "Redo"), "LibraryRedoMenu", Redo_Click, Symbol.Redo);
        redo.IsEnabled = _library.UndoState.CanRedo;
        menu.Items.Add(redo);
    }

    private async Task RenameProjectAsync(string id)
    {
        if (!await EnsureEditsAsync()) return;
        var project = _vm.Snapshot.Projects.FirstOrDefault(candidate => candidate.Id == id);
        if (project == null) return;
        _dialogOpen = true;
        (string Value, string Description)? result;
        try { result = await Dialogs.InputAsync(Root, _text, _text["RenameProject"], _text["ProjectName"], project.Name); }
        finally { _dialogOpen = false; }
        if (result != null)
            await RunAsync(() => _library.PatchProject(id, name: result.Value.Value, expected: project), checkPaths: false);
    }

    private void Navigation_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_vm.IsBusy || _dialogOpen) return;
        var entry = ItemAtSource(NavigationList, e.OriginalSource) as NavigationEntry;
        e.Handled = true;
        var menu = new MenuFlyout();
        if (entry is { IsProject: true } project)
        {
            var paste = MenuItem(Label("粘贴", "Paste"), "ProjectPasteMenu", async (_, _) => await PasteResourcesToProjectAsync(project.Id), Symbol.Paste);
            paste.IsEnabled = CanPasteResources(project.Id);
            menu.Items.Add(paste);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem(_text[project.IsPinned ? "UnpinProject" : "PinProject"], "ProjectPinMenu",
                async (_, _) => await ToggleProjectPinAsync(project)));
            menu.Items.Add(MenuItem(_text["RenameProject"], "ProjectRenameMenu", async (_, _) => await RenameProjectAsync(project.Id), Symbol.Edit));
            var colors = new MenuFlyoutSubItem { Text = _text["ProjectColor"] };
            AutomationProperties.SetAutomationId(colors, "ProjectColorsMenu");
            foreach (var color in LocalResourceLibrary.Core.ProjectColors.Presets)
            {
                var icon = new FontIcon { Glyph = "\uE8B7", FontSize = 17 };
                Services.ProjectIconColor.SetKey(icon, color.Key);
                var item = new ToggleMenuFlyoutItem { Text = Dialogs.ColorLabel(_text, color.Key), IsChecked = project.Color == color.Key, Icon = icon };
                AutomationProperties.SetAutomationId(item, "ProjectColor_" + color.Key);
                item.Click += async (_, _) => await ChangeProjectColorAsync(project.Id, color.Key);
                colors.Items.Add(item);
            }
            menu.Items.Add(colors);
            var groups = new MenuFlyoutSubItem { Text = _text["ProjectGroup"] };
            AutomationProperties.SetAutomationId(groups, "ProjectGroupsMenu");
            void AddGroup(string? id, string name)
            {
                var item = new ToggleMenuFlyoutItem { Text = name, IsChecked = project.GroupId == id };
                AutomationProperties.SetAutomationId(item, "ProjectGroup_" + (id ?? "unassigned"));
                item.Click += async (_, _) => await ChangeProjectGroupAsync(project.Id, id);
                groups.Items.Add(item);
            }
            AddGroup(null, _text["UngroupedProjects"]);
            foreach (var group in _vm.Snapshot.ProjectGroups ?? []) AddGroup(group.Id, group.Name);
            menu.Items.Add(groups);
            menu.Items.Add(MenuItem(_text["ProjectProperties"], "ProjectPropertiesMenu", async (_, _) => await EditProjectPropertiesAsync(project.Id)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem(_text["DeleteProject"], "ProjectDeleteMenu", async (_, _) => await DeleteProjectAsync(project.Id), Symbol.Delete));
        }
        else if (entry is { IsGroup: true, GroupId: { } groupId })
        {
            menu.Items.Add(MenuItem(_text["NewProject"], "GroupNewProjectMenu", async (_, _) => await NewProjectAsync(groupId)));
            menu.Items.Add(MenuItem(_text["RenameGroup"], "GroupRenameMenu", async (_, _) => await RenameGroupAsync(groupId), Symbol.Edit));
            var ordered = (_vm.Snapshot.ProjectGroups ?? []).OrderBy(group => group.SortOrder).ToArray();
            var index = Array.FindIndex(ordered, group => group.Id == groupId);
            var up = MenuItem(_text["MoveGroupUp"], "GroupMoveUpMenu", async (_, _) => await MoveGroupAsync(groupId, -1));
            up.IsEnabled = index > 0;
            menu.Items.Add(up);
            var down = MenuItem(_text["MoveGroupDown"], "GroupMoveDownMenu", async (_, _) => await MoveGroupAsync(groupId, 1));
            down.IsEnabled = index >= 0 && index < ordered.Length - 1;
            menu.Items.Add(down);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem(_text["DeleteGroup"], "GroupDeleteMenu", async (_, _) => await DeleteGroupAsync(groupId), Symbol.Delete));
        }
        else
        {
            menu.Items.Add(MenuItem(_text["NewProject"], "NavigationNewProjectMenu", async (_, _) => await NewProjectAsync()));
            menu.Items.Add(MenuItem(_text["NewGroup"], "NavigationNewGroupMenu", async (_, _) => await NewGroupAsync()));
        }
        AddUndoRedoMenuItems(menu);
        menu.ShowAt(NavigationList, new FlyoutShowOptions { Position = e.GetPosition(NavigationList) });
    }
}
