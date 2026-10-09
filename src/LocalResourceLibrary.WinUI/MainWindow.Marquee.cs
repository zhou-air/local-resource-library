using LocalResourceLibrary.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace LocalResourceLibrary.WinUI;

public sealed partial class MainWindow
{
    private Canvas? _resourceSelectionCanvas;
    private Rectangle? _resourceSelectionRectangle;
    private DispatcherQueueTimer? _resourceSelectionTimer;
    private ListViewBase? _marqueeControl;
    private ScrollViewer? _marqueeScrollViewer;
    private Pointer? _marqueePointer;
    private Point _marqueeOrigin, _marqueePosition;
    private bool _marqueeReady, _marqueeMoved;
    private readonly HashSet<string> _marqueeOriginalSelection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rect> _marqueeItemBounds = new(StringComparer.Ordinal);
    private string[]? _resourceDragSelection;
    private string? _resourceDragCandidateId;

    private void InitializeResourceSelection()
    {
        // A sibling overlay leaves native item selection, virtualization and dragging intact.
        if (ResourceList.Parent is Panel host)
        {
            _resourceSelectionRectangle = new Rectangle
            {
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(48, 0, 120, 215)),
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 0, 120, 215)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _resourceSelectionCanvas = new Canvas { IsHitTestVisible = false };
            _resourceSelectionCanvas.Children.Add(_resourceSelectionRectangle);
            host.Children.Add(_resourceSelectionCanvas);
        }
        foreach (var control in new ListViewBase[] { ResourceList, ResourceGrid })
        {
            control.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ResourceSelection_PointerMoved), true);
            control.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ResourceSelection_PointerReleased), true);
            control.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(ResourceSelection_PointerCanceled), true);
            control.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ResourceSelection_PointerCanceled), true);
            control.SizeChanged += (_, _) =>
            {
                if (_marqueeReady && _marqueeControl == control) CancelResourceMarquee();
            };
        }
        _resourceSelectionTimer = DispatcherQueue.CreateTimer();
        _resourceSelectionTimer.Interval = TimeSpan.FromMilliseconds(33);
        _resourceSelectionTimer.Tick += (_, _) => UpdateResourceMarquee(scrollAtEdge: true);
        Closed += (_, _) => CancelResourceMarquee();
    }

    private static bool ResourceSelectionKeyDown(VirtualKey key) =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private async Task HandleResourceSelectionChangedAsync(ListViewBase control)
    {
        var ids = control.SelectedItems.OfType<ResourceRow>().Select(row => row.Id).ToArray();
        // Native Extended selection may reduce a selected group on mouse-down. Keep that
        // group's stable IDs until we know whether this gesture becomes a resource drag.
        if (ResourceSelectionKeyDown(VirtualKey.LeftButton) &&
            !ResourceSelectionKeyDown(VirtualKey.Control) && !ResourceSelectionKeyDown(VirtualKey.Shift) &&
            ids.Length == 1 && _vm.SelectedRows.Count > 1 && _vm.SelectedRows.Any(row => row.Id == ids[0]))
        {
            _resourceDragSelection = _vm.SelectedRows.Select(row => row.Id).ToArray();
            _resourceDragCandidateId = ids[0];
        }
        else if (!ResourceSelectionKeyDown(VirtualKey.LeftButton))
        {
            _resourceDragSelection = null;
            _resourceDragCandidateId = null;
        }
        await ChangeResourceSelectionAsync(ids);
    }

    private void ResourceSelection_DragItemsStarting(ListViewBase control, DragItemsStartingEventArgs e)
    {
        if (_marqueeControl != null) { e.Cancel = true; return; }
        if (_resourceDragSelection is not { Length: > 1 } ids ||
            !e.Items.OfType<ResourceRow>().Any(row => row.Id == _resourceDragCandidateId)) return;
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        ApplyResourceMarqueeSelection(selected);
        _resourceDragSelection = null;
        _resourceDragCandidateId = null;
    }

    private async void Resource_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase control || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        var source = e.OriginalSource as DependencyObject;
        while (source != null && !ReferenceEquals(source, control))
        {
            // Starting on an item always belongs to native click/range/drag handling.
            if (source is ListViewItem or GridViewItem or ScrollBar or Thumb) return;
            source = VisualTreeHelper.GetParent(source);
        }
        if (_vm.IsBusy || _dialogOpen || _selectionChanging || _rendering) return;
        CancelResourceMarquee();
        _resourceDragSelection = null;
        _resourceDragCandidateId = null;
        e.Handled = true;
        control.Focus(FocusState.Pointer);
        _marqueeControl = control;
        var pointer = e.Pointer;
        _marqueePointer = pointer;
        _marqueePosition = e.GetCurrentPoint(control).Position;
        if (!control.CapturePointer(pointer)) { CancelResourceMarquee(); return; }
        var additive = (e.KeyModifiers & VirtualKeyModifiers.Control) != 0;
        var original = _vm.SelectedRows.Select(row => row.Id).ToArray();
        if (!await EnsureEditsAsync() || _marqueeControl != control || _marqueePointer != pointer)
        {
            if (_marqueePointer == pointer) CancelResourceMarquee();
            return;
        }
        _marqueeOriginalSelection.Clear();
        if (additive) _marqueeOriginalSelection.UnionWith(original);
        ApplyResourceMarqueeSelection(_marqueeOriginalSelection);
        // Keep the item layout fixed throughout an additive gesture too: an existing
        // single-item details pane must not resize/reflow the grid on the first hit.
        _detailsOpen = false;
        UpdatePaneWidths();
        control.UpdateLayout();
        if (_marqueeControl != control || _marqueePointer != pointer) return;
        _marqueeScrollViewer = FindResourceDescendant<ScrollViewer>(control);
        _marqueeOrigin = ToResourceContentPoint(_marqueePosition);
        _marqueeReady = true;
        _marqueeMoved = false;
        _marqueeItemBounds.Clear();
        CacheResourceMarqueeBounds();
        _resourceSelectionTimer?.Start();
    }

    private void ResourceSelection_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeControl == null || _marqueePointer?.PointerId != e.Pointer.PointerId) return;
        if (!e.GetCurrentPoint(_marqueeControl).Properties.IsLeftButtonPressed)
        {
            CancelResourceMarquee();
            return;
        }
        _marqueePosition = e.GetCurrentPoint(_marqueeControl).Position;
        e.Handled = true;
    }

    private void ResourceSelection_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _resourceDragSelection = null;
        _resourceDragCandidateId = null;
        if (_marqueeControl == null || _marqueePointer?.PointerId != e.Pointer.PointerId) return;
        _marqueePosition = e.GetCurrentPoint(_marqueeControl).Position;
        UpdateResourceMarquee(scrollAtEdge: false);
        CancelResourceMarquee();
        e.Handled = true;
    }

    private void ResourceSelection_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueePointer?.PointerId == e.Pointer.PointerId) CancelResourceMarquee();
    }

    private void CancelResourceMarquee()
    {
        _resourceSelectionTimer?.Stop();
        var control = _marqueeControl;
        var pointer = _marqueePointer;
        _marqueeControl = null;
        _marqueePointer = null;
        _marqueeScrollViewer = null;
        _marqueeReady = false;
        _marqueeMoved = false;
        _marqueeOriginalSelection.Clear();
        _marqueeItemBounds.Clear();
        if (_resourceSelectionRectangle != null) _resourceSelectionRectangle.Visibility = Visibility.Collapsed;
        if (control != null && pointer != null) control.ReleasePointerCapture(pointer);
    }

    private Point ToResourceContentPoint(Point position) => new(
        position.X + (_marqueeScrollViewer?.HorizontalOffset ?? 0),
        position.Y + (_marqueeScrollViewer?.VerticalOffset ?? 0));

    private void UpdateResourceMarquee(bool scrollAtEdge)
    {
        if (!_marqueeReady || _marqueeControl is not { } control) return;
        if (_vm.IsBusy || _dialogOpen || control != ActiveResources || control.Visibility != Visibility.Visible)
        {
            CancelResourceMarquee();
            return;
        }
        var position = ToResourceContentPoint(_marqueePosition);
        if (!_marqueeMoved && Math.Abs(position.X - _marqueeOrigin.X) < 4 && Math.Abs(position.Y - _marqueeOrigin.Y) < 4) return;
        _marqueeMoved = true;
        var viewport = ResourceMarqueeViewport(control);
        if (scrollAtEdge && _marqueeScrollViewer is { } scroll)
        {
            var horizontal = ResourceMarqueeScrollDelta(_marqueePosition.X, viewport.Left, viewport.Right);
            var vertical = ResourceMarqueeScrollDelta(_marqueePosition.Y, viewport.Top, viewport.Bottom);
            if ((horizontal != 0 && scroll.ScrollableWidth > 0) || (vertical != 0 && scroll.ScrollableHeight > 0))
                scroll.ChangeView(Math.Clamp(scroll.HorizontalOffset + horizontal, 0, scroll.ScrollableWidth),
                    Math.Clamp(scroll.VerticalOffset + vertical, 0, scroll.ScrollableHeight), null, true);
        }
        CacheResourceMarqueeBounds();
        position = ToResourceContentPoint(_marqueePosition);
        var selection = new Rect(Math.Min(_marqueeOrigin.X, position.X), Math.Min(_marqueeOrigin.Y, position.Y),
            Math.Abs(position.X - _marqueeOrigin.X), Math.Abs(position.Y - _marqueeOrigin.Y));
        var ids = new HashSet<string>(_marqueeOriginalSelection, StringComparer.Ordinal);
        foreach (var (id, bounds) in _marqueeItemBounds)
            if (ResourceRectIntersects(selection, bounds)) ids.Add(id);
        ApplyResourceMarqueeSelection(ids);
        if (_resourceSelectionCanvas is not { } canvas || _resourceSelectionRectangle is not { } rectangle) return;
        var visible = new Rect(selection.X - (_marqueeScrollViewer?.HorizontalOffset ?? 0),
            selection.Y - (_marqueeScrollViewer?.VerticalOffset ?? 0), selection.Width, selection.Height);
        var clipped = ResourceRectIntersection(visible, viewport);
        var overlay = control.TransformToVisual(canvas).TransformBounds(clipped);
        Canvas.SetLeft(rectangle, overlay.X);
        Canvas.SetTop(rectangle, overlay.Y);
        rectangle.Width = Math.Max(0, overlay.Width);
        rectangle.Height = Math.Max(0, overlay.Height);
        rectangle.Visibility = Visibility.Visible;
    }

    private void CacheResourceMarqueeBounds()
    {
        if (_marqueeControl is not { ItemsPanelRoot: { } panel } control) return;
        // Only realized containers are visited. Cache their content coordinates while
        // scrolling so earlier rows remain selected after native virtualization recycles them.
        foreach (var container in panel.Children.OfType<FrameworkElement>())
        {
            if (control.ItemFromContainer(container) is not ResourceRow row || container.ActualWidth <= 0 || container.ActualHeight <= 0) continue;
            var bounds = container.TransformToVisual(control).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            bounds.X += _marqueeScrollViewer?.HorizontalOffset ?? 0;
            bounds.Y += _marqueeScrollViewer?.VerticalOffset ?? 0;
            _marqueeItemBounds[row.Id] = bounds;
        }
    }

    private void ApplyResourceMarqueeSelection(ISet<string> ids)
    {
        if (ids.Count == _vm.SelectedRows.Count && _vm.SelectedRows.All(row => ids.Contains(row.Id))) return;
        var rendering = _rendering;
        _rendering = true;
        try
        {
            _vm.SelectMany(_vm.Rows.Where(row => ids.Contains(row.Id)), preserveDraft: true);
            SyncResourceSelection();
            _detailsOpen = false;
            UpdatePaneWidths();
        }
        finally { _rendering = rendering; }
    }

    private static Rect ResourceMarqueeViewport(ListViewBase control)
    {
        var presenter = FindResourceDescendant<ScrollContentPresenter>(control);
        return presenter == null ? new Rect(0, 0, control.ActualWidth, control.ActualHeight) :
            presenter.TransformToVisual(control).TransformBounds(new Rect(0, 0, presenter.ActualWidth, presenter.ActualHeight));
    }

    private static T? FindResourceDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (FindResourceDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static double ResourceMarqueeScrollDelta(double value, double start, double end)
    {
        const double margin = 28;
        if (value < start + margin) return -Math.Min(24, (start + margin - value) * .65);
        if (value > end - margin) return Math.Min(24, (value - end + margin) * .65);
        return 0;
    }

    private static bool ResourceRectIntersects(Rect left, Rect right) =>
        left.Left < right.Right && left.Right > right.Left && left.Top < right.Bottom && left.Bottom > right.Top;

    private static Rect ResourceRectIntersection(Rect left, Rect right)
    {
        var x = Math.Max(left.Left, right.Left);
        var y = Math.Max(left.Top, right.Top);
        return new Rect(x, y, Math.Max(0, Math.Min(left.Right, right.Right) - x), Math.Max(0, Math.Min(left.Bottom, right.Bottom) - y));
    }
}
