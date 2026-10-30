using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Cheatmaster.App.Services;

/// <summary>
/// The window manager's own move and resize, started from a borderless window's own title bar and
/// edges. Both go through the platform (_NET_WM_MOVERESIZE on X11, xdg_toplevel on Wayland, the
/// system loop on Windows), so snapping and tiling behave exactly as for any other window.
/// </summary>
public static class WindowChrome
{
    public static void DragBy(Window window, Control bar, bool maximizeOnDoubleClick = false)
    {
        bar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) return;
            // A press on a caption button is the button's.
            if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;

            if (maximizeOnDoubleClick && e.ClickCount == 2)
            {
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                e.Handled = true;
                return;
            }

            window.BeginMoveDrag(e);
            e.Handled = true;
        };
    }

    public static void ResizeBy(Window window, Control edge, WindowEdge direction)
    {
        edge.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(edge).Properties.IsLeftButtonPressed) return;
            if (window.WindowState != WindowState.Normal) return;
            window.BeginResizeDrag(direction, e);
            e.Handled = true;
        };
    }
}
