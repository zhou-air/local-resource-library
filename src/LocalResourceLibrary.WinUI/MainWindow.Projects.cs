using LocalResourceLibrary.Core;
using LocalResourceLibrary.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace LocalResourceLibrary.WinUI;

public sealed partial class MainWindow
{
    private const string ProjectDragFormat = "LocalResourceLibrary.Project";
    private NavigationEntry? _draggedProject;
    private sealed record ProjectDropDestination(NavigationEntry Entry, bool After, bool IntoGroup,
        ListViewItem Container);

    private void ToggleNavigationGroup(NavigationEntry entry)
    {
        if (_vm.IsBusy || _dialogOpen || !entry.IsGroup) return;
        SyncDraft();
        _rendering = true;
        try
        {
            _vm.SetGroupExpanded(entry.GroupId!, !entry.IsExpanded);
            // Keep header focus by permanent Group ID, so Down can enter the expanded group's children.
            NavigationList.SelectedItem = _vm.Navigation.FirstOrDefault(candidate => candidate.Id == entry.Id);
        }
        finally { _rendering = false; }
    }

    private void Navigation_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ItemAtSource(NavigationList, e.OriginalSource) is not NavigationEntry { IsGroup: true } group) return;
        e.Handled = true;
        ToggleNavigationGroup(group);
    }

    private void Navigation_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space) || NavigationList.SelectedItem is not NavigationEntry { IsGroup: true } group) return;
        e.Handled = true;
        ToggleNavigationGroup(group);
    }

    private async Task ToggleProjectPinAsync(NavigationEntry project)
    {
        if (_vm.IsBusy || _dialogOpen || !await EnsureEditsAsync()) return;
        await RunAsync(() => _library.SetProjectPinned(project.Id, !project.IsPinned), checkPaths: false);
    }

    private async Task NewProjectAsync(string? groupId = null)
    {
        if (!await EnsureEditsAsync()) return;
        _dialogOpen = true;
        Dialogs.ProjectInput? result;
        try { result = await Dialogs.ProjectAsync(Root, _text, _vm.Snapshot, groupId: groupId); }
        finally { _dialogOpen = false; }
        if (result == null) return;
        Project? project = null;
        if (await RunAsync(() => project = _library.CreateProject(result.Name, result.Description, result.Color, result.GroupId), checkPaths: false) && project != null)
        {
            _rendering = true;
            try { if (project.GroupId != null) _vm.SetGroupExpanded(project.GroupId, true); }
            finally { _rendering = false; }
            _vm.NavigationId = project.Id;
            Render(_vm.Snapshot, null);
        }
    }

    private async void NewGroup_Click(object sender, RoutedEventArgs e) => await NewGroupAsync();

    private async Task NewGroupAsync()
    {
        if (!await EnsureEditsAsync()) return;
        _dialogOpen = true;
        (string Value, string Description)? result;
        try { result = await Dialogs.InputAsync(Root, _text, _text["NewGroup"], _text["GroupName"], ""); }
        finally { _dialogOpen = false; }
        if (result != null) await RunAsync(() => _library.CreateProjectGroup(result.Value.Value), checkPaths: false);
    }

    private async Task RenameGroupAsync(string id)
    {
        if (!await EnsureEditsAsync()) return;
        var group = (_vm.Snapshot.ProjectGroups ?? []).FirstOrDefault(group => group.Id == id);
        if (group == null) return;
        _dialogOpen = true;
        (string Value, string Description)? result;
        try { result = await Dialogs.InputAsync(Root, _text, _text["RenameGroup"], _text["GroupName"], group.Name); }
        finally { _dialogOpen = false; }
        if (result != null) await RunAsync(() => _library.PatchProjectGroup(id, result.Value.Value), checkPaths: false);
    }

    private async Task DeleteGroupAsync(string id)
    {
        if (!await EnsureEditsAsync()) return;
        var group = (_vm.Snapshot.ProjectGroups ?? []).FirstOrDefault(group => group.Id == id);
        if (group == null || !await ConfirmDeletionAsync(_text["DeleteGroup"], _text.Format("DeleteGroupPrompt", group.Name))) return;
        await RunAsync(() => _library.DeleteProjectGroup(id), "Deleted", checkPaths: false);
    }

    private async Task MoveGroupAsync(string id, int direction)
    {
        if (!await EnsureEditsAsync()) return;
        var groups = (_vm.Snapshot.ProjectGroups ?? []).OrderBy(group => group.SortOrder).ToArray();
        var index = Array.FindIndex(groups, group => group.Id == id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= groups.Length) return;
        await RunAsync(() => _library.MoveProjectGroup(id, groups[target].Id, direction > 0), checkPaths: false);
    }

    private async Task ChangeProjectColorAsync(string id, string color)
    {
        if (!await EnsureEditsAsync()) return;
        var project = _vm.Snapshot.Projects.FirstOrDefault(project => project.Id == id);
        if (project != null) await RunAsync(() => _library.PatchProject(id, color: color, expected: project), checkPaths: false);
    }

    private async Task ChangeProjectGroupAsync(string id, string? groupId)
    {
        if (!await EnsureEditsAsync()) return;
        await RunAsync(() => _library.MoveProjectToGroup(id, groupId), checkPaths: false);
    }

    private async Task EditProjectPropertiesAsync(string id)
    {
        if (!await EnsureEditsAsync()) return;
        var project = _vm.Snapshot.Projects.FirstOrDefault(project => project.Id == id);
        if (project == null) return;
        _dialogOpen = true;
        Dialogs.ProjectInput? result;
        try { result = await Dialogs.ProjectAsync(Root, _text, _vm.Snapshot, project); }
        finally { _dialogOpen = false; }
        if (result == null) return;
        var groupChanged = result.GroupId != project.GroupId;
        await RunAsync(() => _library.PatchProject(id,
            name: result.Name == project.Name ? null : result.Name,
            description: result.Description == project.Description ? null : result.Description,
            expected: project, color: result.Color == project.Color ? null : result.Color,
            groupId: groupChanged ? result.GroupId : null,
            clearGroup: groupChanged && result.GroupId == null), checkPaths: false);
    }

    private void Navigation_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedProject = null;
        HideProjectDropIndicator();
        if (_vm.IsBusy || _dialogOpen || e.Items.Count != 1 || e.Items[0] is not NavigationEntry { IsProject: true } project)
        {
            e.Cancel = true;
            return;
        }
        _draggedProject = project;
        e.Data.SetData(ProjectDragFormat, project.Id);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void Navigation_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        _draggedProject = null;
        HideProjectDropIndicator();
    }

    private ProjectDropDestination? ProjectDropTarget(DragEventArgs e)
    {
        if (_draggedProject == null) return null;
        var point = e.GetPosition(NavigationList);
        NavigationEntry? last = null;
        ListViewItem? lastContainer = null;
        foreach (var entry in _vm.Navigation)
        {
            if (NavigationList.ContainerFromItem(entry) is not ListViewItem container) continue;
            var top = container.TransformToVisual(NavigationList).TransformPoint(new Windows.Foundation.Point()).Y;
            if (point.Y >= top && point.Y < top + container.ActualHeight)
            {
                if (entry.IsGroup || entry.Id == "@unassigned") return new(entry, false, true, container);
                return entry.IsProject && entry.IsPinned == _draggedProject.IsPinned && entry.Id != _draggedProject.Id
                    ? new(entry, point.Y >= top + container.ActualHeight / 2, false, container) : null;
            }
            if (point.Y >= top + container.ActualHeight) { last = entry; lastContainer = container; }
        }
        if (last == null || lastContainer == null) return null;
        if (last.IsGroup || last.Id == "@unassigned") return new(last, false, true, lastContainer);
        return last.IsProject && last.IsPinned == _draggedProject.IsPinned && last.Id != _draggedProject.Id
            ? new(last, true, false, lastContainer) : null;
    }

    private void HideProjectDropIndicator() => NavigationDropIndicator.Visibility = Visibility.Collapsed;
    private void Navigation_DragLeave(object sender, DragEventArgs e) { HideProjectDropIndicator(); StopResourceGroupExpand(); }

    private void ShowProjectDropIndicator(ProjectDropDestination destination)
    {
        NavigationDropIndicator.Background = null;
        var top = destination.Container.TransformToVisual(NavigationList).TransformPoint(new Windows.Foundation.Point()).Y;
        var height = destination.Container.ActualHeight;
        NavigationDropIndicator.Margin = new Thickness(4,
            Math.Max(0, destination.IntoGroup ? top : top + (destination.After ? height - 2 : 0)), 4, 0);
        NavigationDropIndicator.Height = destination.IntoGroup ? height : 2;
        NavigationDropIndicator.Visibility = Visibility.Visible;
    }

    private void Navigation_DragOver(object sender, DragEventArgs e)
    {
        if (ResourceNavigationDragOver(e)) return;
        if (!e.DataView.Contains(ProjectDragFormat)) { HideProjectDropIndicator(); return; }
        e.Handled = true;
        var target = ProjectDropTarget(e);
        var accepted = !_vm.IsBusy && !_dialogOpen && target != null;
        e.AcceptedOperation = accepted ? DataPackageOperation.Move : DataPackageOperation.None;
        e.DragUIOverride.IsCaptionVisible = true;
        if (accepted && target is { } destination)
        {
            ShowProjectDropIndicator(destination);
            e.DragUIOverride.Caption = destination.IntoGroup
                ? Label($"移到“{destination.Entry.Name}”", $"Move to {destination.Entry.Name}")
                : Label($"移到“{destination.Entry.Name}”{(destination.After ? "之后" : "之前")}",
                    $"Move {(destination.After ? "after" : "before")} {destination.Entry.Name}");
        }
        else HideProjectDropIndicator();
    }

    private async void Navigation_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(ProjectDragFormat) && HasResourceData(e.DataView)) { await ResourceNavigationDropAsync(e); return; }
        if (!e.DataView.Contains(ProjectDragFormat)) return;
        e.Handled = true;
        var source = _draggedProject;
        var target = ProjectDropTarget(e);
        _draggedProject = null;
        HideProjectDropIndicator();
        if (_vm.IsBusy || _dialogOpen || source == null || target == null) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        if (!await EnsureEditsAsync()) return;
        await RunAsync(() =>
        {
            if (target.IntoGroup) _library.MoveProjectToGroup(source.Id, target.Entry.GroupId);
            else _library.MoveProject(source.Id, target.Entry.Id, target.After);
        }, checkPaths: false);
    }
}
