namespace StayView.Core;

public static class PinnedWindowPolicy
{
    // Minimized/docked tiles are explicit workspace objects, so they can be pinned without
    // first restoring/focusing the real HWND. Normal windows remain pin-eligible only while
    // they are the active browse target.
    public static bool CanPin(bool minimized, bool browsing, bool browsed, bool browseEligible) =>
        minimized || (browsing && browsed && browseEligible);

    // A live native view pin already spans every desktop and must not be moved: Windows
    // clears that pin when its owning desktop is changed. If the native pin is unavailable
    // or was lost, StayView follows the active desktop itself until the user unpins.
    public static bool NeedsDesktopMove(bool nativePinActive, Guid current, Guid owner) =>
        !nativePinActive && current != Guid.Empty && (owner == Guid.Empty || owner != current);

    // Chromium, Electron and DWM invisible borders settle a few pixels off the rect we
    // requested. Treating that as a violation and calling SetWindowPos on every tick
    // makes those windows drop the caret, so small deltas are the window's own frame,
    // not a move the pin must undo.
    public static bool IsSettledAdjustment(Native.RECT now, Native.RECT locked)
    {
        const int slack = 16;
        return Math.Abs(now.Left - locked.Left) <= slack
            && Math.Abs(now.Top - locked.Top) <= slack
            && Math.Abs(now.Width - locked.Width) <= slack
            && Math.Abs(now.Height - locked.Height) <= slack;
    }

    // Pins stay above every other panel, so any real overlap is a panel the pin hides.
    // A few pixels of frame contact is not coverage.
    public static bool IsCoveredBy(Native.RECT window, IEnumerable<Native.RECT> pins)
    {
        if (window.Width <= 0 || window.Height <= 0) return false;
        foreach (var pin in pins)
        {
            if (pin.Width <= 0 || pin.Height <= 0) continue;
            int left = Math.Max(window.Left, pin.Left);
            int top = Math.Max(window.Top, pin.Top);
            int right = Math.Min(window.Right, pin.Right);
            int bottom = Math.Min(window.Bottom, pin.Bottom);
            if (right - left > 8 && bottom - top > 8) return true;
        }
        return false;
    }

    // Keep the window's size and move it to the nearest work-area slot that is not under a pin.
    public static Native.RECT PlaceClearOf(Native.RECT window, IReadOnlyList<Native.RECT> pins, Native.RECT work, int gap = 8)
    {
        if (window.Width <= 0 || window.Height <= 0 || !IsCoveredBy(window, pins)) return window;
        var blockers = pins.Where(p => p.Width > 0 && p.Height > 0).ToList();
        return StableTileLayout.PlaceNew(window, blockers, work, Math.Max(0, gap));
    }
}
