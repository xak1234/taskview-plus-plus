using StayView.Core;
using System.Text.Json;
Fixtures.SetProcessDpiAwarenessContext(-4);
var options=new JsonSerializerOptions{IncludeFields=true,WriteIndented=true};
if(args.Length>0&&args[0]=="stayview-windows"){
    int z=0;
    Native.EnumWindows((h,_)=>{
        int order=z++;
        if(Native.Title(h).StartsWith("StayView") || Native.Title(h)=="Taskview++"){
            Native.GetWindowRect(h,out var rect);
            Console.WriteLine(JsonSerializer.Serialize(new{Handle=h.ToInt64(),Title=Native.Title(h),Visible=Native.IsWindowVisible(h),Topmost=(Native.GetWindowLongPtr(h,-20).ToInt64()&8)!=0,Z=order,Bounds=rect},options));
        }
        return true;
    },0);return;
}
if(args.Length>0&&args[0]=="fixtures"){Fixtures.Run();return;}
if(args.Length>0&&args[0]=="interaction-checks"){await InteractionChecks.Run();return;}
if(args.Length>0&&args[0]=="empty-space-checks"){await Fixtures.CheckEmptySpace();return;}
if(args.Length>0&&args[0]=="window-geometry-checks"){await Fixtures.CheckWindowGeometry();return;}
if(args.Length>0&&args[0]=="pin-hold-checks"){await Fixtures.CheckPinHold();return;}
if(args.Length>0&&args[0]=="taskview-layout-checks"){Environment.ExitCode=TaskViewCalibration.CheckGolden(args.Length>1?args[1]:Path.Combine(AppContext.BaseDirectory,"taskview-golden"));return;}
if(args.Length>0&&args[0]=="taskview-calibration"){await TaskViewCalibration.Run(args.Length>1?args[1]:"taskview-golden",args.Length>2?args[2]:null);return;}
if(args.Length>0&&args[0]=="probe-point"){
    Native.GetCursorPos(out var point);
    if(args.Length>2){point.X=int.Parse(args[1]);point.Y=int.Parse(args[2]);}
    var window=Native.GetAncestor(Native.WindowFromPoint(point),2);
    Native.GetWindowThreadProcessId(window,out var pid);
    Console.WriteLine($"POINT {point.X},{point.Y} root={window} pid={pid} class={Native.Class(window)}");
    var element=System.Windows.Automation.AutomationElement.FromPoint(new System.Windows.Point(point.X,point.Y));
    for(int depth=0;element!=null&&depth<96;depth++){
        var info=element.Current;
        Console.WriteLine($"{depth}: {info.ControlType.ProgrammaticName} pid={info.ProcessId} hwnd={info.NativeWindowHandle} bounds={info.BoundingRectangle} focusable={info.IsKeyboardFocusable} named={!string.IsNullOrWhiteSpace(info.Name)} patterns={string.Join(',',element.GetSupportedPatterns().Select(p=>p.Id))}");
        if(element.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern,out var pattern)){
            if(element.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern,out var vp))Console.WriteLine($"  valueReadonly={((System.Windows.Automation.ValuePattern)vp).Current.IsReadOnly}");
            var text=(System.Windows.Automation.TextPattern)pattern;
            var range=text.RangeFromPoint(new System.Windows.Point(point.X,point.Y));
            range.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Character);
            Console.WriteLine($"  readonly={range.GetAttributeValue(System.Windows.Automation.TextPattern.IsReadOnlyAttribute)} charBounds={string.Join(',',range.GetBoundingRectangles())}");
        }
        if((nint)info.NativeWindowHandle==window)break;
        element=System.Windows.Automation.TreeWalker.RawViewWalker.GetParent(element);
    }
    var watch=System.Diagnostics.Stopwatch.StartNew();
    var probe=new EmptySpaceProbe();
    Console.WriteLine($"EMPTY={await probe.IsEmptyAsync(window,point,point)} reason={probe.LastDecision} elapsed={watch.ElapsedMilliseconds}ms");
    return;
}
if(args.Length>1&&args[0]=="--empty-space-probe"){EmptySpaceProbeServer.Run(args[1]);return;}
if(args.Length>0&&args[0]=="capture-journal"){File.Copy(PlacementStore.Journal,args[1],true);Console.WriteLine("Saved entry placements for independent exit/crash comparison.");return;}
if(args.Length>0&&args[0]=="verify-journal"){
    var saved=JsonSerializer.Deserialize<List<SavedPlacement>>(File.ReadAllText(args[1]),options)!;int fail=0;
    foreach(var s in saved){var h=(nint)s.Handle;if(!Native.IsWindow(h))continue;var actual=Native.Placement(h);Native.GetWindowRect(h,out var bounds);
        bool pass=actual.ShowCmd==s.Placement.ShowCmd&&actual.NormalPosition.Equals(s.Placement.NormalPosition)&&(actual.ShowCmd!=1||bounds.Equals(s.Bounds));
        Console.WriteLine((pass?"PASS":"FAIL")+" original placement: "+Native.Title(h));if(!pass){fail++;Console.WriteLine(JsonSerializer.Serialize(new{expected=s.Placement,actual,expectedBounds=s.Bounds,bounds},options));}
    }Environment.ExitCode=fail>0?1:0;return;
}
if(args.Length>0&&args[0]=="toggle"){Native.EnumWindows((h,_)=>{if(Native.Title(h) is "StayView" or "Taskview++"){Native.PostMessage(h,0x312,1,0);return false;}return true;},0);return;}
if(args.Length>0&&args[0]=="diagnostics"){Console.WriteLine(File.ReadAllText(Path.Combine(Settings.Folder,"stayview.log")).Split('\n').TakeLast(12).Aggregate("",(a,b)=>a+b+"\n"));return;}
if(args.Length>1&&args[0]=="--desktop-broker"){DesktopBroker.Run(args[1]);return;}
if(args.Length>0&&args[0]=="desktops"){var service=new VirtualDesktopService();Console.WriteLine(JsonSerializer.Serialize(service.List(),options));return;}
if(args.Length>0&&args[0]=="foreground"){var h=Native.GetForegroundWindow();Console.WriteLine(JsonSerializer.Serialize(new{Handle=h.ToInt64(),Title=Native.Title(h)},options));return;}
if(args.Length>0&&args[0]=="chrome"){Native.EnumWindows((h,_)=>{if(Native.Title(h)=="StayView"){Native.GetWindowRect(h,out var r);Native.GetWindowThreadProcessId(h,out var pid);string? path=null;try{path=System.Diagnostics.Process.GetProcessById((int)pid).MainModule?.FileName;}catch{}Console.WriteLine(JsonSerializer.Serialize(new{Handle=h.ToInt64(),ProcessId=pid,Path=path,Visible=Native.IsWindowVisible(h),Bounds=r},options));}return true;},0);return;}
if(args.Length>0&&args[0]=="restore"){
    Native.EnumWindows((h,_)=>{
        if(!Native.Title(h).Contains(args.Length>1?args[1]:"GitHub Desktop",StringComparison.OrdinalIgnoreCase))return true;
        bool before=Native.IsIconic(h);var placement=Native.Placement(h);placement.ShowCmd=1;bool placed=Native.SetWindowPlacement(h,ref placement);bool shown=Native.ShowWindow(h,9);Thread.Sleep(250);bool after=Native.IsIconic(h);
        Console.WriteLine(JsonSerializer.Serialize(new{Handle=h.ToInt64(),Title=Native.Title(h),BeforeMinimized=before,SetPlacement=placed,ShowWindow=shown,AfterMinimized=after},options));return false;
    },0);return;
}
if(args.Length>0){
    var catalog=new WindowCatalog(new VirtualDesktopService());
    var windows=catalog.Enumerate().Where(w=>w.Title.Contains("Notepad")||w.Title=="Settings"||w.Title.Contains("File Explorer")).ToList();
    var state=windows.Select(w=>{Native.GetWindowRect(w.Handle,out var r);return new CheckState(w.Handle.ToInt64(),w.Title,r,Native.Placement(w.Handle));}).ToList();
    if(args[0]=="snapshot"){File.WriteAllText(args[1],JsonSerializer.Serialize(state,options));Console.WriteLine("Captured "+state.Count+" test windows.");}
    else if(args[0]=="compare"){
        var before=JsonSerializer.Deserialize<List<CheckState>>(File.ReadAllText(args[1]),options)!;int failures=0;
        foreach(var b in before){var a=state.FirstOrDefault(x=>x.Handle==b.Handle);if(a==null){Console.WriteLine("Closed: "+b.Title);continue;}
            bool pass=a.Placement.ShowCmd==b.Placement.ShowCmd&&(a.Placement.ShowCmd!=1||a.Bounds.Equals(b.Bounds));
            Console.WriteLine((pass?"PASS":"FAIL")+" restored "+b.Title);if(!pass){failures++;Console.WriteLine(JsonSerializer.Serialize(new{before=b,after=a},options));}}
        Environment.ExitCode=failures>0?1:0;
    }else foreach(var s in state)Console.WriteLine(JsonSerializer.Serialize(s,options));
    catalog.Dispose();return;
}
int checks=0;
var defaults=new Settings();if(!defaults.DockEnabled||defaults.DockPosition!=DockPosition.Auto||defaults.SatelliteScale!=25||!defaults.DockMinimizedWindows)throw new Exception("Dock defaults");
if(defaults.GlassOpacity!=65||!defaults.UseSameChromeOpacity||defaults.DockOpacity!=55||defaults.DesktopStripOpacity!=55||defaults.ReduceBlur)throw new Exception("Appearance defaults");
if(defaults.DesktopStripPosition!=DesktopStripPosition.Top||!defaults.KeepMinisSameSize||defaults.AutoArrange||defaults.AutoArrangeGrid!=4||!defaults.AnimateLayout||defaults.DesktopTransition!=DesktopTransitionMode.Slide||defaults.BackgroundTheme!=BackgroundTheme.DarkBlueBlack||defaults.SmallWindowSize!=Settings.SmallWindowSizeDefault||Settings.SmallWindowSizeDefault!=700)throw new Exception("Strip/options defaults");
if(!defaults.DockOnBarContact)throw new Exception("Dock on bar contact should default on");
if(!defaults.PlasmaEffect)throw new Exception("Plasma effect should default on");
if(Settings.SmallWindowSizeMin>=defaults.SmallWindowSize||Settings.SmallWindowSizeMax<=defaults.SmallWindowSize)throw new Exception("Custom thumbnail slider range");
var focusedFloor=FocusedWindowGeometry.MinimumEdgePixels(850,1);
if(focusedFloor<Math.Ceiling(850*0.15)||FocusedWindowGeometry.MinimumEdgePixels(200,1)<32)throw new Exception("Focused resize minimum");
var resizeOrigin=new Native.RECT(100,100,800,600);
var clampRight=FocusedWindowGeometry.ClampResize(new Native.RECT(100,100,80,600),resizeOrigin,focusedFloor);
var clampLeft=FocusedWindowGeometry.ClampResize(new Native.RECT(820,100,80,600),resizeOrigin,focusedFloor);
var clampTop=FocusedWindowGeometry.ClampResize(new Native.RECT(100,650,800,50),resizeOrigin,focusedFloor);
if(clampRight.Left!=100||clampRight.Width!=focusedFloor||clampLeft.Right!=900||clampLeft.Width!=focusedFloor||clampTop.Bottom!=700||clampTop.Height!=focusedFloor)throw new Exception("Focused resize anchor preservation");
var topWork=new Native.RECT(-1920,48,1920,1032);
var userRect=new Native.RECT(-1500,25,900,700);
var visualRect=new Native.RECT(-1492,32,884,684);
var topCorrected=FocusedWindowGeometry.ClampVisibleTop(userRect,visualRect,topWork);
if(topCorrected.Top!=41||topCorrected.Width!=userRect.Width||topCorrected.Height!=userRect.Height)throw new Exception("Focused visible-top clamp");
var targetCorrected=FocusedWindowGeometry.ClampTargetTop(new Native.RECT(-1500,-80,900,700),topWork);
if(targetCorrected.Top!=48||targetCorrected.Width!=900||targetCorrected.Height!=700)throw new Exception("Focused animation-top clamp");
Console.WriteLine("PASS: focused windows keep a 15% configured-size floor while preserving the opposite resize edge.");
// The internal FlyIn opening transition must never be selectable/saved as the user's
// Desktop transition: the picker offers exactly the five user-facing modes.
if((int)DesktopTransitionMode.FlyIn!=5||(int)DesktopTransitionMode.Slide!=6||Enum.GetValues<DesktopTransitionMode>().Length!=7)throw new Exception("FlyIn must stay the internal sixth mode, with Slide appended after it");
var dockLane=new Native.RECT(-300,900,420,90);
foreach(int n in new[]{1,3,12}){
    var dockSources=Enumerable.Range(0,n).Select(i=>new Native.RECT(0,0,i%2==0?1600:700,900)).ToList();
    var dockSlots=ThumbnailLayout.DockSlots(dockSources,dockLane,8);
    if(dockSlots.Count!=n)throw new Exception("Dock lane slot count");
    foreach(var r in dockSlots)if(r.Width<1||r.Height<1||r.Left<dockLane.Left||r.Top<dockLane.Top||r.Right>dockLane.Right||r.Bottom>dockLane.Bottom)throw new Exception("Docked mini escaped the bar dock lane");
    if(dockSlots.Any(r=>r.Width!=dockSlots[0].Width||r.Height!=dockSlots[0].Height))throw new Exception("Docked minis must be equal size");
    for(int i=0;i<n;i++)for(int j=i+1;j<n;j++)if(dockSlots[i].Intersects(dockSlots[j]))throw new Exception("Docked mini overlap");
    // Every docked panel keeps a gap from its neighbour (and from the lane ends).
    for(int i=1;i<n;i++)if(dockSlots[i].Left<=dockSlots[i-1].Right)throw new Exception("Docked panels must keep a border gap");
}
if(ThumbnailLayout.DockSlots([],dockLane,8).Count!=0)throw new Exception("Empty dock lane");
Console.WriteLine("PASS: docked minis stay inside the bar dock lane without overlap.");
// Docked minis alternate sides of the desktop cards. The two lanes are equally wide and
// both are laid out for the busier side, so the minis match on either side, and each
// lane fills toward the cards without ever crossing into the other lane.
Native.RECT leftLane=new(62,20,420,90),rightLane=new(1438,20,420,90);
foreach(int docks in new[]{1,2,3,7,12}){
    int perLane=(docks+1)/2; // left takes the odd one when the count is odd
    var lslots=ThumbnailLayout.DockLaneSlots(perLane,leftLane,20,true);
    var rslots=ThumbnailLayout.DockLaneSlots(perLane,rightLane,20,false);
    if(lslots.Count!=perLane||rslots.Count!=perLane)throw new Exception("Dock lane slot count per side");
    foreach(var r in lslots)if(r.Width<1||r.Height<1||r.Left<leftLane.Left||r.Top<leftLane.Top||r.Right>leftLane.Right||r.Bottom>leftLane.Bottom)throw new Exception("Left docked mini escaped its lane");
    foreach(var r in rslots)if(r.Width<1||r.Height<1||r.Left<rightLane.Left||r.Top<rightLane.Top||r.Right>rightLane.Right||r.Bottom>rightLane.Bottom)throw new Exception("Right docked mini escaped its lane");
    if(lslots.Concat(rslots).Any(r=>r.Width!=rslots[0].Width||r.Height!=rslots[0].Height))throw new Exception("Docked minis must match on both sides");
    // Slot 0 is the mini nearest the cards on each side; the rest fill outward with a gap.
    for(int i=1;i<perLane;i++){
        if(lslots[i].Right>=lslots[i-1].Left)throw new Exception("Left lane lost its border gap or filled the wrong way");
        if(rslots[i].Left<=rslots[i-1].Right)throw new Exception("Right lane lost its border gap or filled the wrong way");
    }
    if(lslots.Any(r=>r.Intersects(rightLane))||rslots.Any(r=>r.Intersects(leftLane)))throw new Exception("A dock lane crossed the desktop cards");
}
if(ThumbnailLayout.DockLaneSlots(0,leftLane,20,true).Count!=0)throw new Exception("Empty dock lane per side");
Console.WriteLine("PASS: docked minis alternate sides of the desktop cards in equal lanes packed toward them.");
var noDropTop=Tiler.OverviewArea(new Native.RECT(0,0,1920,1080),8,1,DesktopStripPosition.Top);
var noDropBottom=Tiler.OverviewArea(new Native.RECT(0,0,1920,1080),8,1,DesktopStripPosition.Bottom);
if(noDropTop.Top<184||noDropBottom.Bottom>1080-184)throw new Exception("Desktop strip no-drop reservation");
var stripLayout=Tiler.DesktopStripLayout(4,true,1920,28,64);
if(Math.Abs(stripLayout.AddWidth-64)>.001)
    throw new Exception("New desktop card must use the requested label width");
