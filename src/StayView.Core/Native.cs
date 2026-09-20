using System.Runtime.InteropServices;
using System.Text;
namespace StayView.Core;
public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT {
        public int Left,Top,Right,Bottom;
        public RECT(int x,int y,int w,int h) {Left=x;Top=y;Right=x+w;Bottom=y+h;}
        public readonly int Width=>Right-Left; public readonly int Height=>Bottom-Top;
        public readonly bool Intersects(RECT r)=>Left<r.Right&&Right>r.Left&&Top<r.Bottom&&Bottom>r.Top;
    }
    [StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT { public int Length,Flags,ShowCmd; public POINT MinPosition,MaxPosition; public RECT NormalPosition; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct MONITORINFO {public int Size;public RECT Monitor,Work;public uint Flags;}
    [StructLayout(LayoutKind.Sequential)] public struct THUMBNAIL {public uint Flags;public RECT Destination,Source; public byte Opacity; [MarshalAs(UnmanagedType.Bool)] public bool Visible;[MarshalAs(UnmanagedType.Bool)] public bool SourceClientOnly;}
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {public int Dx,Dy;public uint MouseData,DwFlags,Time;public nuint ExtraInfo;}
    [StructLayout(LayoutKind.Sequential)] public struct INPUT {public uint Type;public MOUSEINPUT Mouse;}
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT {public POINT Point;public uint MouseData,Flags,Time;public nuint ExtraInfo;}
    public delegate bool EnumProc(nint hwnd,nint data);
    public delegate void WinEventProc(nint hook,uint ev,nint hwnd,int obj,int child,uint thread,uint time);
    public delegate nint HookProc(int code,nint wParam,nint lParam);
    public delegate nint WndProc(nint hwnd,uint msg,nint wp,nint lp);
    public delegate bool MonitorProc(nint monitor,nint hdc,ref RECT rect,nint data);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb,nint data);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(nint h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(nint h);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint h,uint cmd);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint to, bool flag);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(nint h);
    // SetForegroundWindow is refused when another process received the last input (e.g. the
    // user just double-clicked a different app's window). Briefly sharing the input queue with
    // the current foreground thread lifts that restriction.
    public static bool ForceForeground(nint h)
    {
        var fg = GetForegroundWindow();
        if (fg == h) return true;
        uint fgThread = fg == 0 ? 0 : GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try { BringWindowToTop(h); SetForegroundWindow(h); }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }
        var now = GetForegroundWindow();
        return now == h || GetAncestor(now, 3) == h;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(nint h,StringBuilder text,int max);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(nint h,StringBuilder text,int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint h,out uint pid);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint h,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)] public static extern nint SetWindowLongPtr(nint h,int index,nint value);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint h,out RECT r);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(nint h,ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(nint h,ref WINDOWPLACEMENT p);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool SetWindowPos(nint h,nint after,int x,int y,int w,int height,uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint h,int cmd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(nint h,int cmd);
    [DllImport("user32.dll")] public static extern bool PostMessage(nint h,uint msg,nint wp,nint lp);
    [DllImport("user32.dll")] public static extern nint SendMessage(nint h,uint msg,nint wp,nint lp);
    // Hit-testing another process's frame must never block our input hook on a hung app.
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW")] public static extern nint SendMessageTimeout(nint h,uint msg,nint wp,nint lp,uint flags,uint timeout,out nint result);
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint h,uint flags);
    [DllImport("user32.dll")] public static extern nint MonitorFromRect(ref RECT r,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfo(nint h,ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint hdc,nint clip,MonitorProc cb,nint data);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint h);
    [DllImport("user32.dll")] public static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint monitor,int type,out uint x,out uint y);
    public static double MonitorScale(nint monitor) => GetDpiForMonitor(monitor,0,out var dpi,out _)==0 ? Math.Max(1,dpi/96d) : 1;
    [DllImport("user32.dll")] public static extern nint SetWinEventHook(uint min,uint max,nint module,WinEventProc cb,uint pid,uint thread,uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] public static extern nint SetWindowsHookEx(int id,HookProc cb,nint module,uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook,int code,nint wp,nint lp);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)] public static extern uint SendInput(uint count,[In] INPUT[] inputs,int size);
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint h,uint flags);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint h,int id,uint mods,uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint h,int id);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(nint h,nint region,bool redraw);
    [DllImport("user32.dll")] public static extern nint CallWindowProc(nint prev,nint h,uint msg,nint wp,nint lp);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint h,uint attr,out int value,int size);
    [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")] static extern int DwmGetWindowAttributeRect(nint h,uint attr,out RECT value,int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint h,uint attr,ref uint value,int size);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(nint thumb,out POINT size);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint h,out RECT rect);
    [DllImport("user32.dll")] public static extern int FillRect(nint dc,ref RECT rect,nint brush);
    [DllImport("gdi32.dll")] public static extern nint GetStockObject(int index);
    public static RECT MonitorBounds(nint monitor) {var i=new MONITORINFO{Size=Marshal.SizeOf<MONITORINFO>()};GetMonitorInfo(monitor,ref i);return i.Monitor;}
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(nint dest,nint src,out nint thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(nint thumb,ref THUMBNAIL props);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(nint thumb);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    public static RECT WorkArea(nint monitor) {var i=new MONITORINFO{Size=Marshal.SizeOf<MONITORINFO>()};GetMonitorInfo(monitor,ref i);return i.Work;}
    public static IReadOnlyList<nint> Monitors() {var result=new List<nint>();EnumDisplayMonitors(0,0,(nint monitor,nint hdc,ref RECT rect,nint data)=>{result.Add(monitor);return true;},0);return result;}
    public static string Title(nint h) {var s=new StringBuilder(1024);GetWindowText(h,s,s.Capacity);return s.ToString();}
    public static string Class(nint h) {var s=new StringBuilder(256);GetClassName(h,s,s.Capacity);return s.ToString();}
    public static WINDOWPLACEMENT Placement(nint h) {var p=new WINDOWPLACEMENT{Length=Marshal.SizeOf<WINDOWPLACEMENT>()};GetWindowPlacement(h,ref p);return p;}
    // GetWindowRect includes the invisible resize border on many Windows 10/11 windows.
    // DWM thumbnails, and the pixels the user actually sees when the real HWND takes over,
    // line up with DWMWA_EXTENDED_FRAME_BOUNDS instead. Using the USER32 rectangle as a
    // focus-animation endpoint therefore leaves a small but very visible final snap.
    public static bool TryGetVisualBounds(nint h,out RECT r)
    {
        if(DwmGetWindowAttributeRect(h,9,out r,Marshal.SizeOf<RECT>())==0 && r.Width>0 && r.Height>0)
            return true; // DWMWA_EXTENDED_FRAME_BOUNDS
        return GetWindowRect(h,out r) && r.Width>0 && r.Height>0;
    }
    public static void DisableDwmBorder(nint h) {
        // DWMWA_BORDER_COLOR + DWMWA_COLOR_NONE removes Windows 11's thin native
        // outline around otherwise-borderless StayView windows.
        try { uint none=0xFFFFFFFE; DwmSetWindowAttribute(h,34,ref none,sizeof(uint)); } catch { }
    }
    public static void DisableDwmScreenFrame(nint h) {
        // The full-work-area overview should meet the monitor edge with no native
        // non-client outline or rounded-corner antialiasing. AppWindow.Show can cause
        // Windows to reapply those attributes, so callers may safely reassert this.
        DisableDwmBorder(h);
        try { uint doNotRound=1; DwmSetWindowAttribute(h,33,ref doNotRound,sizeof(uint)); } catch { }
    }
    public static void MakeOverviewTrueBorderless(nint h) {
        // Windows App SDK's OverlappedPresenter can leave WS_DLGFRAME/system-box chrome
        // on a titleless window. On this build that rendered as the persistent 1 px white
        // perimeter seen around the full-screen overview even with DWMWA_BORDER_COLOR off.
        // Strip only the classic non-client chrome bits and ask USER32 to recalculate the
        // frame. Keep all other styles (visibility, clipping, etc.) untouched.
        try {
            const long chrome = 0x00CF0000L; // CAPTION/THICKFRAME/SYSMENU/MIN/MAX boxes.
            long style=GetWindowLongPtr(h,-16).ToInt64();
            long next=style & ~chrome;
            if(next!=style) {
                SetWindowLongPtr(h,-16,(nint)next);
                SetWindowPos(h,0,0,0,0,0,0x37); // NOMOVE|NOSIZE|NOZORDER|NOACTIVATE|FRAMECHANGED
            }
        } catch { }
        DisableDwmScreenFrame(h);
    }
}
