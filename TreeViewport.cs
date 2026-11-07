using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Octet;

internal sealed class TreeViewport : Grid
{
    private const double MinZoom = 0.2;
    private const double MaxZoom = 3;
    private const double ViewMargin = 12;

    private readonly Canvas graph;
    private readonly ScaleTransform zoom = new();
    private readonly TranslateTransform offset = new();
    private MouseButton? dragButton;
    private Point lastMousePosition;

    public TreeViewport(Canvas graph)
    {
        this.graph = graph;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        graph.RenderTransform = new TransformGroup { Children = { zoom, offset } };
        // A Canvas measures the graph at its full size, so offscreen branches can be panned into view.
        Children.Add(new Canvas { Children = { graph } });

        RequestBringIntoView += RevealFocusedNode;
    }

    public void ResetView()
    {
        zoom.ScaleX = zoom.ScaleY = 1;
        offset.X = offset.Y = 0;
    }

    public void ZoomBy(int steps) => ZoomAt(new Point(ViewMargin, ViewMargin), steps);

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        var onBackground = e.OriginalSource == this || e.OriginalSource == graph;
        if (dragButton is not null || (e.ChangedButton != MouseButton.Middle &&
            (e.ChangedButton != MouseButton.Left || !onBackground))) return;

        if (!CaptureMouse()) return;
        dragButton = e.ChangedButton;
        lastMousePosition = e.GetPosition(this);
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (dragButton is null) return;

        var position = e.GetPosition(this);
        PanBy(position - lastMousePosition);
        lastMousePosition = position;
        e.Handled = true;
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        if (e.ChangedButton != dragButton) return;

        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (e.OriginalSource != this) return;

        dragButton = null;
        Cursor = Cursors.Hand;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Delta == 0) return;

        var steps = (double)e.Delta / Mouse.MouseWheelDeltaForOneLine;
        ZoomAt(e.GetPosition(this), steps);
        e.Handled = true;
    }

    private void ZoomAt(Point pivot, double steps)
    {
        var previousZoom = zoom.ScaleX;
        var newZoom = Math.Clamp(previousZoom * Math.Pow(1.15, steps), MinZoom, MaxZoom);
        var ratio = newZoom / previousZoom;
        offset.X = pivot.X + (offset.X - pivot.X) * ratio;
        offset.Y = pivot.Y + (offset.Y - pivot.Y) * ratio;
        zoom.ScaleX = zoom.ScaleY = newZoom;
    }

    private void PanBy(Vector delta)
    {
        offset.X += delta.X;
        offset.Y += delta.Y;
    }

    private void RevealFocusedNode(object sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject is not FrameworkElement target || !graph.IsAncestorOf(target) || !target.IsArrangeValid ||
            ActualWidth <= 0 || ActualHeight <= 0 || Mouse.LeftButton == MouseButtonState.Pressed) return;

        var rect = e.TargetRect.IsEmpty ? new Rect(target.RenderSize) : e.TargetRect;
        var bounds = target.TransformToAncestor(this).TransformBounds(rect);
        PanBy(new Vector(RevealOffset(bounds.Left, bounds.Right, ActualWidth),
            RevealOffset(bounds.Top, bounds.Bottom, ActualHeight)));
        e.Handled = true;
    }

    private static double RevealOffset(double start, double end, double viewSize)
    {
        var margin = Math.Min(ViewMargin, viewSize / 4);
        if (end - start > viewSize - 2 * margin || start < margin) return margin - start;
        return end > viewSize - margin ? viewSize - margin - end : 0;
    }
}
