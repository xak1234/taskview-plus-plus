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

    // Position only: slide a USER32 rectangle so its visible frame (offset by the margins)
    // no longer overlaps the desktop bar. Only the bar's own edge is enforced; the other
    // edges, other monitors and the window's size are left alone. A frame that does not
    // fit beside the bar keeps its size: under a top bar it runs past the bottom of the
    // screen; above a bottom bar its title bar stays on screen and it overlaps the bar.
    public static Native.RECT MoveOffBar(Native.RECT window, int leftMargin, int topMargin, int visibleWidth, int visibleHeight, Native.RECT bar, Native.RECT work)
    {
        if (bar.Width < 1 || bar.Height < 1) return window;
        int visibleLeft = window.Left + leftMargin, visibleTop = window.Top + topMargin;
        if (visibleLeft + visibleWidth <= bar.Left || visibleLeft >= bar.Right
            || visibleTop + visibleHeight <= bar.Top || visibleTop >= bar.Bottom) return window;
        bool barAtTop = bar.Top + bar.Height / 2 < work.Top + work.Height / 2;
        int top = barAtTop ? bar.Bottom : Math.Max(work.Top, bar.Top - visibleHeight);
        return new Native.RECT(window.Left, window.Top + top - visibleTop, window.Width, window.Height);
    }

    // Position only: move a same-sized USER32 rectangle so the visible frame (offset from
    // it by the given margins) stays inside the area. Used while a window is being dragged,
    // where resizing it would fight the pointer.
    public static Native.RECT MoveVisibleInside(Native.RECT window, int leftMargin, int topMargin, int visibleWidth, int visibleHeight, Native.RECT area)
    {
        int visibleLeft = window.Left + leftMargin, visibleTop = window.Top + topMargin;
        int left = Math.Max(area.Left, Math.Min(visibleLeft, area.Right - visibleWidth));
        int top = Math.Max(area.Top, Math.Min(visibleTop, area.Bottom - visibleHeight));
        return new Native.RECT(window.Left + left - visibleLeft, window.Top + top - visibleTop, window.Width, window.Height);
    }

    // Where the USER32 rectangle goes (same size) so its visible frame is centred on the
    // tile, then slid inside the area.
    public static Native.RECT CenterOver(Native.RECT window, Native.RECT visual, Native.RECT tile, Native.RECT area)
    {
        int leftMargin = visual.Left - window.Left, topMargin = visual.Top - window.Top;
        int visibleLeft = tile.Left + (tile.Width - visual.Width) / 2;
        int visibleTop = tile.Top + (tile.Height - visual.Height) / 2;
        var centred = new Native.RECT(visibleLeft - leftMargin, visibleTop - topMargin, window.Width, window.Height);
        return MoveVisibleInside(centred, leftMargin, topMargin, visual.Width, visual.Height, area);
    }

    // Shift, and shrink only when needed, so every corner of the visible frame lies
    // inside the work area. A window larger than the desktop is pinned to the top-left
    // of that area and reduced to fit.
    public static Native.RECT FitInside(Native.RECT bounds, Native.RECT area)
    {
        if (area.Width < 1 || area.Height < 1 || bounds.Width < 1 || bounds.Height < 1) return bounds;
        int width = Math.Min(bounds.Width, area.Width);
        int height = Math.Min(bounds.Height, area.Height);
        int left = width < bounds.Width ? area.Left : Math.Min(Math.Max(bounds.Left, area.Left), area.Right - width);
        int top = height < bounds.Height ? area.Top : Math.Min(Math.Max(bounds.Top, area.Top), area.Bottom - height);
        return new Native.RECT(left, top, width, height);
    }

    // Move the USER32 rectangle so the DWM frame it produces stays inside the work area.
    // Invisible resize margins may still lie outside; the border the user sees does not.
    public static Native.RECT FitWindowInside(Native.RECT window, Native.RECT visual, Native.RECT work)
    {
        var fitted = FitInside(visual, work);
        int leftMargin = visual.Left - window.Left;
        int topMargin = visual.Top - window.Top;
        int rightMargin = window.Right - visual.Right;
        int bottomMargin = window.Bottom - visual.Bottom;
        return new Native.RECT(fitted.Left - leftMargin, fitted.Top - topMargin,
            fitted.Width + leftMargin + rightMargin, fitted.Height + topMargin + bottomMargin);
    }
}
