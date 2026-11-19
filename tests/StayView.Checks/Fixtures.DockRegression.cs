using StayView.Core;

static partial class Fixtures
{
    // The real HWNDs can appear undocked without losing logical membership:
    // Explorer raises a restored/uncloaked source after the initial layout pass.
    // Exercise that late raise with a normal (non-topmost) canvas and a pin up.
    public static async Task CheckDesktopPresentation()
    {
        var ready=new TaskCompletionSource<nint[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                var cls=new WindowClass { Proc=proc,Instance=Native.GetModuleHandle(null),
                    Background=6,Name="StayViewDesktopPresentationFixture" };
                RegisterClass(ref cls);
                var handles=new List<nint>();
                foreach(var label in new[]{"dock","canvas","pin","grid"})
                {
                    var h=CreateWindowEx(0,cls.Name,"StayView presentation "+label+" (disposable)",
                        0x00CF0000,180+handles.Count*45,180+handles.Count*35,640,420,0,0,cls.Instance,0);
                    if(h==0)throw new Exception("Could not create presentation fixture");
                    handles.Add(h);Native.ShowWindow(h,4);
                }
                windows=handles.Count;ready.SetResult(handles.ToArray());
                while(GetMessage(out var msg,0,0,0)){TranslateMessage(ref msg);DispatchMessage(ref msg);}
            }
            catch(Exception ex){ready.TrySetException(ex);}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        var handles=await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dock=handles[0];var canvas=handles[1];var pin=handles[2];var grid=handles[3];
        var journal=Path.Combine(Path.GetTempPath(),"StayView-presentation-"+Guid.NewGuid().ToString("N")+".json");
        var desktops=new VirtualDesktopService();
        var home=desktops.Current;
        var other=desktops.List().FirstOrDefault(d=>d.Id!=home);
        var geometry=handles.ToDictionary(h=>h,h=>{Native.GetWindowRect(h,out var r);return r;});
        var placement=handles.ToDictionary(h=>h,Native.Placement);
        try
        {
            using var catalog=new WindowCatalog(desktops,_=>false);
            using var session=new OverviewSession(catalog,desktops,new Settings{AnimateLayout=false},new PlacementStore(journal));
            session.IsPinnedWindow=h=>h==pin;
            session.Enter();session.ApplyDockState(dock,true,true);
            Native.SetWindowPos(pin,-1,0,0,0,0,0x13|0x4000);
            for(int cycle=0;cycle<6;cycle++)
            {
                if(other!=null && cycle%2==0)
                {
                    if(!session.ChangeDesktop(()=>desktops.Switch(other.Id)))throw new Exception("Could not switch fixture desktop");
                    await Task.Delay(180);
                    if(!session.ChangeDesktop(()=>desktops.Switch(home)))throw new Exception("Could not return to fixture desktop");
                    await Task.Delay(180);
                }
                Native.SetWindowPos(canvas,-2,0,0,0,0,0x13|0x4000);
                Native.SetWindowPos(dock,0,0,0,0,0,0x13|0x4000);
                Native.SetWindowPos(grid,0,0,0,0,0,0x13|0x4000);
                await Task.Delay(80);
                if(!Native.IsAbove(dock,canvas))throw new Exception("Late source raise was not reproduced");
                var foreground=Native.GetForegroundWindow();
                // Deliberately omit the dock from the render and leave it in a stale
                // browse list: neither can override the authoritative dock membership.
                session.KeepSourcesBehind(canvas,new[]{grid,pin},new[]{dock,pin});
                await Task.Delay(80);
                if(!Native.IsAbove(canvas,dock)||!Native.IsAbove(canvas,grid)||!Native.IsAbove(pin,canvas))
                    throw new Exception("Late source raise escaped browse presentation repair");
                if(!session.IsDocked(dock)||!session.IsMinimizedDocked(dock))throw new Exception("Presentation repair changed dock status");
                if(Native.GetForegroundWindow()!=foreground)throw new Exception("Presentation repair stole focus");
                foreach(var h in handles)
                {
                    Native.GetWindowRect(h,out var actual);
                    if(!actual.Equals(geometry[h])||Native.Placement(h).ShowCmd!=placement[h].ShowCmd
                        ||desktops.WindowDesktop(h)!=home)throw new Exception("Presentation repair changed geometry, show state or desktop");
                }
            }
            Console.WriteLine("PASS: six late source raises stay behind the canvas; pin, dock, show state, geometry and ownership retained");
            Console.WriteLine(other==null?"SKIP: presentation desktop round trips need two desktops":"PASS: three native desktop round trips retain presentation state");
        }
        finally
        {
            if(home!=Guid.Empty && desktops.Current!=home)desktops.Switch(home);
            foreach(var h in handles)Native.PostMessage(h,0x10,0,0);
            thread.Join(2000);
            try{File.Delete(journal);}catch(IOException){}
        }
    }

