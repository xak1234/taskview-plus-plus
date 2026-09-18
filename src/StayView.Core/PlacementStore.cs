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
    readonly Dictionary<nint,SavedPlacement> saved=[];
    public IReadOnlyDictionary<nint,SavedPlacement> Entries=>saved;
    static bool TrySnapshot(nint h,int z,Guid desktopId,out SavedPlacement snapshot)
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
    public void Persist() {Directory.CreateDirectory(Settings.Folder);File.WriteAllText(Journal+".tmp",JsonSerializer.Serialize(saved.Values,json));File.Move(Journal+".tmp",Journal,true);}
    // A window the user deliberately dragged keeps where they put it: refresh its journal
    // entry (same z-order and desktop) so dismissal restores the new spot, not the old one.
    public void Recapture(nint h) {
        if(!saved.TryGetValue(h,out var old))return;
        // Never remove the known-good journal entry until a complete replacement exists.
        // A transient process/window race must not turn a recapture failure into lost
        // restore state for the rest of the session.
        if(!TrySnapshot(h,old.Z,old.DesktopId,out var replacement))return;
        saved[h]=replacement;
        Persist();
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
    void PersistOrDelete(){if(saved.Count>0)Persist();else try{File.Delete(Journal);}catch{}}
    static bool SameWindow(SavedPlacement s) {
        var h=(nint)s.Handle;if(!Native.IsWindow(h))return false;
        Native.GetWindowThreadProcessId(h,out var pid);if(pid!=s.ProcessId)return false;
        try{return Process.GetProcessById((int)pid).StartTime.ToUniversalTime().Ticks==s.ProcessStarted;}catch{return false;}
    }
    public static bool RestoreOne(SavedPlacement s,VirtualDesktopService? desktops=null) {
        if(!SameWindow(s))return true;
        var h=(nint)s.Handle;var p=s.Placement;p.Length=System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>();
        bool ok=Native.SetWindowPlacement(h,ref p);
        if(p.ShowCmd==1) {var r=s.Bounds;
            if(Native.MonitorFromRect(ref r,0)==0) {var work=Native.WorkArea(Native.MonitorFromWindow(h,2));r=new(work.Left+20,work.Top+20,Math.Min(r.Width,work.Width-40),Math.Min(r.Height,work.Height-40));}
            ok=Native.SetWindowPos(h,s.Topmost?-1:0,r.Left,r.Top,r.Width,r.Height,0x10|0x4000)&&ok;
        }else ok=Native.SetWindowPos(h,s.Topmost?-1:0,0,0,0,0,0x13|0x4000)&&ok;
        // A failed restore remains in the journal; never substitute an invented size.
        if(s.DesktopId!=Guid.Empty){desktops??=new VirtualDesktopService();if(desktops.WindowDesktop(h)!=s.DesktopId)ok=desktops.Move(h,s.DesktopId)&&ok;}
        return ok;
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
