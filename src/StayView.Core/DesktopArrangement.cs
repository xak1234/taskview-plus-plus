namespace StayView.Core;

// One window's remembered arrangement on a single virtual desktop.
public readonly record struct DesktopWindowState(
    Native.RECT Bounds,
    int ShowCmd,
    bool Docked,
    bool MinimizedDocked,
    bool Focused,
    int FocusOrder,
    Native.RECT Tile,
    bool HasTile,
    int Z);

// Decides how a saved desktop arrangement maps back onto the live dock set.
// Pinned windows are not part of a desktop's memory: they travel with the user.
public static class DesktopArrangement
{
    public static bool Include(bool pinned) => !pinned;

    // The show command used to put a window back when its desktop is re-entered while the
    // overview is up, or null to leave it alone. A window remembered as minimized is never
    // minimized for real here: the overview keeps minimized sources restored behind the
    // canvas so their dock/tile thumbnails stay live, so minimizing it only played the
    // minimize animation over the switch, then the restore animation, and eventually got
    // the window flagged as minimizing itself. Iconic ones are left for the keep-alive;
    // shown ones are only repositioned.
    public static int? RecallShowCmd(int rememberedShowCmd, bool iconicNow)
        => rememberedShowCmd is 2 or 6 or 7 ? (iconicNow ? null : 4)
            : rememberedShowCmd == 3 ? 3 : 4;

    public static void MembershipEdits(
        IEnumerable<KeyValuePair<nint, DesktopWindowState>> saved,
        Func<nint, bool> dockedNow,
        List<nint> dock,
        List<nint> undock)
    {
        dock.Clear();
        undock.Clear();
        foreach (var (h, state) in saved)
        {
            bool now = dockedNow(h);
            if (state.Docked && !now) dock.Add(h);
            else if (!state.Docked && now) undock.Add(h);
        }
    }
}
