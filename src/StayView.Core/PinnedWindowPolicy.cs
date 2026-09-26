namespace StayView.Core;

public static class PinnedWindowPolicy
{
    // Minimized canvas tiles can be pinned without first restoring/focusing the real HWND.
    // Normal windows remain pin-eligible only while they are the active browse target.
    public static bool CanPin(bool minimized, bool browsing, bool browsed, bool browseEligible) =>
        minimized || (browsing && browsed && browseEligible);

    // Pin and dock exclude each other. A docked window cannot be pinned (it would need
    // unpinning before it could leave the dock), and a pin is never docked.
    public static bool CanPinDocked(bool docked) => !docked;
    public static bool CanDock(bool pinned) => !pinned;

    // A StayView pin belongs to the desktop it was pinned on. Creating or switching
    // desktops must not adopt it: moving it makes that window look like a member of
    // whichever desktop is on screen. A live native view pin already spans every
    // desktop and must not be moved either — Windows clears that pin when its
    // owning desktop changes. If a window was carried off its home desktop, send
    // it back. An unknown owner is left alone.
    public static bool NeedsHomeReturn(bool nativePinActive, Guid home, Guid owner) =>
        !nativePinActive && home != Guid.Empty && owner != Guid.Empty && owner != home;

    // Changing desktop raises the overview and drops the browse. An in-front pin
    // left HWND_TOPMOST then sits under that overview: its corner marker is lost
    // and every later focus fight blanks the thumbnail. Park it as a tile pin.
    // A tile pin, and a minimized pin, stay as they are.
    public static bool ParkInFrontPin(bool tileOnly, bool minimized) => !tileOnly && !minimized;

    // A pin freezes the window. Focusing it must not expand it, and its tile must not move.
    public static bool CanFocus(bool pinned) => !pinned;
    public static bool CanDragTile(bool pinned) => !pinned;

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