if(Math.Abs((stripLayout.AddLeft+stripLayout.AddWidth/2)-960)>.001)
    throw new Exception("New desktop card must be horizontally centered");
var oddStripLayout=Tiler.DesktopStripLayout(3,true,1500,28,64);
if(Math.Abs((oddStripLayout.AddLeft+oddStripLayout.AddWidth/2)-750)>.001||oddStripLayout.DesktopLefts.Count!=3)
    throw new Exception("New desktop card must remain centered with an odd desktop count");
foreach(var (count,canvasW) in new[]{(1,1920d),(2,1920d),(3,1500d),(4,1920d),(9,1920d),(16,1280d)})
{
    var snug=Tiler.DesktopStripLayout(count,true,canvasW,28,64,6);
    int left=(count+1)/2;
    var lefts=snug.DesktopLefts;
    if(Math.Abs((snug.AddLeft+snug.AddWidth/2)-canvasW/2)>.001)throw new Exception("Snug + card left the centre");
    if(left>0&&Math.Abs(snug.AddLeft-(lefts[left-1]+snug.CardWidth)-6)>.001)throw new Exception("Desktop left of + is not a tiny gap away");
    if(count>left&&Math.Abs(lefts[left]-(snug.AddLeft+snug.AddWidth)-6)>.001)throw new Exception("Desktop right of + is not a tiny gap away");
    for(int i=1;i<lefts.Count;i++)if(i!=left&&Math.Abs(lefts[i]-(lefts[i-1]+snug.CardWidth)-28)>.001)throw new Exception("Desktop-to-desktop gap changed");
    if(lefts.Count>0&&(lefts[0]<-.001||lefts[^1]+snug.CardWidth>canvasW+.001))throw new Exception("Snug desktop strip escaped the canvas");
}
Console.WriteLine("PASS: desktops sit a tiny gap either side of the centred + card and keep their own spacing.");
foreach(var area in new[]{new Native.RECT(0,0,1920,900),new Native.RECT(-1920,100,1500,800),new Native.RECT(0,-1080,900,1000)})
for(int n=1;n<=12;n++){
    var windows=Enumerable.Range(1,n).Select(i=>new AppWindow(i,"Test",1,false,false)).ToList();
    foreach(nint expanded in new nint[]{0,1}){
        var rects=Tiler.Layout(n,area,18,300,expanded,windows);
        if(rects.Count!=n)throw new Exception("Tile count");
        foreach(var r in rects)if(r.Width<=0||r.Height<=0||r.Left<area.Left||r.Top<area.Top||r.Right>area.Right||r.Bottom>area.Bottom)throw new Exception("Bounds");
        for(int i=0;i<n;i++)for(int j=i+1;j<n;j++)if(rects[i].Intersects(rects[j]))throw new Exception("Overlap");
        checks++;
    }
    int compactHeight=Tiler.CompactHeight(n,area.Width,18,300,72);
    var compact=Tiler.Compact(n,new Native.RECT(area.Left,area.Top,area.Width,compactHeight),18,300,72);
    if(compact.Count!=n)throw new Exception("Compact tile count");
    foreach(var r in compact)if(r.Width<=0||r.Height<=0||r.Left<area.Left||r.Top<area.Top||r.Right>area.Right||r.Bottom>area.Top+compactHeight)throw new Exception("Compact bounds");
    for(int i=0;i<n;i++)for(int j=i+1;j<n;j++)if(compact[i].Intersects(compact[j]))throw new Exception("Compact overlap");

    var sources=Enumerable.Range(0,n).Select(i=>new Native.RECT(0,0,900+i*37,600+i*19)).ToList();
    foreach(var position in new[]{DockPosition.Right,DockPosition.Bottom}){
        var dockLayout=Tiler.DockLayout(area,18,position,n);var slots=Tiler.DockSlots(sources,dockLayout.Dock,18,position,true,120,80);
        if(slots.Count!=n)throw new Exception("Dock slot count");
        foreach(var r in slots)if(r.Width<120||r.Height<80)throw new Exception("Dock minimum size");
        if(slots.Any(r=>r.Width!=slots[0].Width||r.Height!=slots[0].Height))throw new Exception("Dock equal mini size");
        for(int i=0;i<n;i++)for(int j=i+1;j<n;j++)if(slots[i].Intersects(slots[j]))throw new Exception("Dock overlap");
    }
    var stage=Tiler.SatelliteStage(area,18,n);var satellites=Tiler.SatelliteLayout(sources,stage.SatelliteArea,18,25,true,120,80);
    if(satellites.Count!=n)throw new Exception("Satellite count");
    foreach(var r in satellites)if(r.Width<=0||r.Height<=0||r.Left<stage.SatelliteArea.Left||r.Top<stage.SatelliteArea.Top||r.Right>stage.SatelliteArea.Right||r.Bottom>stage.SatelliteArea.Bottom)throw new Exception("Satellite bounds");
    if(satellites.Any(r=>r.Width!=satellites[0].Width||r.Height!=satellites[0].Height))throw new Exception("Satellite equal mini size");
    for(int i=0;i<n;i++)for(int j=i+1;j<n;j++)if(satellites[i].Intersects(satellites[j]))throw new Exception("Satellite overlap");

    var topArea=Tiler.OverviewArea(area,18,1,DesktopStripPosition.Top);var bottomArea=Tiler.OverviewArea(area,18,1,DesktopStripPosition.Bottom);
    if(topArea.Top<=area.Top||bottomArea.Bottom>=area.Bottom||bottomArea.Top>=topArea.Top)throw new Exception("Strip stage reservation");
}
Console.WriteLine($"PASS: {checks} legacy grid scenarios plus top/bottom strip reservations, equal mini layouts, dock overflow and negative monitor coordinates.");

