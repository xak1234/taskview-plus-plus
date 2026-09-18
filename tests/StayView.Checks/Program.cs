using StayView.Core;
using System.Text.Json;
Fixtures.SetProcessDpiAwarenessContext(-4);
var options=new JsonSerializerOptions{IncludeFields=true,WriteIndented=true};
if(args.Length>0&&args[0]=="stayview-windows"){
    int z=0;
    Native.EnumWindows((h,_)=>{
        int order=z++;
        if(Native.Title(h).StartsWith("StayView")){
            Native.GetWindowRect(h,out var rect);
            Console.WriteLine(JsonSerializer.Serialize(new{Handle=h.ToInt64(),Title=Native.Title(h),Visible=Native.IsWindowVisible(h),Topmost=(Native.GetWindowLongPtr(h,-20).ToInt64()&8)!=0,Z=order,Bounds=rect},options));
        }
        return true;
    },0);return;
}
if(args.Length>0&&args[0]=="fixtures"){Fixtures.Run();return;}
if(args.Length>0&&args[0]=="capture-journal"){File.Copy(PlacementStore.Journal,args[1],true);Console.WriteLine("Saved entry placements for independent exit/crash comparison.");return;}
if(args.Length>0&&args[0]=="verify-journal"){
    var saved=JsonSerializer.Deserialize<List<SavedPlacement>>(File.ReadAllText(args[1]),options)!;int fail=0;
    foreach(var s in saved){var h=(nint)s.Handle;if(!Native.IsWindow(h))continue;var actual=Native.Placement(h);Native.GetWindowRect(h,out var bounds);
        bool pass=actual.ShowCmd==s.Placement.ShowCmd&&actual.NormalPosition.Equals(s.Placement.NormalPosition)&&(actual.ShowCmd!=1||bounds.Equals(s.Bounds));
        Console.WriteLine((pass?"PASS":"FAIL")+" original placement: "+Native.Title(h));if(!pass){fail++;Console.WriteLine(JsonSerializer.Serialize(new{expected=s.Placement,actual,expectedBounds=s.Bounds,bounds},options));}
    }Environment.ExitCode=fail>0?1:0;return;
}
if(args.Length>0&&args[0]=="toggle"){Native.EnumWindows((h,_)=>{if(Native.Title(h)=="StayView"){Native.PostMessage(h,0x312,1,0);return false;}return true;},0);return;}
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
foreach(var area in new[]{new Native.RECT(0,184,1920,850),new Native.RECT(-2560,-1000,2560,1300),new Native.RECT(20,200,700,300)})
foreach(int count in new[]{0,1,2,8,24,80}){
    var tiles=ThumbnailLayout.Arrange(count,area,24,360,240);
    if(tiles.Count!=count)throw new Exception("Overview lost a window");
    for(int i=0;i<tiles.Count;i++){
        var r=tiles[i];
        if(r.Width>360||r.Height>240||r.Left<area.Left||r.Top<area.Top||r.Right>area.Right||r.Bottom>area.Bottom)throw new Exception("Overview tile grew outside its bounded miniature");
        for(int j=i+1;j<tiles.Count;j++)if(r.Intersects(tiles[j]))throw new Exception("Initial overview overlap");
    }
}
Console.WriteLine("PASS: bounded overview thumbnails, empty desktops, dense grids, and negative monitor coordinates.");
var defaults=new Settings();if(!defaults.DockEnabled||defaults.DockPosition!=DockPosition.Auto||defaults.SatelliteScale!=25||!defaults.DockMinimizedWindows)throw new Exception("Dock defaults");
if(defaults.GlassOpacity!=65||!defaults.UseSameChromeOpacity||defaults.DockOpacity!=55||defaults.DesktopStripOpacity!=55||defaults.ReduceBlur)throw new Exception("Appearance defaults");
if(defaults.DesktopStripPosition!=DesktopStripPosition.Top||!defaults.KeepMinisSameSize||defaults.AutoArrange||defaults.AutoArrangeGrid!=4||!defaults.AnimateLayout||defaults.DesktopTransition!=DesktopTransitionMode.Appear||defaults.BackgroundTheme!=BackgroundTheme.DarkBlueBlack||defaults.SmallWindowSize!=Settings.SmallWindowSizeDefault||Settings.SmallWindowSizeDefault!=700)throw new Exception("Strip/options defaults");
if(Settings.SmallWindowSizeMin>=defaults.SmallWindowSize||Settings.SmallWindowSizeMax<=defaults.SmallWindowSize)throw new Exception("Custom thumbnail slider range");
// The internal FlyIn opening transition must never be selectable/saved as the user's
// Desktop transition: the picker offers exactly the five user-facing modes.
if((int)DesktopTransitionMode.FlyIn!=5||Enum.GetValues<DesktopTransitionMode>().Length!=6)throw new Exception("FlyIn must stay the internal sixth mode");
var sourceSizes=new[]{new Native.RECT(0,0,1600,900),new Native.RECT(0,0,900,1600),new Native.RECT(0,0,1200,800)};
var sourceTiles=ThumbnailLayout.ArrangeSources(sourceSizes,new Native.RECT(0,0,1920,900),6,180);
if(sourceTiles.Count!=3||sourceTiles.Any(r=>Math.Max(r.Width,r.Height)>180))throw new Exception("Small-window long-edge sizing");
for(int i=0;i<sourceTiles.Count;i++)for(int j=i+1;j<sourceTiles.Count;j++)if(sourceTiles[i].Intersects(sourceTiles[j]))throw new Exception("Sized tile overlap");
// Opening layout uses a finger-width 28 DIP panel gap. Inflate each panel by half the
// channel width; neighbouring panels must still not overlap.
var fingerTiles=ThumbnailLayout.ArrangeSources(Enumerable.Repeat(new Native.RECT(0,0,1600,900),6).ToList(),new Native.RECT(0,0,1920,900),28,300);
for(int i=0;i<fingerTiles.Count;i++)for(int j=i+1;j<fingerTiles.Count;j++){
    var a=fingerTiles[i];var b=fingerTiles[j];
    var expanded=new Native.RECT(a.Left-13,a.Top-13,a.Width+26,a.Height+26);
    if(expanded.Intersects(b))throw new Exception("Opening panels lost finger gap");
}
foreach(var grid in new[]{2,4,5})
{
    var gridArea=new Native.RECT(-500,180,1800,900);
    foreach(var count in new[]{1,grid*grid,grid*grid+grid})
    {
        var cells=ThumbnailLayout.GridCells(gridArea,grid,count,6);
        int expectedRows=Math.Max(grid,(count+grid-1)/grid);
        if(cells.Count!=grid*expectedRows)throw new Exception("Auto-arrange grid capacity");
        foreach(var r in cells)if(r.Left<gridArea.Left||r.Top<gridArea.Top||r.Right>gridArea.Right||r.Bottom>gridArea.Bottom)throw new Exception("Auto-arrange grid escaped canvas");
        for(int i=0;i<cells.Count;i++)for(int j=i+1;j<cells.Count;j++)if(cells[i].Intersects(cells[j]))throw new Exception("Auto-arrange grid overlap");
    }
}
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
// Task-View grid: aspect-preserving justified rows keep a margin gap and never overlap.
foreach(var gridArea2 in new[]{new Native.RECT(8,192,1904,704),new Native.RECT(-2552,-1248,2544,900)})
foreach(int gnum in new[]{1,3,6,12}){
    var gsources=Enumerable.Range(0,gnum).Select(i=>new Native.RECT(0,0,i%2==0?1600:800,900)).ToList();
    var gtiles=ThumbnailLayout.ArrangeSources(gsources,gridArea2,14,260);
    if(gtiles.Count!=gnum)throw new Exception("Grid tile count");
    foreach(var r in gtiles)if(r.Width<1||r.Height<1||r.Left<gridArea2.Left||r.Top<gridArea2.Top||r.Right>gridArea2.Right||r.Bottom>gridArea2.Bottom)throw new Exception("Grid tile escaped the canvas");
    for(int i=0;i<gnum;i++)for(int j=i+1;j<gnum;j++)if(gtiles[i].Intersects(gtiles[j]))throw new Exception("Grid tile overlap");
}
Console.WriteLine("PASS: Task-View grid keeps tiles gapped, non-overlapping, and in-canvas.");
// Regressions: a single wide source used to pass the height-only fit check;
// dense layouts used to stop shrinking at 20% even when rows still overflowed.
foreach(var canvasArea in new[]{new Native.RECT(0,0,250,1000),new Native.RECT(-500,-300,700,300)})
foreach(int count in new[]{1,24,80})
{
    var sources=Enumerable.Repeat(new Native.RECT(0,0,1600,900),count).ToList();
    var cells=ThumbnailLayout.ArrangeSources(sources,canvasArea,28,900);
    if(cells.Count!=count)throw new Exception("Dense source layout lost windows");
    for(int i=0;i<cells.Count;i++)
    {
        var r=cells[i];
        if(r.Width<1||r.Height<1||r.Left<canvasArea.Left||r.Top<canvasArea.Top||r.Right>canvasArea.Right||r.Bottom>canvasArea.Bottom)
            throw new Exception("Narrow/dense source layout escaped canvas");
        for(int j=i+1;j<cells.Count;j++)if(r.Intersects(cells[j]))throw new Exception("Dense source overlap");
    }
}
Console.WriteLine("PASS: narrow and dense source layouts remain bounded without overlap.");
var noDropTop=Tiler.OverviewArea(new Native.RECT(0,0,1920,1080),8,1,DesktopStripPosition.Top);
var noDropBottom=Tiler.OverviewArea(new Native.RECT(0,0,1920,1080),8,1,DesktopStripPosition.Bottom);
if(noDropTop.Top<184||noDropBottom.Bottom>1080-184)throw new Exception("Desktop strip no-drop reservation");
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
    if(BrowseReconciler.ClassifyForeground(selected,selected,selected,false,selected)!=BrowseTarget.Keep)
        throw new Exception("Selected window still foreground must keep the browse");
    if(BrowseReconciler.ClassifyForeground(selected,101,selected,false,0)!=BrowseTarget.Keep)
        throw new Exception("An owned popup of the selected window must keep the browse");
    if(BrowseReconciler.ClassifyForeground(selected,canvas,canvas,true,0)!=BrowseTarget.Refront)
        throw new Exception("Our own canvas taking focus must re-front the browsed window, not drop to the grid");
    if(BrowseReconciler.ClassifyForeground(selected,canvas,canvas,true,200)!=BrowseTarget.Refront)
        throw new Exception("Our own canvas must win even if the handle also resolves to a source");
    if(BrowseReconciler.ClassifyForeground(selected,200,200,false,200)!=BrowseTarget.Follow)
        throw new Exception("Another managed source taking focus must be followed");
    if(BrowseReconciler.ClassifyForeground(selected,300,300,false,0)!=BrowseTarget.Grid)
        throw new Exception("An untiled window taking focus must return to the grid");
    if(BrowseReconciler.ClassifyForeground(selected,0,0,false,0)!=BrowseTarget.Grid)
        throw new Exception("No foreground at all must return to the grid");
    Console.WriteLine("PASS: browse reconciliation keeps, re-fronts, follows and grids the right foreground.");
}
record CheckState(long Handle,string Title,Native.RECT Bounds,Native.WINDOWPLACEMENT Placement);
