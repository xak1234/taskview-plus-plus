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
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
    static readonly Native.WndProc proc = Procedure;
    static int windows;
    public static Task CheckPinHold()
    {
        if(!FocusedClickPolicy.CanPinHold(2)
            ||!FocusedClickPolicy.CanPinHold(null,true)
            ||FocusedClickPolicy.CanPinHold(1)
            ||FocusedClickPolicy.CanPinHold(8)
            ||FocusedClickPolicy.CanPinHold(20)
            ||FocusedClickPolicy.CanPinHold(null,false))
            throw new Exception("Title-bar pin-hold policy accepted application content or rejected a valid caption");
        Console.WriteLine("PASS: pin hold is title-bar-only; client content and caption buttons remain native.");
        return Task.CompletedTask;
    }
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
        RunFixtures();
    }
    public static async Task CheckWindowGeometry()
    {
        var ready=new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{
            try {
                var cls=new WindowClass{Proc=proc,Instance=Native.GetModuleHandle(null),Background=6,Name="StayViewGeometryFixture"};
                RegisterClass(ref cls);
                var h=CreateWindowEx(0,cls.Name,"StayView geometry test (disposable)",0x00CF0000,240,260,800,600,0,0,cls.Instance,0);
                if(h==0)throw new Exception("Could not create geometry fixture");
                windows=1;Native.ShowWindow(h,4);Native.ShowWindow(h,4);ready.SetResult(h);
                while(GetMessage(out var message,0,0,0)){TranslateMessage(ref message);DispatchMessage(ref message);}
            }catch(Exception ex){ready.TrySetException(ex);}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        var window=await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try {
            Native.GetWindowRect(window,out var original);
            Native.ShowWindow(window,7);
            if(!Native.IsIconic(window))throw new Exception("Fixture did not minimize");
            if(!PlacementStore.TrySnapshot(window,0,Guid.Empty,out var saved))throw new Exception("Minimized snapshot failed");
            var p=saved.Placement;
            p.NormalPosition=new(p.NormalPosition.Left+120,p.NormalPosition.Top+70,p.NormalPosition.Width,p.NormalPosition.Height);
            saved.Placement=p;
            // Reproduce the old tile drag's translated iconic sentinel exactly.
            saved.Bounds=new(-31880,-31930,original.Width,original.Height);
            Native.ShowWindow(window,4);
            if(!PlacementStore.ApplyPendingGeometry(window,saved))throw new Exception("Pending geometry failed");
            Native.GetWindowRect(window,out var actual);
            var expected=new Native.RECT(original.Left+120,original.Top+70,original.Width,original.Height);
            if(!actual.Equals(expected))throw new Exception($"Iconic sentinel reused: expected {expected.Left},{expected.Top}; got {actual.Left},{actual.Top}");
            Console.WriteLine("PASS: dragging a minimized tile restores its normal position, never its translated -32000 sentinel.");
            saved.Placement=Native.Placement(window);saved.Bounds=new(-32000,-32000,original.Width,original.Height);
            Native.SetWindowPos(window,0,original.Left,original.Top,0,0,0x15);
            if(!PlacementStore.RestoreOne(saved))throw new Exception("Sentinel journal recovery failed");
            await Task.Delay(100);Native.GetWindowRect(window,out actual);
            if(!actual.Equals(expected))throw new Exception("Recovery invented a position instead of using NormalPosition");
            Console.WriteLine("PASS: stale iconic journal bounds recover to the saved normal placement.");
            var desktops=new VirtualDesktopService();
            var current=desktops.Current;
            var foreign=desktops.List().FirstOrDefault(d=>d.Id!=current);
            if(current==Guid.Empty || foreign==null)throw new Exception("Desktop checks require two existing desktops");
            string journal=Path.Combine(Path.GetTempPath(),"StayView-geometry-"+Guid.NewGuid().ToString("N")+".json");
            using var catalog=new WindowCatalog(desktops,h=>h==window);
            using(var session=new OverviewSession(catalog,desktops,new Settings{AnimateLayout=false},new PlacementStore(journal)))
            {
                session.Enter();
                var destination=new Native.RECT(original.Left+40,original.Top+60,original.Width,original.Height);
                if(!session.MoveToDesktop(window,foreign.Id,destination))throw new Exception("Move into popped-out desktop failed");
                await Task.Delay(100);
                Native.GetWindowRect(window,out actual);
                if(desktops.WindowDesktop(window)!=foreign.Id || desktops.Current!=current || !actual.Equals(destination))throw new Exception("Incoming drop changed desktop, size, or position");
                if(session.Placements.Entries[window].DesktopId!=foreign.Id)throw new Exception("Incoming drop was not journaled");
                Console.WriteLine("PASS: transfer into another desktop preserves size/drop position without switching desktops.");
                Native.ShowWindow(window,7);
                session.RestoreMinimizedSourceForOverview(window);
                if(!Native.IsIconic(window)||desktops.Current!=current)throw new Exception("Passive refresh restored a foreign-desktop window");
                if(!session.MoveToDesktop(window,current,original))throw new Exception("Move out to current desktop failed");
                await Task.Delay(100);Native.GetWindowRect(window,out actual);
                if(desktops.WindowDesktop(window)!=current || desktops.Current!=current || !actual.Equals(original))throw new Exception("Outgoing drop lost its desktop or placement");
                session.Exit();
                await Task.Delay(100);Native.GetWindowRect(window,out actual);
                if(desktops.WindowDesktop(window)!=current || !actual.Equals(original))throw new Exception("Exit undid the explicit desktop transfer");
                Console.WriteLine("PASS: foreign minimized sources stay passive; dragging out and dismissing retain the current desktop and placement.");
            }
            bool wasSystemPinned=desktops.IsWindowPinned(window);
            if(!desktops.PinWindow(window)||!desktops.IsWindowPinned(window))throw new Exception("Windows view pin did not engage for the disposable fixture");
            if(!wasSystemPinned&&!desktops.UnpinWindow(window))throw new Exception("Could not release the disposable Windows view pin");
            Console.WriteLine("PASS: Windows view pin engages for a normal disposable window.");

            // Exercise the same fallback TrayApp uses when native view pinning is absent.
            // Moving desktop ownership is deliberately tested only while unpinned because
            // Windows clears a native view pin when MoveViewToDesktop is called.
            if(!wasSystemPinned)
            {
                if(!desktops.Move(window,foreign.Id))throw new Exception("Could not move the fallback-pin fixture away from the current desktop");
                await Task.Delay(100);
                var owner=desktops.WindowDesktop(window);
                if(!PinnedWindowPolicy.NeedsDesktopMove(false,current,owner))throw new Exception("Fallback pin policy did not request a move to the active desktop");
                if(!desktops.Move(window,current))throw new Exception("Could not follow the active desktop with the fallback-pin fixture");
                await Task.Delay(100);Native.GetWindowRect(window,out actual);
                if(desktops.WindowDesktop(window)!=current||!actual.Equals(original))throw new Exception("Fallback pin follow lost desktop ownership or geometry");
                Console.WriteLine("PASS: fallback pinning follows the active desktop without changing window geometry.");
            }
            Native.ShowWindow(window,7);
            await Task.Delay(100);
            if(!Native.IsIconic(window))throw new Exception("Fixture did not minimize before minimized-pin check");
            bool minimizedWasPinned=desktops.IsWindowPinned(window);
            if(!desktops.PinWindow(window)||!desktops.IsWindowPinned(window))throw new Exception("Windows view pin did not engage for a minimized fixture");
            if(!Native.IsIconic(window))throw new Exception("Pinning a minimized fixture unexpectedly restored it");
            if(!minimizedWasPinned&&!desktops.UnpinWindow(window))throw new Exception("Could not release the minimized disposable Windows view pin");
            Native.ShowWindow(window,9);
            await Task.Delay(100);
            Console.WriteLine("PASS: an already-minimized window can be pinned without being focused or restored first.");
            string dockJournal=Path.Combine(Path.GetTempPath(),"StayView-dock-"+Guid.NewGuid().ToString("N")+".json");
            using(var dockSession=new OverviewSession(catalog,desktops,new Settings{AnimateLayout=false},new PlacementStore(dockJournal)))
            {
                dockSession.IsPinnedWindow=h=>h==window;
                dockSession.Enter();
                dockSession.BeginUserMinimize(window);
                if(!dockSession.IsDocked(window))throw new Exception("Pinned focused minimize did not establish dock state");
                if(dockSession.UndockTile(window))throw new Exception("Pinned minimized window was allowed to undock");
                Native.ShowWindow(window,7);
                if(!Native.IsIconic(window))throw new Exception("Fixture did not enter native minimize state for dock conversion");
                dockSession.NoteUserMinimized(window);
                await Task.Delay(100);
                if(Native.IsIconic(window)||!dockSession.IsDocked(window))throw new Exception("Focused minimize did not restore its live dock source");
                if(dockSession.Placements.Entries.TryGetValue(window,out var dockSaved) && dockSaved.Placement.ShowCmd is 2 or 6 or 7)
                    throw new Exception("Focused minimize incorrectly persisted a Windows minimized show state");
                Console.WriteLine("PASS: a pinned focused window can minimize into a locked live dock without losing its saved normal state.");
                dockSession.Exit();
            }
            if(File.Exists(dockJournal))File.Delete(dockJournal);
            // The previous regression intentionally leaves docked windows minimized when
            // StayView exits. Restore the disposable fixture before testing normal-window
            // focus geometry so this case exercises the real visible frame, not -32000.
            await Task.Delay(150); // allow Exit's asynchronous SW_MINIMIZE to land first
            Native.ShowWindow(window,9); // SW_RESTORE
            await Task.Delay(100);
            string topJournal=Path.Combine(Path.GetTempPath(),"StayView-top-"+Guid.NewGuid().ToString("N")+".json");
            var monitor=Native.MonitorFromWindow(window,2);
            var work=Native.WorkArea(monitor);
            Native.GetWindowRect(window,out var beforeTopClamp);
            Native.SetWindowPos(window,0,beforeTopClamp.Left,work.Top-120,0,0,0x15);
            if(!Native.TryGetVisualBounds(window,out var beforeVisual) || beforeVisual.Top>=work.Top)
                throw new Exception("Fixture could not place a visible frame above the work area");
            using(var topSession=new OverviewSession(catalog,desktops,new Settings{AnimateLayout=false},new PlacementStore(topJournal)))
            {
                topSession.Enter();
                if(!topSession.EnsureActivationTopVisible(window))
                {
                    Native.GetWindowRect(window,out var enteredBounds);
                    Native.TryGetVisualBounds(window,out var enteredVisual);
                    var enteredMonitor=Native.MonitorFromWindow(window,2);
                    var enteredWork=Native.WorkArea(enteredMonitor);
                    throw new Exception($"Focused activation did not correct an off-screen title bar; iconic={Native.IsIconic(window)} zoomed={Native.IsZoomed(window)} bounds={enteredBounds.Left},{enteredBounds.Top},{enteredBounds.Width}x{enteredBounds.Height} visual={enteredVisual.Left},{enteredVisual.Top},{enteredVisual.Width}x{enteredVisual.Height} work={enteredWork.Left},{enteredWork.Top},{enteredWork.Width}x{enteredWork.Height}");
                }
                await Task.Delay(50);
                Native.GetWindowRect(window,out var afterTopClamp);
                if(!Native.TryGetVisualBounds(window,out var afterVisual) || afterVisual.Top<work.Top)
                    throw new Exception("Focused activation left the visible title bar above the work area");
                if(afterTopClamp.Width!=beforeTopClamp.Width || afterTopClamp.Height!=beforeTopClamp.Height)
                    throw new Exception("Focused activation top correction resized the real window");
                topSession.Exit();
                await Task.Delay(50);
                if(!Native.TryGetVisualBounds(window,out afterVisual) || afterVisual.Top<work.Top)
                    throw new Exception("Session exit restored the stale off-screen top position");
                Console.WriteLine("PASS: focus expansion shifts an off-screen title bar into the work area without resizing it.");
            }
            if(File.Exists(topJournal))File.Delete(topJournal);
            if(File.Exists(journal))File.Delete(journal);
        }
        finally {Native.PostMessage(window,0x10,0,0);thread.Join(2000);}
    }
    static void RunFixtures() {
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