// Browse reconciliation. The regression this guards: pressing a tile while a window is
// focused makes OUR canvas the foreground window, so once the gesture ends the selected
// window is no longer in front. Classifying that as a foreign window returns to the grid
// and lowers every source — the browsed window vanishes out from under the user.
{
    nint selected=100, canvas=900;
    if(BrowseReconciler.ClassifyForeground(selected,selected,selected,false,false,selected)!=BrowseTarget.Keep)
        throw new Exception("Selected window still foreground must keep the browse");
    if(BrowseReconciler.ClassifyForeground(selected,101,selected,false,false,0)!=BrowseTarget.Keep)
        throw new Exception("An owned popup of the selected window must keep the browse");
    if(BrowseReconciler.ClassifyForeground(selected,canvas,canvas,true,true,0)!=BrowseTarget.Refront)
        throw new Exception("A completed StayView canvas gesture must re-front the browsed window");
    if(BrowseReconciler.ClassifyForeground(selected,canvas,canvas,true,false,0)!=BrowseTarget.Grid)
        throw new Exception("An incidental canvas foreground handoff must not resurrect a closing window");
    if(BrowseReconciler.ClassifyForeground(selected,canvas,canvas,true,true,200)!=BrowseTarget.Refront)
        throw new Exception("An explicit canvas gesture must win even if the handle also resolves to a source");
    if(BrowseReconciler.ClassifyForeground(selected,200,200,false,false,200)!=BrowseTarget.Follow)
        throw new Exception("Another managed source taking focus must be followed");
    if(BrowseReconciler.ClassifyForeground(selected,300,300,false,false,0)!=BrowseTarget.Grid)
        throw new Exception("An untiled window taking focus must return to the grid");
    if(BrowseReconciler.ClassifyForeground(selected,0,0,false,false,0)!=BrowseTarget.Keep)
        throw new Exception("Transient no-foreground handoff must keep the browse");
    Console.WriteLine("PASS: browse reconciliation keeps, re-fronts, follows and grids the right foreground.");
    // A secondary can disappear while primary focus remains unchanged. Both slots must
    // be reconciled before the foreground fast path, preserving survivor order.
    nint[] pair = [100, 200];
    foreach (var unavailable in pair)
    {
        var survivors = BrowseReconciler.AvailableWindows(pair, h => h != unavailable);
        if (!survivors.SequenceEqual(pair.Where(h => h != unavailable)))
            throw new Exception("Unavailable primary or secondary remained browsed");
    }
    if (BrowseReconciler.AvailableWindows(pair, _ => false).Count != 0)
        throw new Exception("No available windows must resolve to the grid");
    if (!BrowseReconciler.AvailableWindows(new nint[] { 200, 100, 200, 0 }, _ => true).SequenceEqual(new nint[] { 200, 100 }))
        throw new Exception("Browse reconciliation changed focus order or retained duplicate/zero handles");
    var pins=new HashSet<nint>{100,300};
    var limited=BrowseReconciler.LimitWithPinned(new nint[]{200,100,300,400,500},pins,2);
    if(!limited.SequenceEqual(new nint[]{200,100,300,400}))
        throw new Exception("Pinned focused windows must not consume the configured focus-window allowance");
    Console.WriteLine("PASS: unavailable primary and secondary windows release browse state in focus order.");
}
// Native hit codes: client, caption, system menu, resize frame, caption buttons,
// unknown/timeout. Only an actual caption can consume a double-click.
foreach (int? hit in new int?[] { null, -2, -1, 0, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21 })
{
    if (FocusedClickPolicy.CanShrink(hit)) throw new Exception("App content/control double-click was claimed as shrink");
    if (hit != null && FocusedClickPolicy.CanDrag(hit, false, true)) throw new Exception("Top-strip fallback stole a confirmed app/control hit");
    if (hit != null && hit != 1 && FocusedClickPolicy.CanDrag(hit, true, true)) throw new Exception("Win modifier stole a native control/resize action");
}
if(FocusedClickPolicy.PinBlocksNonClient(1)||FocusedClickPolicy.PinBlocksNonClient(8)||FocusedClickPolicy.PinBlocksNonClient(20)
    ||!FocusedClickPolicy.PinBlocksNonClient(2)||!FocusedClickPolicy.PinBlocksNonClient(9)||!FocusedClickPolicy.PinBlocksNonClient(10)
    ||!FocusedClickPolicy.PinBlocksNonClient(null,true))
    throw new Exception("Pinned-window non-client policy must allow content/minimize/close and block move/resize/maximize");
