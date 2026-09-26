using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace StayView.Core;
public sealed record DesktopInfo(Guid Id,string Name,bool Current,string Wallpaper="");
internal sealed class DirectVirtualDesktopService
{
    IVirtualDesktopManager? standard; IDesktopManager? manager; IViewCollection? views; IPinnedViews? pinned;
    public bool Available=>manager!=null;
    public string Status {get;private set;}="Desktop controls unavailable on this Windows build; current-desktop tiling remains available.";
    public DirectVirtualDesktopService() {
        try {standard=(IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new("AA509086-5CA9-4C25-8F95-589D3C07B48A"))!)!;}catch{}
        Connect();
    }
    long reconnectAfter,reconnectDelay=1000;
    // Explorer restarts, and a single failed call during a shell animation, used to turn
    // desktop support off for the life of the broker. Reconnect instead, backing off from
    // 1 s to 30 s while it keeps failing.
    bool EnsureManager()
    {
        if(manager!=null)return true;
        long now=Environment.TickCount64;
        if(now<reconnectAfter)return false;
        Connect(); // a failed connect backs off through Disable
        return manager!=null;
    }
    void BackOff()
    {
        reconnectAfter=Environment.TickCount64+reconnectDelay;
        reconnectDelay=Math.Min(30000,reconnectDelay*2);
    }
    void Connect() {
        // This ABI is deliberately gated. Unknown builds use the documented current-desktop API.
        if(Environment.OSVersion.Version.Build is <26100 or >26200)return;
        try {
            var shell=(IShellServices)Activator.CreateInstance(Type.GetTypeFromCLSID(new("C2F03A33-21F5-47FA-B4BB-156362A2F239"))!)!;
            var sid=new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");var iid=typeof(IDesktopManager).GUID;
            manager=(IDesktopManager)shell.QueryService(ref sid,ref iid);
            sid=iid=typeof(IViewCollection).GUID;views=(IViewCollection)shell.QueryService(ref sid,ref iid);
            _=manager.GetCount();Status="Windows virtual desktops";
            try { sid=new Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD");iid=typeof(IPinnedViews).GUID;pinned=(IPinnedViews)shell.QueryService(ref sid,ref iid); } catch { }
        }catch(Exception ex){Disable(ex);}
    }
    [DllImport("combase.dll")] static extern nint WindowsGetStringRawBuffer(nint value,out uint length);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(nint value);
    static string ReadHString(nint value) {
        if(value==0)return "";
        try {var buffer=WindowsGetStringRawBuffer(value,out var length);return Marshal.PtrToStringUni(buffer,(int)length)??"";}
        finally {WindowsDeleteString(value);}
    }
    // A connect can succeed while calls keep failing (Explorer busy, RPC_E_CALL_REJECTED):
    // back off here too, or every broker call reconnects and logs. Only a call that
    // actually succeeds resets the delay (see Current).
    void Disable(Exception ex){manager=null;views=null;pinned=null;BackOff();Log.Write("Private desktop API (will reconnect): "+ex.Message);}
    public bool IsCurrent(nint h){try{return standard?.IsWindowOnCurrentVirtualDesktop(h)??true;}catch{return true;}}
    public Guid WindowDesktop(nint h){try{return standard?.GetWindowDesktopId(h)??Guid.Empty;}catch{return Guid.Empty;}}
    public Guid Current {get{if(!EnsureManager())return Guid.Empty;try{var id=manager!.GetCurrentDesktop().GetId();reconnectDelay=1000;return id;}catch(Exception ex){Disable(ex);return Guid.Empty;}}}
    public IReadOnlyList<DesktopInfo> List() {
        try{
            if(!EnsureManager())return [];var current=Current;
            if(manager==null)return [];
            manager.GetDesktops(out var array);array.GetCount(out var count);
            var result=new List<DesktopInfo>();var iid=typeof(IDesktop).GUID;
            try{for(int i=0;i<count;i++){array.GetAt(i,ref iid,out var obj);var d=(IDesktop)obj;var id=d.GetId();string name=RegistryName(id);if(string.IsNullOrWhiteSpace(name))try{name=ReadHString(d.GetName());}catch{name="";}if(string.IsNullOrWhiteSpace(name))name="Desktop "+(i+1);string wallpaper;try{wallpaper=ReadHString(d.GetWallpaperPath());}catch{wallpaper="";}result.Add(new(id,name,id==current,wallpaper));Marshal.ReleaseComObject(d);}}finally{Marshal.ReleaseComObject(array);}
            return result;
        }catch(Exception ex){Disable(ex);return [];}
    }
    public bool Switch(Guid id)=>Do(()=>manager!.SwitchDesktop(manager.FindDesktop(ref id)));
    public bool Create()=>Do(()=>manager!.SwitchDesktop(manager.CreateDesktop()));
    public bool Remove(Guid id) {var fallback=List().FirstOrDefault(x=>x.Id!=id);return fallback!=null&&Do(()=>{var other=fallback.Id;manager!.RemoveDesktop(manager.FindDesktop(ref id),manager.FindDesktop(ref other));});}
    public bool MoveDesktop(Guid id,int index)=>Do(()=>manager!.MoveDesktop(manager.FindDesktop(ref id),index));
    public bool Move(nint h,Guid id)=>Do(()=>{views!.GetViewForHwnd(h,out var view);try{manager!.MoveViewToDesktop(view,manager.FindDesktop(ref id));}finally{Marshal.ReleaseComObject(view);}});
    public bool Pin(nint h) {
        if(!EnsureManager()||pinned==null||views==null)return false;
        try { if(views.GetViewForHwnd(h,out var view)!=0)return false;try{if(!pinned.IsViewPinned(view))pinned.PinView(view);return true;}finally{Marshal.ReleaseComObject(view);} }
        catch(Exception ex){Log.Write("Pin window: "+ex.Message);return false;}
    }
    public bool IsPinned(nint h) {
        if(!EnsureManager()||pinned==null||views==null)return false;
        try { if(views.GetViewForHwnd(h,out var view)!=0)return false;try{return pinned.IsViewPinned(view);}finally{Marshal.ReleaseComObject(view);} }
        catch(Exception ex){Log.Write("Read window pin: "+ex.Message);return false;}
    }
    public bool Unpin(nint h) {
        if(!EnsureManager()||pinned==null||views==null)return false;
        try { if(views.GetViewForHwnd(h,out var view)!=0)return false;try{if(pinned.IsViewPinned(view))pinned.UnpinView(view);return true;}finally{Marshal.ReleaseComObject(view);} }
        catch(Exception ex){Log.Write("Unpin window: "+ex.Message);return false;}
    }
    public void MoveOwnWindow(nint h,Guid id){try{if(id!=Guid.Empty)standard?.MoveWindowToDesktop(h,ref id);}catch{}}
    static string RegistryName(Guid id)
    {
        try{return Registry.GetValue($@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\{{{id}}}","Name","") as string??"";}
        catch{return "";}
    }
    bool Do(Action action){if(!EnsureManager())return false;try{action();return true;}catch(Exception ex){Log.Write("Desktop operation: "+ex.Message);return false;}}
}
[ComImport,Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IVirtualDesktopManager {bool IsWindowOnCurrentVirtualDesktop(nint h);Guid GetWindowDesktopId(nint h);void MoveWindowToDesktop(nint h,ref Guid id);}
[ComImport,Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellServices {[return:MarshalAs(UnmanagedType.IUnknown)]object QueryService(ref Guid sid,ref Guid iid);}
[ComImport,Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IObjectArray {void GetCount(out int count);void GetAt(int index,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object obj);}
[ComImport,Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IView {}
[ComImport,Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IViewCollection {[PreserveSig]int GetViews(out IObjectArray a);[PreserveSig]int GetViewsByZOrder(out IObjectArray a);[PreserveSig]int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)]string id,out IObjectArray a);[PreserveSig]int GetViewForHwnd(nint h,out IView view);}
[ComImport,Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDesktop {
    bool IsViewVisible(IView view);Guid GetId();
    nint GetName();
    nint GetWallpaperPath();
    bool IsRemote();
}
[ComImport,Guid("53F5CA0B-158F-4124-900C-057158060B27"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDesktopManager {
    int GetCount();void MoveViewToDesktop(IView view,IDesktop desktop);bool CanViewMoveDesktops(IView view);
    IDesktop GetCurrentDesktop();void GetDesktops(out IObjectArray desktops);
    [PreserveSig]int GetAdjacentDesktop(IDesktop from,int direction,out IDesktop desktop);
    void SwitchDesktop(IDesktop desktop);void SwitchDesktopAndMoveForegroundView(IDesktop desktop);IDesktop CreateDesktop();void MoveDesktop(IDesktop desktop,int index);void RemoveDesktop(IDesktop desktop,IDesktop fallback);IDesktop FindDesktop(ref Guid id);
}

[ComImport,Guid("4CE81583-1E4C-4632-A621-07A53543148F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPinnedViews {
    bool IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)]string id);
    void PinAppID([MarshalAs(UnmanagedType.LPWStr)]string id);
    void UnpinAppID([MarshalAs(UnmanagedType.LPWStr)]string id);
    bool IsViewPinned(IView view); void PinView(IView view); void UnpinView(IView view);
}
