using LocalResourceLibrary.Core;
using LocalResourceLibrary.WinUI.Services;
using LocalResourceLibrary.WinUI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace LocalResourceLibrary.WinUI;

public sealed partial class MainWindow
{
    private ClipboardReferencePayload? _cutReferences;
    private readonly HashSet<string> _completedCutTokens = new(StringComparer.Ordinal);
    private string? _hoveredResourceGroup;
    private DispatcherQueueTimer? _resourceHoverTimer;

    private void InitializeResourceInteractions()
    {
        _library.EnableHistory = true;
        _library.HistoryChanged += LibraryHistoryChanged;
        Clipboard.ContentChanged += Clipboard_ContentChanged;
        Closed += (_, _) =>
        {
            _library.HistoryChanged -= LibraryHistoryChanged;
            Clipboard.ContentChanged -= Clipboard_ContentChanged;
            _resourceHoverTimer?.Stop();
        };
        UpdateInteractionCommands();
    }

    private void LibraryHistoryChanged(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(UpdateInteractionCommands);

    private void UpdateInteractionCommands()
    {
        var state = _library.UndoState;
        UndoButton.IsEnabled = !_vm.IsBusy && !_dialogOpen && state.CanUndo;
        RedoButton.IsEnabled = !_vm.IsBusy && !_dialogOpen && state.CanRedo;
        ToolTipService.SetToolTip(UndoButton, state.CanUndo ? _text["Undo"] + " · " + _text["History" + state.UndoDescription] + " (Ctrl+Z)" : _text["Undo"]);
        ToolTipService.SetToolTip(RedoButton, state.CanRedo ? _text["Redo"] + " · " + _text["History" + state.RedoDescription] + " (Ctrl+Y)" : _text["Redo"]);
    }

    private bool IsTextEditingFocus()
    {
        var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (element != null)
        {
            if (element is TextBox or RichEditBox or PasswordBox) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private static bool IsShiftDrag(DragEventArgs e) => (e.Modifiers & DragDropModifiers.Shift) != 0;

    private static bool HasResourceData(DataPackageView view) => view.Contains(ResourceClipboard.ReferenceFormat) ||
        view.Contains(StandardDataFormats.StorageItems) || view.Contains(StandardDataFormats.Text) ||
        view.Contains(StandardDataFormats.WebLink) || view.Contains(StandardDataFormats.ApplicationLink);

    private bool CanPasteResources(string? projectId = null)
    {
        if (_vm.IsBusy || _dialogOpen || (projectId ?? _vm.CurrentProjectId) == null) return false;
        try { return HasResourceData(Clipboard.GetContent()); }
        catch { return false; }
    }

    private async void CopyResources_Click(object sender, RoutedEventArgs e) => await CopyResourcesAsync(false);
    private async void CutResources_Click(object sender, RoutedEventArgs e) => await CopyResourcesAsync(true);
    private async void PasteResources_Click(object sender, RoutedEventArgs e) => await PasteResourcesToProjectAsync(_vm.CurrentProjectId);

    private async Task CopyResourcesAsync(bool cut)
    {
        if (_vm.IsBusy || _dialogOpen || !_vm.HasSelection || !await EnsureEditsAsync()) return;
        try
        {
            var rows = _vm.SelectedRows.ToArray();
            var payload = new ClipboardReferencePayload(1, _library.DatabasePath, rows.Select(row => row.Id).ToArray(),
                _vm.CurrentProjectId, cut, Guid.NewGuid().ToString("N"));
            // Only custom references and text are exported, so Explorer cannot move the original files.
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetData(ResourceClipboard.ReferenceFormat, ResourceClipboard.Encode(payload));
            data.SetText(string.Join(Environment.NewLine, rows.Select(row => row.Target)));
            Clipboard.SetContent(data);
            _cutReferences = cut ? payload : null;
            RefreshCutAppearance();
            _vm.Status = _text.Format(cut ? "ResourcesCut" : "ResourcesCopied", rows.Length);
        }
        catch (Exception exception) { await ShowErrorAsync(exception); }
    }

    private async void Clipboard_ContentChanged(object? sender, object args)
    {
        try
        {
            var view = Clipboard.GetContent();
            var currentCut = _cutReferences;
            if (currentCut != null)
            {
                if (!view.Contains(ResourceClipboard.ReferenceFormat)) CancelCutResources();
                else
                {
                    var data = await view.GetDataAsync(ResourceClipboard.ReferenceFormat);
                    var payload = ResourceClipboard.Decode(data as string ?? "", _library.DatabasePath);
                    if (ReferenceEquals(currentCut, _cutReferences) && payload.Token != currentCut.Token) CancelCutResources();
                }
            }
        }
        catch { CancelCutResources(); }
    }

    private void CancelCutResources()
    {
        if (_cutReferences != null) _completedCutTokens.Add(_cutReferences.Token);
        _cutReferences = null;
        RefreshCutAppearance();
    }

    private void RefreshCutAppearance()
    {
        var cut = _cutReferences;
        var ids = cut?.ItemIds.ToHashSet(StringComparer.Ordinal);
        foreach (var row in _vm.Rows)
            row.IsCut = cut != null && (cut.SourceProjectId == null || cut.SourceProjectId == _vm.CurrentProjectId) && ids!.Contains(row.Id);
    }

    private async Task PasteResourcesToProjectAsync(string? projectId)
    {
        if (_vm.IsBusy || _dialogOpen) return;
        if (projectId == null) { _vm.Status = _text["PasteChooseProject"]; return; }
        try { await PasteDataAsync(Clipboard.GetContent(), projectId); }
        catch (Exception exception) { await ShowErrorAsync(exception); }
    }

    private async Task PasteDataAsync(DataPackageView view, string? projectId, bool fromDrag = false, bool move = false)
    {
        if (_vm.IsBusy || _dialogOpen || !await EnsureEditsAsync()) return;
        if (view.Contains(ResourceClipboard.ReferenceFormat))
        {
            if (projectId == null) { _vm.Status = _text["PasteChooseProject"]; return; }
            ClipboardReferencePayload payload;
            try
            {
                payload = ResourceClipboard.Decode(await view.GetDataAsync(ResourceClipboard.ReferenceFormat) as string ?? "", _library.DatabasePath);
            }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
            {
                // A second library can import its public paths, but never use another library's stable IDs.
                await ImportExternalDataAsync(view, projectId);
                return;
            }
            var isMove = fromDrag ? move : payload.IsCut && !_completedCutTokens.Contains(payload.Token);
            ResourceReferenceResult? result = null;
            if (!await RunAsync(() => result = _library.TransferResources(payload.ItemIds, projectId,
                payload.SourceProjectId, isMove), checkPaths: false) || result == null) return;
            if (!fromDrag && payload.IsCut)
            {
                _completedCutTokens.Add(payload.Token);
                _cutReferences = null;
                RefreshCutAppearance();
            }
            _vm.Status = result.AddedMemberships == 0 && result.RemovedMemberships == 0
                ? _text["ReferencesAlreadyPresent"]
                : _text.Format(result.RemovedMemberships > 0 ? "ResourcesMoved" : "ResourcesPasted",
                    result.RemovedMemberships > 0 ? result.RemovedMemberships : result.AddedMemberships);
        }
        else await ImportExternalDataAsync(view, projectId);
    }

    private async Task ImportExternalDataAsync(DataPackageView view, string? projectId)
    {
        string[] targets;
        try
        {
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                targets = (await view.GetStorageItemsAsync()).Select(item => item.Path).ToArray();
                if (targets.Length == 0 || targets.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException();
            }
            else if (view.Contains(StandardDataFormats.WebLink)) targets = ResourceClipboard.ParseText((await view.GetWebLinkAsync()).AbsoluteUri);
            else if (view.Contains(StandardDataFormats.ApplicationLink)) targets = ResourceClipboard.ParseText((await view.GetApplicationLinkAsync()).AbsoluteUri);
            else if (view.Contains(StandardDataFormats.Text)) targets = ResourceClipboard.ParseText(await view.GetTextAsync());
            else throw new ArgumentException();
        }
        catch (ArgumentException)
        {
            _vm.Status = _text["ClipboardUnsupported"];
            return;
        }
        await ImportReferencesAsync(targets, projectId);
    }

    private async Task ImportReferencesAsync(string[] targets, string? projectId)
    {
        ResourceImportResult? result = null;
        if (await RunAsync(() => result = _library.ImportReferences(targets, projectId), checkPaths: false) && result != null)
            _vm.Status = _text.Format("AddResult", result.Added, result.Existing);
    }

    private void Resource_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        ResourceSelection_DragItemsStarting((ListViewBase)sender, e);
        if (_vm.IsBusy || _dialogOpen || _vm.IsDirty || !_vm.HasSelection) { e.Cancel = true; return; }
        var payload = new ClipboardReferencePayload(1, _library.DatabasePath, _vm.SelectedRows.Select(row => row.Id).ToArray(),
            _vm.CurrentProjectId, false, Guid.NewGuid().ToString("N"));
        e.Data.SetData(ResourceClipboard.ReferenceFormat, ResourceClipboard.Encode(payload));
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        CancelResourceMarquee();
    }

    private NavigationEntry? ResourceProjectDropTarget(DragEventArgs e, out ListViewItem? container)
    {
        container = null;
        var point = e.GetPosition(NavigationList);
        foreach (var entry in _vm.Navigation)
        {
            if (NavigationList.ContainerFromItem(entry) is not ListViewItem candidate) continue;
            var top = candidate.TransformToVisual(NavigationList).TransformPoint(new Point()).Y;
            if (point.Y >= top && point.Y < top + candidate.ActualHeight && point.X >= 0 && point.X <= NavigationList.ActualWidth)
            {
                container = candidate;
                return entry;
            }
        }
        return null;
    }

    private bool ResourceNavigationDragOver(DragEventArgs e)
    {
        if (!HasResourceData(e.DataView) || e.DataView.Contains(ProjectDragFormat)) return false;
        e.Handled = true;
        var entry = ResourceProjectDropTarget(e, out var container);
        ScheduleResourceGroupExpand(entry);
        var accepted = !_vm.IsBusy && !_dialogOpen && entry is { IsProject: true };
        // External Explorer drags always report Copy, including files cut in Explorer.
        var moving = e.DataView.Contains(ResourceClipboard.ReferenceFormat) && IsShiftDrag(e) && _vm.CurrentProjectId != null;
        e.AcceptedOperation = accepted ? moving ? DataPackageOperation.Move : DataPackageOperation.Copy : DataPackageOperation.None;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.Caption = accepted ? _text.Format(moving ? "DragMoveToProject" : "DragAddToProject", entry!.Name) : _text["DropChooseProject"];
        if (accepted && container != null)
        {
            var top = container.TransformToVisual(NavigationList).TransformPoint(new Point()).Y;
            NavigationDropIndicator.Margin = new Thickness(4, Math.Max(0, top), 4, 0);
            NavigationDropIndicator.Height = container.ActualHeight;
            NavigationDropIndicator.Background = (Brush)Root.Resources["ResourceDropHighlightBrush"];
            NavigationDropIndicator.Visibility = Visibility.Visible;
        }
        else HideProjectDropIndicator();
        return true;
    }

    private async Task ResourceNavigationDropAsync(DragEventArgs e)
    {
        e.Handled = true;
        var entry = ResourceProjectDropTarget(e, out _);
        var move = IsShiftDrag(e);
        StopResourceGroupExpand();
        HideProjectDropIndicator();
        if (_vm.IsBusy || _dialogOpen || entry is not { IsProject: true }) { e.AcceptedOperation = DataPackageOperation.None; return; }
        var deferral = e.GetDeferral();
        try
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            await PasteDataAsync(e.DataView, entry.Id, fromDrag: true, move: move);
        }
        catch (Exception exception) { await ShowErrorAsync(exception); }
        finally { deferral.Complete(); }
    }

    private void ScheduleResourceGroupExpand(NavigationEntry? entry)
    {
        if (entry is not { IsGroup: true, IsExpanded: false, GroupId: { } groupId }) { StopResourceGroupExpand(); return; }
        if (_hoveredResourceGroup == groupId) return;
        _hoveredResourceGroup = groupId;
        _resourceHoverTimer ??= DispatcherQueue.CreateTimer();
        _resourceHoverTimer.Stop();
        _resourceHoverTimer.Interval = TimeSpan.FromMilliseconds(650);
        _resourceHoverTimer.IsRepeating = false;
        _resourceHoverTimer.Tick -= ResourceHoverTimer_Tick;
        _resourceHoverTimer.Tick += ResourceHoverTimer_Tick;
        _resourceHoverTimer.Start();
    }

    private void ResourceHoverTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var entry = _vm.Navigation.FirstOrDefault(candidate => candidate.GroupId == _hoveredResourceGroup && candidate.IsGroup);
        if (entry is { IsExpanded: false }) ToggleNavigationGroup(entry);
        _hoveredResourceGroup = null;
    }

    private void StopResourceGroupExpand() { _resourceHoverTimer?.Stop(); _hoveredResourceGroup = null; }

    private async void Undo_Click(object sender, RoutedEventArgs e) => await ApplyHistoryAsync(false);
    private async void Redo_Click(object sender, RoutedEventArgs e) => await ApplyHistoryAsync(true);

    private async Task ApplyHistoryAsync(bool redo)
    {
        if (_vm.IsBusy || _dialogOpen || !await EnsureEditsAsync()) return;
        CancelResourceMarquee();
        LibraryHistoryResult? result = null;
        if (!await RunAsync(() => result = redo ? _library.Redo() : _library.Undo(), checkPaths: false) || result == null) return;
        CancelCutResources();
        _vm.Status = result.Conflict ? _text["HistoryConflict"] : result.Applied
            ? _text.Format(redo ? "HistoryRedone" : "HistoryUndone", _text["History" + result.Description])
            : _text[redo ? "NothingToRedo" : "NothingToUndo"];
    }
}