if (!FocusedClickPolicy.CanShrink(2) || !FocusedClickPolicy.CanDrag(2, false, false)) throw new Exception("Confirmed caption lost its overview gestures");
if (!FocusedClickPolicy.CanShrink(null, true) || FocusedClickPolicy.CanShrink(1, true)) throw new Exception("Caption timeout fallback must work only when native hit testing is unknown");
if(!FocusedClickPolicy.CanPinHold(2)||!FocusedClickPolicy.CanPinHold(null,true)
    ||FocusedClickPolicy.CanPinHold(1)||FocusedClickPolicy.CanPinHold(8)||FocusedClickPolicy.CanPinHold(20)||FocusedClickPolicy.CanPinHold(null,false))
    throw new Exception("Pin hold must be restricted to a confirmed or safe fallback title bar");
if (!FocusedClickPolicy.CanDrag(1, true, false) || FocusedClickPolicy.CanDrag(1, false, true)) throw new Exception("Client drag must require Win, including custom top strips");
if (!FocusedClickPolicy.CanDrag(null, false, true) || FocusedClickPolicy.CanDrag(null, false, false)) throw new Exception("Timed-out hit test lost conservative caption drag fallback");
if(FocusedClickPolicy.CanShrink(20) || FocusedClickPolicy.CanDrag(20,false,true) || FocusedClickPolicy.CanDrag(20,true,true))throw new Exception("Native X/Close must stay entirely with the application");
if(!PinnedWindowPolicy.CanPin(true,false,false,false))throw new Exception("Minimized windows must be pin-eligible without focus");
if(!PinnedWindowPolicy.CanPin(false,true,true,true))throw new Exception("Focused browsed windows must remain pin-eligible");
if(PinnedWindowPolicy.CanPin(false,false,false,true)||PinnedWindowPolicy.CanPin(false,true,false,true)||PinnedWindowPolicy.CanPin(false,true,true,false))
    throw new Exception("Ordinary windows must still require an active eligible browse target before pinning");
