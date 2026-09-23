namespace StayView.Core;

// One window's remembered arrangement on a single virtual desktop.
public readonly record struct DesktopWindowState(
    Native.RECT Bounds,
    int ShowCmd,
    bool Docked,
    bool MinimizedDocked,
    bool Focused,
    Native.RECT Tile,
    bool HasTile,
    int Z);

// Decides how a saved desktop arrangement maps back onto the live dock set.
// Pinned windows are not part of a desktop's memory: they travel with the user.
public static class DesktopArrangement
{
    public static bool Include(bool pinned) => !pinned;

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
