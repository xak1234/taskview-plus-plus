using System.Runtime.InteropServices;
using StayView.Core;

static class Fixtures
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WindowClass {
        public uint Style; public Native.WndProc Proc; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? Menu; public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] struct Message { public nint Hwnd; public uint Id; public nuint Wp; public nint Lp; public uint Time; public Native.POINT Point; public uint Private; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern ushort RegisterClass(ref WindowClass c);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint data);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern nint DefWindowProc(nint h,uint m,nint w,nint l);
    [DllImport("user32.dll")] static extern bool GetMessage(out Message m,nint h,uint min,uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message m);
    [DllImport("user32.dll")] static extern nint DispatchMessage(ref Message m);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern nuint SetTimer(nint h,nuint id,uint ms,nint proc);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool SetWindowText(nint h,string text);
    static readonly Native.WndProc proc = Procedure;
    static int windows;
    public static async Task CheckEmptySpace()
    {
        var ready = new TaskCompletionSource<(nint Window, nint Edit, nint Button, nint Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var cls = new WindowClass { Proc=proc, Instance=Native.GetModuleHandle(null), Background=6, Name="StayViewEmptySpaceFixture" };
                RegisterClass(ref cls);
                var h=CreateWindowEx(0,cls.Name,"StayView empty-space test (disposable)",0x00CF0000,100,100,1100,750,0,0,cls.Instance,0);
                if(h==0)throw new Exception("Could not create empty-space fixture");
                windows=1;
                var edit=CreateWindowEx(0x200,"EDIT","Double-click text should stay in the app",0x503000C4,24,104,600,220,h,(nint)100,cls.Instance,0);
                var button=CreateWindowEx(0,"BUTTON","App button",0x50000000,24,48,180,42,h,(nint)101,cls.Instance,0);
                var label=CreateWindowEx(0,"STATIC","App text",0x50000000,24,12,540,30,h,(nint)102,cls.Instance,0);
                Native.ShowWindow(h,1); Native.ShowWindow(h,4);
                Native.SetWindowPos(h,-1,0,0,0,0,0x13);
                ready.SetResult((h,edit,button,label));
                while(GetMessage(out var message,0,0,0)){TranslateMessage(ref message);DispatchMessage(ref message);}
            }
            catch(Exception ex){ready.TrySetException(ex);}
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var fixture=await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Native.GetWindowRect(fixture.Window,out var r);
            var blank=new Native.POINT { X=r.Left+850,Y=r.Top+550 };
            var probe=new EmptySpaceProbe();
            bool empty=false;
            // First UIA activation may be cold; retries are bounded and are only a test warmup.
            for(int i=0;i<10&&!empty;i++){empty=await probe.IsEmptyAsync(fixture.Window,blank,blank);if(!empty)await Task.Delay(100);}
            if(!empty)throw new Exception("Real empty client background was not recognized");
            Console.WriteLine("PASS: real Win32 empty background recognized by accessibility.");
            foreach(var control in new[]{fixture.Edit,fixture.Button,fixture.Text})
            {
                Native.GetWindowRect(control,out r);
                var point=new Native.POINT { X=r.Left+10,Y=r.Top+10 };
                if(await probe.IsEmptyAsync(fixture.Window,point,point))throw new Exception("Real app control classified as empty background");
                if(await probe.IsEmptyAsync(fixture.Window,blank,point))throw new Exception("Mixed blank/control double-click classified as empty");
            }
            Console.WriteLine("PASS: real edit, button and text controls, including mixed-point double-clicks, stay protected.");
        }
        finally { Native.PostMessage(fixture.Window,0x10,0,0); thread.Join(2000); }
    }
    public static void Run() {
        var cls=new WindowClass { Proc=proc, Instance=Native.GetModuleHandle(null), Background=6, Name="StayViewAcceptanceFixture" };
        RegisterClass(ref cls);
        for(int i=0;i<3;i++) {
            var h=CreateWindowEx(0,cls.Name,"StayView fixture "+new[]{"normal","maximized","minimized"}[i],0x00CF0000,
                140+i*100,180+i*70,1600+i*100,1000+i*60,0,0,cls.Instance,0);
            windows++;
            var text=CreateWindowEx(0,"STATIC","Disposable StayView test window. No user documents.",0x50000000,24,12,540,30,h,(nint)102,cls.Instance,0);
            CreateWindowEx(0,"BUTTON","Test real click",0x50000000,24,48,180,42,h,(nint)101,cls.Instance,0);
            CreateWindowEx(0x200,"EDIT",string.Join("\r\n",Enumerable.Range(1,100).Select(n=>"Line "+n+" — real editable, scrollable HWND")),0x503000C4,24,104,640,300,h,(nint)100,cls.Instance,0);
            Native.ShowWindow(h,new[]{1,3,2}[i]);
            if(i==0)Native.ShowWindow(h,4); // Override hidden STARTUPINFO on the first window.
        }
        Console.WriteLine("Three disposable fixture windows ready.");
        while(GetMessage(out var message,0,0,0)){TranslateMessage(ref message);DispatchMessage(ref message);}
    }
    static nint Procedure(nint h,uint message,nint wp,nint lp) {
        if(message==0x111 && (wp.ToInt64()&0xffff)==101){SetWindowText(h,"StayView fixture — native button clicked");return 0;}
        if(message==0x10){DestroyWindow(h);return 0;}
        if(message==2){if(--windows==0)PostQuitMessage(0);return 0;}
        return DefWindowProc(h,message,wp,lp);
    }
}
