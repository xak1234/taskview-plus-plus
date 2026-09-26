using StayView.Core;
namespace StayView;
// Builds the saved state from the live windows on every desktop.
static class WorkspaceCapture
{
    public static List<SavedWindow> Capture(WindowCatalog catalog,VirtualDesktopService desktops,
        Func<nint,Native.WINDOWPLACEMENT?> userPlacement,Func<nint,bool> docked,Func<nint,bool?> dockLeft,
        IReadOnlyDictionary<nint,(bool TileOnly,Guid Home,Native.RECT Bounds,bool Maximized,bool AllDesktops)> pins)
    {
        var folders=ExplorerFolders();
        var result=new List<SavedWindow>();
        int z=0;
        foreach(var w in catalog.Enumerate(true))
        {
            var h=w.Handle;
            Native.GetWindowThreadProcessId(h,out var pid);
            if(!ProcessInfo.TryRead(pid,out var exe,out var cmd,out var key)){Log.Write($"[workspace] skipped {h}: process not readable (elevated?)");continue;}
            if(ProcessInfo.IsPackaged(exe))continue;
            // Elevated (admin) apps are readable but relaunching them raises a UAC prompt at
            // every sign-in: never saved.
            if(ProcessInfo.IsElevated(pid)){Log.Write($"[workspace] skipped {h}: elevated {Path.GetFileName(exe)}");continue;}
            string? folder=ProcessInfo.IsExplorer(exe)&&folders.TryGetValue(h,out var f)?f:null;
            if(ProcessInfo.IsExplorer(exe)&&folder==null)continue; // not a folder window we can reopen
            var placement=userPlacement(h)??Native.Placement(h);
            bool isDocked=docked(h);
            if(isDocked&&placement.ShowCmd is not (2 or 6 or 7))placement.ShowCmd=2; // docked = minimized outside the overview
            pins.TryGetValue(h,out var pin);bool pinned=pins.ContainsKey(h);
            result.Add(new SavedWindow(exe,cmd,key,folder,Native.Class(h),w.Title,desktops.WindowDesktop(h),placement,z++,
                isDocked&&!pinned,dockLeft(h)??true,pinned,pin.TileOnly,pin.Home,pin.Bounds,pin.Maximized,pin.AllDesktops));
        }
        return result;
    }
    // HWND -> folder path of every open File Explorer window (Shell.Application).
    internal static Dictionary<nint,string> ExplorerFolders()
    {
        var map=new Dictionary<nint,string>();
        try
        {
            dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach(dynamic window in shell.Windows())
            {
                try
                {
                    string? url=window.LocationURL;
                    if(string.IsNullOrEmpty(url)||!url.StartsWith("file:",StringComparison.OrdinalIgnoreCase))continue;
                    map[(nint)(long)window.HWND]=new Uri(url).LocalPath;
                }
                catch{}
            }
        }
        catch(Exception ex){Log.Write("[workspace] explorer folders unavailable: "+ex.Message);}
        return map;
    }
}
