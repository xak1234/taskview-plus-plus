namespace StayView.Core;

// Dragging a focused (large) window into the dock. The window itself is kept off the
// desktop bar while it moves, so the decision is made from the cursor: released over the
// bar, the window docks. There is no hold-to-dock here, because holding a big window near
// the bar is ordinary positioning. Pins never dock (pin and dock exclude each other).
public static class FocusedDockDrop
{
    public static bool ShouldDock(Native.POINT cursor, Native.RECT bar, bool pinned, bool maximized)
        => !pinned && !maximized && bar.Width > 0 && bar.Height > 0
            && cursor.X >= bar.Left && cursor.X < bar.Right && cursor.Y >= bar.Top && cursor.Y < bar.Bottom;
}
