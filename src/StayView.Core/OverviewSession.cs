namespace StayView.Core;

// A Task View that stays open. Tiles are DWM thumbnails; clicking one brings the real
// window to the front at its true size (the overlay drops out of the way and is
// re-summoned by the hotkey). Automatic layout never rewrites real window placement;
// an explicit miniature drag is the exception because the user intends that gesture to
// change where the real desktop window will live.
public sealed class OverviewSession : IDisposable
{
    public readonly PlacementStore Placements = new();
    readonly WindowCatalog catalog;
    readonly Settings settings;
    public readonly VirtualDesktopService Desktops;
    readonly Dictionary<nint, Native.RECT> positions = [];
    readonly Dictionary<nint, Native.RECT> currentCells = [];
    readonly Dictionary<nint, int> gridAssignments = [];
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
    // Windows explicitly minimized by the user while the overview is already open stay
    // genuinely minimized. Restoring them only to feed DWM caused the real HWND to paint
    // for a frame after the native minimize animation before it was lowered again.
    readonly HashSet<nint> userMinimized = [];
    nint draggingTile;
    nint foreground;
    int layoutSmallWindowSize;
    public nint Selected { get; private set; }
    public bool Active { get; private set; }
    public bool DragActive => draggingTile != 0;
    public IReadOnlyList<nint> DockedSources => docked;
    public bool IsDocked(nint h) => docked.Contains(h);
    public int DesktopTransitionVersion { get; private set; }
    public DesktopTransitionMode DesktopTransition { get; private set; } = DesktopTransitionMode.Appear;
    public event Action<IReadOnlyDictionary<nint, IReadOnlyList<Tile>>>? LayoutChanged;
    public event Action? Leaving;
    public event Action<Guid>? DesktopRemoved;
    // Raised when the current virtual desktop actually changed while the overview is up,
    // before the grid re-renders. TrayApp uses it to leave browse mode: the browsed
    // window is on the old desktop, so the new desktop must show its grid, not stay
    // dropped behind windows for a window that is no longer here.
    public event Action? DesktopSwitched;
    public OverviewSession(WindowCatalog catalog, VirtualDesktopService desktops, Settings settings)
    { this.catalog = catalog; Desktops = desktops; this.settings = settings; layoutSmallWindowSize=settings.SmallWindowSize; }

