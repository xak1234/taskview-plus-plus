using System.Runtime.InteropServices;
using StayView.Core;
namespace StayView;
sealed class TrayIcon:IDisposable
{
    [StructLayout(LayoutKind.Sequential)]struct MenuInfo{public uint Size,Mask,Style,MaxHeight;public nint Background;public uint Help;public nuint Data;}
    [StructLayout(LayoutKind.Sequential)]struct MeasureItem{public uint Type,ControlId,ItemId,Width,Height;public nuint Data;}
    [StructLayout(LayoutKind.Sequential)]struct DrawItem{public uint Type,ControlId,ItemId,Action,State;public nint Item,Hdc;public Native.RECT Rect;public nuint Data;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct Data{
        public int Size;public nint Hwnd;public uint Id,Flags,Callback;public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string InfoTitle;
        public uint InfoFlags;public Guid Guid;public nint Balloon;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern bool Shell_NotifyIcon(uint msg,ref Data data);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern uint ExtractIconEx(string file,int index,nint[]? large,nint[]? small,uint count);
    [DllImport("user32.dll")]static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")]static extern nint CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool AppendMenu(nint menu,uint flags,nuint id,string? text);
    [DllImport("user32.dll")]static extern int TrackPopupMenu(nint menu,uint flags,int x,int y,int reserved,nint owner,nint rect);
    [DllImport("user32.dll")]static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")]static extern bool GetCursorPos(out Native.POINT p);
    [DllImport("user32.dll")]static extern bool SetMenuInfo(nint menu,ref MenuInfo info);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int DrawText(nint dc,string text,int count,ref Native.RECT rect,uint format);
    [DllImport("gdi32.dll")]static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")]static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")]static extern uint SetTextColor(nint dc,uint color);
    [DllImport("gdi32.dll")]static extern int SetBkMode(nint dc,int mode);
    [DllImport("gdi32.dll")]static extern nint SelectObject(nint dc,nint obj);
    static readonly string[] menuText={"","Open Overview","Options","Exit Taskview++"};
    static nint menuBrush,menuSelectedBrush;
    static double menuScale=1;
    Data data;
    readonly nint icon;
    public TrayIcon(nint hwnd,string hotkey){
        var small=new nint[1];
        try{ExtractIconEx(Environment.ProcessPath!,0,null,small,1);}catch{}
        icon=small[0];
        data=new(){Size=Marshal.SizeOf<Data>(),Hwnd=hwnd,Id=1,Flags=7,Callback=0x8001,Icon=icon,Tip="Taskview++ • "+hotkey,Info="",InfoTitle=""};Add();
    }
    public void Add()=>Shell_NotifyIcon(0,ref data);
    public void Update(string hotkey){data.Tip="Taskview++ • "+hotkey;Shell_NotifyIcon(1,ref data);}
    public int Menu(){
        var menu=CreatePopupMenu();
        menuScale=Math.Max(1,Native.GetDpiForWindow(data.Hwnd)/96d);
        menuBrush=CreateSolidBrush(0x00363636);menuSelectedBrush=CreateSolidBrush(0x00545454);
        var info=new MenuInfo{Size=(uint)Marshal.SizeOf<MenuInfo>(),Mask=2,Background=menuBrush};SetMenuInfo(menu,ref info);
        AppendMenu(menu,0x100,1,null);AppendMenu(menu,0x100,2,null);AppendMenu(menu,0x100,3,null);
        GetCursorPos(out var p);Native.SetForegroundWindow(data.Hwnd);
        int id=TrackPopupMenu(menu,0x100|2,p.X,p.Y,0,data.Hwnd,0);DestroyMenu(menu);
        if(menuBrush!=0)DeleteObject(menuBrush);if(menuSelectedBrush!=0)DeleteObject(menuSelectedBrush);menuBrush=menuSelectedBrush=0;
        Native.PostMessage(data.Hwnd,0,0,0);return id;
    }
    public static bool HandleOwnerDraw(uint msg,nint lp,out nint result)
    {
        result=0;
        if(msg==0x2C) // WM_MEASUREITEM
        {
            var m=Marshal.PtrToStructure<MeasureItem>(lp);if(m.Type!=1||m.ItemId<1||m.ItemId>=menuText.Length)return false;
            m.Width=(uint)Math.Round(170*menuScale);m.Height=(uint)Math.Round(32*menuScale);Marshal.StructureToPtr(m,lp,false);return true;
        }
        if(msg!=0x2B)return false; // WM_DRAWITEM
        var d=Marshal.PtrToStructure<DrawItem>(lp);if(d.Type!=1||d.ItemId<1||d.ItemId>=menuText.Length)return false;
        var r=d.Rect;Native.FillRect(d.Hdc,ref r,(d.State&1)!=0?menuSelectedBrush:menuBrush);
        var old=SelectObject(d.Hdc,Native.GetStockObject(17));SetBkMode(d.Hdc,1);SetTextColor(d.Hdc,0x00FFFFFF);
        r.Left+=(int)Math.Round(12*menuScale);r.Right-=(int)Math.Round(10*menuScale);
        DrawText(d.Hdc,menuText[d.ItemId],-1,ref r,0x20|0x4|0x800);
        if(old!=0)SelectObject(d.Hdc,old);return true;
    }
    public void Dispose(){Shell_NotifyIcon(2,ref data);if(icon!=0)DestroyIcon(icon);}
}
