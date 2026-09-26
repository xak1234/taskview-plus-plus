namespace StayView.Core;

// A Task View that stays open. Tiles are DWM thumbnails; clicking one brings the real
// window to the front at its true size (the overlay drops out of the way and is
// re-summoned by the hotkey). Automatic layout never rewrites real window placement;
// an explicit miniature drag is the exception because the user intends that gesture to
// change where the real desktop window will live.
public sealed class OverviewSession : IDisposable
{
    public readonly PlacementStore Placements;
    readonly WindowCatalog catalog;
    readonly Settings settings;
    public readonly VirtualDesktopService Desktops;
    readonly Dictionary<nint, Native.RECT> positions = [];
    readonly Dictionary<nint, Native.RECT> currentCells = [];
    readonly Dictionary<nint, int> arrangeOrder = [];
    readonly List<nint> docked = [];
    readonly Dictionary<nint, Native.RECT> settling = [];
    // A miniature drag is now a real desktop placement edit. Keep the source HWND hidden
    // at its old location while the grid is up, but remember which saved placements must
    // be applied before their next focus handoff.
    readonly HashSet<nint> tileMoved = [];
    // Windows that were minimized when this session opened. Remembered because the first
    // reflow restores them behind the canvas to keep their thumbnails live, which clears
    // the very state the Dock minimized windows option keys off.
    readonly HashSet<nint> minimizedOnEntry = [];
    // Semantic minimized state survives the no-activate restore used to keep DWM dock
    // thumbnails live. DockView uses this instead of Native.IsIconic so only windows that
    // are actually minimized in Taskview++ receive the minimized styling.
    readonly HashSet<nint> minimizedDocked = [];
    // Windows currently passing through a focused native minimize gesture. In StayView a
    // focused minimize is a dock command: the shell is allowed to finish its minimize
    // transition, then the source is restored non-activating behind the opaque overview so
    // the dock keeps a live DWM source without changing the placement journal to minimized.
    readonly HashSet<nint> userMinimized = [];
    readonly Dictionary<nint,(int Count,long Since)> restoreAttempts = [];
    readonly HashSet<nint> selfMinimizing = [];
    readonly HashSet<nint> restoredSinceAttempt = [];
    nint draggingTile;
    nint foreground;
    Guid observedDesktop;
    public Guid PreviousDesktop { get; private set; }
    public nint Selected { get; private set; }
    public bool Active { get; private set; }
    public bool DragActive => draggingTile != 0;
    public IReadOnlyList<nint> DockedSources => docked;
    public bool IsDocked(nint h) => docked.Contains(h);
    public Func<nint,bool>? IsPinnedWindow { get; set; }
    public Func<nint,bool>? KeepMinimized { get; set; }
    public bool IsPinned(nint h) => IsPinnedWindow?.Invoke(h) == true;
    // Monitor -> screen rectangle of the desktop bar shown on it (empty when none).
    public Func<nint,Native.RECT>? StripBarFor { get; set; }
    public Native.RECT StripBar(nint monitor) => StripBarFor?.Invoke(monitor) ?? default;
    // A clamp for moving window h (USER32 rectangle in, same-sized rectangle out) that keeps
    // its visible frame off whichever monitor's desktop bar it is over. The frame margins are
    // measured once here, so a drag builds this at its start and calls it per pointer move.
    // Null when the overview is closed or the window's geometry is unknown.
    public Func<Native.RECT,Native.RECT>? StripClamp(nint h)
    {
        if(!Active || h==0 || !Native.GetWindowRect(h,out var current) || !Native.TryGetVisualBounds(h,out var visual))return null;
        int leftMargin=visual.Left-current.Left,topMargin=visual.Top-current.Top,width=visual.Width,height=visual.Height;
        return rect=>{
            var probe=new Native.RECT(rect.Left+leftMargin,rect.Top+topMargin,width,height);
            var monitor=Native.MonitorFromRect(ref probe,2);
            return monitor==0?rect:FocusedWindowGeometry.MoveOffBar(rect,leftMargin,topMargin,width,height,StripBar(monitor),Native.WorkArea(monitor));
        };
    }
    public Native.RECT KeepOffStrip(nint h, Native.RECT windowRect) => StripClamp(h)?.Invoke(windowRect) ?? windowRect;
    public bool IsMinimizedDocked(nint h) => minimizedDocked.Contains(h);
    public int DesktopTransitionVersion { get; private set; }
    public int DesktopTransitionDirection { get; private set; } = 1;
    public DesktopTransitionMode DesktopTransition { get; private set; } = DesktopTransitionMode.Appear;
    public event Action<IReadOnlyDictionary<nint, IReadOnlyList<Tile>>>? LayoutChanged;
    public event Action? Leaving;
    public event Action<Guid>? DesktopRemoved;
    // Raised when the current virtual desktop actually changed while the overview is up,
    // before the grid re-renders. TrayApp uses it to leave browse mode: the browsed
    // window is on the old desktop, so the new desktop must show its grid, not stay
    // dropped behind windows for a window that is no longer here.
    public event Action? DesktopSwitched;
    public OverviewSession(WindowCatalog catalog, VirtualDesktopService desktops, Settings settings,PlacementStore? placements=null)
    { this.catalog = catalog; Desktops = desktops; this.settings = settings; Placements=placements??new(); }