    public void Toggle() { if (Active) Exit(); else Enter(); }
    public void Enter()
    {
        if (Active) return;
        foreground = Native.GetForegroundWindow();
        Selected = foreground;
        Active = true;
        // Must run before the first Reflow, which restores minimized sources.
        minimizedOnEntry.Clear();
        userMinimized.Clear();
        foreach (var w in catalog.Enumerate())
            if (w.Minimized || Native.IsIconic(w.Handle)) minimizedOnEntry.Add(w.Handle);
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
        foreach(var h in gridAssignments.Keys.Where(h=>!Native.IsWindow(h)).ToList())gridAssignments.Remove(h);
        foreach(var h in positions.Keys.Where(h=>!Native.IsWindow(h)).ToList())positions.Remove(h);
        docked.RemoveAll(h=>!Native.IsWindow(h));
        var output = new Dictionary<nint, IReadOnlyList<Tile>>();
        currentCells.Clear();
        foreach (var m in Native.Monitors())
        {
            var group = windows.Where(w => w.Monitor == m && !docked.Contains(w.Handle)).ToList();
            var work = Native.WorkArea(m);
            double dpi = Native.MonitorScale(m);
            var area = Tiler.OverviewArea(work,28,dpi,settings.DesktopStripPosition);
            var sourceRects=new List<Native.RECT>(group.Count);
            foreach(var w in group)
            {
                if(Placements.Entries.TryGetValue(w.Handle,out var savedPlacement))sourceRects.Add(savedPlacement.Placement.NormalPosition);
                else if(Native.GetWindowRect(w.Handle,out var source))sourceRects.Add(source);
                else sourceRects.Add(new Native.RECT(0,0,16,10));
            }
            int longEdge=Math.Max(1,(int)Math.Round(settings.SmallWindowSize*dpi));
            // Uniform tiles: every satellite is sized to one common box so the grid is
            // even. All sizing uses a single shared aspect (the group's median, clamped),
            // instead of each window's own aspect. Disable KeepMinisSameSize to fall back
            // to per-window aspect-preserving sizes.
            var sizingRects=sourceRects;
            if(settings.KeepMinisSameSize && sourceRects.Count>0)
            {
                var aspects=sourceRects.Select(s=>Math.Max(1,s.Width)/(double)Math.Max(1,s.Height)).OrderBy(a=>a).ToList();
                double commonAspect=Math.Clamp(aspects[aspects.Count/2],.6,2.2);
                var common=new Native.RECT(0,0,(int)Math.Round(commonAspect*1000),1000);
                sizingRects=Enumerable.Repeat(common,sourceRects.Count).ToList();
            }
            // The desktop strip is a hard no-drop zone. Manual positions are always
            // clamped to the legal canvas below it (or above it when the bar is at
            // the bottom), and never to the full work area.
            var canvasArea = Tiler.OverviewArea(work,8,dpi,settings.DesktopStripPosition);
            // Task-View grid: keep a finger-width visual channel between panels on the
            // initial/opening layout. Use DIPs so the gap remains visually consistent on
            // high-DPI monitors. User-dragged tiles keep their explicitly chosen position.
            int panelGap=Math.Max(1,(int)Math.Round(28*dpi));
            var slots = settings.AutoArrange ? null : ThumbnailLayout.ArrangeSources(sizingRects, canvasArea, panelGap, longEdge);
            var gridCells=settings.AutoArrange?ThumbnailLayout.GridCells(area,settings.AutoArrangeGrid,group.Count,panelGap):null;
            if(gridCells!=null)EnsureGridAssignments(group,gridCells.Count);
            var tiles = new List<Tile>();
            var occupied = group.Where(w=>positions.ContainsKey(w.Handle))
                .Select(w=>ThumbnailLayout.Clamp(positions[w.Handle],canvasArea)).ToList();
            bool initialLayout=occupied.Count==0;
            for (int i = 0; i < group.Count; i++)
            {
                var w = group[i];
                Native.RECT cell;
                if(settings.AutoArrange&&gridCells!=null)
                {
                    int slot=gridAssignments[w.Handle];
                    cell=ThumbnailLayout.FitInCell(sizingRects[i],gridCells[slot],longEdge);
                }
                else if(positions.TryGetValue(w.Handle,out var saved))
                {
                    cell=ThumbnailLayout.Clamp(saved,canvasArea);
                    positions[w.Handle]=cell;
                }
                else
                {
                    cell=initialLayout ? slots![i] : StableTileLayout.PlaceNew(slots![i],occupied,canvasArea,panelGap);
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
        // Some packaged/WinUI apps remain in a minimized-style presentation when asked
        // to SW_SHOWNOACTIVATE. Force a true normal restore using the placement journal's
        // normal rectangle so the DWM tile represents the real standard-sized app.
        if (Placements.Entries.TryGetValue(h, out var saved))
        {
            var p = saved.Placement;
            p.Length = System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>();
            p.ShowCmd = 1; // SW_SHOWNORMAL
            p.NormalPosition = saved.Placement.NormalPosition;
            Native.SetWindowPlacement(h, ref p);
            // WINDOWPLACEMENT.NormalPosition is workspace-relative on some window styles;
            // SetWindowPos expects screen coordinates. Let SetWindowPlacement perform the
            // restore, then only change z-order so we never reinterpret its coordinates.
            Native.SetWindowPos(h, 1, 0, 0, 0, 0, 0x13 | 0x4000); // HWND_BOTTOM + NOMOVE|NOSIZE|NOACTIVATE + ASYNC
        }
        else
        {
            Native.ShowWindowAsync(h, 9); // SW_RESTORE fallback when no journal entry exists.
            Native.SetWindowPos(h, 1, 0, 0, 0, 0, 0x13 | 0x4000);
        }
    }
    void RestoreMinimizedSourcesForOverview(IEnumerable<AppWindow> windows)
    {
        foreach (var w in windows)
            if (w.Minimized || Native.IsIconic(w.Handle)) RestoreMinimizedSourceForOverview(w.Handle);
    }
    public void BeginTileDrag(nint h)
    {
        if(Active && h != 0) draggingTile = h;
    }
    public void EndTileDrag(nint h = 0)
    {
        if(h == 0 || draggingTile == h) draggingTile = 0;
    }
    // Docking is canvas state only: the source HWND is never moved or minimised.
    public bool DockTile(nint h)
    {
        if(!Active||h==0||draggingTile!=0||!Native.IsWindow(h))return false;
        // Manual layout: pin every current cell (this tile's too) so docking/undocking
        // never repacks untouched tiles and an undocked tile returns to its old spot.
        if(!settings.AutoArrange)foreach(var pair in currentCells)positions.TryAdd(pair.Key,pair.Value);
        if(!docked.Contains(h))docked.Add(h);
        gridAssignments.Remove(h);currentCells.Remove(h);
        Reflow();
        return true;
    }
    public bool UndockTile(nint h)
    {
        if(!Active||!docked.Contains(h)||!Native.IsWindow(h))return false;
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
        // The user put this one back on the canvas themselves; a later settings change
        // must not silently re-dock it just because it opened minimized.
        minimizedOnEntry.Remove(h);
        Reflow();
        return true;
    }
    public void DropTile(nint h,Native.RECT rect,Native.RECT original)
    {
        MoveTile(h,rect);
        Native.RECT final=rect;
        if(settings.AutoArrange&&Active)
        {
            var probe=rect;
            var monitor=Native.MonitorFromRect(ref probe,2);
            if(monitor==0)monitor=Native.MonitorFromWindow(h,2);
            var group=catalog.Enumerate().Where(w=>w.Monitor==monitor&&!docked.Contains(w.Handle)).ToList();
            if(group.All(w=>w.Handle!=h)){Reflow();return;}
            var work=Native.WorkArea(monitor);double dpi=Native.MonitorScale(monitor);
            var area=Tiler.OverviewArea(work,28,dpi,settings.DesktopStripPosition);
            int panelGap=Math.Max(1,(int)Math.Round(28*dpi));
            var cells=ThumbnailLayout.GridCells(area,settings.AutoArrangeGrid,group.Count,panelGap);
            EnsureGridAssignments(group,cells.Count);
            int nearest=Enumerable.Range(0,cells.Count)
                .OrderBy(i=>DistanceSquared(Center(rect),Center(cells[i]))).First();
            int old=gridAssignments[h];
            var occupant=group.FirstOrDefault(w=>w.Handle!=h&&gridAssignments.TryGetValue(w.Handle,out var slot)&&slot==nearest);
            if(occupant!=null)gridAssignments[occupant.Handle]=old;
            gridAssignments[h]=nearest;
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
    // Docks (or releases) the windows that were already minimized when this session
    // opened, to match the current setting. Canvas state only, like every other dock
    // action: the source HWND is never moved or minimised by it. Callers reflow.
    public void ApplyDockMinimizedSetting()
    {
        foreach (var h in minimizedOnEntry.ToList())
        {
            if (!Native.IsWindow(h)) { minimizedOnEntry.Remove(h); continue; }
            if (settings.DockMinimizedWindows) { if (!docked.Contains(h)) docked.Add(h); }
            else docked.Remove(h);
        }
    }
    public void ApplyAutoArrangeSetting()
    {
        if(settings.AutoArrange){positions.Clear();gridAssignments.Clear();}
        else
        {
            positions.Clear();
            foreach(var pair in currentCells)positions[pair.Key]=pair.Value;
            gridAssignments.Clear();
        }
    }
    public void ApplySmallWindowSizeSetting()
    {
        double ratio=settings.SmallWindowSize/(double)Math.Max(1,layoutSmallWindowSize);
        foreach(var (h,r) in positions.ToList())
            positions[h]=new(r.Left,r.Top,Math.Max(1,(int)Math.Round(r.Width*ratio)),Math.Max(1,(int)Math.Round(r.Height*ratio)));
        layoutSmallWindowSize=settings.SmallWindowSize;
    }
    void EnsureGridAssignments(IReadOnlyList<AppWindow> group,int slotCount)
    {
        var used=new HashSet<int>();
        foreach(var w in group)
        {
            if(gridAssignments.TryGetValue(w.Handle,out var slot)&&slot>=0&&slot<slotCount&&used.Add(slot))continue;
            gridAssignments.Remove(w.Handle);
        }
        foreach(var w in group.Where(w=>!gridAssignments.ContainsKey(w.Handle)))
        {
            int slot=Enumerable.Range(0,slotCount).First(i=>!used.Contains(i));
            gridAssignments[w.Handle]=slot;used.Add(slot);
        }
    }
    static Native.POINT Center(Native.RECT r)=>new(){X=r.Left+r.Width/2,Y=r.Top+r.Height/2};
    static long DistanceSquared(Native.POINT a,Native.POINT b){long dx=a.X-b.X,dy=a.Y-b.Y;return dx*dx+dy*dy;}
    // The Task-View action: bring the real window to the front at its true size and
    // focus it. The overlay's own topmost state is dropped by the caller (OverlayChrome
    // / TrayApp) so this window can actually sit in front of the acrylic.
    public bool Activate(nint h)
    {
        if (!Active || !Native.IsWindow(h)) return false;
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
            Native.PostMessage(h, 0x112, (nint)0xF120, 0); // WM_SYSCOMMAND / SC_RESTORE
            Native.ShowWindowAsync(h, 9);                 // conventional fallback
        }
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
        if(userIsRestoring)
        {
            Placements.Recapture(h);
            minimizedOnEntry.Remove(h);
            docked.Remove(h);
        }
        userMinimized.Remove(h);
        Selected = h;
        return true;
    }
    public void Close(nint h) { if (Active) Native.PostMessage(h, 0x10, 0, 0); }
    // The user dragged a real window while browsing; keep that placement on dismissal.
    public void NoteUserMoved(nint h) { if (Active) { Placements.Recapture(h); tileMoved.Remove(h); } }
    // Apply a miniature-drag placement while the overview is still covering the source.
    // This is called before focus animation (and again by Activate for minimized sources
    // that finish restoring between retries), so a plain focus click never relocates a
    // window: only a prior miniature drag can change this target.
    public void PrepareActivationGeometry(nint h)
    {
        if(!Active||!tileMoved.Contains(h)||!Native.IsWindow(h)
            ||!Placements.Entries.TryGetValue(h,out var saved))return;
        if(Native.IsIconic(h)||Native.IsZoomed(h))
        {
            var p=Native.Placement(h);
            p.Length=System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>();
            p.NormalPosition=saved.Placement.NormalPosition;
            Native.SetWindowPlacement(h,ref p);
            return;
        }
        // Position only: a tile drag must never resize the real window. Do this under the
        // opaque overview before its DWM miniature starts expanding, so the animation's
        // endpoint is the exact visible frame that will take over.
        if(!Native.SetWindowPos(h,0,saved.Bounds.Left,saved.Bounds.Top,0,0,0x15))
            Log.Write($"Tile placement move failed for {h}: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }
    public void NoteUserMinimized(nint h)
    {
        if(!Active || !Native.IsIconic(h))return;
        Placements.MarkMinimized(h);
        userMinimized.Add(h);
        // A minimize performed while the overview is already active is a return-to-desktop
        // action, not a dock action. Keep the source on the desktop/grid so ReconcileBrowse
        // can re-summon the overview with its tile in the normal canvas. The dock-minimized
        // setting applies only to windows that were already minimized when the session opened.
        docked.Remove(h);
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
        if (!Active || h == 0 || !Native.IsWindow(h) || Native.IsIconic(h)) { settling.Remove(h); return; }
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
        Placements.Recapture(h);
    }
    static bool Same(Native.RECT a, Native.RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    public bool MoveToDesktop(nint h, Guid id)
    {
        if (!Active || h == 0 || id == Guid.Empty || !Native.IsWindow(h)) return false;
        if (!Desktops.Move(h, id)) return false;
        // The user explicitly moved this window to another desktop, so that desktop must
        // become part of its persisted session state rather than being undone on Exit().
        Placements.SetDesktop(h, id);
        docked.Remove(h);
        minimizedOnEntry.Remove(h);
        positions.Remove(h);
        currentCells.Remove(h);
        gridAssignments.Remove(h);
        Reflow();
        return true;
    }
    public bool ChangeDesktop(Func<bool> change)
    {
        bool changed=change();
        if(!changed)return false;
        DesktopChanged();
        return true;
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
        if(switched)DesktopChanged();
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
        finally { draggingTile=0; Leaving?.Invoke(); positions.Clear(); currentCells.Clear(); gridAssignments.Clear(); docked.Clear(); settling.Clear(); tileMoved.Clear(); minimizedOnEntry.Clear(); userMinimized.Clear(); Selected = 0; }
        if (Native.IsWindow(foreground) && Desktops.IsCurrent(foreground)) Native.SetForegroundWindow(foreground);
    }
    public void Dispose() => Exit();
}

public static class ThumbnailLayout
{
    public static IReadOnlyList<Native.RECT> GridCells(Native.RECT area,int grid,int count,int gap)
    {
        if(count<=0)return [];
        grid=grid is 2 or 4 or 5?grid:4;gap=Math.Max(0,gap);
        int rows=Math.Max(grid,(count+grid-1)/grid);
        gap=Math.Min(gap,Math.Max(0,Math.Min(area.Width/Math.Max(1,grid*3),area.Height/Math.Max(1,rows*3))));
        int width=Math.Max(1,(area.Width-gap*(grid-1))/grid);
        int height=Math.Max(1,(area.Height-gap*(rows-1))/rows);
        var result=new List<Native.RECT>(grid*rows);
        for(int row=0;row<rows;row++)for(int col=0;col<grid;col++)
            result.Add(new Native.RECT(area.Left+col*(width+gap),area.Top+row*(height+gap),width,height));
        return result;
    }
    public static Native.RECT FitInCell(Native.RECT source,Native.RECT cell,int preferredLongEdge)
    {
        var (w,h)=SizeForSource(source,preferredLongEdge);
        double fit=Math.Min(1,Math.Min(cell.Width/(double)Math.Max(1,w),cell.Height/(double)Math.Max(1,h)));
        w=Math.Max(1,(int)Math.Round(w*fit));h=Math.Max(1,(int)Math.Round(h*fit));
        return new(cell.Left+(cell.Width-w)/2,cell.Top+(cell.Height-h)/2,w,h);
    }
    public static (int W,int H) SizeForSource(Native.RECT source,int longEdge)
    {
        int sw=Math.Max(1,source.Width),sh=Math.Max(1,source.Height);longEdge=Math.Max(1,longEdge);
        if(sw>=sh)return(longEdge,Math.Max(1,(int)Math.Round(longEdge*sh/(double)sw)));
        return(Math.Max(1,(int)Math.Round(longEdge*sw/(double)sh)),longEdge);
    }
    public static IReadOnlyList<Native.RECT> ArrangeSources(IReadOnlyList<Native.RECT> sources,Native.RECT area,int gap,int preferredLongEdge)
    {
        if(sources.Count==0)return [];
        gap=Math.Max(0,gap);preferredLongEdge=Math.Max(1,preferredLongEdge);
        double scale=1;
        for(int attempt=0;attempt<24;attempt++)
        {
            var sizes=sources.Select(s=>SizeForSource(s,Math.Max(1,(int)Math.Round(preferredLongEdge*scale)))).ToList();
            var rows=new List<List<(int Index,int W,int H)>>();
            var row=new List<(int,int,int)>();int rowWidth=0;
            for(int i=0;i<sizes.Count;i++)
            {
                var (w,h)=sizes[i];
                int next=row.Count==0?w:rowWidth+gap+w;
                if(row.Count>0&&next>area.Width){rows.Add(row);row=[];rowWidth=0;}
                row.Add((i,w,h));rowWidth=row.Count==1?w:rowWidth+gap+w;
            }
            if(row.Count>0)rows.Add(row);
            int totalHeight=rows.Sum(r=>r.Max(x=>x.H))+gap*Math.Max(0,rows.Count-1);
            if(totalHeight<=area.Height && rows.All(r=>r.Sum(x=>x.W)+gap*Math.Max(0,r.Count-1)<=area.Width))
            {
                var result=new Native.RECT[sources.Count];
                int y=area.Top+Math.Max(0,(area.Height-totalHeight)/2);
                foreach(var r in rows)
                {
                    int rowW=r.Sum(x=>x.W)+gap*Math.Max(0,r.Count-1);
                    int rowH=r.Max(x=>x.H),x=area.Left+Math.Max(0,(area.Width-rowW)/2);
                    foreach(var item in r){result[item.Index]=new(x,y+(rowH-item.H)/2,item.W,item.H);x+=item.W+gap;}
                    y+=rowH+gap;
                }
                return result;
            }
            scale*=Math.Clamp(area.Height/(double)Math.Max(1,totalHeight),.55,.92);
        }
        var fallback=Arrange(sources.Count,area,gap,preferredLongEdge,preferredLongEdge);
        return sources.Select((source,i)=>FitInCell(source,fallback[i],preferredLongEdge)).ToList();
    }
    public static IReadOnlyList<Native.RECT> Arrange(int count, Native.RECT area, int gap, int maxWidth, int maxHeight)
    {
        if (count <= 0) return [];
        int columns = Math.Min(count, Math.Max(1, (area.Width + gap) / (maxWidth + gap)));
        int rows = (count + columns - 1) / columns;
        // Shrink thumbnails, never their source windows, to keep the whole set visible.
        while (rows * 90 + (rows - 1) * gap > area.Height && columns < count)
        { columns++; rows = (count + columns - 1) / columns; }
        gap = Math.Min(gap, Math.Max(0, Math.Min(area.Width / Math.Max(1, columns * 3), area.Height / Math.Max(1, rows * 3))));
        int width = Math.Max(1, Math.Min(maxWidth, (area.Width - (columns - 1) * gap) / columns));
        int height = Math.Max(1, Math.Min(maxHeight, (area.Height - (rows - 1) * gap) / rows));
        var result = new List<Native.RECT>();
        for (int i = 0; i < count; i++)
        {
            int row = i / columns, col = i % columns, inRow = Math.Min(columns, count - row * columns);
            int x = area.Left + (area.Width - inRow * width - (inRow - 1) * gap) / 2 + col * (width + gap);
            result.Add(new(x, area.Top + row * (height + gap), width, height));
        }
        return result;
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
