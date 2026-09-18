namespace StayView.Core;

// What browsing should resolve to once the world has drifted.
public enum BrowseTarget
{
    Keep,    // the selected window is still in front — nothing to do
    Refront, // OUR canvas took the foreground — put the browsed window back in front
    Follow,  // another managed source took the foreground — browse that instead
    Grid,    // anything else — the honest state is the grid
}

// The decision half of TrayApp.ReconcileBrowse, kept pure so the browse invariant can be
// tested without Win32. TrayApp still owns the cheap short-circuits (not browsing, session
// inactive, activating, gesture in progress, selected gone) because those run every 500 ms
// tick and must not pay for a foreground/catalog lookup.
public static class BrowseReconciler
{
    // `foregroundIsOwnCanvas` is the case that makes tile-dragging-while-focused possible.
    // Pressing a tile makes the overview HWND the foreground window, so after the gesture
    // ends the selected window is no longer foreground. Treating that as "some window we do
    // not tile" sends us to the grid and lowers every source — which is exactly the window
    // the user was working in disappearing. Our own canvas is not a third-party window: the
    // browse is still live, so restore the selected window rather than abandoning it.
    public static BrowseTarget ClassifyForeground(
        nint selected,
        nint foreground,
        nint foregroundRoot,
        bool foregroundIsOwnCanvas,
        nint foregroundSource)
    {
        if (selected != 0 && (foreground == selected || foregroundRoot == selected)) return BrowseTarget.Keep;
        if (foregroundIsOwnCanvas) return BrowseTarget.Refront;
        if (foregroundSource != 0 && foregroundSource != selected) return BrowseTarget.Follow;
        return BrowseTarget.Grid;
    }
}