var pinDesktop=Guid.NewGuid();
if(!PinnedWindowPolicy.NeedsDesktopMove(false,pinDesktop,Guid.NewGuid())
    ||!PinnedWindowPolicy.NeedsDesktopMove(false,pinDesktop,Guid.Empty)
    ||PinnedWindowPolicy.NeedsDesktopMove(true,pinDesktop,Guid.NewGuid())
    ||PinnedWindowPolicy.NeedsDesktopMove(false,pinDesktop,pinDesktop)
    ||PinnedWindowPolicy.NeedsDesktopMove(false,Guid.Empty,Guid.NewGuid()))
    throw new Exception("Pinned windows must follow the active desktop until unpinned");
if(DesktopArrangement.Include(true)||!DesktopArrangement.Include(false))
    throw new Exception("Desktop memory must skip pinned windows and keep every other window");
{
    nint kept=1, changed=2;
    var saved=new Dictionary<nint,DesktopWindowState>
    {
        [kept]=new(new Native.RECT(0,0,10,10),1,false,false,false,default,false,0),
        [changed]=new(new Native.RECT(0,0,10,10),1,true,false,true,default,false,1)
    };
    var dock=new List<nint>(); var undock=new List<nint>();
    DesktopArrangement.MembershipEdits(saved,h=>h==kept,dock,undock);
    if(dock.Count!=1||dock[0]!=changed||undock.Count!=1||undock[0]!=kept)
        throw new Exception("Returning to a desktop must restore the dock membership that was remembered");
}
var lockedPin=new Native.RECT(10,20,300,200);
if(!PinnedWindowPolicy.IsSettledAdjustment(lockedPin,lockedPin)
    ||!PinnedWindowPolicy.IsSettledAdjustment(new Native.RECT(18,12,308,192),lockedPin)
    ||PinnedWindowPolicy.IsSettledAdjustment(new Native.RECT(40,20,300,200),lockedPin))
    throw new Exception("Pinned geometry must absorb frame slack without accepting a real move");
var pinRect=new Native.RECT(100,100,200,200);
var covered=new Native.RECT(150,150,180,160);
var beside=new Native.RECT(400,100,80,80);
var sliver=new Native.RECT(292,100,40,80);
if(!PinnedWindowPolicy.IsCoveredBy(covered,new[]{pinRect})
    ||PinnedWindowPolicy.IsCoveredBy(beside,new[]{pinRect})
    ||PinnedWindowPolicy.IsCoveredBy(sliver,new[]{pinRect}))
    throw new Exception("A pin hides a panel only when the overlap is more than a frame sliver");
var placed=PinnedWindowPolicy.PlaceClearOf(covered,new[]{pinRect},new Native.RECT(0,0,900,700));
if(placed.Width!=covered.Width||placed.Height!=covered.Height||PinnedWindowPolicy.IsCoveredBy(placed,new[]{pinRect}))
    throw new Exception("A window brought back from the dock must keep its size and sit clear of the pin");
