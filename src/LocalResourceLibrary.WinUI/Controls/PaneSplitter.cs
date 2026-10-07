using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace LocalResourceLibrary.WinUI.Controls;

public sealed class PaneDragDeltaEventArgs(double horizontalChange) : EventArgs
{
    public double HorizontalChange { get; } = horizontalChange;
}

public sealed class PaneSplitter : Control
{
    private Thumb? _thumb;
    public PaneSplitter()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
    public event EventHandler<PaneDragDeltaEventArgs>? DragDelta;
    public event RoutedEventHandler? DragCompleted;

    protected override void OnApplyTemplate()
    {
        if (_thumb != null)
        {
            _thumb.DragDelta -= Thumb_DragDelta;
            _thumb.DragCompleted -= Thumb_DragCompleted;
        }
        base.OnApplyTemplate();
        _thumb = GetTemplateChild("PART_Thumb") as Thumb;
        if (_thumb != null)
        {
            _thumb.DragDelta += Thumb_DragDelta;
            _thumb.DragCompleted += Thumb_DragCompleted;
        }
    }

    private void Thumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        DragDelta?.Invoke(this, new(e.HorizontalChange));
    }
    private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e) => DragCompleted?.Invoke(this, new());
    protected override AutomationPeer OnCreateAutomationPeer() => new SplitterPeer(this);
    private sealed class SplitterPeer(PaneSplitter owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(PaneSplitter);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;
    }
}
