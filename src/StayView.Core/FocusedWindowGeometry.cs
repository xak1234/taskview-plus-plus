namespace StayView.Core;

public static class FocusedWindowGeometry
{
    public const double MinimumScale = 0.15;

    // SmallWindowSize is stored in DIP. Keep a focused real window from shrinking below
    // 15% of that configured edge on the monitor it is being resized on. The 32 px floor
    // matches WindowCatalog's smallest managed top-level source and prevents a resized
    // browsed window from immediately becoming too small to remain in the overview.
    public static int MinimumEdgePixels(int configuredSizeDip, double dpiScale)
    {
        if (dpiScale <= 0) dpiScale = 1;
        return Math.Max(32, (int)Math.Ceiling(configuredSizeDip * dpiScale * MinimumScale));
    }

    // Preserve the edge opposite the user's resize handle. Comparing each side with the
    // gesture's starting rectangle lets this work for left/right/top/bottom and corners
    // without intercepting the application's native resize hit testing.
    public static Native.RECT ClampResize(Native.RECT current, Native.RECT origin, int minimumEdge)
    {
        int left = current.Left, top = current.Top, right = current.Right, bottom = current.Bottom;
        minimumEdge = Math.Max(1, minimumEdge);

        if (current.Width < minimumEdge)
        {
            bool leftMoved = Math.Abs(current.Left - origin.Left) > Math.Abs(current.Right - origin.Right);
            if (leftMoved) left = right - minimumEdge;
            else right = left + minimumEdge;
        }
        if (current.Height < minimumEdge)
        {
            bool topMoved = Math.Abs(current.Top - origin.Top) > Math.Abs(current.Bottom - origin.Bottom);
            if (topMoved) top = bottom - minimumEdge;
            else bottom = top + minimumEdge;
        }
        return new Native.RECT(left, top, right - left, bottom - top);
    }

    // Keep the pixels the user actually sees from escaping above the monitor work area.
    // `windowBounds` is USER32's rectangle (which can include invisible resize borders),
    // while `visualBounds` is the DWM frame. Shift the whole window by the DWM overflow so
    // its title bar lands exactly at the usable top without changing size.
    public static Native.RECT ClampVisibleTop(Native.RECT windowBounds, Native.RECT visualBounds, Native.RECT workArea)
    {
        if (visualBounds.Top >= workArea.Top) return windowBounds;
        int dy = workArea.Top - visualBounds.Top;
        return new Native.RECT(windowBounds.Left, windowBounds.Top + dy, windowBounds.Width, windowBounds.Height);
    }

    // Used for animation targets that do not currently have a live visual frame (notably an
    // iconic source). This is intentionally position-only: oversized windows keep their size.
    public static Native.RECT ClampTargetTop(Native.RECT target, Native.RECT workArea)
        => target.Top >= workArea.Top ? target
            : new Native.RECT(target.Left, workArea.Top, target.Width, target.Height);
}
