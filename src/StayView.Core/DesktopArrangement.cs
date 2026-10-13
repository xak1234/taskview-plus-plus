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
    int Z,
    Native.RECT NormalPosition=default,
    bool HasScreenBounds=false);

// Decides how a saved desktop arrangement maps back onto the live dock set.
// Pinned windows are not part of a desktop's memory: they travel with the user.
public static class DesktopArrangement
{
    public static bool Include(bool pinned) => !pinned;

    // A cloaked/temporarily hidden source can disappear from one enumeration.
    // Merge that partial sample instead of deleting its last known arrangement.
    public static Dictionary<nint,DesktopWindowState> MergeSnapshots(
        IReadOnlyDictionary<nint,DesktopWindowState> previous,
        IReadOnlyDictionary<nint,DesktopWindowState> observed,
        Func<nint,bool> retain)
    {
        var result=previous.Where(pair=>retain(pair.Key)).ToDictionary(pair=>pair.Key,pair=>pair.Value);
        foreach(var pair in observed)result[pair.Key]=pair.Value;
        return result;
    }

    // WINDOWPLACEMENT uses workspace coordinates; GetWindowRect/SetWindowPos
    // use screen coordinates. Never replace NormalPosition with screen Bounds.
    public static Native.WINDOWPLACEMENT RecallPlacement(DesktopWindowState saved,
        Native.WINDOWPLACEMENT current,int show)
    {
        current.Length=System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>();
        current.ShowCmd=show;
        if(saved.NormalPosition.Width>0 && saved.NormalPosition.Height>0)
            current.NormalPosition=saved.NormalPosition;
        return current;
    }

    public static bool RestoreWindow(nint h,DesktopWindowState saved)
    {
        if(!Native.IsWindow(h))return false;
        bool iconic=Native.IsIconic(h),maximized=Native.IsZoomed(h);
        if(RecallShowCmd(saved.ShowCmd,iconic) is not int show)return true;
        if(show==4 && !iconic && !maximized)
        {
            // Most desktop switches need no native move at all. In particular,
            // a previously minimized source has no saved screen rectangle.
            if(!saved.HasScreenBounds)return true;
            var bounds=saved.Bounds;
            if(bounds.Width<=0 || bounds.Height<=0)return false;
            if(Native.GetWindowRect(h,out var actual) && actual.Equals(bounds))return true;
            FocusedWindowGeometry.LogIfCaptionHidden(h,bounds,"desktop return");
            return Native.SetWindowPos(h,0,bounds.Left,bounds.Top,bounds.Width,bounds.Height,0x14|0x4000);
        }
        var current=Native.Placement(h);
        var placement=RecallPlacement(saved,current,show);
        // Reapplying ShowCmd=3 activates an already maximized app unnecessarily.
        if(placement.ShowCmd==current.ShowCmd && placement.NormalPosition.Equals(current.NormalPosition))return true;
        if(placement.ShowCmd==4)FocusedWindowGeometry.LogIfCaptionHidden(h,placement.NormalPosition,"desktop return");
        return Native.SetWindowPlacement(h,ref placement);
    }

    // A desktop remembers an explicit empty focus list as well as full-size
    // windows. Pins retain their full-size/tile state and do not consume the cap.
    public static IReadOnlyList<nint> FocusOnArrival(IEnumerable<nint> remembered,
        IEnumerable<nint> currentPins,Func<nint,bool> available,int maxUnpinned)
    {
        var pins=currentPins.Where(available).ToHashSet();
        return BrowseReconciler.LimitWithPinned(
            remembered.Concat(pins).Where(available),pins,maxUnpinned);
    }

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
