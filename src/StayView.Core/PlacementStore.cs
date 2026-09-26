using System.Diagnostics;
using System.Text.Json;
namespace StayView.Core;
public sealed class SavedPlacement
{
    public long Handle {get;set;} public uint ProcessId {get;set;} public long ProcessStarted {get;set;}
    public Native.WINDOWPLACEMENT Placement {get;set;} public Native.RECT Bounds {get;set;}
    public long Monitor {get;set;} public uint Dpi {get;set;} public int Z {get;set;}
    public bool Topmost {get;set;} public bool Visible {get;set;} public int Cloaked {get;set;}
    public Guid DesktopId {get;set;}
}
public sealed class PlacementStore
{
    static readonly JsonSerializerOptions json=new(){IncludeFields=true,WriteIndented=true};
    public static string Journal=>Path.Combine(Settings.Folder,"placements.json");
    readonly string journal;
    public PlacementStore(string? journalPath=null){journal=journalPath??Journal;}
    readonly Dictionary<nint,SavedPlacement> saved=[];
    public IReadOnlyDictionary<nint,SavedPlacement> Entries=>saved;
    public static bool TrySnapshot(nint h,int z,Guid desktopId,out SavedPlacement snapshot)
    {
        snapshot=new();
        if(h==0||!Native.IsWindow(h))return false;
        Native.GetWindowThreadProcessId(h,out var pid);if(pid==0)return false;long started;
        try{started=Process.GetProcessById((int)pid).StartTime.ToUniversalTime().Ticks;}catch{return false;}
        if(!Native.GetWindowRect(h,out var rect))return false;
        var placement=new Native.WINDOWPLACEMENT{Length=System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>()};
        if(!Native.GetWindowPlacement(h,ref placement))return false;
        Native.DwmGetWindowAttribute(h,14,out var cloaked,4);
        snapshot=new(){Handle=h,ProcessId=pid,ProcessStarted=started,Placement=placement,Bounds=rect,Monitor=Native.MonitorFromWindow(h,2),Dpi=Native.GetDpiForWindow(h),Z=z,Topmost=(Native.GetWindowLongPtr(h,-20).ToInt64()&8)!=0,Visible=Native.IsWindowVisible(h),Cloaked=cloaked,DesktopId=desktopId};
        return true;
    }
    // Returns true only when a new entry was actually journaled, so callers can skip a
    // redundant disk write when the captured set is unchanged.
    public bool Capture(nint h,int z,Guid desktopId=default) {
        if(saved.ContainsKey(h))return false;
        if(!TrySnapshot(h,z,desktopId,out var snapshot))return false;
        saved[h]=snapshot;
        return true;
    }
    public void Persist() {Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journal))!);File.WriteAllText(journal+".tmp",JsonSerializer.Serialize(saved.Values,json));File.Move(journal+".tmp",journal,true);}
    // A window the user deliberately dragged keeps where they put it: refresh its journal
    // entry (same z-order and desktop) so dismissal restores the new spot, not the old one.
    // dx/dy is how far StayView itself has moved the window for the overview (centre over
    // tile, off the desktop bar, clear of a pin). That part is presentation, not the
    // user's placement, so it is taken back out of what is journaled.
    public void Recapture(nint h,int dx=0,int dy=0) {
        if(!saved.TryGetValue(h,out var old))return;
        // Never remove the known-good journal entry until a complete replacement exists.
        // A transient process/window race must not turn a recapture failure into lost
        // restore state for the rest of the session.
        if(!TrySnapshot(h,old.Z,old.DesktopId,out var replacement))return;
        if(dx!=0||dy!=0)
        {
            replacement.Bounds=Offset(replacement.Bounds,-dx,-dy);
            var p=replacement.Placement;p.NormalPosition=Offset(p.NormalPosition,-dx,-dy);replacement.Placement=p;
        }
        saved[h]=replacement;
        Persist();
    }
    static Native.RECT Offset(Native.RECT r,int dx,int dy)=>new(r.Left+dx,r.Top+dy,r.Width,r.Height);
    // Update only geometry after StayView makes a safety correction (for example moving a
    // title bar back below the top of the work area). Preserve the original show state,
    // z-order/topmost intent and desktop metadata rather than recapturing temporary overview
    // state such as HWND_BOTTOM.
    public void UpdateGeometry(nint h,int dx=0,int dy=0)
    {
        if(!saved.TryGetValue(h,out var entry) || Native.IsIconic(h) || !Native.GetWindowRect(h,out var bounds))return;
        var current=Native.Placement(h);
        var p=entry.Placement;
        // dx/dy: StayView's own presentation moves, taken back out (see Recapture).
        p.NormalPosition=Offset(current.NormalPosition,-dx,-dy);
        entry.Placement=p;
        entry.Bounds=Offset(bounds,-dx,-dy);
        entry.Monitor=Native.MonitorFromWindow(h,2);
        entry.Dpi=Native.GetDpiForWindow(h);
        Persist();
    }
    public void MarkMinimized(nint h)
    {
        if(!saved.TryGetValue(h,out var entry) || !Native.IsIconic(h))return;
        var placement=new Native.WINDOWPLACEMENT{Length=System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>()};
        if(!Native.GetWindowPlacement(h,ref placement))return;
        // Retain the last on-screen bounds; GetWindowRect on an iconic window is a sentinel.
        entry.Placement=placement;
        Persist();
    }
    // Moving an overview miniature is an explicit user placement change. Translate the
    // saved real-window rectangle by the same screen-space delta while preserving size,
    // show state, z-order and desktop. The real HWND is deliberately not touched here:
    // OverviewSession applies this target while the opaque overview still covers it, or
    // Restore() applies it on dismissal. That prevents a hidden source from flashing.
    public bool Translate(nint h,int dx,int dy)
    {
        if((dx==0&&dy==0)||!saved.TryGetValue(h,out var entry))return false;
        static Native.RECT Shift(Native.RECT r,int x,int y)
            => new(r.Left+x,r.Top+y,r.Width,r.Height);
        var bounds=entry.Bounds;
        if(Native.MonitorFromRect(ref bounds,0)!=0)entry.Bounds=Shift(bounds,dx,dy);
        var p=entry.Placement;
        p.NormalPosition=Shift(p.NormalPosition,dx,dy);
        entry.Placement=p;
        var target=entry.Bounds;
        entry.Monitor=Native.MonitorFromRect(ref target,2);
        Persist();
        return true;
    }
    // A deliberate virtual-desktop move is user state, not temporary overview state.
    // Update only the desktop id so geometry/show-state still restore exactly as captured.
    public void SetDesktop(nint h, Guid desktopId) {
        if(!saved.TryGetValue(h,out var entry))return;
        entry.DesktopId=desktopId;
        Persist();
    }
    // Removing a virtual desktop is an explicit user action. Windows migrates every window
    // on it to a surviving desktop, so the journal must stop trying to restore those
    // windows to a GUID that no longer exists. Their new desktop is now Windows/user state.
    public void ForgetDesktop(Guid desktopId) {
        bool changed=false;
        foreach(var entry in saved.Values)
            if(entry.DesktopId==desktopId){entry.DesktopId=Guid.Empty;changed=true;}
        if(changed)Persist();
    }
    void PersistOrDelete(){if(saved.Count>0)Persist();else try{File.Delete(journal);}catch{}}
    static bool SameWindow(SavedPlacement s) {
        var h=(nint)s.Handle;if(!Native.IsWindow(h))return false;
        Native.GetWindowThreadProcessId(h,out var pid);if(pid!=s.ProcessId)return false;
        try{return Process.GetProcessById((int)pid).StartTime.ToUniversalTime().Ticks==s.ProcessStarted;}catch{return false;}
    }
    public static bool RestoreOne(SavedPlacement s,VirtualDesktopService? desktops=null) {
        if(!SameWindow(s))return true;
        var h=(nint)s.Handle;
        // The app hid this window itself during the session (closed to the tray, for
        // example). SetWindowPlacement with any show command would bring it back.
        if(!Native.IsWindowVisible(h))return true;
        var p=s.Placement;p.Length=System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>();
        int show=p.ShowCmd;
        if(show==1)p.ShowCmd=4; // Restore geometry without activating another desktop.
        else if(show is 2 or 6 or 7)p.ShowCmd=7;
        bool ok=Native.SetWindowPlacement(h,ref p);
        var r=s.Bounds;
        if(show==1 && Native.MonitorFromRect(ref r,0)!=0) {
            ok=Native.SetWindowPos(h,s.Topmost?-1:0,r.Left,r.Top,r.Width,r.Height,0x10|0x4000)&&ok;
        }else ok=Native.SetWindowPos(h,s.Topmost?-1:0,0,0,0,0,0x13|0x4000)&&ok;
        // A failed restore remains in the journal; never substitute an invented size.
        if(s.DesktopId!=Guid.Empty){desktops??=new VirtualDesktopService();if(desktops.WindowDesktop(h)!=s.DesktopId)ok=desktops.Move(h,s.DesktopId)&&ok;}
        return ok;
    }
    // Bounds captured while iconic are not screen positions. In that case let Windows
    // interpret NormalPosition itself, including taskbar/workspace offsets.
    public static bool ApplyPendingGeometry(nint h,SavedPlacement saved)
    {
        var bounds=saved.Bounds;
        if(Native.IsIconic(h)||Native.IsZoomed(h)||Native.MonitorFromRect(ref bounds,0)==0)
        {
            var p=Native.Placement(h);
            p.Length=System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>();
            p.NormalPosition=saved.Placement.NormalPosition;
            if(!Native.IsIconic(h)&&!Native.IsZoomed(h))p.ShowCmd=4;
            else if(Native.IsIconic(h))p.ShowCmd=7;
            return Native.SetWindowPlacement(h,ref p);
        }
        return Native.SetWindowPos(h,0,bounds.Left,bounds.Top,0,0,0x15);
    }
    public void Restore() {
        // One desktop service for the whole batch instead of one COM activation per window.
        var desktops=new VirtualDesktopService();
        foreach(var s in saved.Values.OrderByDescending(x=>x.Z).ToList())if(RestoreOne(s,desktops))saved.Remove((nint)s.Handle);
        if(saved.Count>0)Log.Write("Some placements need recovery on the next launch.");
        PersistOrDelete();
    }
    public bool RestoreWindow(nint h){
        if(!saved.TryGetValue(h,out var s))return true;
        if(!RestoreOne(s)){Persist();return false;}
        saved.Remove(h);PersistOrDelete();return true;
    }
    public static void Recover() {
        try{
            if(!File.Exists(Journal))return;
            var list=JsonSerializer.Deserialize<List<SavedPlacement>>(File.ReadAllText(Journal),json)??[];
            var pending=new List<SavedPlacement>();
            var desktops=new VirtualDesktopService();
            foreach(var s in list.OrderByDescending(x=>x.Z))if(!RestoreOne(s,desktops))pending.Add(s);
            if(pending.Count==0){File.Delete(Journal);return;}
            File.WriteAllText(Journal+".tmp",JsonSerializer.Serialize(pending,json));
            File.Move(Journal+".tmp",Journal,true);
            Log.Write("Recovery left "+pending.Count+" placement(s) pending for the next launch.");
        }catch(Exception ex){Log.Write(ex.ToString());}
    }
}
public static class Log {public static void Write(string text){try{Directory.CreateDirectory(Settings.Folder);File.AppendAllText(Path.Combine(Settings.Folder,"stayview.log"),DateTime.Now.ToString("O")+" "+text+Environment.NewLine);}catch{}}}
