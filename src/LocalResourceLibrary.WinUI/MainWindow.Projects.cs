using LocalResourceLibrary.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace LocalResourceLibrary.WinUI;

public sealed partial class MainWindow
{
    private const string ProjectDragFormat = "LocalResourceLibrary.Project";
    private NavigationEntry? _draggedProject;

    private async Task ToggleProjectPinAsync(NavigationEntry project)
    {
        if (_vm.IsBusy || _dialogOpen || !await EnsureEditsAsync()) return;
        await RunAsync(() => _library.SetProjectPinned(project.Id, !project.IsPinned), checkPaths: false);
    }

    private void Navigation_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedProject = null;
        if (_vm.IsBusy || _dialogOpen || e.Items.Count != 1 || e.Items[0] is not NavigationEntry { IsProject: true } project)
        {
            e.Cancel = true;
            return;
        }
        _draggedProject = project;
        e.Data.SetData(ProjectDragFormat, project.Id);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void Navigation_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e) => _draggedProject = null;

    private (NavigationEntry Project, bool After)? ProjectDropTarget(DragEventArgs e)
    {
        if (_draggedProject == null) return null;
        var point = e.GetPosition(NavigationList);
        NavigationEntry? last = null;
        foreach (var entry in _vm.Navigation)
        {
            if (NavigationList.ContainerFromItem(entry) is not ListViewItem container) continue;
            var top = container.TransformToVisual(NavigationList).TransformPoint(new Windows.Foundation.Point()).Y;
            if (point.Y >= top && point.Y < top + container.ActualHeight)
                return entry.IsProject && entry.IsPinned == _draggedProject.IsPinned
                    ? (entry, point.Y >= top + container.ActualHeight / 2) : null;
            if (point.Y >= top + container.ActualHeight) last = entry;
        }
        return last is { IsProject: true } && last.IsPinned == _draggedProject.IsPinned ? (last, true) : null;
    }

    private void Navigation_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(ProjectDragFormat)) return;
        e.Handled = true;
        var target = ProjectDropTarget(e);
        e.AcceptedOperation = !_vm.IsBusy && !_dialogOpen && target != null ? DataPackageOperation.Move : DataPackageOperation.None;
        e.DragUIOverride.IsCaptionVisible = true;
        if (target is { } destination)
            e.DragUIOverride.Caption = _text.IsEnglish
                ? $"Move {(destination.After ? "after" : "before")} {destination.Project.Name}"
                : $"移到“{destination.Project.Name}”{(destination.After ? "之后" : "之前")}";
    }

    private async void Navigation_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(ProjectDragFormat)) return;
        e.Handled = true;
        var source = _draggedProject;
        var target = ProjectDropTarget(e);
        _draggedProject = null;
        if (_vm.IsBusy || _dialogOpen || source == null || target == null || source.Id == target.Value.Project.Id) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        if (!await EnsureEditsAsync()) return;
        await RunAsync(() => _library.MoveProject(source.Id, target.Value.Project.Id, target.Value.After), checkPaths: false);
    }
}
