using StayView.Core;

static partial class Fixtures
{
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
                Check(accepted && !session.IsDocked(window),"explicit user activation may restore and release the dock");
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
                Native.ShowWindow(window,7);await Task.Delay(50);
                Check(DesktopArrangement.RestoreWindow(window,saved with{ShowCmd=2,HasScreenBounds=false})
                    &&Native.IsIconic(window),"passive desktop restore leaves a minimized HWND minimized");
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
}