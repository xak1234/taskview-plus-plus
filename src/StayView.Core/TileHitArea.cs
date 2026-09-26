namespace StayView.Core;

// Where a canvas tile answers hover and press. A tile is normally hit at its grid cell
// and at its animated (visual) position. A focused window's stand-in is different: its
// thumbnail is parked over the real window, so the grid cell is empty. Hitting that
// empty cell drew a glowing ghost outline that dragged the focused window.
public static class TileHitArea
{
    public static bool Contains(Native.RECT cell, Native.RECT visual, bool standIn, Native.POINT p)
    {
        bool parked = standIn && visual.Width > 0 && visual.Height > 0;
        return Inside(visual, p) || (!parked && Inside(cell, p));
    }

    static bool Inside(Native.RECT r, Native.POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
}