if(!PinnedWindowPolicy.PlaceClearOf(beside,new[]{pinRect},new Native.RECT(0,0,900,700)).Equals(beside))
    throw new Exception("A window that is already clear of pins must stay where it is");
Console.WriteLine("PASS: minimized and focused pin eligibility share the same all-desktops persistence policy.");
Console.WriteLine("PASS: intelligent clicks preserve content, tabs, caption buttons and resizing; confirmed or safe timeout-caption hits shrink.");
foreach (var id in new[] { 50008, 50023, 50026, 50028, 50036, 50032, 50033 })
    if (!FocusedClickPolicy.IsBackgroundContainer(id)) throw new Exception("Empty container cannot toggle focus");
foreach (var id in new[] { 50000, 50003, 50004, 50005, 50007, 50020, 50024, 50025, 50030 })
    if (FocusedClickPolicy.IsBackgroundContainer(id)) throw new Exception("Button, text, document or item was considered empty");
foreach (var id in new[] { 10000, 10002, 10003, 10005, 10010, 10014, 10015, 10024 })
    if (!FocusedClickPolicy.IsInteractivePattern(id)) throw new Exception("Interactive container pattern must protect app content");
Console.WriteLine("PASS: empty container backgrounds are eligible; text, files, controls and interactive containers are protected.");
if(!FocusedClickPolicy.IsOutsideText(50,50,new double[]{100,100,20,20}))throw new Exception("Whitespace near text must be eligible");
if(FocusedClickPolicy.IsOutsideText(110,110,new double[]{100,100,20,20}))throw new Exception("Text character must remain native");
if(FocusedClickPolicy.IsOutsideText(120,120,new double[]{100,100,20,20}))throw new Exception("Text boundary must remain native");
if(FocusedClickPolicy.IsOutsideText(50,50,new double[]{0,0,10,10,40,40,20,20}))throw new Exception("All text rectangles must be checked");
if(FocusedClickPolicy.IsOutsideText(0,0,new double[]{double.NaN,0,1,1}) || FocusedClickPolicy.IsOutsideText(0,0,new double[]{1}))throw new Exception("Unknown text geometry must stay native");
Console.WriteLine("PASS: browser whitespace is distinguished from actual text rectangles and malformed geometry.");
var pageBounds=new System.Windows.Rect(100,200,1000,700);
if(!FocusedClickPolicy.IsPageSizedHandler(pageBounds,pageBounds)
    || FocusedClickPolicy.IsPageSizedHandler(new System.Windows.Rect(100,200,100,50),pageBounds)
    || FocusedClickPolicy.IsPageSizedHandler(System.Windows.Rect.Empty,pageBounds))throw new Exception("Only page-sized delegated handlers may count as whitespace");
