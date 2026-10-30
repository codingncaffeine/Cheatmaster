using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cheatmaster.App.Services;

namespace Cheatmaster.App.Views;

/// <summary>
/// Invisible strips along the edges of a borderless window that resize it, laid over the window's
/// content as the last child of its root panel. Gone while the window is maximised.
/// </summary>
public sealed class ResizeGrips : Panel
{
    private const double Thickness = 6;

    public ResizeGrips()
    {
        IsHitTestVisible = true;
        Background = null;
        Add(WindowEdge.North, HorizontalAlignment.Stretch, VerticalAlignment.Top, StandardCursorType.TopSide);
        Add(WindowEdge.South, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, StandardCursorType.BottomSide);
        Add(WindowEdge.West, HorizontalAlignment.Left, VerticalAlignment.Stretch, StandardCursorType.LeftSide);
        Add(WindowEdge.East, HorizontalAlignment.Right, VerticalAlignment.Stretch, StandardCursorType.RightSide);
        Add(WindowEdge.NorthWest, HorizontalAlignment.Left, VerticalAlignment.Top, StandardCursorType.TopLeftCorner);
        Add(WindowEdge.NorthEast, HorizontalAlignment.Right, VerticalAlignment.Top, StandardCursorType.TopRightCorner);
        Add(WindowEdge.SouthWest, HorizontalAlignment.Left, VerticalAlignment.Bottom, StandardCursorType.BottomLeftCorner);
        Add(WindowEdge.SouthEast, HorizontalAlignment.Right, VerticalAlignment.Bottom, StandardCursorType.BottomRightCorner);
    }

    private void Add(WindowEdge edge, HorizontalAlignment h, VerticalAlignment v, StandardCursorType cursor)
    {
        bool corner = edge is WindowEdge.NorthWest or WindowEdge.NorthEast or WindowEdge.SouthWest or WindowEdge.SouthEast;
        var strip = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Width = corner || h != HorizontalAlignment.Stretch ? Thickness * (corner ? 2 : 1) : double.NaN,
            Height = corner || v != VerticalAlignment.Stretch ? Thickness * (corner ? 2 : 1) : double.NaN,
            Cursor = new Cursor(cursor),
            Tag = edge
        };
        Children.Add(strip);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        foreach (var child in Children)
        {
            if (child is Border { Tag: WindowEdge edge } strip) WindowChrome.ResizeBy(window, strip, edge);
        }

        void Sync() => IsVisible = window.WindowState == WindowState.Normal && window.CanResize;
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property == Window.WindowStateProperty || args.Property == Window.CanResizeProperty) Sync();
        };
        Sync();
    }

    // Only the strips take input; the middle stays with the content underneath.
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);
        return default;
    }
}