    public void Toggle() { if (Active) Exit(); else Enter(); }
    public void Enter()
    {
        if (Active) return;
        foreground = Native.GetForegroundWindow();
        // Our own windows (the launch splash, options) are never the app to return to.
        Native.GetWindowThreadProcessId(foreground, out var foregroundPid);
        if (foregroundPid == Environment.ProcessId) foreground = 0;
        Selected = foreground;
        Active = true;
        observedDesktop = Desktops.Current;
        // Must run before the first Reflow, which restores minimized sources.
        minimizedOnEntry.Clear();
        minimizedDocked.Clear();
        userMinimized.Clear();
        foreach (var w in catalog.Enumerate())
            if (w.Minimized || Native.IsIconic(w.Handle)) { minimizedOnEntry.Add(w.Handle); minimizedDocked.Add(w.Handle); }
        ApplyDockMinimizedSetting();
        // Soft animation: the opening render flies each miniature in from its window's
        // real screen position, as Task View does. Signalled the same way a desktop
        // switch signals its transition; only this first render of the session uses it.
        if (settings.AnimateLayout) { DesktopTransition = DesktopTransitionMode.FlyIn; DesktopTransitionVersion++; }
        try { Reflow(); } catch { Exit(); throw; }
    }
    public IReadOnlyList<AppWindow> DesktopWindows() => catalog.EnumerateDesktopPictures();
    public void Reflow()
    {
        // A user drag owns tile geometry until mouse-up. No timer, topology refresh,
        // auto-layout or other caller may rebuild the overview underneath it.
        if (!Active || draggingTile != 0) return;
        // Capture each original exactly once for the ENTIRE session, including
        // other desktops. A desktop switch never replaces the original journal.
        var all = DesktopWindows();
        int z = 0;
        bool captured = false;
        foreach (var w in all) captured |= Placements.Capture(w.Handle, z++, Desktops.WindowDesktop(w.Handle));
        // Capture is idempotent; only touch the disk journal when a new window was added,
        // so a tile drag/drop or dock reflow does not rewrite an identical file.
        if (captured) Placements.Persist();
        var windows = catalog.Enumerate();
        // The dock is session-global, not desktop-local. Keep its sources from the
        // all-desktops pass so a manually docked window remains represented after the
        // user changes virtual desktop. Ordinary canvas tiles still come strictly from
        // the current desktop below.
        var dockedWindows = all.Where(w => docked.Contains(w.Handle)).ToList();
        foreach(var h in arrangeOrder.Keys.Where(h=>!Native.IsWindow(h)).ToList())arrangeOrder.Remove(h);
        foreach(var h in positions.Keys.Where(h=>!Native.IsWindow(h)).ToList())positions.Remove(h);
        docked.RemoveAll(h=>!Native.IsWindow(h));
        var output = new Dictionary<nint, IReadOnlyList<Tile>>();
        currentCells.Clear();
        foreach (var m in Native.Monitors())
        {
            var work = Native.WorkArea(m);
            double dpi = Native.MonitorScale(m);
            // Windows 11 Task View layout: same order, sizes, rows and gaps (TaskViewLayout).
            var group = ArrangeOrder(windows.Where(w => w.Monitor == m && !docked.Contains(w.Handle)).ToList());
            var slots = TaskViewCells(m, group);
            // The desktop strip is a hard no-drop zone. Manual positions are always
            // clamped to the legal canvas below it (or above it when the bar is at
            // the bottom), and never to the full work area.
            var canvasArea = Tiler.OverviewArea(work,8,dpi,settings.DesktopStripPosition);
            int panelGap=TaskViewLayout.HorizontalGap(dpi);
            var tiles = new List<Tile>();
            var occupied = group.Where(w=>positions.ContainsKey(w.Handle))
                .Select(w=>ThumbnailLayout.Clamp(positions[w.Handle],canvasArea)).ToList();
            bool initialLayout=occupied.Count==0;
            for (int i = 0; i < group.Count; i++)
            {
                var w = group[i];
                Native.RECT cell;
                if(settings.AutoArrange)cell=slots[i];
                else if(positions.TryGetValue(w.Handle,out var saved))
                {
                    cell=ThumbnailLayout.Clamp(saved,canvasArea);
                    positions[w.Handle]=cell;
                }
                else
                {
                    cell=initialLayout ? slots[i] : StableTileLayout.PlaceNew(slots[i],occupied,canvasArea,panelGap);
                    positions[w.Handle]=cell;
                    occupied.Add(cell);
                }
                currentCells[w.Handle] = cell;
                tiles.Add(new(w, cell, cell, true, TileRole.Satellite));
            }
            // Docked windows render in the desktop bar; the overlay lays out their cells.
            foreach (var h in docked)
            {
                var w = dockedWindows.FirstOrDefault(x => x.Handle == h && x.Monitor == m);
                if (w != null) tiles.Add(new(w, default, default, true, TileRole.Dock));
            }
            output[m] = tiles;
        }
        LayoutChanged?.Invoke(output);
        // A minimized source can stop supplying live DWM content, leaving only the
        // tile's hover outline visible. While the overview grid is up, keep minimized
        // sources restored at their real normal placement behind the opaque overlay so
        // their thumbnails remain live. The original show-state is still journaled and
        // restored when StayView exits.
        // A global dock source can belong to another virtual desktop. Keep its DWM source
        // live just like a current-desktop dock source; this never changes desktop ownership.
        RestoreMinimizedSourcesForOverview(
            windows.Concat(dockedWindows).GroupBy(w => w.Handle).Select(g => g.First()));
    }
    public void MoveTile(nint h, Native.RECT rect)
    {
        if(IsPinned(h))return;
        // Live drag remains canvas-only. The real desktop placement is committed once on
        // a successful drop, so moving a miniature never causes the hidden HWND to trail
        // the pointer or flash behind the overview.
        positions[h] = rect;
        currentCells[h] = rect;
    }
    public void RestoreMinimizedSourcesForOverview()
    {
        if (!Active || draggingTile != 0) return;
        RestoreMinimizedSourcesForOverview(catalog.Enumerate());
    }
    public void RestoreMinimizedSourceForOverview(nint h)
    {
        if (!Active || h == 0 || !Native.IsWindow(h) || !Native.IsIconic(h) || userMinimized.Contains(h)) return;
        // Some apps (tray panels such as Windscribe) minimize themselves again whenever
        // they lose focus. Restoring them every tick flashes their tile forever, so a window
        // that re-minimizes three times within a few seconds is left minimized this session.
        if (selfMinimizing.Contains(h)) return;
        // A global dock thumbnail is not permission to activate a foreign desktop.
        // Unknown ownership also waits until the shell can positively identify it.
        var current=Desktops.Current;
        var owner=Desktops.WindowDesktop(h);
        if(!IsPinned(h)&&(current==Guid.Empty || owner!=current))return;
        // Count only real "restored, then minimized itself again" cycles: a slow app still
        // restoring from the last request is not counted (see selfMinimizing above).
        long now = Environment.TickCount64;
        var (count, since) = restoreAttempts.GetValueOrDefault(h);
        if (now - since > 8000) { count = 0; since = now; }   // a cycle spans ~2 ticks
        if (restoredSinceAttempt.Remove(h)) count++;
        if (count >= 3)
        {
            restoreAttempts.Remove(h);
            selfMinimizing.Add(h);
            Log.Write($"[restore] {Native.Title(h)} ({h}) keeps minimizing itself; leaving it minimized");
            return;
        }
        restoreAttempts[h] = (count, since);
        // Some packaged/WinUI apps remain in a minimized-style presentation when asked
        // to SW_SHOWNOACTIVATE. Force a true normal restore using the placement journal's
        // normal rectangle so the DWM tile represents the real standard-sized app.
        if (Placements.Entries.TryGetValue(h, out var saved))
        {
            var p = saved.Placement;
            p.Length = System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>();
            p.ShowCmd = 4; // SW_SHOWNOACTIVATE: passive DWM maintenance must not steal focus.
            p.NormalPosition = saved.Placement.NormalPosition;
            Native.SetWindowPlacement(h, ref p);
            // WINDOWPLACEMENT.NormalPosition is workspace-relative on some window styles;
            // SetWindowPos expects screen coordinates. Let SetWindowPlacement perform the
            // restore, then only change z-order so we never reinterpret its coordinates.
            Native.SetWindowPos(h, 1, 0, 0, 0, 0, 0x13 | 0x4000); // HWND_BOTTOM + NOMOVE|NOSIZE|NOACTIVATE + ASYNC
        }
        else
        {
            Native.ShowWindowAsync(h, 4); // SW_SHOWNOACTIVATE
            Native.SetWindowPos(h, 1, 0, 0, 0, 0, 0x13 | 0x4000);
        }
    }
    void RestoreMinimizedSourcesForOverview(IEnumerable<AppWindow> windows)
    {
        ConfirmUserMinimizedSources();
        foreach (var w in windows)
        {
            if (w.Minimized || Native.IsIconic(w.Handle)) RestoreMinimizedSourceForOverview(w.Handle);
            // Seen restored after we asked: a later re-minimize is the app's own doing.
            else if (restoreAttempts.ContainsKey(w.Handle)) restoredSinceAttempt.Add(w.Handle);
        }
    }
    public void BeginTileDrag(nint h)
    {
        if(Active && h != 0 && !IsPinned(h)) { draggingTile = h; lastPreview = null; }
    }
    public void EndTileDrag(nint h = 0)
    {
        if(h == 0 || draggingTile == h) draggingTile = 0;
    }
    // Docking is canvas state only: the source HWND is never moved or minimised.
    // A pin is never docked, whatever asks: drag, minimize, or the dock-minimized setting.
    public bool DockTile(nint h)
    {
        if(!Active||h==0||draggingTile!=0||!Native.IsWindow(h)||!PinnedWindowPolicy.CanDock(IsPinned(h)))return false;
        // Manual layout: pin every current cell (this tile's too) so docking/undocking
        // never repacks untouched tiles and an undocked tile returns to its old spot.
        if(!settings.AutoArrange)foreach(var pair in currentCells)positions.TryAdd(pair.Key,pair.Value);
        if(!docked.Contains(h))docked.Add(h);
        arrangeOrder.Remove(h);currentCells.Remove(h);
        Reflow();
        return true;
    }
    // Membership only. The caller restores a whole desktop and reflows once.
    public void ApplyDockState(nint h, bool dock, bool minimized)
    {
        if(!Active||h==0||!Native.IsWindow(h)||IsPinned(h))return;
        if(dock)
        {
            if(!docked.Contains(h))docked.Add(h);
            if(minimized)minimizedDocked.Add(h);else minimizedDocked.Remove(h);
            arrangeOrder.Remove(h);
            currentCells.Remove(h);
        }
        else
        {
            docked.Remove(h);
            minimizedDocked.Remove(h);
        }
    }
    public bool TryGetCanvasCell(nint h, out Native.RECT cell) => currentCells.TryGetValue(h, out cell);
    public bool UndockTile(nint h)
    {
        if(!Active||!docked.Contains(h)||!Native.IsWindow(h)||IsPinned(h))return false;
        // The dock follows the user between virtual desktops. Clicking a docked item still
        // has the existing click-to-undock meaning, but if that item belongs to a different
        // desktop first bring the real window to the desktop the user is currently on.
        // That makes the global dock useful without silently switching desktops and without
        // changing the established two-step dock -> canvas -> focus interaction.
        var current=Desktops.Current;
        if(current!=Guid.Empty)
        {
            var owner=Desktops.WindowDesktop(h);
            bool elsewhere=owner!=Guid.Empty?owner!=current:!Desktops.IsCurrent(h);
            if(elsewhere)
            {
                if(!Desktops.Move(h,current))return false;
                // Moving it out of the global dock onto this canvas is an explicit user
                // desktop move. Persist that choice so session exit/crash recovery does
                // not send it back to the desktop where it was first captured.
                Placements.SetDesktop(h,current);
            }
        }
        docked.Remove(h);
        minimizedDocked.Remove(h);
        // The user put this one back on the canvas themselves; a later settings change
        // must not silently re-dock it just because it opened minimized.
        minimizedOnEntry.Remove(h);
        Reflow();
        return true;
    }
    // Where the other canvas tiles on the dragged tile's monitor move to while `h` is held
    // at `rect` (Task View spacing, TileRepulsion). Nothing is committed.
    public IReadOnlyDictionary<nint,Native.RECT> PreviewDrag(nint h,Native.RECT rect)
    {
        if(!Active||h==0){LastYielded=rect;return new Dictionary<nint,Native.RECT>();}
        var probe=rect;
        var monitor=Native.MonitorFromRect(ref probe,2);
        if(monitor==0){LastYielded=rect;return new Dictionary<nint,Native.RECT>();}
        double dpi=Native.MonitorScale(monitor);
        var canvas=Tiler.OverviewArea(Native.WorkArea(monitor),8,dpi,settings.DesktopStripPosition);
        var home=new Dictionary<nint,Native.RECT>();
        foreach(var (source,cell) in currentCells)
        {
            if(source==h)continue;
            var c=cell;
            if(Native.MonitorFromRect(ref c,2)==monitor)home[source]=cell;
        }
        var pinned=home.Keys.Where(IsPinned).ToHashSet();
        var (hGap,vGap,header)=TaskViewLayout.MinimumSpacing(dpi);
        // A pin does not move, and the dragged window cannot pass through it.
        var yielded=TileRepulsion.YieldToFixed(rect,pinned.Select(id=>home[id]),canvas,hGap,vGap,header);
        LastYielded=yielded;
        // The previous frame keeps each pushed neighbour escaping to the same side.
        // The dragged tile's own cell is still its pre-drag home (committed only on drop).
        Native.RECT? vacated=currentCells.TryGetValue(h,out var from)?from:null;
        lastPreview=TileRepulsion.Resolve(h,yielded,home,pinned,canvas,hGap,vGap,header,lastPreview,vacated);
        return lastPreview;
    }
    // Where the dragged window actually sits after pinned windows have pushed it aside.
    public Native.RECT LastYielded { get; private set; }
    public Native.RECT ClearOfPins(nint dragged, Native.RECT desired)
    {
        PreviewDrag(dragged, desired);
        return LastYielded.Width>0?LastYielded:desired;
    }
    Dictionary<nint,Native.RECT>? lastPreview;
    public void DropTile(nint h,Native.RECT rect,Native.RECT original)
    {
        if(IsPinned(h))return;
        // Manual layout: neighbours the drop pushed aside stay where they were pushed.
        // They are canvas-only moves; only the dragged tile's real window is relocated.
        // The dropped tile itself settles where it overlaps nothing (its old slot or the
        // nearest free spot); if the canvas has no room at all the whole drop snaps back.
        List<KeyValuePair<nint,Native.RECT>>? pushed=null;
        bool settledElsewhere=false;
        if(!settings.AutoArrange&&settings.RepelWindows&&Active)
        {
            var preview=PreviewDrag(h,rect);
            var probe=rect;var monitor=Native.MonitorFromRect(ref probe,2);
            if(monitor!=0)
            {
                double dpi=Native.MonitorScale(monitor);
                var canvas=Tiler.OverviewArea(Native.WorkArea(monitor),8,dpi,settings.DesktopStripPosition);
                var (hGap,vGap,header)=TaskViewLayout.MinimumSpacing(dpi);
                Native.RECT? origin=currentCells.TryGetValue(h,out var from)?from:null;
                var settled=TileRepulsion.Settle(rect,origin,preview.Values,canvas,hGap,vGap,header);
                if(settled is {} s)
                {
                    settledElsewhere=!s.Equals(rect);
                    rect=s;
                    pushed=preview.Where(p=>currentCells.TryGetValue(p.Key,out var home)&&!home.Equals(p.Value)).ToList();
                }
                else if(origin is {} back){settledElsewhere=true;rect=back;}
            }
        }
        MoveTile(h,rect);
        if(pushed is {Count:>0})
            foreach(var (source,cell) in pushed){positions[source]=cell;currentCells[source]=cell;}
        if(pushed is {Count:>0}||settledElsewhere)Reflow();
        lastPreview=null;
        Native.RECT final=rect;
        if(settings.AutoArrange&&Active)
        {
            var probe=rect;
            var monitor=Native.MonitorFromRect(ref probe,2);
            if(monitor==0)monitor=Native.MonitorFromWindow(h,2);
            var group=ArrangeOrder(catalog.Enumerate().Where(w=>w.Monitor==monitor&&!docked.Contains(w.Handle)).ToList());
            if(group.All(w=>w.Handle!=h)){Reflow();return;}
            MoveInOrder(group,h,NearestSlot(TaskViewCells(monitor,group),Center(rect)));
            positions.Remove(h);
            Reflow();
            if(currentCells.TryGetValue(h,out var snapped))final=snapped;
        }
        if(!Active)return;
        int dx=final.Left-original.Left,dy=final.Top-original.Top;
        // Only the tile the user actually dragged changes its real desktop placement.
        // Any neighbour moved automatically by Auto Arrange keeps its existing HWND spot.
        if((dx!=0||dy!=0)&&Placements.Translate(h,dx,dy))tileMoved.Add(h);
    }
    // Keep the slots neighbours slid into while another window was dragged.
    // Pinned windows are not in this set: the repel solve leaves them at home.
    public void CommitNeighbourRepel(nint dragged, Native.RECT draggedRect)
    {
        if(!Active||settings.AutoArrange||!settings.RepelWindows)return;
        foreach(var (source,cell) in PreviewDrag(dragged,draggedRect))
        {
            if(source==dragged||IsPinned(source))continue;
            if(!currentCells.TryGetValue(source,out var home)||home.Equals(cell))continue;
            positions[source]=cell;
            currentCells[source]=cell;
        }
        lastPreview=null;
    }
    // Docks (or releases) the windows that were already minimized when this session
    // opened, to match the current setting. Canvas state only, like every other dock
    // action: the source HWND is never moved or minimised by it. Callers reflow.
    public void ApplyDockMinimizedSetting()
    {
        foreach (var h in minimizedOnEntry.ToList())
        {
            if (!Native.IsWindow(h)) { minimizedOnEntry.Remove(h); continue; }
            if (settings.DockMinimizedWindows && PinnedWindowPolicy.CanDock(IsPinned(h))) { if (!docked.Contains(h)) docked.Add(h); }
            else docked.Remove(h);
        }
    }
    public void ApplyAutoArrangeSetting()
    {
        if(settings.AutoArrange){positions.Clear();arrangeOrder.Clear();}
        else
        {
            positions.Clear();
            foreach(var pair in currentCells)positions[pair.Key]=pair.Value;
            arrangeOrder.Clear();
        }
    }
    // Task View order is most-recently-used first. It is taken once from the z-order
    // captured at session entry and then kept, so tiles do not reshuffle every time focus
    // changes; windows that appear later join at the front, as they do in Task View.
    List<AppWindow> ArrangeOrder(List<AppWindow> group)
    {
        var fresh=group.Where(w=>!arrangeOrder.ContainsKey(w.Handle))
            .OrderBy(w=>Placements.Entries.TryGetValue(w.Handle,out var saved)?saved.Z:-1).ToList();
        int front=arrangeOrder.Count==0?0:arrangeOrder.Values.Min();
        for(int i=0;i<fresh.Count;i++)arrangeOrder[fresh[i].Handle]=front-fresh.Count+i;
        return group.OrderBy(w=>arrangeOrder[w.Handle]).ToList();
    }
    void MoveInOrder(List<AppWindow> ordered,nint h,int index)
    {
        var handles=ordered.Select(w=>w.Handle).Where(x=>x!=h).ToList();
        handles.Insert(Math.Clamp(index,0,handles.Count),h);
        for(int i=0;i<handles.Count;i++)arrangeOrder[handles[i]]=i;
    }
    // Task View thumbnail rectangles for `ordered` on a monitor, laid out in the canvas
    // band that the desktop strip leaves free.
    IReadOnlyList<Native.RECT> TaskViewCells(nint monitor,IReadOnlyList<AppWindow> ordered)
    {
        var work=Native.WorkArea(monitor);double dpi=Native.MonitorScale(monitor);
        var canvas=Tiler.CanvasArea(work,dpi,settings.DesktopStripPosition);
        var region=new Native.RECT(work.Left,canvas.Top,work.Width,canvas.Height);
        var sources=new List<Native.RECT>(ordered.Count);
        foreach(var w in ordered)
        {
            if(Placements.Entries.TryGetValue(w.Handle,out var saved))sources.Add(saved.Placement.NormalPosition);
            else if(Native.GetWindowRect(w.Handle,out var source))sources.Add(source);
            else sources.Add(new Native.RECT(0,0,16,10));
        }
        return TaskViewLayout.Layout(sources,Native.MonitorBounds(monitor),region,dpi);
    }
    static int NearestSlot(IReadOnlyList<Native.RECT> cells,Native.POINT point)=>
        cells.Count==0?0:Enumerable.Range(0,cells.Count).OrderBy(i=>DistanceSquared(point,Center(cells[i]))).First();
    static Native.POINT Center(Native.RECT r)=>new(){X=r.Left+r.Width/2,Y=r.Top+r.Height/2};
    static long DistanceSquared(Native.POINT a,Native.POINT b){long dx=a.X-b.X,dy=a.Y-b.Y;return dx*dx+dy*dy;}
    // The Task-View action: bring the real window to the front at its true size and
    // focus it. The overlay's own topmost state is dropped by the caller (OverlayChrome
    // / TrayApp) so this window can actually sit in front of the acrylic.
    public bool Activate(nint h)
    {
        if (!Active || !Native.IsWindow(h)) return false;
        var current=Desktops.Current;
        var owner=Desktops.WindowDesktop(h);
        if(!IsPinned(h)&&(current!=Guid.Empty && owner!=Guid.Empty ? owner!=current : !Desktops.IsCurrent(h)))return false;
        PrepareActivationGeometry(h);
        bool userIsRestoring = Placements.Entries.TryGetValue(h, out var savedBefore)
            && savedBefore.Placement.ShowCmd is 2 or 6 or 7;
        // A minimized source restores to ITS saved normal rectangle, not a canvas rect.
        // Some Electron/Chromium windows (observed with Claude) ignore SW_RESTORE and
        // SetWindowPlacement while minimized, yet respond correctly to the same native
        // SC_RESTORE command used by the taskbar/system menu. This is intentionally only
        // used for an explicit tile activation: SC_RESTORE may activate the target, so the
        // passive overview-thumbnail keepalive path must continue using no-activate restore.
        if (Native.IsIconic(h))
        {
            if(Native.SendMessageTimeout(h,0x112,(nint)0xF120,0,0x2,120,out _)==0)
                Native.PostMessage(h, 0x112, (nint)0xF120, 0);
            Native.ShowWindowAsync(h, 9);
            if(Native.IsIconic(h))return false;
        }
        // The first geometry pass may have run while the HWND was still iconic.
        PrepareActivationGeometry(h);
        // Restore/placement APIs can legally produce a frame whose caption sits above the
        // current work area (stale monitor topology, workspace offsets, or app self-moves).
        // Correct it before foreground handoff so the focused title bar never appears offscreen.
        EnsureActivationTopVisible(h);
        // Lift off the bottom of the z-order (overview keeps sources lowered) and focus.
        // A denied z-order request (e.g. an elevated console) must not prevent the
        // independent foreground request. Cross-thread activation completes asynchronously;
        // the UI caller retries while keeping the overview out of the way.
        Native.SetWindowPos(h, 0, 0, 0, 0, 0, 0x13 | 0x4000);
        Native.ForceForeground(h);
        var focused = Native.GetForegroundWindow();
        // Success requires the window to be BOTH the foreground AND actually restored.
        // ShowWindowAsync only POSTS the restore, so on an early attempt a just-minimized
        // window can be foreground while still iconic; the caller's retry loop re-issues
        // the restore, and a normal window un-minimizes within a tick or two and then
        // succeeds. Committing the browse while it is still iconic would suppress its tile
        // with nothing visible on screen — the vanish seen when focusing an undocked
        // (minimized) CLI, or a window like a background updater that re-minimizes itself.
        // Such a window simply fails to activate; the caller leaves its tile in place.
        if(Native.IsIconic(h))return false;
        if(focused != h && Native.GetAncestor(focused, 3) != h)return false;
        // Focusing a previously minimized tile is a new explicit user state. Replace the
        // journal's old minimized show-state now that the real window is restored, so an
        // eventual StayView dismissal does not minimize it again behind the user's back.
        if(userIsRestoring || (tileMoved.Contains(h) && !Native.IsZoomed(h)))
        {
            RecaptureUserPlacement(h);
            tileMoved.Remove(h);
            minimizedOnEntry.Remove(h);
            docked.Remove(h);
        }
        userMinimized.Remove(h);
        minimizedDocked.Remove(h);
        Selected = h;
        return true;
    }
    public void Close(nint h)
    {
        if (!Active || h == 0 || !Native.IsWindow(h)) return;
        var root = Native.GetAncestor(h, 2);
        if (root != 0) h = root;
        // The corner X sends WM_SYSCOMMAND/SC_CLOSE. A bare WM_CLOSE is ignored by
        // some custom frames, which then look like the button did nothing.
        Native.PostMessage(h, 0x0112, (nint)0xF060, 0);
    }
    // The user dragged a real window while browsing; keep that placement on dismissal.
    public void NoteUserMoved(nint h) { if (Active) { Placements.Recapture(h); tileMoved.Remove(h); presentationOffset.Remove(h); presentedAt.Remove(h); } }
    // Apply a miniature-drag placement while the overview is still covering the source.
    // This is called before focus animation (and again by Activate for minimized sources
    // that finish restoring between retries), so a plain focus click never relocates a
    // window: only a prior miniature drag can change this target.
    public void PrepareActivationGeometry(nint h)
    {
        if(!Active||!tileMoved.Contains(h)||!Native.IsWindow(h)
            ||!Placements.Entries.TryGetValue(h,out var saved))return;
        // Position only: a tile drag must never resize the real window. Do this under the
        // opaque overview before its DWM miniature starts expanding, so the animation's
        // endpoint is the exact visible frame that will take over.
        if(!PlacementStore.ApplyPendingGeometry(h,saved))
            Log.Write($"Tile placement move failed for {h}: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        // The window now sits exactly at the user's tile-drag placement: any earlier
        // presentation offset no longer applies (subtracting it would journal a spot the
        // user never chose).
        else { presentationOffset.Remove(h); presentedAt.Remove(h); }
    }
    // Focusing a tile expands the window over that tile: move the real window (position
    // only) so its visible frame is centred on the tile, inside the work area. This is an
    // overview-only presentation, so it is NOT journaled: dismissal puts the window back
    // where it was, unless the user moves it themselves (NoteUserMoved recaptures).
    public bool CenterOverTile(nint h)
    {
        // A tile the user dragged already carries their chosen placement for this window
        // (PrepareActivationGeometry applies it, and Activate applies it again): centring
        // would expand to one spot and then jump to the other.
        if(!Active || h==0 || tileMoved.Contains(h) || !Native.IsWindow(h) || Native.IsIconic(h) || Native.IsZoomed(h) || IsPinned(h)
            || !currentCells.TryGetValue(h,out var cell)
            || !Native.GetWindowRect(h,out var window) || !Native.TryGetVisualBounds(h,out var visual))return false;
        var monitor=Native.MonitorFromWindow(h,2);
        if(monitor==0)return false;
        var target=FocusedWindowGeometry.CenterOver(window,visual,cell,Native.WorkArea(monitor));
        return MovePresented(h,target.Left,target.Top);
    }
    // How far StayView has moved each window purely for the overview (centre over tile,
    // off the desktop bar, clear of a pin). Every automatic journal update takes this back
    // out, so only placement the user chose is restored on dismissal. A move the user
    // makes themselves (NoteUserMoved) is journaled as-is and resets the offset.
    readonly Dictionary<nint,(int X,int Y)> presentationOffset=[];
    public bool MovePresented(nint h,int left,int top)
    {
        if(!Active || h==0 || !Native.GetWindowRect(h,out var current))return false;
        int dx=left-current.Left,dy=top-current.Top;
        if(dx==0&&dy==0)return false;
        if(!Native.SetWindowPos(h,0,left,top,0,0,0x15))return false; // NOSIZE|NOZORDER|NOACTIVATE
        var offset=presentationOffset.GetValueOrDefault(h);
        presentationOffset[h]=(offset.X+dx,offset.Y+dy);
        presentedAt[h]=new Native.RECT(left,top,current.Width,current.Height);
        return true;
    }
    // Journal the window's current state minus StayView's presentation moves.
    void RecaptureUserPlacement(nint h)
    {
        var offset=presentationOffset.GetValueOrDefault(h);
        Placements.Recapture(h,offset.X,offset.Y);
    }
    // After a native move/resize by the user: journal where they put it, then slide the
    // frame off the desktop bar as presentation only.
    public void NoteUserMovedOffStrip(nint h)
    {
        if(!Active || h==0)return;
        NoteUserMoved(h);
        // A drag that ended maximized (dragged to the top edge) is left to Windows.
        if(Native.IsZoomed(h) || !Native.GetWindowRect(h,out var moved))return;
        var clear=KeepOffStrip(h,moved);
        MovePresented(h,clear.Left,clear.Top);
    }
    // Where the focus animation of a visible frame ends: inside the work area, off the bar.
    // Must match what EnsureActivationTopVisible does to the real window.
    public Native.RECT FocusTarget(nint monitor, Native.RECT visual)
    {
        var work=Native.WorkArea(monitor);
        var fitted=FocusedWindowGeometry.FitInside(visual,work);
        return FocusedWindowGeometry.MoveOffBar(fitted,0,0,fitted.Width,fitted.Height,StripBar(monitor),work);
    }
    public bool EnsureActivationTopVisible(nint h)
    {
        if(!Active || h==0 || !Native.IsWindow(h) || Native.IsIconic(h) || Native.IsZoomed(h)
            || !Native.GetWindowRect(h,out var windowBounds) || !Native.TryGetVisualBounds(h,out var visualBounds))return false;
        // Anchor the correction to the window's assigned monitor, not to the stray pixels.
        // In stacked-monitor layouts an already-off-top frame can overlap the monitor above;
        // MonitorFromRect would then legitimize the bad position by switching work areas.
        var monitor=Native.MonitorFromWindow(h,2); // MONITOR_DEFAULTTONEAREST
        if(monitor==0)return false;
        var work=Native.WorkArea(monitor);
        bool changed=false;
        // A frame outside the work area is a real placement fault: fix it and journal it.
        var corrected=FocusedWindowGeometry.FitWindowInside(windowBounds,visualBounds,work);
        if(!corrected.Equals(windowBounds))
        {
            if(!Native.SetWindowPos(h,0,corrected.Left,corrected.Top,corrected.Width,corrected.Height,0x14))return false; // NOZORDER|NOACTIVATE
            var offset=presentationOffset.GetValueOrDefault(h);
            Placements.UpdateGeometry(h,offset.X,offset.Y);
            changed=true;
        }
        // The desktop bar only exists while the overview is up. Slide the frame off it
        // without resizing, as presentation only, so dismissal restores the user's spot.
        var visible=FocusedWindowGeometry.FitInside(visualBounds,work);
        int leftMargin=visualBounds.Left-windowBounds.Left,topMargin=visualBounds.Top-windowBounds.Top;
        var clear=FocusedWindowGeometry.MoveOffBar(corrected,leftMargin,topMargin,visible.Width,visible.Height,StripBar(monitor),work);
        if(MovePresented(h,clear.Left,clear.Top))changed=true;
        return changed;
    }
    // Focused native Minimize means "dock" while StayView is active. Establish the dock
    // cell before the shrink animation starts, then reserve the source until Windows has
    // completed its own minimize transition. The source is restored behind the overview at
    // completion so the dock remains live; the journal deliberately stays non-minimized.
    public bool BeginUserMinimize(nint h)
    {
        if(!Active || h==0 || !Native.IsWindow(h) || !PinnedWindowPolicy.CanDock(IsPinned(h)))return false;
        userMinimized.Add(h);
        minimizedDocked.Add(h);
        return docked.Contains(h) || DockTile(h);
    }
    // The window was restored before StayView finished docking it (EVENT_SYSTEM_MINIMIZEEND
    // is the RESTORE event). Only an unfinished minimize is cancelled: a window StayView
    // already docked and restored behind the canvas keeps its minimized-docked state.
    public void CancelUserMinimize(nint h)
    {
        if(!Active || Native.IsIconic(h) || !userMinimized.Remove(h))return;
        minimizedDocked.Remove(h);
    }
    public void NoteUserMinimized(nint h)
    {
        if(!Active || !Native.IsIconic(h) || !PinnedWindowPolicy.CanDock(IsPinned(h)))return;
        userMinimized.Add(h);
        FinishUserMinimize(h);
    }
    // Dock a window the user minimized, then restore it behind the canvas so its dock
    // thumbnail is live. A refused dock (a tile drag in progress) leaves it minimized and
    // in userMinimized, so the next grid-maintenance pass tries again.
    bool FinishUserMinimize(nint h)
    {
        // Out of the set before DockTile: DockTile reflows, and a reflow runs this confirm
        // pass again, which must not re-enter for the same window.
        userMinimized.Remove(h);
        if(!docked.Contains(h) && !DockTile(h)){ userMinimized.Add(h); return false; }
        minimizedDocked.Add(h);
        RestoreMinimizedSourceForOverview(h);
        return true;
    }
    // The minimize itself has no reliable completion event, so grid maintenance finishes
    // the minimize-to-dock conversion once it sees the HWND iconic.
    public void ConfirmUserMinimizedSources()
    {
        if(!Active)return;
        foreach(var h in userMinimized.ToList())
        {
            if(!Native.IsWindow(h)){userMinimized.Remove(h);continue;}
            if(!Native.IsIconic(h))continue;
            FinishUserMinimize(h);
        }
    }
    // A user RESIZE of the focused window is theirs to keep (edge/corner drag, maximize,
    // Windows snap) — the input hook can't see it, since resize-frame presses pass through
    // to Windows, so this timer catches it and rewrites the journal so the tile size, a
    // later minimize-restore, the next focus and dismissal all use the new size.
    //
    // It is deliberately NOT a general "whatever the geometry is now" recapture:
    //  - A position-only change is ignored. A real user title-bar move goes through the
    //    input hook (WindowDragged -> NoteUserMoved -> Recapture); a move this timer sees
    //    with the SIZE unchanged is the window (or an app/update process) relocating
    //    itself, which must not be journaled or the window reopens/exits where the user
    //    never put it.
    //  - A change measured against a parked/off-screen baseline (the -32000,-32000
    //    minimize sentinel) is a restore-from-minimized, not a resize, so it is skipped:
    //    journaling it would drop the window's minimized show-state.
    public void SyncUserGeometry(nint h, bool immediate = false)
    {
        // Pinned geometry is captured when the pin is created and is immutable until the
        // pin is released. Never let the periodic resize journal race the restoration path
        // and accidentally bless a transient app/self-initiated size change.
        if (!Active || h == 0 || IsPinned(h) || !Native.IsWindow(h) || Native.IsIconic(h)) { settling.Remove(h); return; }
        if (!Placements.Entries.TryGetValue(h, out var saved) || !Native.GetWindowRect(h, out var now)) return;
        bool sizeChanged = now.Width != saved.Bounds.Width || now.Height != saved.Bounds.Height;
        var baseline = saved.Bounds;
        bool baselineOnScreen = Native.MonitorFromRect(ref baseline, 0) != 0; // MONITOR_DEFAULTTONULL
        if (!sizeChanged || !baselineOnScreen) { settling.Remove(h); return; }
        // One still sample first, so a resize in progress is not journaled (and rewritten
        // to disk) on every tick while the pointer is still moving.
        if (!immediate && (!settling.TryGetValue(h, out var previous) || !Same(now, previous)))
        { settling[h] = now; return; }
        settling.Remove(h);
        // Maximized: only NormalPosition is journaled, and that is still the presented spot,
        // so the offset comes out. A snap (Win+Arrow, Snap Layouts) or any window no longer
        // where StayView put it was placed by someone else: journal that exact rectangle.
        // Only a plain resize of a still-presented window keeps the offset out.
        if(!Native.IsZoomed(h) && (Native.IsWindowArranged(h) || !IsStillPresented(h,now)))
        { Placements.Recapture(h); presentationOffset.Remove(h); presentedAt.Remove(h); }
        else RecaptureUserPlacement(h);
    }
    // The last rectangle MovePresented produced, per window. A window no longer at the
    // spot StayView put it was moved by someone else, so its offset is stale.
    readonly Dictionary<nint,Native.RECT> presentedAt=[];
    bool IsStillPresented(nint h,Native.RECT now)
        => presentationOffset.ContainsKey(h) && presentedAt.TryGetValue(h,out var at) && at.Left==now.Left && at.Top==now.Top;
    static bool Same(Native.RECT a, Native.RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    public bool MoveToDesktop(nint h, Guid id, Native.RECT? dropBounds=null)
    {
        if (!Active || h == 0 || id == Guid.Empty || !Native.IsWindow(h) || IsPinned(h)) return false;
        if(!PlacementStore.TrySnapshot(h,0,Desktops.WindowDesktop(h),out var before))return false;
        if(Placements.Capture(h,0,before.DesktopId))Placements.Persist();
        if (!Desktops.Move(h, id)) return false;
        if(dropBounds is Native.RECT rect)
        {
            // A drag is explicit placement intent; normalize maximized/minimized windows
            // without activation, then place the real window exactly once on release.
            if(Native.IsIconic(h)||Native.IsZoomed(h))Native.ShowWindow(h,4);
            if(Native.IsIconic(h)||!Native.SetWindowPos(h,0,rect.Left,rect.Top,rect.Width,rect.Height,0x14))
            {
                if(before.DesktopId!=Guid.Empty)Desktops.Move(h,before.DesktopId);
                PlacementStore.RestoreOne(before,Desktops);
                Log.Write("Desktop drop placement failed for "+h);
                Reflow();return false;
            }
        }
        // The user explicitly moved this window to another desktop, so that desktop must
        // become part of its persisted session state rather than being undone on Exit().
        Placements.SetDesktop(h, id);
        if(dropBounds!=null){Placements.Recapture(h);tileMoved.Remove(h);userMinimized.Remove(h);}
        presentationOffset.Remove(h); presentedAt.Remove(h);
        docked.Remove(h);
        minimizedDocked.Remove(h);
        minimizedOnEntry.Remove(h);
        positions.Remove(h);
        currentCells.Remove(h);
        arrangeOrder.Remove(h);
        Reflow();
        return true;
    }
    public bool ChangeDesktop(Func<bool> change)
    {
        bool changed=change();
        if(!changed)return false;
        ObserveDesktopChange();
        return true;
    }
    public void PositionTransferredTile(nint h,Native.POINT point)
    {
        if(!Active||!currentCells.TryGetValue(h,out var cell))return;
        var monitor=Native.MonitorFromWindow(h,2);
        double dpi=Native.MonitorScale(monitor);
        var area=Tiler.OverviewArea(Native.WorkArea(monitor),8,dpi,settings.DesktopStripPosition);
        if(settings.AutoArrange)
        {
            var group=ArrangeOrder(catalog.Enumerate().Where(w=>w.Monitor==monitor&&!docked.Contains(w.Handle)).ToList());
            MoveInOrder(group,h,NearestSlot(TaskViewCells(monitor,group),point));
        }
        else positions[h]=DesktopDropGeometry.AtPoint(cell,point,area);
        Reflow();
    }
    // Both our desktop cards and Windows' own switching go through this boundary.
    // Unknown broker results do not erase the last known desktop or create a switch.
    public bool ObserveDesktopChange()
    {
        if(!Active)return false;
        var current=Desktops.Current;
        if(current==Guid.Empty || current==observedDesktop)return false;
        bool changed=observedDesktop!=Guid.Empty;
        if(changed)PreviousDesktop=observedDesktop;
        observedDesktop=current;
        if(changed)DesktopChanged();
        return changed;
    }
    public bool RemoveDesktop(Guid id)
    {
        if(id==Guid.Empty)return false;
        var before=Desktops.Current;
        if(!Desktops.Remove(id))return false;
        // Windows has already migrated windows off the removed desktop. Forget the dead
        // GUID in the journal so exit/crash recovery never tries to move them back there.
        Placements.ForgetDesktop(id);
        // Consumers that own UI for this desktop must tear it down before the reflow below
        // can raise or repaint those windows against a desktop that no longer exists.
        DesktopRemoved?.Invoke(id);
        var after=Desktops.Current;
        bool switched=before==id || (before!=Guid.Empty&&after!=Guid.Empty&&before!=after);
        if(switched){PreviousDesktop=before;observedDesktop=after;DesktopChanged();}
        else if(Active)Reflow();
        return true;
    }
    void DesktopChanged()
    {
        if(!Active)return;
        // Mark only actual desktop switches for delivery animation. Ordinary reflows
        // (window open/close, docking, monitor changes, removing an inactive desktop)
        // remain stable and never replay it.
        DesktopTransition=settings.DesktopTransition;
        // Slide direction: +1 when the new desktop is to the right of the old one.
        var order=Desktops.List().Select(d=>d.Id).ToList();
        int from=order.IndexOf(PreviousDesktop),to=order.IndexOf(observedDesktop);
        DesktopTransitionDirection=from>=0&&to>=0&&to<from?-1:1;
        DesktopTransitionVersion++;
        // Clear browse state (via the subscriber) BEFORE the reflow, so the new desktop's
        // grid renders topmost instead of staying dropped for a window left behind.
        DesktopSwitched?.Invoke();
        Reflow();
    }
    public void Exit()
    {
        if (!Active) return;
        // Journal the focused window's final geometry before it is restored, so an Esc
        // that lands right after a resize still dismisses to the size the user chose.
        SyncUserGeometry(Selected, true);
        Active = false;
        // Docked tiles are the overview's minimized set: whatever is in the dock on the way
        // out is left minimized (after its placement is restored, so it un-minimizes to the
        // right rectangle later), and the next Enter docks it again via DockMinimizedWindows.
        var toMinimize = docked.Where(h => Native.IsWindow(h) && !Native.IsIconic(h)).ToList();
        // Restore behind the still-opaque canvas, then uncover the normal desktop.
        try { Placements.Restore(); foreach (var h in toMinimize) Native.ShowWindowAsync(h, 6); } // SW_MINIMIZE
        finally { draggingTile=0; Leaving?.Invoke(); positions.Clear(); currentCells.Clear(); arrangeOrder.Clear(); docked.Clear(); settling.Clear(); tileMoved.Clear(); minimizedOnEntry.Clear(); minimizedDocked.Clear(); userMinimized.Clear(); restoreAttempts.Clear(); selfMinimizing.Clear(); restoredSinceAttempt.Clear(); presentationOffset.Clear(); presentedAt.Clear(); Selected = 0; }
        if (Native.IsWindow(foreground) && Desktops.IsCurrent(foreground)) Native.SetForegroundWindow(foreground);
    }
    public void Dispose() => Exit();
}

public static class ThumbnailLayout
{
    public static (int W,int H) SizeForSource(Native.RECT source,int longEdge)
    {
        int sw=Math.Max(1,source.Width),sh=Math.Max(1,source.Height);longEdge=Math.Max(1,longEdge);
        if(sw>=sh)return(longEdge,Math.Max(1,(int)Math.Round(longEdge*sh/(double)sw)));
        return(Math.Max(1,(int)Math.Round(longEdge*sw/(double)sh)),longEdge);
    }
    // Left-aligned row of equal live minis. Every docked tile uses the same 16:9
    // rectangle so the dock reads as one consistent strip; the whole row shrinks
    // uniformly when it would otherwise overflow the available lane.
    public static IReadOnlyList<Native.RECT> DockSlots(IReadOnlyList<Native.RECT> sources,Native.RECT region,int gap)
    {
        int n=sources.Count;if(n==0)return [];
        gap=Math.Max(0,gap);
        // Keep the full gap between panels AND at both horizontal ends, but only a small
        // vertical margin so the minis fill most of the (taller) lane height. The row
        // shrinks uniformly to fit; the gap is only reduced if it alone would not fit.
        int vGap=Math.Min(gap,Math.Max(2,region.Height/12));
        int height=Math.Max(1,region.Height-2*vGap);
        int slack=region.Width-gap*(n+1);
        if(slack<n){gap=Math.Max(0,(region.Width-n)/(n+1));slack=region.Width-gap*(n+1);}
        double available=Math.Max(1,slack);
        const double aspect=16d/9d;
        int desiredW=Math.Max(1,(int)Math.Round(height*aspect));
        double fit=Math.Min(1,available/Math.Max(1d,desiredW*n));
        int h=Math.Max(1,(int)Math.Round(height*fit));
        int w=Math.Max(1,(int)Math.Round(h*aspect));
        // Rounding can make the row a few pixels wider than the lane at high counts.
        // Clamp the common width rather than letting the final tile escape the dock.
        int maxCommonW=Math.Max(1,(region.Width-gap*(n+1))/Math.Max(1,n));
        if(w>maxCommonW){w=maxCommonW;h=Math.Max(1,(int)Math.Round(w/aspect));}
        int x=region.Left+gap;
        var result=new List<Native.RECT>(n);
        for(int i=0;i<n;i++){result.Add(new(x,region.Top+(region.Height-h)/2,w,h));x+=w+gap;}
        return result;
    }
    // One dock lane's worth of slots. Callers pass the busier side's count for both
    // lanes, so a docked mini is exactly the same size whichever lane it lands on.
    // packRight mirrors the row within the lane, which is what makes a left-hand lane
    // pack toward the desktop cards instead of away from them.
    public static IReadOnlyList<Native.RECT> DockLaneSlots(int count,Native.RECT lane,int gap,bool packRight)
    {
        if(count<=0)return [];
        var slots=DockSlots(Enumerable.Repeat(new Native.RECT(0,0,16,9),count).ToList(),lane,gap);
        if(!packRight)return slots;
        var flipped=new List<Native.RECT>(slots.Count);
        foreach(var slot in slots)flipped.Add(new(lane.Left+(lane.Right-slot.Right),slot.Top,slot.Width,slot.Height));
        return flipped;
    }
    public static Native.RECT Clamp(Native.RECT r, Native.RECT area)
    {
        int w = Math.Min(r.Width, area.Width), h = Math.Min(r.Height, area.Height);
        return new(Math.Clamp(r.Left, area.Left, area.Right - w), Math.Clamp(r.Top, area.Top, area.Bottom - h), w, h);
    }
}