    // Only this disposable HWND is captured or restored. No user window is moved.
    public static async Task CheckPassiveDockActivation()
    {
        nint oldForeground=Native.GetForegroundWindow();
        var ready=new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                var cls=new WindowClass { Proc=proc,Instance=Native.GetModuleHandle(null),
                    Background=6,Name="StayViewDockRegressionFixture" };
                RegisterClass(ref cls);
                var h=CreateWindowEx(0,cls.Name,"StayView dock regression (disposable)",
                    0x00CF0000,180,180,760,520,0,0,cls.Instance,0);
                if(h==0)throw new Exception("Could not create native dock fixture");
                windows=1; Native.ShowWindow(h,4); ready.SetResult(h);
                while(GetMessage(out var msg,0,0,0)){TranslateMessage(ref msg);DispatchMessage(ref msg);}
            }
            catch(Exception ex){ready.TrySetException(ex);}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        nint window=await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string journal=Path.Combine(Path.GetTempPath(),"StayView-dock-regression-"+Guid.NewGuid().ToString("N")+".json");
        int failures=0;
        void Check(bool passed,string label)
        {
            Console.WriteLine((passed?"PASS: ":"FAIL: ")+label);
            if(!passed)failures++;
        }
        try
        {
            var desktops=new VirtualDesktopService();
            using var catalog=new WindowCatalog(desktops,h=>h==window);
            var store=new PlacementStore(journal);
            Native.ShowWindow(window,7);
            if(!Native.IsIconic(window))throw new Exception("Fixture did not minimize");
            if(!store.Capture(window,0,desktops.WindowDesktop(window)))throw new Exception("Capture failed");
            using(var session=new OverviewSession(catalog,desktops,
                new Settings{AnimateLayout=false,NativeWindowControls=true,DockMinimizedWindows=false},store))
            {
                session.Enter();
                session.ApplyDockState(window,true,true);
                Native.ShowWindow(window,4); Native.ForceForeground(window);
                await Task.Delay(100);
                if(Native.GetForegroundWindow()!=window)throw new Exception("Fixture never became foreground");
                bool accepted=session.Activate(window,requestForeground:false);
                Check(!accepted,"passive foreground observation must not activate a docked source");
                Check(session.IsDocked(window),"minimized journal + passive foreground must keep dock membership");
                Check(session.IsMinimizedDocked(window),"passive observation must preserve minimized-dock metadata");
                session.ApplyDockState(window,true,false);
                accepted=session.Activate(window,requestForeground:false);
                Check(!accepted && session.IsDocked(window),"manual dock also rejects passive activation");
                accepted=session.Activate(window,requestForeground:true);
                Check(!accepted && session.IsDocked(window),"desktop focus recall cannot release a dock just by requesting foreground");
                accepted=session.Activate(window,requestForeground:true,restoreDocked:true);
                Check(accepted && !session.IsDocked(window),"explicit user activation may restore and release the dock");
                session.BeginUserMinimize(window);
                session.CancelUserMinimize(window);
                Check(session.IsDocked(window) && session.IsMinimizedDocked(window),
                    "late native restore completion retains semantic minimized-dock state");
                session.ApplyDockState(window,false,false);
                Native.GetWindowRect(window,out var original);
                var placement=Native.Placement(window);
                var saved=new DesktopWindowState(original,1,false,false,true,1,default,false,0,placement.NormalPosition,true);
                bool restored=true;
                for(int i=0;i<10;i++)
                {
                    Native.SetWindowPos(window,0,original.Left+45,original.Top+30,original.Width,original.Height,0x14);
                    await Task.Delay(25);
                    restored &= DesktopArrangement.RestoreWindow(window,saved);
                    await Task.Delay(40);
                    Native.GetWindowRect(window,out var actual);
                    restored &= actual.Equals(original);
                }
                Check(restored,"ten native restore cycles retain the exact screen position and size");
                Check(Native.Placement(window).NormalPosition.Equals(placement.NormalPosition),
                    "ten restores retain the original workspace rectangle without drift");
                Native.ShowWindow(window,3);await Task.Delay(100);
                Check(DesktopArrangement.RestoreWindow(window,saved),"normal placement restores from maximized state");
                await Task.Delay(100);Native.GetWindowRect(window,out var unmaximized);
                Check(!Native.IsZoomed(window)&&unmaximized.Equals(original),"restore from maximized keeps the original geometry");
                // The preceding maximize/restore posts window-state work asynchronously.
                // Let that complete before asserting that passive recall preserves a
                // newly minimized source rather than observing an older restore in flight.
                await Task.Delay(180);
                Native.ShowWindow(window,7);await Task.Delay(160);
                bool wasIconic=Native.IsIconic(window);
                bool unchanged=DesktopArrangement.RestoreWindow(window,saved with{ShowCmd=2,HasScreenBounds=false});
                await Task.Delay(60);
                Check(wasIconic && unchanged && Native.IsIconic(window),
                    "passive desktop restore leaves a minimized HWND minimized");
                Native.ShowWindow(window,4);
                var originalDesktop=desktops.Current;
                var otherDesktop=desktops.List().FirstOrDefault(d=>d.Id!=originalDesktop);
                if(originalDesktop==Guid.Empty||otherDesktop==null)
                    Console.WriteLine("SKIP: desktop round trip needs two existing virtual desktops");
                else
                {
                    session.DockTile(window);
                    bool kept=true,ownership=true;
                    try
                    {
                        for(int i=0;i<4;i++)
                        {
                            var destination=i%2==0?otherDesktop.Id:originalDesktop;
                            if(!session.ChangeDesktop(()=>desktops.Switch(destination)))
                                throw new Exception("Could not switch test desktop");
                            await Task.Delay(200);session.ObserveDesktopChange();
                            kept &= session.IsDocked(window);
                            ownership &= desktops.WindowDesktop(window)==originalDesktop;
                        }
                    }
                    finally
                    {
                        if(desktops.Current!=originalDesktop)desktops.Switch(originalDesktop);
                        session.ObserveDesktopChange();
                    }
                    Check(kept,"dock membership survives four actual Windows virtual-desktop switches");
                    Check(ownership,"desktop switching never migrates the docked window's owner");
                    Native.GetWindowRect(window,out var afterSwitch);
                    Check(afterSwitch.Equals(original),"desktop round trips retain native window geometry");
                    // A live-preview permission from an earlier visit must not turn
                    // returning to this desktop into an unsolicited restore command.
                    Native.ShowWindow(window,7);await Task.Delay(100);
                    var minimizedPlacement=Native.Placement(window);
                    try
                    {
                        if(!session.ChangeDesktop(()=>desktops.Switch(otherDesktop.Id)))throw new Exception("Could not leave minimized fixture");
                        await Task.Delay(200);
                        if(!session.ChangeDesktop(()=>desktops.Switch(originalDesktop)))throw new Exception("Could not revisit minimized fixture");
                        for(int sample=0;sample<12;sample++)
                        {
                            session.RestoreMinimizedSourceForOverview(window);
                            await Task.Delay(40);
                            if(!Native.IsIconic(window))throw new Exception("A desktop arrival restored a minimized source");
                        }
                    }
                    finally
                    {
                        if(desktops.Current!=originalDesktop)desktops.Switch(originalDesktop);
                        session.ObserveDesktopChange();
                    }
                    Check(Native.Placement(window).NormalPosition.Equals(minimizedPlacement.NormalPosition)
                        && session.IsDocked(window) && desktops.WindowDesktop(window)==originalDesktop,
                        "arrival and repeated passive preview ticks preserve minimized state, dock, placement and owner");
                    var fg=Native.GetForegroundWindow();
                    session.SelectForPresentation(window);
                    Check(session.Selected==0 && Native.IsIconic(window) && Native.GetForegroundWindow()==fg,
                        "presentation selection cannot restore or select a docked source");
                    session.ApplyDockState(window,false,false);
                    session.SelectForPresentation(window);
                    Check(session.Selected==window && Native.IsIconic(window) && Native.GetForegroundWindow()==fg,
                        "logical presentation selection does not activate or restore a native window");
                }
            }
        }
        finally
        {
            Native.PostMessage(window,0x10,0,0); thread.Join(2000);
            if(oldForeground!=0 && Native.IsWindow(oldForeground))Native.SetForegroundWindow(oldForeground);
            try{File.Delete(journal);}catch(IOException){}
        }
        Console.WriteLine($"DOCK_REGRESSION_FAILURES={failures}");
        if(failures!=0)throw new Exception("Passive dock activation regression");
    }

    // External fixture process: WindowCatalog correctly ignores windows in its own
    // process, so in-process Win32 fixtures cannot test enumeration on foreign desktops.
    public static async Task CheckForeignDockReentry()
    {
        var oldForeground=Native.GetForegroundWindow();
        var desktops=new VirtualDesktopService();
        var home=desktops.Current;
        var other=desktops.List().FirstOrDefault(d=>d.Id!=home);
        if(home==Guid.Empty||other==null)
        {
            Console.WriteLine("SKIP: foreign dock re-entry requires two virtual desktops");
            return;
        }
        using var fixtures=System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!,"fixtures")
                {UseShellExecute=false,CreateNoWindow=true})!;
        nint target=0;
        string journal=Path.Combine(Path.GetTempPath(),"StayView-foreign-dock-"+Guid.NewGuid().ToString("N")+".json");
        OverviewSession? session=null;
        try
        {
            for(int i=0;i<60 && target==0;i++)
            {
                Native.EnumWindows((h,_)=>
                {
                    Native.GetWindowThreadProcessId(h,out var pid);
                    if(pid==fixtures.Id && Native.Title(h)=="StayView fixture normal")target=h;
                    return true;
                },0);
                if(target==0)await Task.Delay(100);
            }
            if(target==0)throw new Exception("Could not find out-of-process normal fixture");
            using var catalog=new WindowCatalog(desktops,h=>h==target);
            session=new OverviewSession(catalog,desktops,
                new Settings{AnimateLayout=false,NativeWindowControls=true,DockMinimizedWindows=true},
                new PlacementStore(journal));
            if(!catalog.Enumerate(true).Any(w=>w.Handle==target))
                throw new Exception("Out-of-process fixture not visible in all-desktop catalog");
            session.Enter();
            if(!session.DockTile(target))throw new Exception("Could not dock fixture");
            session.Exit();
            for(int i=0;i<25 && !Native.IsIconic(target);i++)await Task.Delay(40);
            if(!Native.IsIconic(target))throw new Exception("Docked fixture did not minimize on exit");
            if(!desktops.Switch(other.Id))throw new Exception("Could not switch to alternate desktop");
            await Task.Delay(400);
            var visibleEverywhere=catalog.Enumerate(true).Any(w=>w.Handle==target);
            session.Enter();
            bool dockRetained=session.IsDocked(target);
            Console.WriteLine($"FOREIGN_DOCK_REENTRY: seenAll={visibleEverywhere} docked={dockRetained} ownerRetained={desktops.WindowDesktop(target)==home}");
            if(!dockRetained)
                throw new Exception("Foreign desktop dock state was lost on overview re-entry");
            if(desktops.WindowDesktop(target)!=home)
                throw new Exception("Docked fixture changed virtual-desktop owner");
            session.Exit();
            Console.WriteLine("PASS: dock survives overview close/reopen on another desktop without migrating its source");
        }
        finally
        {
            session?.Exit();
            if(desktops.Current!=home)desktops.Switch(home);
            if(target!=0 && Native.IsWindow(target))Native.PostMessage(target,0x10,0,0);
            try{if(!fixtures.WaitForExit(2000))fixtures.Kill();}catch{}
            if(oldForeground!=0 && Native.IsWindow(oldForeground))Native.SetForegroundWindow(oldForeground);
            try{File.Delete(journal);}catch(IOException){}
        }
    }}
