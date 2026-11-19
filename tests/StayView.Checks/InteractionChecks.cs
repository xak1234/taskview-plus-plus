using System.Diagnostics;
using System.Runtime.InteropServices;
using StayView.Core;

static class InteractionChecks
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] static extern nint GetDlgItem(nint parent,int id);
    static Native.POINT Center(Native.RECT r)=>new(){X=r.Left+r.Width/2,Y=r.Top+r.Height/2};
    static void Button(bool down)
    {
        var input=new Native.INPUT { Type=0,Mouse=new Native.MOUSEINPUT { DwFlags=down?2u:4u } };
        if(Native.SendInput(1,new[]{input},Marshal.SizeOf<Native.INPUT>())!=1)throw new Exception("Mouse input was rejected");
    }
    static async Task Click(Native.POINT p)
    { SetCursorPos(p.X,p.Y); await Task.Delay(60); Button(true); await Task.Delay(60); Button(false); }
    static async Task RightHold(Native.POINT point)
    {
        SetCursorPos(point.X,point.Y);await Task.Delay(80);
        var down=new Native.INPUT { Type=0,Mouse=new Native.MOUSEINPUT { DwFlags=8u } };
        var up=new Native.INPUT { Type=0,Mouse=new Native.MOUSEINPUT { DwFlags=16u } };
        if(Native.SendInput(1,new[]{down},Marshal.SizeOf<Native.INPUT>())!=1)throw new Exception("Right press rejected");
        try { await Task.Delay(1200); }
        finally { Native.SendInput(1,new[]{up},Marshal.SizeOf<Native.INPUT>()); }
        await Task.Delay(200);
    }
    static async Task Drag(Native.POINT from,int dx,int dy)
    {
        SetCursorPos(from.X,from.Y); await Task.Delay(80); Button(true); await Task.Delay(80);
        try { for(int i=1;i<=20;i++){SetCursorPos(from.X+dx*i/20,from.Y+dy*i/20);await Task.Delay(16);} }
        finally { Button(false); }
        await Task.Delay(600);
    }
    static Native.RECT Bounds(nint h) { Native.GetWindowRect(h,out var r); return r; }
    static void Assert(bool value,string message) { if(!value)throw new Exception(message);Console.WriteLine("PASS: "+message); }
    static async Task Toggle(nint overlay) { Native.PostMessage(overlay,0x312,1,0);await Task.Delay(1100); }
    public static async Task Run(bool nativeOnly = false,bool desktopOnly = false,bool gridOnly = false,bool focusOnly = false)
    {
        nint overlay=0;
        for(int i=0;i<50 && overlay==0;i++)
        { Native.EnumWindows((h,_)=>{if(Native.Title(h)=="Taskview++")overlay=h;return true;},0);if(overlay==0)await Task.Delay(100); }
        if(overlay==0 || Native.IsWindowVisible(overlay))throw new Exception("Start StayView with its overview closed before running interaction-checks");
        Native.GetCursorPos(out var cursor);
        using var fixtures=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixtures"){UseShellExecute=false,CreateNoWindow=true})!;
        var owned=new List<nint>();
        try
        {
            for(int i=0;i<50 && owned.Count<3;i++)
            {
                await Task.Delay(100);owned.Clear();
                Native.EnumWindows((h,_)=>{Native.GetWindowThreadProcessId(h,out var pid);if(pid==fixtures.Id && Native.Title(h).StartsWith("StayView fixture"))owned.Add(h);return true;},0);
            }
            var target=owned.Single(h=>Native.Title(h)=="StayView fixture normal");
            var original=Bounds(target);
            var settings=Settings.Load();
            using var catalog=new WindowCatalog(new VirtualDesktopService());
            var monitor=Native.MonitorFromWindow(target,2);
            var group=catalog.Enumerate().Where(w=>w.Monitor==monitor && !(settings.DockMinimizedWindows && w.Minimized)).ToList();
            // Enumeration is z-order, which is the Task View (most recently used) order.
            var sourceRects=group.Select(w=>Native.Placement(w.Handle).NormalPosition).ToList();
            var work=Native.WorkArea(monitor);double dpi=Native.MonitorScale(monitor);
            var area=Tiler.OverviewArea(work,8,dpi,settings.DesktopStripPosition);
            var canvas=Tiler.CanvasArea(work,dpi,settings.DesktopStripPosition);
            var slots=TaskViewLayout.Layout(sourceRects,Native.MonitorBounds(monitor),new Native.RECT(work.Left,canvas.Top,work.Width,canvas.Height),dpi);
            var tile=slots[group.FindIndex(w=>w.Handle==target)];
            if(focusOnly)
            {
                // Keep one disposable second-source tile unobscured by the real
                // focused fixture so we can exercise an actual tile drag under
                // live browsing rather than skipping it for accidental overlap.
                var otherFixture=owned.Where(h=>h!=target)
                    .Select(h=>group.FindIndex(w=>w.Handle==h))
                    .FirstOrDefault(i=>i>=0 && i<slots.Count,-1);
                if(otherFixture>=0)
                {
                    var otherCentre=Center(slots[otherFixture]);
                    var left=new Native.RECT(work.Left+140,work.Top+180,original.Width,original.Height);
                    var right=new Native.RECT(work.Right-original.Width-140,work.Top+180,original.Width,original.Height);
                    bool Includes(Native.RECT r,Native.POINT p)=>
                        p.X>=r.Left&&p.X<r.Right&&p.Y>=r.Top&&p.Y<r.Bottom;
                    var chosen=Includes(right,otherCentre)?left:right;
                    Native.SetWindowPos(target,0,chosen.Left,chosen.Top,0,0,0x15|0x4000);
                    await Task.Delay(100);
                    original=Bounds(target);
                }
            }
            await Toggle(overlay);
            Assert(Native.IsWindowVisible(overlay),"overview opened");
            if(settings.AnimateLayout && !nativeOnly)
            {
                await Click(Center(tile));await Task.Delay(20);
                await Click(new Native.POINT { X=area.Left+1,Y=area.Top+1 });await Task.Delay(450);
                Assert((Native.GetWindowLongPtr(overlay,-20).ToInt64()&8)!=0,"a rapid canvas press cancels pending focus without hiding the overview");
            }
            await Click(Center(tile));
            var focusWait=Stopwatch.StartNew();
            while(Native.GetForegroundWindow()!=target && focusWait.ElapsedMilliseconds<250)await Task.Delay(10);
            Assert(Native.GetForegroundWindow()==target && !Native.IsIconic(target),"tile click focuses its real window");
            Assert(Bounds(target).Equals(original),"focus preserves real window geometry");
            // While a real window is focused, dragging another disposable fixture's
            // miniature should leave that full-size HWND stationary for every frame.
            if(focusOnly)
            {
            var candidateSlots=owned.Where(h=>h!=target)
                .Select(h=>group.FindIndex(w=>w.Handle==h))
                .Where(i=>i>=0 && i<slots.Count)
                .Select(i=>slots[i])
                .Where(r=>{
                    var p=Center(r);
                    return p.X<original.Left || p.X>=original.Right ||
                           p.Y<original.Top || p.Y>=original.Bottom;
                }).ToList();
            Assert(candidateSlots.Count>0,"an unobscured independent fixture preview is available for drag");
            if(candidateSlots.Count>0)
            {
                int displacedSamples=0;
                var sampleTask=Task.Run(async ()=>{
                    for(int sample=0;sample<90;sample++)
                    {
                        if(!Bounds(target).Equals(original))Interlocked.Increment(ref displacedSamples);
                        await Task.Delay(12);
                    }
                });
                await Drag(Center(candidateSlots[0]),72,0);
                await sampleTask;
                Assert(displacedSamples==0 && Bounds(target).Equals(original),
                    "dragging another preview never displaces the focused real window");
            }
            }
            // Moving a different window must not make the focused real HWND jitter.
            // This sends native move events without activating the other window.
            var independent=owned.First(h=>h!=target && Native.Title(h)=="StayView fixture maximized");
            Native.ShowWindowAsync(independent,9);
            await Task.Delay(120);
            Native.ForceForeground(target);
            await Task.Delay(200);
            Assert(Native.GetForegroundWindow()==target,"focused test window retained before unrelated movement");
            var independentOrigin=Bounds(independent);
            try
            {
                for(int shift=0;shift<8;shift++)
                {
                    int offset=shift%2==0?36:-24;
                    Native.SetWindowPos(independent,0,independentOrigin.Left+offset,independentOrigin.Top+20,
                        0,0,0x15|0x4000);
                    await Task.Delay(90);
                    Assert(Bounds(target).Equals(original),
                        $"focused real window remains stationary while another HWND moves (sample {shift+1})");
                }
            }
            finally
            {
                Native.SetWindowPos(independent,0,independentOrigin.Left,independentOrigin.Top,0,0,0x15|0x4000);
            }
            if(focusOnly)
            {
                Console.WriteLine("PASS: focused HWND geometry stayed stable through unrelated movement.");
                return;
            }
            if(desktopOnly)
            {
                var desktopService=new VirtualDesktopService();
                var home=desktopService.Current;
                var other=desktopService.List().First(d=>d.Id!=home).Id;
                // Keep a docked fixture genuinely minimized on the other desktop.
                // Sampling throughout the switch catches restore/re-minimize flicker
                // even when a final-state assertion would report success.
                var sleeping=owned.Single(h=>Native.Title(h)=="StayView fixture minimized");
                Assert(desktopService.Move(sleeping,other),"move disposable minimized source to the other desktop");
                Native.ShowWindowAsync(sleeping,7);await Task.Delay(250);
                Assert(Native.IsIconic(sleeping),"foreign dock source starts minimized");
                async Task RoundTrip()
                {
                    var expected=owned.ToDictionary(h=>h,h=>(Bounds:Bounds(h),Placement:Native.Placement(h),
                        Iconic:Native.IsIconic(h),Zoomed:Native.IsZoomed(h),Desktop:desktopService.WindowDesktop(h)));
                    using var sampling=new CancellationTokenSource();
                    int samples=0,changed=0;
                    var sampleTask=Task.Run(async ()=>{
                        while(!sampling.IsCancellationRequested)
                        {
                            foreach(var (h,s) in expected)
                            {
                                var p=Native.Placement(h);
                                if(!Native.IsWindow(h)||!Bounds(h).Equals(s.Bounds)||Native.IsIconic(h)!=s.Iconic
                                    ||Native.IsZoomed(h)!=s.Zoomed||p.ShowCmd!=s.Placement.ShowCmd
                                    ||!p.NormalPosition.Equals(s.Placement.NormalPosition))Interlocked.Increment(ref changed);
                            }
                            Interlocked.Increment(ref samples);await Task.Delay(10);
                        }
                    });
                    try { Assert(desktopService.Switch(other),"switch to other desktop");await Task.Delay(900); }
                    finally
                    {
                        try { desktopService.Switch(home);await Task.Delay(1200); }
                        finally { sampling.Cancel();await sampleTask; }
                    }
                    Assert(samples>20 && changed==0,$"all fixture bounds and show states remain unchanged throughout desktop round trip ({samples} samples, {changed} changes)");
                    Assert(expected.All(p=>desktopService.WindowDesktop(p.Key)==p.Value.Desktop),"desktop switching preserves every fixture's owner");
                }
                await RoundTrip();
                Assert(Native.IsAbove(target,overlay) && !Native.IsIconic(target),"ordinary full-size presentation retained after desktop round trip");
                if(!gridOnly)
                {
                    var roundTripCaption=new Native.POINT { X=Bounds(target).Left+150,Y=Bounds(target).Top+16 };
                    await RightHold(roundTripCaption);
                    Assert((Native.GetWindowLongPtr(target,-20).ToInt64()&8)!=0,"focused fixture pinned");
                    await RoundTrip();
                    Assert(Native.IsAbove(target,overlay) && (Native.GetWindowLongPtr(target,-20).ToInt64()&8)!=0,"full-size pinned presentation retained after desktop round trip");
                    await RightHold(roundTripCaption);
                    }
                await Toggle(overlay); // explicitly return to the unfocused grid
                Assert(Native.GetForegroundWindow()==overlay,"returning to the grid releases the hidden app taskbar focus");
                await RoundTrip();
                Assert(Native.IsAbove(overlay,target) && (Native.GetWindowLongPtr(overlay,-20).ToInt64()&8)!=0,"unfocused grid remains above its sources after desktop round trip");
                await Toggle(overlay);
                Console.WriteLine("PASS: desktop focus round trips completed.");
                return;
            }

            if(nativeOnly)
            {
                var button=GetDlgItem(target,101);
                Assert(button!=0,"native fixture button exists");
                await Click(Center(Bounds(button)));await Task.Delay(100);
                Assert(Native.Title(target).Contains("native button clicked"),"first application click works immediately after selecting its tile");
            }
            await Toggle(overlay);
            if(!settings.AutoArrange)
            {
                int dx=tile.Right+120<=area.Right?120:-120;
                await Drag(Center(tile),dx,0);
                tile=ThumbnailLayout.Clamp(new Native.RECT(tile.Left+dx,tile.Top,tile.Width,tile.Height),area);
                Assert(Bounds(target).Equals(original),"dragging a miniature does not move the real window");
            }
            await Click(Center(tile));await Task.Delay(900);
            Assert(Native.GetForegroundWindow()==target,"dragged tile remains clickable at its dropped position");
            var before=Bounds(target);
            var caption=new Native.POINT { X=before.Left+before.Width/2,Y=before.Top+(int)(15*dpi) };
            await Drag(caption,96,64);
            var moved=Bounds(target);
            Assert(Math.Abs(moved.Left-before.Left-96)<=3 && Math.Abs(moved.Top-before.Top-64)<=3,$"native title-bar drag follows the pointer (before {before.Left},{before.Top},{before.Width}x{before.Height}; after {moved.Left},{moved.Top},{moved.Width}x{moved.Height}; native={settings.NativeWindowControls})");
            await Toggle(overlay);
            await Click(Center(tile));await Task.Delay(900);
            Assert(Native.GetForegroundWindow()==target && Bounds(target).Equals(moved),"shrink and refocus preserve both tile and real-window positions");
            var movedCaption=new Native.POINT { X=moved.Left+moved.Width/2,Y=moved.Top+(int)(15*dpi) };
            await Click(movedCaption);await Click(movedCaption);await Task.Delay(750);
            // An intentionally focused caption double-click returns to the StayView
            // miniature in both native-control and custom-control modes.
            Assert((Native.GetWindowLongPtr(overlay,-20).ToInt64()&8)!=0,
                "caption double-click shrinks back to the overview");
            Assert(!Native.IsZoomed(target),"caption double-click must not maximize the focused source");
            if(settings.NativeWindowControls)
            {
                if(nativeOnly)
                {
                    await Click(Center(tile));await Task.Delay(900);
                    Assert(Native.GetForegroundWindow()==target,
                        "reopening the shrunk tile restores native focus");
                    var logPath=Path.Combine(Settings.Folder,"stayview.log");
                    int logStart=File.ReadAllText(logPath).Length;
                    var currentBounds=Bounds(target);
                    var dockFrom=new Native.POINT { X=currentBounds.Left+currentBounds.Width/2,Y=currentBounds.Top+(int)(15*dpi) };
                    await RightHold(dockFrom);
                    var pinLog=File.ReadAllText(logPath);
                    Assert(pinLog[Math.Min(logStart,pinLog.Length)..].Contains($"[pin] pinned {target} at"),
                        "title-bar hold pins a focused window with native controls enabled");
                    Assert(Bounds(target).Equals(currentBounds),"pinning preserves the focused window placement");
                    await RightHold(dockFrom);
                    pinLog=File.ReadAllText(logPath);
                    Assert(pinLog[Math.Min(logStart,pinLog.Length)..].Contains($"[pin] released {target}"),
                        "another title-bar hold releases the pin");
                    int dockY=settings.DesktopStripPosition==DesktopStripPosition.Top
                        ? work.Top+(int)(40*dpi) : work.Bottom-(int)(40*dpi);
                    await Drag(dockFrom,0,dockY-dockFrom.Y);
                    await Task.Delay(500);
                    var dockLog=File.ReadAllText(logPath);
                    Assert(dockLog[Math.Min(logStart,dockLog.Length)..].Contains($"[dock] focused drag {target} -> dock"),
                        "dragging a focused window onto the desktop bar docks it with native controls enabled");
                    Console.WriteLine("PASS: native focus, first client click, preview drag, title-bar movement, shrink/refocus and bar docking checks completed.");
                    return;
                }
            }
            await Click(Center(tile));await Task.Delay(900);
            Assert(Native.GetForegroundWindow()==target,"caption shrink leaves the remembered tile responsive");
            if(settings.MaxBrowsedWindows>1)
            {
                var secondary=owned.Single(h=>Native.Title(h)=="StayView fixture maximized");
                Native.ShowWindowAsync(secondary,9);await Task.Delay(200);
                Native.ForceForeground(secondary);await Task.Delay(750);
                Assert(Native.GetForegroundWindow()==secondary,"a second real window can take focus while browsing");
                Native.PostMessage(secondary,0x112,0xF020,0);await Task.Delay(1400);
                Assert(Native.GetForegroundWindow()==target && !Native.IsIconic(target),"minimising a second focused window preserves the surviving window");
                Assert(Bounds(target).Equals(moved),"secondary minimise does not relocate the surviving window");
            }
            // A focused native minimize now docks the window, retaining a restored
            // non-activating HWND behind the opaque overview to keep DWM previews live.
            var minimizeLogPath=Path.Combine(Settings.Folder,"stayview.log");
            int minimizeLogStart=File.Exists(minimizeLogPath)?File.ReadAllText(minimizeLogPath).Length:0;
            Native.PostMessage(target,0x112,0xF020,0);
            await Task.Delay(1400);
            Assert(Native.IsWindowVisible(overlay),"minimising the focused window keeps the overview available");
            var minimizeLog=File.ReadAllText(minimizeLogPath);
            var logTail=minimizeLog[Math.Min(minimizeLogStart,minimizeLog.Length)..];
            Assert(logTail.Contains($"[minimize] {target} -> dock"),
                "user minimize changes the focused window to docked state");
            Assert(logTail.Contains($"[minimize] {target} restored iconic=False docked=True") && !Native.IsIconic(target),
                "a docked window stays restored behind the canvas for its live thumbnail");
            var minimizeJournal=System.Text.Json.JsonSerializer.Deserialize<List<SavedPlacement>>(
                File.ReadAllText(PlacementStore.Journal),new System.Text.Json.JsonSerializerOptions{IncludeFields=true})!;
            Assert(minimizeJournal.Single(s=>s.Handle==target).Placement.ShowCmd is not (2 or 6 or 7),
                "docking keeps its original restored placement in the crash journal");
            await Toggle(overlay);
            await Task.Delay(400);
            Assert(!Native.IsWindowVisible(overlay),"overview dismisses after docking");
            Assert(Native.IsIconic(target),"a docked window is minimized when the overview is dismissed");
            Console.WriteLine("PASS: Windows interaction smoke checks completed.");
        }
        finally
        {
            if(Native.IsWindowVisible(overlay)){await Toggle(overlay);if(Native.IsWindowVisible(overlay))await Toggle(overlay);}
            foreach(var h in owned)if(Native.IsWindow(h))Native.PostMessage(h,0x10,0,0);
            await Task.WhenAny(fixtures.WaitForExitAsync(),Task.Delay(2000));
            SetCursorPos(cursor.X,cursor.Y);
        }
    }
}
