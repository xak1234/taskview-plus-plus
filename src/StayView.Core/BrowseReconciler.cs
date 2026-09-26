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
    // Reconcile every browsed window, not only the primary. A minimized, hidden,
    // closed or departed secondary must release its suppressed miniature too.
    public static IReadOnlyList<nint> AvailableWindows(IEnumerable<nint> browsed, Func<nint, bool> available)
        => browsed.Where(h => h != 0 && available(h)).Distinct().ToArray();

    // Pinned focused windows are outside the user's ordinary focus-window allowance.
    // Preserve the existing focus order, keep every pinned handle, and apply the cap only
    // to unpinned windows. This stays pure so settings changes and promotion ordering are
    // testable without HWNDs.
    public static IReadOnlyList<nint> LimitWithPinned(
        IEnumerable<nint> ordered,
        IReadOnlySet<nint> pinned,
        int maxUnpinned)
    {
        maxUnpinned=Math.Max(0,maxUnpinned);
        int unpinned=0;
        var seen=new HashSet<nint>();
        var result=new List<nint>();
        foreach(var h in ordered)
        {
            if(h==0||!seen.Add(h))continue;
            if(pinned.Contains(h)){result.Add(h);continue;}
            if(unpinned++<maxUnpinned)result.Add(h);
        }
        return result;
    }

    // `foregroundIsOwnCanvas` alone is NOT enough reason to re-front the selected window.
    // A browser/window that is closing naturally exposes the overview underneath it; if we
    // blindly interpret that foreground change as a StayView gesture we can resurrect the
    // closing HWND and bounce focus between it and the canvas. `ownCanvasGesture` is true
    // only for a completed StayView tile drag that intentionally caused this handoff.
    //
    // Nor is it a reason to drop to the grid. An empty-canvas press returns to the grid on
    // its own path. The canvas also takes the foreground when a press lands on the focused
    // window's full-size stand-in, or WinUI activates the island; the grid then looked like
    // the focused window dropping out by itself. So wait: a closing window is gone by the
    // next pass and the caller returns to the grid. `canvasSettled` means the canvas has
    // held the foreground past that wait while the selected window is still here, so it
    // goes back in front.
    public static BrowseTarget ClassifyForeground(
        nint selected,
        nint foreground,
        nint foregroundRoot,
        bool foregroundIsOwnCanvas,
        bool ownCanvasGesture,
        nint foregroundSource,
        bool canvasSettled = false)
    {
        if (foreground == 0) return BrowseTarget.Keep; // transient activation handoff
        if (selected != 0 && (foreground == selected || foregroundRoot == selected)) return BrowseTarget.Keep;
        if (foregroundIsOwnCanvas) return ownCanvasGesture || canvasSettled ? BrowseTarget.Refront : BrowseTarget.Keep;
        if (foregroundSource != 0 && foregroundSource != selected) return BrowseTarget.Follow;
        return BrowseTarget.Grid;
    }
}