var stableArea=new Native.RECT(-1200,240,1200,800);
var occupiedTiles=new[]{new Native.RECT(-1000,400,400,240),new Native.RECT(-560,400,400,240)};
var originalTiles=occupiedTiles.ToArray();
var newcomer=StableTileLayout.PlaceNew(occupiedTiles[0],occupiedTiles,stableArea,28);
if(!occupiedTiles.SequenceEqual(originalTiles))throw new Exception("Adding a window moved an existing tile");
if(occupiedTiles.Any(r=>r.Intersects(newcomer)))throw new Exception("New window covered a remembered tile despite free space");
if(newcomer.Left<stableArea.Left || newcomer.Top<stableArea.Top || newcomer.Right>stableArea.Right || newcomer.Bottom>stableArea.Bottom)throw new Exception("New tile escaped negative-coordinate canvas");
if(!StableTileLayout.PlaceNew(occupiedTiles[0],[],stableArea,28).Equals(occupiedTiles[0]))throw new Exception("Unoccupied tile position changed");
Console.WriteLine("PASS: new windows use free canvas space without changing remembered tile positions.");
var previewArea=new Native.RECT(500,100,400,200);
var remoteArea=new Native.RECT(-1920,0,1920,1080);
var mapped=DesktopDropGeometry.MapPoint(new(){X=700,Y=200},previewArea,remoteArea);
if(mapped.X!=-960 || mapped.Y!=540)throw new Exception("Popout center mapped to the wrong desktop/monitor");
var clampedDrop=DesktopDropGeometry.AtPoint(new(0,0,800,600),new(){X=-1900,Y=5},remoteArea);
if(!clampedDrop.Equals(new Native.RECT(-1920,0,800,600)))throw new Exception("Desktop drop changed size or escaped the work area");
var oversizedDrop=DesktopDropGeometry.AtPoint(new(0,0,2400,1400),mapped,remoteArea);
if(!oversizedDrop.Equals(new Native.RECT(-1920,0,2400,1400)))throw new Exception("Desktop drop resized an oversized window");
Console.WriteLine("PASS: popout drops map negative-coordinate monitors and preserve window sizes at boundaries.");
{
    // Task View layout: golden parity plus structural invariants on other monitors.
    if(TaskViewCalibration.CheckGolden(Path.Combine(AppContext.BaseDirectory,"taskview-golden"))!=0)throw new Exception("TaskViewLayout diverged from recorded Task View layouts");
    foreach(var (monitor,region,dpi) in new[]{
        (new Native.RECT(0,0,1920,1080),new Native.RECT(0,180,1920,851),1.0),
        (new Native.RECT(-2560,-200,2560,1440),new Native.RECT(-2560,40,2560,1150),1.25),
        (new Native.RECT(0,0,3440,1440),new Native.RECT(0,0,3440,1200),1.0),
        (new Native.RECT(0,0,2560,1440),new Native.RECT(0,315,2560,1040),1.75),
        (new Native.RECT(0,0,3840,2160),new Native.RECT(0,315,3840,1760),1.75)})
    foreach(int n in new[]{1,2,5,9,17,30})
    {
        var sources=Enumerable.Range(0,n).Select(i=>new Native.RECT(0,0,600+(i*137)%1400,400+(i*89)%900)).ToList();
        var cells=TaskViewLayout.Layout(sources,monitor,region,dpi);
        var content=TaskViewLayout.ContentArea(monitor,region);
        // Exactly the spacing a drag uses: the laid-out grid must never count as "too close".
        var (hg,vg,hd)=TaskViewLayout.MinimumSpacing(dpi);
        for(int i=0;i<n;i++)
        {
            var c=cells[i];
            if(c.Width>sources[i].Width+1||c.Height>sources[i].Height+1)throw new Exception("Task View layout enlarged a window");
            if(c.Left<content.Left-1||c.Right>content.Right+1||c.Top-hd<content.Top-1||c.Bottom>content.Bottom+1)throw new Exception($"Task View tile left its area ({n} windows)");
            double aspect=sources[i].Width/(double)sources[i].Height,got=c.Width/(double)c.Height;
            if(Math.Abs(got-aspect)/aspect>.05&&c.Width>20&&c.Height>20)throw new Exception("Task View layout distorted a window");
            for(int j=i+1;j<n;j++)if(TileRepulsion.TooClose(c,cells[j],hg,vg,hd))throw new Exception($"Task View tiles closer than the Task View gap ({n} windows)");
        }
        // Order is row-major: every later window is further right on its row or on a lower row.
        for(int i=1;i<n;i++)if(cells[i].Top<cells[i-1].Top||(cells[i].Top==cells[i-1].Top&&cells[i].Left<=cells[i-1].Left))throw new Exception("Task View order is not row-major");
    }
    {
        // Many windows stay responsive (Reflow runs this per monitor).
        var many=Enumerable.Range(0,150).Select(i=>new Native.RECT(0,0,500+(i*211)%1500,300+(i*97)%1000)).ToList();
        TaskViewLayout.Layout(many.Take(20).ToList(),new Native.RECT(0,0,3840,2160),new Native.RECT(0,180,3840,1930),1.0); // JIT warm-up
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var manyCells=TaskViewLayout.Layout(many,new Native.RECT(0,0,3840,2160),new Native.RECT(0,180,3840,1930),1.0);
        if(manyCells.Count!=150||watch.ElapsedMilliseconds>60)throw new Exception($"Task View layout of 150 windows took {watch.ElapsedMilliseconds}ms");
    }
    // A drag that starts on a laid-out grid moves nothing until it reaches a neighbour (175% rounding).
    {
        var monitor=new Native.RECT(0,0,2560,1440);double dpi=1.75;
        var sources=Enumerable.Repeat(new Native.RECT(0,0,1280,800),9).ToList();
        var cells=TaskViewLayout.Layout(sources,monitor,new Native.RECT(0,315,2560,1040),dpi);
        var gridHome=Enumerable.Range(1,8).ToDictionary(i=>(nint)i,i=>cells[i]);
        var (hg,vg,hd)=TaskViewLayout.MinimumSpacing(dpi);
        var held=TileRepulsion.Resolve(99,cells[0],gridHome,new HashSet<nint>(),new Native.RECT(0,315,2560,1040),hg,vg,hd);
        if(gridHome.Any(p=>!held[p.Key].Equals(p.Value)))throw new Exception("Holding a tile in its own slot moved its neighbours");
    }
    Console.WriteLine("PASS: Task View layout keeps gaps, aspect, row-major order and bounds on other monitor sizes and DPI, and stays fast.");

    // Repel while dragging.
    var canvas=new Native.RECT(0,200,1920,800);
    const int G=24,V=26,H=39;
    var home=new Dictionary<nint,Native.RECT>{
        [1]=new(100,300,400,250),[2]=new(524,300,400,250),[3]=new(948,300,400,250),
        [4]=new(100,615,400,250),[5]=new(1500,700,300,200)};
    bool Clean(IReadOnlyDictionary<nint,Native.RECT> all)
    {
        var list=all.Values.ToList();
        for(int i=0;i<list.Count;i++)for(int j=i+1;j<list.Count;j++)if(TileRepulsion.TooClose(list[i],list[j],G,V,H))return false;
        return true;
    }
    // Far away: nobody moves.
    var far=TileRepulsion.Resolve(99,new(1500,300,120,80),home,new HashSet<nint>(),canvas,G,V,H);
    if(home.Any(p=>!far[p.Key].Equals(p.Value)))throw new Exception("Repel moved tiles that were not near the dragged tile");
    // Dragged onto tile 2: it and anything it touches must clear, keeping the gap; tile 5 unaffected.
    var draggedRect=new Native.RECT(560,320,400,250);
    var pushed=TileRepulsion.Resolve(99,draggedRect,home,new HashSet<nint>(),canvas,G,V,H);
    var all=new Dictionary<nint,Native.RECT>(pushed){[99]=draggedRect};
    if(!Clean(all))throw new Exception("Repel left tiles closer than the Task View gap");
    if(pushed[2].Equals(home[2]))throw new Exception("Tile under the dragged tile did not move");
    if(!pushed[5].Equals(home[5]))throw new Exception("Unrelated tile moved");
    if(pushed.Values.Any(r=>r.Left<canvas.Left||r.Top<canvas.Top||r.Right>canvas.Right||r.Bottom>canvas.Bottom))throw new Exception("Repel pushed a tile off the canvas");
    // Cascade: a push that lands on a third tile moves that one too (checked by Clean above);
    // determinism: identical input gives identical output.
    var again=TileRepulsion.Resolve(99,draggedRect,home,new HashSet<nint>(),canvas,G,V,H);
    if(pushed.Any(p=>!again[p.Key].Equals(p.Value)))throw new Exception("Repel is not deterministic");
    // Pinned tiles never move, even when covered.
    var pinnedSet=new HashSet<nint>{2};
    var withPin=TileRepulsion.Resolve(99,draggedRect,home,pinnedSet,canvas,G,V,H);
    if(!withPin[2].Equals(home[2]))throw new Exception("Repel moved a pinned tile");
    // Solving from home each frame: moving away restores home positions.
    var back=TileRepulsion.Resolve(99,new(1500,300,120,80),home,new HashSet<nint>(),canvas,G,V,H);
    if(home.Any(p=>!back[p.Key].Equals(p.Value)))throw new Exception("Neighbours did not return home once the dragged tile left");
    // Just inside the gap (not overlapping) still repels.
    var nearRect=new Native.RECT(home[1].Right+10,home[1].Top,300,250);
    var near=TileRepulsion.Resolve(99,nearRect,new Dictionary<nint,Native.RECT>{[1]=home[1]},new HashSet<nint>(),canvas,G,V,H);
    if(TileRepulsion.TooClose(near[1],nearRect,G,V,H))throw new Exception("A tile inside the Task View gap was not repelled");
    // Tiles the user left close together elsewhere are not the drag's business.
    var tight=new Dictionary<nint,Native.RECT>(home){[6]=new(1500,300,300,150),[7]=new(1500,460,300,150)};
    var untouched=TileRepulsion.Resolve(99,draggedRect,tight,new HashSet<nint>(),canvas,G,V,H);
    if(!untouched[6].Equals(tight[6])||!untouched[7].Equals(tight[7]))throw new Exception("Repel separated tiles the drag never reached");
    // Hysteresis: once pushed right, a neighbour keeps escaping right as the drag crosses its centre.
    var single=new Dictionary<nint,Native.RECT>{[1]=new(800,300,300,400)};
    var firstPush=TileRepulsion.Resolve(99,new(700,300,300,400),single,new HashSet<nint>(),canvas,G,V,H);
    var crossed=TileRepulsion.Resolve(99,new(810,300,300,400),single,new HashSet<nint>(),canvas,G,V,H,firstPush);
    if(firstPush[1].Left<=800||crossed[1].Left<=810)throw new Exception("Pushed neighbour swung to the other side");
    // A full Task View canvas: sweeping a tile anywhere over it must never leave tiles
    // overlapping (drawn behind one another); boxed-in neighbours swap into the vacated slot.
    foreach(var (count,dpi) in new[]{(6,1.0),(9,1.0),(12,1.0),(9,1.5)})
    {
        var monitor=new Native.RECT(0,0,1920,1080);
        var region=new Native.RECT(0,180,1920,851);
        var cellsAll=TaskViewLayout.Layout(Enumerable.Repeat(new Native.RECT(0,0,1600,900),count).ToList(),monitor,region,dpi);
        var (hg,vg,hd)=TaskViewLayout.MinimumSpacing(dpi);
        var packedCanvas=new Native.RECT(0,Math.Min(region.Top,cellsAll.Min(c=>c.Top)-hd),1920,region.Bottom-Math.Min(region.Top,cellsAll.Min(c=>c.Top)-hd));
        var packedHome=Enumerable.Range(1,count-1).ToDictionary(i=>(nint)i,i=>cellsAll[i]);
        var fullHome=new Dictionary<nint,Native.RECT>(packedHome){[0]=cellsAll[0]};
        Dictionary<nint,Native.RECT>? prev=null;
        var d0=cellsAll[0];
        for(int y=packedCanvas.Top+hd;y+d0.Height<=packedCanvas.Bottom;y+=37)
        for(int x=packedCanvas.Left;x+d0.Width<=packedCanvas.Right;x+=41)
        {
            var held=new Native.RECT(x,y,d0.Width,d0.Height);
            var solved=TileRepulsion.Resolve(0,held,fullHome,new HashSet<nint>(),packedCanvas,hg,vg,hd,prev);
            prev=solved;
            // While held, the dragged tile may sit over a boxed-in neighbour; neighbours never overlap each other.
            var tiles=solved.Values.ToList();
            for(int i=0;i<tiles.Count;i++)for(int j=i+1;j<tiles.Count;j++)
                if(tiles[i].Intersects(tiles[j]))throw new Exception($"Repel left neighbours overlapping ({count} tiles, drag at {x},{y})");
            // Released here, the tile settles somewhere that overlaps nothing.
            // No room at all means the drop is cancelled (the untouched home layout is clean).
            if(TileRepulsion.Settle(held,cellsAll[0],tiles,packedCanvas,hg,vg,hd) is {} settled && tiles.Any(t=>t.Intersects(settled)))
                throw new Exception($"Dropped tile left behind/over another ({count} tiles, drop at {x},{y})");
        }
        // Dragging the first tile onto the second swaps them.
        var onto=TileRepulsion.Resolve(0,cellsAll[1],fullHome,new HashSet<nint>(),packedCanvas,hg,vg,hd);
        if(Math.Abs(onto[1].Left-cellsAll[0].Left)>1||Math.Abs(onto[1].Top-cellsAll[0].Top)>1)throw new Exception($"Boxed-in neighbour did not take the vacated slot ({count} tiles): got {onto[1].Left},{onto[1].Top},{onto[1].Width}x{onto[1].Height} want {cellsAll[0].Left},{cellsAll[0].Top},{cellsAll[0].Width}x{cellsAll[0].Height}; dragged at {cellsAll[1].Left},{cellsAll[1].Top}; canvas {packedCanvas.Left},{packedCanvas.Top},{packedCanvas.Width}x{packedCanvas.Height}");
    }
    // A drag frame on a crowded full canvas stays within budget (runs on the UI thread).
    {
        var monitor=new Native.RECT(0,0,1920,1080);var region=new Native.RECT(0,180,1920,851);
        var cells=TaskViewLayout.Layout(Enumerable.Repeat(new Native.RECT(0,0,1600,900),16).ToList(),monitor,region,1.0);
        var full=Enumerable.Range(0,16).ToDictionary(i=>(nint)i,i=>cells[i]);
        var (hg,vg,hd)=TaskViewLayout.MinimumSpacing(1.0);
        var crowdedCanvas=new Native.RECT(0,Math.Min(region.Top,cells.Min(c=>c.Top)-hd),1920,851);
        TileRepulsion.Resolve(0,cells[5],full,new HashSet<nint>(),crowdedCanvas,hg,vg,hd); // warm-up
        var sw=System.Diagnostics.Stopwatch.StartNew();
        for(int x=0;x<1920-cells[0].Width;x+=240)TileRepulsion.Resolve(0,new Native.RECT(x,cells[5].Top,cells[0].Width,cells[0].Height),full,new HashSet<nint>(),crowdedCanvas,hg,vg,hd);
        if(sw.ElapsedMilliseconds>200)throw new Exception($"Repel on a crowded 16-tile canvas is too slow ({sw.ElapsedMilliseconds}ms for 8 frames)");
    }
    // A crowded canvas terminates and keeps every tile on the canvas.
    var crowdCanvas=new Native.RECT(0,0,900,600);
    var crowd=Enumerable.Range(0,12).ToDictionary(i=>(nint)(i+1),i=>new Native.RECT((i%4)*220,40+(i/4)*190,200,140));
    var crowded=TileRepulsion.Resolve(99,new(300,200,300,200),crowd,new HashSet<nint>(),crowdCanvas,G,V,H);
    if(crowded.Count!=12||crowded.Values.Any(r=>r.Left<0||r.Top<0||r.Right>900||r.Bottom>600))throw new Exception("Crowded repel escaped the canvas");
    Console.WriteLine("PASS: dragging repels neighbours to the Task View gap, cascades, spares pinned tiles and returns them home.");
}
record CheckState(long Handle,string Title,Native.RECT Bounds,Native.WINDOWPLACEMENT Placement);
