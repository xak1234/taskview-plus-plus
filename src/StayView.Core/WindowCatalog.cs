using System.Diagnostics;
namespace StayView.Core;
public sealed record AppWindow(nint Handle,string Title,nint Monitor,bool Minimized,bool Preview);
public sealed class WindowCatalog:IDisposable
{
    readonly VirtualDesktopService desktops;
    readonly Func<nint,bool>? include;
    readonly Native.WinEventProc callback;
    readonly List<nint> hooks=[];
    public event Action? ForegroundChanged;
    public event Action<nint, bool>? MoveSizeChanged;
    public event Action<nint, bool>? MinimizeChanged;
    public event Action<nint>? LocationChanged;
    public WindowCatalog(VirtualDesktopService desktops,Func<nint,bool>? include=null) {
        this.desktops=desktops;this.include=include;
        // LOCATIONCHANGE is filtered to OBJID_WINDOW and consumed only for browsed sources.
        // It gives focused-window outlines a compositor-speed geometry signal and lets the
        // native resize path enforce its configured minimum while the gesture is in flight.
        callback=(_,ev,h,obj,_,_,_)=>{
            if(ev==3)ForegroundChanged?.Invoke();
            else if(ev is 10 or 11)MoveSizeChanged?.Invoke(h,ev==10);
            else if(ev is 0x16 or 0x17)MinimizeChanged?.Invoke(h,ev==0x16);
            else if(ev==0x800B && obj==0 && h!=0)LocationChanged?.Invoke(h);
        };
        hooks.Add(Native.SetWinEventHook(3,3,0,callback,0,0,2));
        hooks.Add(Native.SetWinEventHook(10,11,0,callback,0,0,2)); // native move/resize start/end
        hooks.Add(Native.SetWinEventHook(0x800B,0x800B,0,callback,0,0,2)); // EVENT_OBJECT_LOCATIONCHANGE
        // EVENT_SYSTEM_MINIMIZESTART / END. The start event lets StayView put its own
        // shrink-to-grid transition in front before Windows' taskbar minimize animation
        // becomes visible; the end event records the user's final iconic state.
        hooks.Add(Native.SetWinEventHook(0x16,0x17,0,callback,0,0,2));
    }
    static readonly HashSet<string> excluded=new(StringComparer.OrdinalIgnoreCase){"Progman","WorkerW","Shell_TrayWnd","Shell_SecondaryTrayWnd","ImmersiveShell","XamlExplorerHost","Windows.UI.Core.CoreWindow"};
    static readonly HashSet<string> excludedProcesses=new(StringComparer.OrdinalIgnoreCase){"StayView","SearchHost","StartMenuExperienceHost","ShellExperienceHost","TextInputHost","GameBar","GameBarFTServer","NVIDIA Overlay"};
    // pid -> process name, valid only within a single enumeration pass (cleared each
    // EnumerateCore). Collapses the per-window Process.GetProcessById cost for processes
    // that own several windows; never spans passes, so recycled pids cannot go stale.
    readonly Dictionary<uint,string?> processNames=[];
    public IReadOnlyList<AppWindow> Enumerate(bool allDesktops=false) => EnumerateCore(allDesktops);
    // The current desktop id resolved during the most recent current-desktop pass, so
    // callers building a topology signature need not make a second broker round-trip.
    public Guid LastCurrent { get; private set; }
    // Inactive desktop pictures need shell-cloaked sources; these never enter the managed tile list.
    public IReadOnlyList<AppWindow> EnumerateDesktopPictures() => EnumerateCore(true);
    HashSet<nint> lastCurrent=[];
    Guid enumCurrent;
    IReadOnlyList<AppWindow> EnumerateCore(bool allDesktops) {
        var result=new List<AppWindow>();
        // Read the current desktop id ONCE for the whole pass. IsWindowOnCurrentVirtualDesktop
        // is known to return false spuriously for every window at once; comparing each
        // window's own desktop id to this single value is stable, and an unknown current
        // id (Empty, e.g. broker unavailable) keeps every window rather than wiping them.
        enumCurrent=allDesktops?Guid.Empty:SafeCurrent();
        if(!allDesktops)LastCurrent=enumCurrent;
        processNames.Clear();
        Native.EnumWindows((h,_)=>{
            if(include!=null&&!include(h))return true;
            if(Reject(h,allDesktops,out var window)==null)result.Add(window!);
            return true;
        },0);
        if(!allDesktops)
        {
            // Diagnostics: a live window leaving the overview is logged with the exact
            // rule that dropped it, re-evaluated now to expose transient flapping.
            var now=result.Select(w=>w.Handle).ToHashSet();
            foreach(var h in lastCurrent)
                if(!now.Contains(h)&&Native.IsWindow(h))
                    Log.Write($"[vanish] {Native.Title(h)} ({h}): {Reject(h,false,out _)??"included again on recheck"}");
            lastCurrent=now;
        }
        return result;
    }
    // Returns why a top-level window is excluded, or null (with the window) if it is a source.
    string? Reject(nint h,bool allDesktops,out AppWindow? window) {
        window=null;
        if(!Native.IsWindowVisible(h))return "not visible";
        Native.GetWindowThreadProcessId(h,out var pid);
        if(pid==Environment.ProcessId)return "own process";
        if(Native.GetWindow(h,4)!=0)return "owned window";
        if((Native.GetWindowLongPtr(h,-20).ToInt64()&0x80)!=0)return "tool window";
        var cls=Native.Class(h);if(excluded.Contains(cls))return "shell class "+cls;
        // Never let another StayView UI/broker/guardian instance become a source in a
        // desktop miniature. The current UI process is already excluded by pid above;
        // this also covers stale/parallel copies. The process name is resolved once per
        // pid per pass (many windows share one process).
        if(!processNames.TryGetValue(pid,out var name))
        {
            try{using var process=Process.GetProcessById((int)pid);name=process.ProcessName;}
            catch(Exception ex){processNames[pid]=null;return "process lookup failed: "+ex.GetType().Name;}
            processNames[pid]=name;
        }
        if(name==null)return "process lookup failed";
        if(excludedProcesses.Contains(name))return "excluded process "+name;
        if(!allDesktops&&!OnCurrentDesktop(h))return "not on current desktop";
        Native.DwmGetWindowAttribute(h,14,out var cloaked,4);
        if(cloaked!=0&&!(allDesktops&&cloaked==2&&desktops.Available))return "cloaked "+cloaked;
        var title=Native.Title(h);if(string.IsNullOrWhiteSpace(title))return "empty title";
        Native.GetWindowRect(h,out var r);
        bool min=Native.IsIconic(h);
        if(!min&&(r.Width<32||r.Height<32||Native.MonitorFromRect(ref r,0)==0))return $"tiny or off-screen rect {r.Left},{r.Top} {r.Width}x{r.Height}";
        var m=Native.MonitorFromWindow(h,2);var work=Native.WorkArea(m);
        var style=Native.GetWindowLongPtr(h,-16).ToInt64();
        bool fullscreen=!min&&(style&0x00C00000)==0&&r.Width>=work.Width&&r.Height>=work.Height;
        // Conservatively skip borderless full-monitor windows, including exclusive games.
        if(fullscreen)return $"borderless fullscreen {r.Width}x{r.Height} style=0x{style:X}";
        bool fixedSize=(style&0x00040000)==0;
        window=new(h,title,m,min,fullscreen||fixedSize);
        return null;
    }
    Guid SafeCurrent()
    {
        try{return desktops.Current;}catch{return Guid.Empty;}
    }
    bool OnCurrentDesktop(nint h)
    {
        if(enumCurrent==Guid.Empty)return true;            // Unknown current desktop: never wipe.
        var id=desktops.WindowDesktop(h);
        if(id==Guid.Empty)return desktops.IsCurrent(h);    // Unknown window desktop: trust the boolean.
        return id==enumCurrent;
    }
    public void Dispose(){foreach(var h in hooks)if(h!=0)Native.UnhookWinEvent(h);GC.KeepAlive(callback);}
}
