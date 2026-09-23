using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using StayView.Core;
namespace StayView;
sealed class TrayApp
{
    readonly App app;
    readonly Settings settings = Settings.Load();
    readonly VirtualDesktopService desktops = new();
    readonly WindowCatalog catalog;
    readonly OverviewSession session;
    readonly OverlayChrome primary;
    readonly Dictionary<nint, OverlayChrome> overlays = [];
    readonly InputHooks input;
    readonly EmptySpaceProbe emptySpace = new();
    readonly TrayIcon tray;
    readonly DispatcherQueue queue;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    string signature = "";
    bool stopped;
    // While browsing, a clicked window sits in front of the overview. The overlays have
    // dropped topmost; the timer and foreground events must not yank them back on top.
    bool browsing;
    readonly HashSet<nint> nativeGestures = [];
    bool tileDragInteraction;
    bool refrontAfterCanvasGesture;
    readonly Dictionary<nint,Native.RECT> nativeGestureOrigins = [];
    readonly HashSet<nint> pendingBrowseGeometry = [];
    int browseGeometryQueued;
    // Windows currently going through a user-initiated native minimize while browsed.
    // MINIMIZESTART is early enough to put StayView's DWM shrink in front of the shell's
    // taskbar animation; MINIMIZEEND then journals the real iconic state.
    readonly HashSet<nint> overviewMinimizing = [];
    Dictionary<nint,Native.RECT> WindowBounds(IEnumerable<nint> windows)
    {
        var bounds=new Dictionary<nint,Native.RECT>();
        foreach(var h in windows)
        {
            if(!Native.IsWindow(h))continue;
            // Animate to the visible DWM frame, not USER32's larger invisible resize
            // rectangle. Otherwise the miniature reaches one rectangle and the real
            // focused window appears a few pixels away, which reads as a final jump.
            if(!Native.IsIconic(h) && Native.TryGetVisualBounds(h,out var r))
            {
                var monitor=Native.MonitorFromWindow(h,2);
                bounds[h]=monitor==0?r:FocusedWindowGeometry.ClampTargetTop(r,Native.WorkArea(monitor));
                continue;
            }
            // Once Windows has completed a minimize, GetWindowRect is no longer the real
            // on-screen rectangle. The placement journal deliberately retains the last
            // visible bounds, so use those as the animation origin. This lets the DWM
            // miniature shrink from the window's former focused position without ever
            // restoring/flashing the real HWND just to obtain geometry.
            if(session.Placements.Entries.TryGetValue(h,out var saved))
            {
                var savedBounds=saved.Bounds; var monitor=Native.MonitorFromWindow(h,2);
                bounds[h]=monitor==0?savedBounds:FocusedWindowGeometry.ClampTargetTop(savedBounds,Native.WorkArea(monitor));
            }
        }
        return bounds;
    }
    void CancelAnimations() { foreach(var chrome in overlays.Values) chrome.CancelWindowAnimation(); }
    void CommitAnimations() { foreach(var chrome in overlays.Values) chrome.CommitWindowAnimation(); }
    Task AnimateWindowsAsync(IReadOnlyDictionary<nint,Native.RECT> bounds,bool expanding)
        => Task.WhenAll(overlays.Values.Select(c=>c.AnimateWindowsAsync(bounds,expanding)));
    // The windows currently browsed in front of the grid, primary (session.Selected /
    // foreground) first, at most two. A third promotion demotes the oldest back under the
    // canvas so its tile reappears.
    readonly List<nint> browsed = new();
    // TileOnly: pinned from its tile (right-hold on a window that is not focused). The tile
    // stays on the canvas, locked and marked; the real window is not brought in front until
    // the user clicks the tile, which turns it into an ordinary (in-front) pin.
    sealed record PinState(Guid HomeDesktop,Native.RECT Bounds,bool Maximized,bool SystemPinned,bool OwnsSystemPin,bool TileOnly=false);
    readonly Dictionary<nint,PinState> pinnedWindows = [];
    readonly Dictionary<Guid,Dictionary<nint,DesktopWindowState>> desktopStates = [];
    bool preserveWindowState;
    readonly HashSet<nint> pinRestoring = [];
    readonly Dictionary<nint,(int Count,long Since)> pinSnapBacks = [];
    readonly HashSet<nint> pinBuriedLogged = [];
    long pinBuriedReset;
    int MaxBrowsed => Math.Clamp(settings.MaxBrowsedWindows, Settings.MaxBrowsedWindowsMin, Settings.MaxBrowsedWindowsMax);
    // IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop is observed to flap false for
    // groups of otherwise-live windows during shell transitions. WindowCatalog already
    // avoids trusting that boolean when desktop IDs are available; browse eligibility must
    // use the same stable rule or a focused window can be dropped spuriously mid-click/move.
    bool IsCurrentDesktopStable(nint h)
    {
        try
        {
            var current=desktops.Current;
            if(current==Guid.Empty)return true; // unknown current desktop: never wipe browse state
            var id=desktops.WindowDesktop(h);
            return id==Guid.Empty?desktops.IsCurrent(h):id==current;
        }
        catch{return true;}
    }
    bool CanBrowse(nint h) => Native.IsWindow(h) && Native.IsWindowVisible(h) && !Native.IsIconic(h) && (IsPinned(h)||IsCurrentDesktopStable(h));
    bool IsPinned(nint h)=>pinnedWindows.ContainsKey(h);
    bool IsStayViewWindow(nint h)
    {
        if(h==0)return false;
        var root=Native.GetAncestor(h,2); if(root==0)root=h;
        if(root==primary.Handle || root==popout?.Handle || root==optionsWindow?.Handle || overlays.Values.Any(c=>c.Handle==root))return true;
        // WinUI delivers hits to a child/input HWND, not the Window handle we stored.
        Native.GetWindowThreadProcessId(root,out var pid);
        return pid==(uint)Environment.ProcessId;
    }
    nint PinTargetAt(Native.POINT p)
    {
        var hit=primary.HitSource(p);
        if(hit!=0)return hit;
        foreach(var chrome in overlays.Values)
            if(chrome!=primary && (hit=chrome.HitSource(p))!=0)return hit;
        return 0;
    }
    HashSet<nint> PinnedSet()=>pinnedWindows.Keys.ToHashSet();
    // Foreground-churn breaker. Every foreground change re-raises the overview and all pins;
    // with several pins (or an app that reacts to losing topmost) those raises can feed
    // back into more foreground changes, flashing windows in a loop. A burst of changes
    // pauses that automatic z-order work for a second so it settles; the 500 ms timer
    // keeps reconciling meanwhile. Each burst is logged as [fg-churn] for diagnosis.
    readonly Queue<long> foregroundTimes=new();
    long churnUntil;
    int churnLogged;
    bool ForegroundChurning()
    {
        long now=Environment.TickCount64;
        foregroundTimes.Enqueue(now);
        while(foregroundTimes.Count>0&&now-foregroundTimes.Peek()>1500)foregroundTimes.Dequeue();
        if(foregroundTimes.Count>=12)
        {
            if(now>=churnUntil)churnLogged=0;
            churnUntil=now+1000;
        }
        if(now>=churnUntil)return false;
        if(churnLogged++<60)
        {
            var fg=Native.GetForegroundWindow();
            Log.Write($"[fg-churn] n={foregroundTimes.Count} fg={fg} '{Native.Title(fg)}' class={Native.Class(fg)} browsing={browsing} activating={activating} browsed={string.Join(",",browsed)} pins={string.Join(",",pinnedWindows.Keys)} drag={session.DragActive}");
        }
        return true;
    }
    // Pins shown in front of the overview (tile-only pins stay tiles on the canvas).
    IReadOnlyList<nint> CurrentPinned()
    {
        return pinnedWindows.Where(pair=>!pair.Value.TileOnly&&Native.IsWindow(pair.Key)&&Native.IsWindowVisible(pair.Key)&&!Native.IsIconic(pair.Key)&&!session.IsMinimizedDocked(pair.Key))
            .Select(pair=>pair.Key).ToList();
    }
    void PublishBrowsed()
    {
        var pins=PinnedSet();
        foreach(var chrome in overlays.Values)chrome.SetBrowsed(browsed,pins);
    }
    void TogglePin(nint h)
    {
        if(!session.Active||h==0||!Native.IsWindow(h))return;
        if(IsPinned(h))
        {
            if(!ReleasePin(h,true))return;
            Log.Write($"[pin] released {h}");
            if(CanBrowse(h)&&browsed.Contains(h))Promote(h);else {PublishBrowsed();session.Reflow();}
            return;
        }
        bool minimized=session.IsMinimizedDocked(h)||Native.IsIconic(h);
        // A right-hold on an ordinary grid tile pins that window too (2026-09-22): it is
        // brought in front at real size exactly like a focused pin, without a prior click.
        // Any tile or other non-focused window the user holds can be pinned. The focused
        // window still has to be the one they are actually browsing.
        bool fromTile=!browsed.Contains(h);
        if(!fromTile&&!PinnedWindowPolicy.CanPin(minimized,browsing,browsed.Contains(h),CanBrowse(h))){Log.Write($"[pin] refused {h}: minimized={minimized} browsing={browsing} browsed={browsed.Contains(h)} canBrowse={CanBrowse(h)}");return;}
        if(!TryPinGeometry(h,out var bounds,out var maximized)){Log.Write($"[pin] refused {h}: no geometry");return;}
        if(!minimized)session.NoteUserMoved(h);
        var desktop=desktops.WindowDesktop(h);if(desktop==Guid.Empty)desktop=desktops.Current;
        bool alreadySystemPinned=settings.NativeDesktopPin&&desktops.IsWindowPinned(h);
        bool systemPinned=settings.NativeDesktopPin&&(alreadySystemPinned||desktops.PinWindow(h));
        pinnedWindows[h]=new(desktop,bounds,maximized,systemPinned,systemPinned&&!alreadySystemPinned,TileOnly:fromTile&&!minimized);
        Log.Write($"[pin] pinned {h} at {bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}; all-desktops={systemPinned}");
        if(minimized){PublishBrowsed();session.Reflow();}
        else if(fromTile)
        {
            // Pinned in place as a tile: marked and locked on the canvas, the real window is
            // NOT brought in front or focused. Clicking the tile later turns it into an
            // in-front pin (ShowTilePin).
            PublishBrowsed();
            session.Reflow();
            Log.Write($"[pin] tile pinned in place {h}");
        }
        else
        {
            Promote(h);
            GivePinnedKeyboardFocus(h);
        }
    }
    // Clicking a tile-only pin brings its real window in front as an ordinary pin.
    void ShowTilePin(nint h)
    {
        if(!pinnedWindows.TryGetValue(h,out var pin)||!pin.TileOnly)return;
        pinnedWindows[h]=pin with {TileOnly=false};
        KeepPinClearOfBar(h);
        Log.Write($"[pin] tile pin brought in front {h}");
    }
    // The real window of a tile pin appears at its real position; if that lies under the
    // desktop bar it would seem to jump up into the bar. Nudge it (same size) into the
    // canvas area first, so the pin is locked somewhere it is actually usable.
    void KeepPinClearOfBar(nint h)
    {
        if(!Native.GetWindowRect(h,out var r)||Native.IsZoomed(h)||Native.IsIconic(h))return;
        var monitor=Native.MonitorFromWindow(h,2);
        var canvas=Tiler.OverviewArea(Native.WorkArea(monitor),8,Native.MonitorScale(monitor),settings.DesktopStripPosition);
        if(r.Height>canvas.Height)return;
        int top=settings.DesktopStripPosition==DesktopStripPosition.Bottom
            ? Math.Min(r.Top,canvas.Bottom-r.Height)
            : Math.Max(r.Top,canvas.Top);
        if(top==r.Top)return;
        Native.SetWindowPos(h,0,r.Left,top,r.Width,r.Height,0x14);
        if(pinnedWindows.TryGetValue(h,out var pin))pinnedWindows[h]=pin with {Bounds=new Native.RECT(r.Left,top,r.Width,r.Height)};
        Log.Write($"[pin] moved {h} clear of the desktop bar to {r.Left},{top}");
    }
    // Z-order alone is not keyboard focus. A pinned window the user is working in has to
    // become the foreground window, or keystrokes stay in the overview (or the previously
    // focused app) and the pin looks interactive while refusing text.
    void GivePinnedKeyboardFocus(nint h)
    {
        if(!session.Active||!IsPinned(h)||!Native.IsWindow(h)||Native.IsIconic(h)||session.IsMinimizedDocked(h))return;
        if(pinnedWindows[h].TileOnly)return;   // a tile pin stays a tile until clicked
        foreach(var chrome in overlays.Values)chrome.DropTopmost();
        if(!session.Activate(h))
            Log.Write($"[pin] keyboard focus was not taken by {h}; fg={Native.GetForegroundWindow()}");
        if(pinnedWindows.TryGetValue(h,out var pin)&&Native.GetWindowRect(h,out var now)&&now.Width>0&&now.Height>0)
            pinnedWindows[h]=pin with {Bounds=now,Maximized=Native.IsZoomed(h)};
        foreach(var chrome in overlays.Values)chrome.ReassertBrowseZOrder();
        RaisePinsAboveOthers();
    }
    bool TryPinGeometry(nint h,out Native.RECT bounds,out bool maximized)
    {
        maximized=Native.IsZoomed(h);
        if(!Native.IsIconic(h)&&!session.IsMinimizedDocked(h)&&Native.GetWindowRect(h,out bounds))return bounds.Width>0&&bounds.Height>0;
        if(session.Placements.Entries.TryGetValue(h,out var saved))
        {
            bounds=saved.Bounds;
            var probe=bounds;
            if(bounds.Width<=0||bounds.Height<=0||Native.MonitorFromRect(ref probe,0)==0)bounds=saved.Placement.NormalPosition;
            maximized=saved.Placement.ShowCmd==3;
            return bounds.Width>0&&bounds.Height>0;
        }
        bounds=Native.Placement(h).NormalPosition;
        return bounds.Width>0&&bounds.Height>0;
    }
    bool ReleasePin(nint h,bool keepOnCurrent=false)
    {
        pinSnapBacks.Remove(h);
        if(!pinnedWindows.TryGetValue(h,out var pin))return true;
        if(pin.OwnsSystemPin&&Native.IsWindow(h)&&!desktops.UnpinWindow(h))
        {Log.Write($"[pin] could not release Windows desktop pin for {h}");return false;}
        pinnedWindows.Remove(h);pinRestoring.Remove(h);
        if(Native.IsWindow(h))Native.SetWindowPos(h,(nint)(-2),0,0,0,0,0x13|0x4000); // leave the topmost band
        if(keepOnCurrent&&Native.IsWindow(h))
        {
            var current=desktops.Current;
            if(current!=Guid.Empty)
            {
                var owner=desktops.WindowDesktop(h);
                if(owner==Guid.Empty||owner!=current)desktops.Move(h,current);
                session.Placements.SetDesktop(h,current);
            }
        }
        return true;
    }
    void ReleaseAllPins(){foreach(var h in pinnedWindows.Keys.ToList())ReleasePin(h);}
    void PrunePins(bool reconcileDesktop=false)
    {
        bool changed=false;
        foreach(var h in pinnedWindows.Keys.ToList())
        {
            if(!Native.IsWindow(h)){ReleasePin(h);changed=true;continue;}
            var pin=pinnedWindows[h];
            // Reconcile only at a real desktop boundary. A working native view pin spans
            // every desktop and changing its owner would make Windows clear the pin. If
            // that native pin is unavailable/lost, follow the current desktop ourselves;
            // minimized/docked pins use the same path as focused pins.
            if(reconcileDesktop)
            {
                bool nativePinActive=pin.SystemPinned&&desktops.IsWindowPinned(h);
                var current=desktops.Current;
                var owner=desktops.WindowDesktop(h);
                if(PinnedWindowPolicy.NeedsDesktopMove(nativePinActive,current,owner))
                {
                    if(desktops.Move(h,current)&&pin.SystemPinned&&!desktops.IsWindowPinned(h))
                        desktops.PinWindow(h);
                }
            }
            RestorePinnedGeometry(h);
        }
        if(changed)PublishBrowsed();
    }
    void RestorePinnedGeometry(nint h)
    {
        if(!pinnedWindows.TryGetValue(h,out var pin)||pinRestoring.Contains(h)||!Native.IsWindow(h)||Native.IsIconic(h))return;
        try
        {
            pinRestoring.Add(h);
            bool minimized=session.IsMinimizedDocked(h);
            bool zoomed=Native.IsZoomed(h);
            bool typing=PinnedHasKeyboardFocus(h);
            // Async only: a synchronous ShowWindow/SetWindowPos on a foreign window from the
            // thread that hosts the low-level hooks can block in win32k against explorer/DWM.
            // Skip the correction while this window holds the caret. A show-state or
            // position fight here is what makes some pinned windows refuse text entry.
            if(!minimized&&zoomed!=pin.Maximized)
            {
                if(!typing)Native.ShowWindowAsync(h,pin.Maximized?3:9);
                return;
            }
            if((minimized||!pin.Maximized)&&Native.GetWindowRect(h,out var now)&&!now.Equals(pin.Bounds))
            {
                // A few pixels is the window's own frame, not a user move. Remember it.
                // A real jump is put back at once - this runs on every LOCATIONCHANGE, so a
                // pinned window does not follow a drag (native moves are also cancelled in
                // MoveSizeChanged; apps that drag themselves from their client area, such as
                // Windscribe, are corrected here). Settled adjustments above never trigger
                // SetWindowPos, so text entry is not disturbed.
                // Primary button: physical right when the user swapped buttons (SM_SWAPBUTTON).
                bool mouseDown=(Native.GetAsyncKeyState(Native.GetSystemMetrics(23)!=0?0x02:0x01)&0x8000)!=0;
                if(PinnedWindowPolicy.IsSettledAdjustment(now,pin.Bounds))
                    pinnedWindows[h]=pin with {Bounds=now};
                else
                {
                    // An app that keeps re-positioning itself on its own (tray panels
                    // re-anchor) would fight the lock and flash. After three snap-backs with
                    // no button held (the user is not dragging) within 5 s, accept where the
                    // app puts itself. A user drag never counts toward this.
                    long t=Environment.TickCount64;
                    var (count,since)=pinSnapBacks.GetValueOrDefault(h);
                    if(t-since>5000){count=0;since=t;}
                    if(!mouseDown&&++count>=3)
                    {
                        pinSnapBacks.Remove(h);
                        pinnedWindows[h]=pin with {Bounds=now};
                        Log.Write($"[pin] {h} keeps moving itself; pin now holds it at {now.Left},{now.Top}");
                        return;
                    }
                    pinSnapBacks[h]=(count,since);
                    if(!mouseDown)Log.Write($"[pin] restored {h} to {pin.Bounds.Left},{pin.Bounds.Top} after a move to {now.Left},{now.Top}");
                    Native.SetWindowPos(h,0,pin.Bounds.Left,pin.Bounds.Top,pin.Bounds.Width,pin.Bounds.Height,0x4014); // +SWP_ASYNCWINDOWPOS
                }
            }
        }
        finally{pinRestoring.Remove(h);}
    }
    static bool PinnedHasKeyboardFocus(nint h)
    {
        var fg=Native.GetForegroundWindow();
        return h!=0 && (fg==h || Native.GetAncestor(fg,2)==h || Native.GetAncestor(fg,3)==h);
    }
    void RememberLeavingDesktop()
    {
        var id=session.PreviousDesktop;
        if(id==Guid.Empty)return;
        nint focused=0;
        foreach(var h in browsed)if(!IsPinned(h)){focused=h;break;}
        var snap=new Dictionary<nint,DesktopWindowState>();
        int z=0;
        foreach(var w in catalog.Enumerate(true))
        {
            if(!DesktopArrangement.Include(IsPinned(w.Handle)))continue;
            Guid owner;
            try{owner=desktops.WindowDesktop(w.Handle);}catch{continue;}
            if(owner!=id)continue;
            var placement=Native.Placement(w.Handle);
            Native.RECT bounds=placement.NormalPosition;
            if(!Native.IsIconic(w.Handle)&&!Native.IsZoomed(w.Handle)&&Native.GetWindowRect(w.Handle,out var live)&&live.Width>0&&live.Height>0)
                bounds=live;
            bool hasTile=session.TryGetCanvasCell(w.Handle,out var tile);
            snap[w.Handle]=new DesktopWindowState(bounds,placement.ShowCmd,session.IsDocked(w.Handle),session.IsMinimizedDocked(w.Handle),w.Handle==focused,tile,hasTile,z++);
        }
        if(snap.Count>0)desktopStates[id]=snap;
        Log.Write($"[desktop] remembered {id} windows={snap.Count} focused={focused}");
    }
    void RecallArrivingDesktop()
    {
        var id=desktops.Current;
        if(id==Guid.Empty||!desktopStates.TryGetValue(id,out var snap)||snap.Count==0)return;
        var dock=new List<nint>();
        var undock=new List<nint>();
        DesktopArrangement.MembershipEdits(snap,session.IsDocked,dock,undock);
        foreach(var h in dock)if(snap.TryGetValue(h,out var s))session.ApplyDockState(h,true,s.MinimizedDocked);
        foreach(var h in undock)session.ApplyDockState(h,false,false);
        foreach(var (h,s) in snap)
        {
            if(!Native.IsWindow(h)||IsPinned(h))continue;
            RestoreRememberedWindow(h,s);
            if(!s.Docked&&s.HasTile)session.MoveTile(h,s.Tile);
        }
        session.Reflow();
        RaisePinsAboveOthers();
        Log.Write($"[desktop] restored {id} windows={snap.Count}");
    }
    bool WindowShouldStayMinimized(nint h)
    {
        var id=desktops.Current;
        return id!=Guid.Empty && desktopStates.TryGetValue(id,out var snap) && snap.TryGetValue(h,out var state) && state.ShowCmd is 2 or 6 or 7;
    }
    void RestoreRememberedWindow(nint h,DesktopWindowState s)
    {
        var placement=Native.Placement(h);
        placement.Length=System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>();
        placement.ShowCmd=s.ShowCmd is 2 or 6 or 7 ? 7 : s.ShowCmd==3 ? 3 : 4;
        if(s.Bounds.Width>0 && s.Bounds.Height>0)placement.NormalPosition=s.Bounds;
        Native.SetWindowPlacement(h,ref placement);
        if(placement.ShowCmd==4 && s.Bounds.Width>0 && s.Bounds.Height>0)
            Native.SetWindowPos(h,0,s.Bounds.Left,s.Bounds.Top,s.Bounds.Width,s.Bounds.Height,0x14);
    }
    void RestorePinsForCurrentDesktop()
    {
        if(!session.Active)return;
        PrunePins(true);
        Log.Write($"[desktop] restore-pins current={CurrentPinned().Count}/{pinnedWindows.Count} fg={Native.GetForegroundWindow()}");
        // Z-order only. Location and minimized/maximized status are restored separately
        // and must not be rewritten just because the desktop changed.
        RaisePinsAboveOthers();
    }
    List<Native.RECT> PinRects()
    {
        var rects=new List<Native.RECT>();
        foreach(var h in CurrentPinned())
            if(Native.GetWindowRect(h,out var rect)&&rect.Width>0&&rect.Height>0)rects.Add(rect);
        return rects;
    }
    // HWND_TOPMOST. HWND_TOP cannot cover the foreground window after a desktop switch,
    // which left other windows both above and below the pins.
    void RaisePinsAboveOthers()
    {
        foreach(var h in CurrentPinned())
        {
            // Diagnostic: a pin beneath an overview that is in the topmost band means the
            // overview buried it (a merely lower-band overview is re-ordered harmlessly here).
            foreach(var chrome in overlays.Values)
                if(Native.IsWindowVisible(chrome.Handle)&&!Native.IsIconic(h)&&(Native.GetWindowLongPtr(chrome.Handle,-20).ToInt64()&8)!=0
                    &&Native.IsAbove(chrome.Handle,h)&&pinBuriedLogged.Add(h))
                    Log.Write($"[pin] {h} was below overlay {chrome.Handle}; fg={Native.GetForegroundWindow()} browsing={browsing} activating={activating} popoutInteracting={popout?.IsInteracting==true}");
            Native.SetWindowPos(h,(nint)(-1),0,0,0,0,0x13|0x4000);
        }
        if(pinBuriedLogged.Count>0&&Environment.TickCount64-pinBuriedReset>5000){pinBuriedLogged.Clear();pinBuriedReset=Environment.TickCount64;}
        optionsWindow?.BringToFront();
        // Popped-out desktops stay above pins, the overview, and every other window.
        if(popout!=null && !popout.Suspended)popout.BringToFront();
    }
    // A docked window coming back, or a window about to be focused, must not open on top
    // of the same pixels a pin already owns. Pins remain above it either way.
    void PlaceClearOfPins(nint h)
    {
        if(h==0||IsPinned(h)||!Native.IsWindow(h)||Native.IsIconic(h))return;
        var pins=PinRects();
        if(pins.Count==0)return;
        var rect=Native.IsZoomed(h)?Native.Placement(h).NormalPosition:default;
        if(rect.Width<=0&&!Native.GetWindowRect(h,out rect))return;
        if(!PinnedWindowPolicy.IsCoveredBy(rect,pins))return;
        var probe=rect;
        var monitor=Native.MonitorFromRect(ref probe,2);
        if(monitor==0)monitor=Native.MonitorFromWindow(h,2);
        if(monitor==0)return;
        var clear=PinnedWindowPolicy.PlaceClearOf(rect,pins,Native.WorkArea(monitor));
        if(clear.Equals(rect))return;
        if(!Native.SetWindowPos(h,0,clear.Left,clear.Top,clear.Width,clear.Height,0x14))return;
        session.NoteUserMoved(h);
        Log.Write($"[pin] placed {h} clear of pinned panels at {clear.Left},{clear.Top}");
    }
    void RevealDockedWindow(nint h)
    {
        if(!session.Active||h==0)return;
        PlaceClearOfPins(h);
        if(!session.UndockTile(h))return;
        if(session.TryGetCanvasCell(h,out var cell))
        {
            var pins=PinRects();
            if(PinnedWindowPolicy.IsCoveredBy(cell,pins))
            {
                var probe=cell;
                var monitor=Native.MonitorFromRect(ref probe,2);
                if(monitor==0)monitor=Native.MonitorFromWindow(h,2);
                if(monitor!=0)
                {
                    double dpi=Native.MonitorScale(monitor);
                    var area=Tiler.OverviewArea(Native.WorkArea(monitor),8,dpi,settings.DesktopStripPosition);
                    var clear=PinnedWindowPolicy.PlaceClearOf(cell,pins,area);
                    if(!clear.Equals(cell)){session.MoveTile(h,clear);session.Reflow();}
                }
            }
        }
        RaisePinsAboveOthers();
    }
    void QueueBrowseGeometry(nint h)
    {
        if(h==0)return;
        lock(pendingBrowseGeometry)
        {
            pendingBrowseGeometry.Add(h);
            if(browseGeometryQueued!=0)return;
            browseGeometryQueued=1;
        }
        if(!queue.TryEnqueue(FlushBrowseGeometry))
            lock(pendingBrowseGeometry){browseGeometryQueued=0;pendingBrowseGeometry.Clear();}
    }
    void FlushBrowseGeometry()
    {
        nint[] changed;
        lock(pendingBrowseGeometry)
        {
            changed=pendingBrowseGeometry.ToArray();
            pendingBrowseGeometry.Clear();
            browseGeometryQueued=0;
        }
        if(!session.Active||!browsing)return;
        foreach(var h in changed)
        {
            if(!browsed.Contains(h)||!nativeGestures.Contains(h)||!nativeGestureOrigins.TryGetValue(h,out var origin))continue;
            EnforceFocusedResizeFloor(h,origin);
        }
        foreach(var chrome in overlays.Values)chrome.RefreshBrowseGeometry();
    }
    void EnforceFocusedResizeFloor(nint h,Native.RECT origin)
    {
        if(!Native.IsWindow(h)||Native.IsIconic(h)||Native.IsZoomed(h)||!Native.GetWindowRect(h,out var current))return;
        var monitor=Native.MonitorFromWindow(h,2);
        int minimum=FocusedWindowGeometry.MinimumEdgePixels(Settings.SmallWindowSizeDefault,Native.MonitorScale(monitor));
        var corrected=FocusedWindowGeometry.ClampResize(current,origin,minimum);
        if(corrected.Equals(current))return;
        Native.SetWindowPos(h,0,corrected.Left,corrected.Top,corrected.Width,corrected.Height,0x14);
    }
    void Promote(nint h)
    {
        foreach(var pin in CurrentPinned())if(!browsed.Contains(pin))browsed.Add(pin);
        browsed.Remove(h); browsed.Insert(0, h);
        browsed.RemoveAll(b => b != h && !CanBrowse(b));
        var before=browsed.ToList();
        var limited=BrowseReconciler.LimitWithPinned(browsed,PinnedSet(),MaxBrowsed);
        browsed.Clear();browsed.AddRange(limited);
        var demoted=before.Where(b=>!browsed.Contains(b)&&!IsPinned(b)).ToList();
        PublishBrowsed();
        foreach (var d in demoted) Native.SetWindowPos(d, 1, 0, 0, 0, 0, 0x13); // HWND_BOTTOM
    }
    void ClearBrowsed() { browsed.Clear(); PublishBrowsed(); }
    // Settings lowered the cap while more windows were browsed: demote the oldest extras.
    void TrimBrowsed() { if (browsing && browsed.Count > 0) Promote(browsed[0]); }
    // Send one browsed window back under the canvas, keeping the other in front (and focused).
    async void Demote(nint h)
    {
        if(!session.Active || !browsed.Contains(h) || IsPinned(h))return;
        if(browsed.Count==1){ReturnToGrid();return;}
        int version=++activationVersion;
        activating=true;
        CancelAnimations();
        var animation=AnimateWindowsAsync(WindowBounds(new[]{h}),false);
        browsed.Remove(h);
        var keep = browsed.FirstOrDefault();
        // Restore the demoted source's miniature BEFORE burying its real HWND. Lowering the
        // real window while its DWM copy is still suppressed creates a compositor-frame hole
        // where both representations are hidden.
        PublishBrowsed();
        Native.SetWindowPos(h, 1, 0, 0, 0, 0, 0x13); // HWND_BOTTOM
        try { await animation; }
        finally
        {
            if(version==activationVersion)
            { CancelAnimations(); activating=false; if(session.Active)EnterBrowsing(keep); }
        }
    }
    bool activating;
    int activationVersion;
    OptionsWindow? optionsWindow;
    DesktopPopoutWindow? popout;
    public TrayApp(App app)
    {
        this.app = app; queue = DispatcherQueue.GetForCurrentThread();
        catalog = new(desktops); session = new(catalog, desktops, settings);
        primary = new(session, settings, ShowOptions);
        input = new(primary.Handle, settings); tray = new(primary.Handle, input.HotkeyText);
        input.IsActive = () => session.Active;
        input.IsPinnedWindow=IsPinned;
        input.IsOverviewWindow=IsStayViewWindow;
        input.OverviewPinTarget=PinTargetAt;
        session.IsPinnedWindow=IsPinned;
        session.KeepMinimized=WindowShouldStayMinimized;
        // Left-drag on the focused window's title bar moves it; keep the moved placement.
        input.BrowsedWindow = () => session.Selected;
        input.WindowDragged += h => queue.TryEnqueue(async () => {
            // Win+drag posts cross-thread moves. Let the last posted move reach the app
            // before capturing it; native caption moves use EVENT_SYSTEM_MOVESIZEEND below.
            await Task.Delay(80);
            if(session.Active && !input.IsDragging && CanBrowse(h))session.NoteUserMoved(h);
        });
        catalog.MoveSizeChanged += (h,started) => queue.TryEnqueue(() => {
            if(IsPinned(h))
            {
                // A pinned window does not move: end Windows' move/size loop the moment it
                // starts (covers custom title bars the caption block cannot see).
                if(started)Native.PostMessage(h,0x001F,0,0); // WM_CANCELMODE
                RestorePinnedGeometry(h);foreach(var chrome in overlays.Values)chrome.RefreshBrowseGeometry();return;
            }
            if(started)
            {
                if(session.Active && browsed.Contains(h))
                {
                    nativeGestures.Add(h);
                    if(Native.GetWindowRect(h,out var origin))nativeGestureOrigins[h]=origin;
                    foreach(var chrome in overlays.Values)chrome.ReassertBrowseZOrder();
                }
            }
            else
            {
                bool wasGesture=nativeGestures.Remove(h);
                bool hadOrigin=nativeGestureOrigins.Remove(h,out var origin);
                if(!wasGesture||!session.Active)return;
                if(hadOrigin)EnforceFocusedResizeFloor(h,origin);
                session.NoteUserMoved(h);
                foreach(var chrome in overlays.Values)chrome.RefreshBrowseGeometry();
                foreach(var chrome in overlays.Values)chrome.ReassertBrowseZOrder();
            }
        });
        catalog.LocationChanged += h => {
            if(IsPinned(h)){queue.TryEnqueue(()=>{RestorePinnedGeometry(h);foreach(var chrome in overlays.Values)chrome.RefreshBrowseGeometry();});return;}
            if(session.Active && browsing && browsed.Contains(h))QueueBrowseGeometry(h);
        };
        catalog.MinimizeChanged += (h,started) => queue.TryEnqueue(() => {
            if(!session.Active || h==0 || !Native.IsWindow(h))
            { overviewMinimizing.Remove(h); return; }
            if(started)
            {
                if(!browsing || !browsed.Contains(h) || !overviewMinimizing.Add(h))return;
                // Focused Minimize is StayView's dock gesture. Establish the dock slot
                // before starting the shrink journey; OverviewSession lets the native
                // minimize finish, then restores the HWND non-activating behind the canvas
                // so the dock keeps a live source. Native X/Close is not intercepted.
                session.BeginUserMinimize(h);
                // Start the registered-thumbnail journey while the real HWND still has its
                // last visible bounds, then put that HWND behind the overview. Windows may
                // continue its native minimize internally, but its taskbar-bound animation
                // is covered by StayView rather than shown to the user.
                if(browsed.Count>1)Demote(h);
                else ReturnToGrid();
                return;
            }
            if(!overviewMinimizing.Remove(h))return;
            if(Native.IsIconic(h))session.NoteUserMinimized(h);
            else session.CancelUserMinimize(h);
            // Refresh persistent adornments immediately. In particular, if this source was
            // still rendered with an old dock role, its frame disappears in this same turn
            // instead of waiting for a later topology reflow/timer tick.
            foreach(var chrome in overlays.Values)chrome.PruneDeadTiles();
        });
        emptySpace.Warm();
        input.Toggle += () => queue.TryEnqueue(HotkeyToggle);
        input.PinToggle += h => queue.TryEnqueue(()=>TogglePin(h));
        input.ClientDoubleClick += (h, first, second, gesture) => queue.TryEnqueue(async () =>
        {
            int version = activationVersion;
            if (!browsing || !session.Active || !browsed.Contains(h)) return;
            if (!await emptySpace.IsEmptyAsync(h, first, second)) return;
            // Never apply a delayed provider result after the user has moved on.
            if (version != activationVersion || gesture != input.GestureVersion
                || !browsing || !session.Active || !browsed.Contains(h) || !CanBrowse(h)) return;
            if (!Native.GetCursorPos(out var cursor)
                || Math.Abs(cursor.X - second.X) >= 6 || Math.Abs(cursor.Y - second.Y) >= 6) return;
            var foreground = Native.GetForegroundWindow();
            if (foreground != h && Native.GetAncestor(foreground, 3) != h) return;
            Demote(h);
        });
        // Only confirmed title-bar double-clicks shrink a browsed window. Content and
        // control double-clicks are passed through by InputHooks.
        input.DoubleClick += h => queue.TryEnqueue(() => {
            if (!session.Active || !browsing) return;
            // With two windows browsed, only the double-clicked one shrinks back; the other
            // stays in front. The last one out returns the grid.
            if (browsed.Count > 1 && browsed.Contains(h)) Demote(h); else HotkeyToggle();
        });
        // Escape keeps its established meaning: dismiss the whole overview. Session
        // teardown releases every pin after restoring the original desktop placements.
        input.Escape += () => queue.TryEnqueue(() => {
            bool wasActive=session.Active;
            session.Exit();
            // Esc leaves Taskview++: show the banner as it goes (cosmetic, never blocks).
            if(wasActive)
                try{new SplashWindow().ShowPassiveAndFade();}
                catch(Exception ex){Log.Write("Esc banner failed: "+ex.Message);}
        });
        input.DesktopDirection += direction => queue.TryEnqueue(() => {
            var list = desktops.List(); int i = list.ToList().FindIndex(d => d.Current) + direction;
            if (i >= 0 && i < list.Count) session.ChangeDesktop(() => desktops.Switch(list[i].Id));
        });
        WireOverlay(primary);
        primary.ToggleRequested += () => queue.TryEnqueue(HotkeyToggle);
        primary.TrayMessage += message => {
            if (message == 0x202) HotkeyToggle();
            else if (message == 0x205) {
                switch (tray.Menu()) { case 1: session.Enter(); break; case 2: ShowOptions(); break; case 3: ExitWithBanner(); break; }
            }
        };
        session.LayoutChanged += layout => {
            input.SetOverview(true);
            foreach (var pair in overlays.Where(x => !layout.ContainsKey(x.Key))) pair.Value.Hide();
            foreach (var (monitor, tiles) in layout) {
                if (!overlays.TryGetValue(monitor, out var chrome)) {
                    chrome = overlays.Count == 0 ? primary : NewOverlay();
                    overlays[monitor] = chrome;
                }
                chrome.Render(monitor, tiles, input.HotkeyText);
                if(browsing)chrome.SetBrowsed(browsed,PinnedSet());
            }
            // Render re-asserts the overlay's topmost, which would bury an open popout.
            // Pins are raised last so neither the overview nor another desktop's windows
            // can sit above them.
            if(popout?.IsInteracting != true)popout?.BringToFront();
            optionsWindow?.BringToFront();
            RaisePinsAboveOthers();
            // Seed the topology signature from the layout that was just rendered.
            // This prevents the timer's first tick from immediately repacking a
            // canvas the user may already have started arranging.
            signature = TopologySignature();
        };
        session.DesktopRemoved += id => {
            if(popout?.DesktopId==id)ClosePopout();
            var current=desktops.Current;
            foreach(var h in pinnedWindows.Where(p=>p.Value.HomeDesktop==id).Select(p=>p.Key).ToList())
                pinnedWindows[h]=pinnedWindows[h] with {HomeDesktop=current};
        };
        session.Leaving += () => { activationVersion++; activating=false; CancelAnimations(); nativeGestures.Clear(); overviewMinimizing.Clear(); ClosePopout(); CloseOptions(); browsing = false; ClearBrowsed(); ReleaseAllPins(); pinnedWindows.Clear(); pinRestoring.Clear(); pinSnapBacks.Clear(); input.Browsing = false; input.SetOverview(false); foreach (var chrome in overlays.Values) chrome.Hide(); signature = ""; };
        // A desktop switch while browsing ends the browse: the focused window is on the
        // desktop we left. Clear the flags only — the switch's own reflow renders the new
        // grid, and with browsedSource cleared that render re-asserts the overview topmost.
        session.DesktopSwitched += () => {
            Log.Write($"[desktop] switched -> {desktops.Current}; pins={pinnedWindows.Count} browsing={browsing} browsed={string.Join(",",browsed)}");
            RememberLeavingDesktop();
            SyncPopoutForDesktop();
            // A first activation can still be retrying before browsing becomes true.
            // Invalidate it on every desktop switch, even in that pre-commit state.
            browsing = false; input.Browsing = false; activating = false; activationVersion++;
            CancelAnimations(); nativeGestures.Clear(); overviewMinimizing.Clear();
            tileDragInteraction=false; refrontAfterCanvasGesture=false;
            ClearBrowsed();
            // DesktopSwitched is raised before OverviewSession reflows the new desktop.
            // Move fallback pins now so focused and minimized pins are already members of
            // the destination workspace when that reflow enumerates its windows.
            PrunePins(true);
            queue.TryEnqueue(() => { RestorePinsForCurrentDesktop(); RecallArrivingDesktop(); });
        };
        catalog.ForegroundChanged += () => queue.TryEnqueue(() => {
            if(!session.Active) return;
            if(ForegroundChurning()){ session.ObserveDesktopChange(); return; }
            if(preserveWindowState){RaisePinsAboveOthers();return;}
            var popped=popout;
            if(popped!=null && !popped.Suspended)
            {
                var fgNow=Native.GetForegroundWindow();
                if(fgNow==popped.Handle || Native.GetAncestor(fgNow,2)==popped.Handle)
                { RaisePinsAboveOthers(); return; }
            }
            if(session.ObserveDesktopChange())return;
            if(activating) return;
            PrunePins();
            // OS-level desktop switches do not raise OverviewSession.DesktopSwitched.
            // Synchronize before any z-order work so a pinned popout representing the new
            // current desktop cannot be raised briefly before the timer notices the switch.
            SyncPopoutForDesktop();
            if(browsing) { ReconcileBrowse(); RaisePinsAboveOthers(); return; }
            // A newly launched foreground app must not be buried behind the grid before
            // its first tile exists. Capture it, then use the ordinary focus handoff.
            var fg=Native.GetForegroundWindow();
            var root=fg==0?0:Native.GetAncestor(fg,3);
            var launched=catalog.Enumerate().FirstOrDefault(w=>w.Handle==fg||w.Handle==root);
            if(launched!=null && !session.Placements.Entries.ContainsKey(launched.Handle))
            { session.Reflow(); EnterBrowsing(launched.Handle); return; }
            // The overview keeps itself above source windows; an open popout is then put
            // back on top of it, rather than suppressing the overview's own z-order work.
            foreach(var chrome in overlays.Values) chrome.KeepAbove();
            if(popout?.IsInteracting != true)popout?.BringToFront();
            optionsWindow?.BringToFront();
            RaisePinsAboveOthers();
        });
        timer.Tick += (_, _) => {
            if (!session.Active) return;
            if(session.ObserveDesktopChange())return;
            // Keep this ahead of all interaction early-outs. A Win+Ctrl+Arrow / Task View
            // switch must hide or restore the pinned popout even during browse/drag state.
            SyncPopoutForDesktop();
            // Runs even while browsing (no reflow happens then), so a window closed from
            // the app itself never leaves a clickable ghost tile behind on the canvas.
            foreach (var chrome in overlays.Values) chrome.PruneDeadTiles();
            PrunePins();
            RaisePinsAboveOthers();
            // Runs while browsing, which is exactly when the focused window is being
            // moved, resized, maximized or snapped by the user.
            if (browsing)
            {
                foreach (var b in browsed.Where(b=>!IsPinned(b)&&!nativeGestures.Contains(b)).ToList()) session.SyncUserGeometry(b);
                // ReconcileBrowse deliberately leaves active pointer/native move gestures
                // alone. Maintain only browse z-order during that pause so a focused
                // window cannot sit hidden behind the overview until release.
                foreach(var chrome in overlays.Values)chrome.ReassertBrowseZOrder();
                RaisePinsAboveOthers();
                ReconcileBrowse();
            }
            if (browsing || activating || session.DragActive || overlays.Values.Any(c => c.IsDragging) || popout?.IsInteracting == true) return;
            try {
                // A window minimized while the grid is open can lose its DWM pixels and
                // look like a glowing ghost tile. Restore such sources behind the overview
                // before refreshing any live desktop/thumbnail composition.
                session.RestoreMinimizedSourcesForOverview();
                popout?.Refresh();
                optionsWindow?.BringToFront();
                // Auto-layout is only for a desktop change or a window entering or
                // leaving the current desktop. Minimize, focus, source rectangle,
                // monitor/work-area changes and user tile movement never repack.
                var next = TopologySignature();
                if (next != signature)
                {
                    signature = next; session.Reflow();
                }
                else foreach (var chrome in overlays.Values) chrome.UpdateDesktopPictures();
            } catch (Exception ex) { Log.Write(ex.ToString()); } // Never dismiss for a shell refresh failure.
        };
        timer.Start();
        Log.Write("Persistent DWM overview ready. " + input.HotkeyText + "; " + desktops.Status);
    }
    // Normal executable startup should present StayView immediately. Keep this explicit
    // rather than entering from the constructor so every event/timer/overlay is fully wired
    // before the first session reflow, and helper processes never take this path.
    public void OpenOnLaunch()
    {
        if(!stopped && !session.Active)session.Enter();
    }
    OverlayChrome NewOverlay() { var chrome = new OverlayChrome(session, settings, ShowOptions); WireOverlay(chrome); return chrome; }
    void WireOverlay(OverlayChrome chrome)
    {
        chrome.FloatingPanel = popout?.Handle ?? 0;
        chrome.TileActivated += h => queue.TryEnqueue(() => EnterBrowsing(h));
        chrome.DockedTileClicked += h => queue.TryEnqueue(() => RevealDockedWindow(h));
        chrome.InteractionStarted += () => {
            if(activating)
            {
                activationVersion++; activating=false; CancelAnimations();
                // A new press supersedes the old transition. If a browse had already
                // committed, keep it visible. If this was the FIRST focus attempt and it
                // was cancelled after DropTopmost(), restore the grid immediately; otherwise
                // the version-mismatched EnterBrowsing.finally intentionally does no repair.
                if(browsing)PublishBrowsed();
                else
                {
                    ClearBrowsed();
                    foreach(var overlay in overlays.Values)overlay.RaiseTopmost();
                    if(popout?.IsInteracting!=true)popout?.BringToFront();
                    optionsWindow?.BringToFront();
                }
            }
            if(browsing && session.Active)
                foreach(var overlay in overlays.Values)overlay.ReassertBrowseZOrder();
        };
        chrome.TileDragStarted += () => {
            tileDragInteraction=true;
            if(browsing && session.Active)
                foreach(var overlay in overlays.Values)overlay.ReassertBrowseZOrder();
        };
        chrome.TileClose += h => queue.TryEnqueue(() => session.Close(h));
        chrome.TilePinToggle += h => queue.TryEnqueue(()=>TogglePin(h));
        chrome.PinGestureCompleted += h => queue.TryEnqueue(()=>GivePinnedKeyboardFocus(h));
        chrome.TileDragMoved += (h,p) => {
            var target = popout;
            if (target == null) return;
            bool over = target.ContainsScreenPoint(p);
            target.SetDropTarget(over);
            if (over) target.ShowDragPreview(h,p);
        };
        chrome.TileDragEnded += () => {
            popout?.SetDropTarget(false); popout?.ClearDragPreview();
            if(tileDragInteraction && browsing && session.Active)refrontAfterCanvasGesture=true;
            tileDragInteraction=false;
        };
        chrome.ExternalTileDrop = DropTileOnPopout;
        chrome.DesktopPopoutRequested += (desktop, work) => queue.TryEnqueue(() => ShowPopout(desktop, work, chrome.Handle));
        chrome.DesktopBackgroundRequested += desktop => queue.TryEnqueue(() => ChangeDesktopBackground(desktop));
        chrome.OverviewPointerDown += () => {
            var open=optionsWindow;
            if(open!=null)queue.TryEnqueue(()=>{if(optionsWindow==open)CloseOptions();});
        };
        // Only an EMPTY-canvas press returns to the grid while browsing. A press on a tile
        // is left to DockView, so tiles can be dragged/rearranged even while a window is
        // focused. (The overview answers MA_NOACTIVATE, so this cannot be read from the
        // foreground; DockView tells us whether the press hit a tile or bare canvas.)
        chrome.BackgroundPressed += () => queue.TryEnqueue(() => {
            if(!browsing || !session.Active) return;
            refrontAfterCanvasGesture=false;
            ReturnToGrid();
        });
    }
    bool DropTileOnPopout(nint h, Native.POINT point)
    {
        var target = popout;
        if (target == null || !target.ContainsScreenPoint(point)) return false;
        target.ClearDragPreview();
        target.SetDropTarget(false);
        var current = desktops.WindowDesktop(h);
        if (current != Guid.Empty && current == target.DesktopId) return false;
        if (!session.MoveToDesktop(h, target.DesktopId,target.DropBounds(h,point))) return false;
        if(browsing)ReconcileBrowse();
        target.Refresh();
        return true;
    }
    bool DropWindowFromPopout(nint h,Native.POINT point)
    {
        if(!session.Active)return false;
        var current=desktops.Current;
        if(current==Guid.Empty)return false;
        var probe=new Native.RECT(point.X,point.Y,1,1);
        var monitor=Native.MonitorFromRect(ref probe,0);
        if(monitor==0 || !overlays.TryGetValue(monitor,out var chrome) || !Native.IsWindowVisible(chrome.Handle))return false;
        Native.GetWindowRect(h,out var bounds);
        if(Native.IsIconic(h)||Native.IsZoomed(h))bounds=Native.Placement(h).NormalPosition;
        var target=DesktopDropGeometry.AtPoint(bounds,point,Native.WorkArea(monitor));
        if(!session.MoveToDesktop(h,current,target))return false;
        session.PositionTransferredTile(h,point);
        popout?.Refresh();
        return true;
    }
    void ShowPopout(DesktopInfo desktop, Native.RECT work, nint owner)
    {
        if (!session.Active) return;
        // Only alternate desktops pop out: the current desktop already contains the
        // overview, so previewing it would nest the overview inside its own popout.
        if (IsCurrentDesktop(desktop.Id) || desktop.Current) return;
        ClosePopout();
        var w = new DesktopPopoutWindow(session, settings, desktop, owner);
        w.ExternalWindowDrop=DropWindowFromPopout;
        w.WindowDragMoved+=(h,p)=>{
            foreach(var chrome in overlays.Values)
                if(w.ContainsScreenPoint(p))chrome.ClearExternalDragPreview();
                else chrome.ShowExternalDragPreview(h,p);
        };
        w.WindowDragEnded+=()=>{foreach(var chrome in overlays.Values)chrome.ClearExternalDragPreview();};
        w.UserCloseRequested += id => { foreach(var chrome in overlays.Values)chrome.FlashDesktopCard(id); };
        w.Dismissed += () => { if (popout == w) { popout = null; SetFloatingPanel(0); } };
        // Pinned windows were reported vanishing on a popout press. Re-assert them at once
        // (not only on the next 500 ms tick) and record the state for diagnosis.
        w.PressChanged += down => queue.TryEnqueue(() => {
            if (!session.Active) return;
            if (pinnedWindows.Count > 0)
                Log.Write($"[popout] {(down?"press":"release")} fg={Native.GetForegroundWindow()} browsing={browsing} activating={activating} browsed={string.Join(",",browsed)} pins={string.Join(",",pinnedWindows.Keys)}");
            RaisePinsAboveOthers();
        });
        preserveWindowState=true;
        popout = w;
        w.Present(work);
        SetFloatingPanel(w.Handle);
        RaisePinsAboveOthers();
        queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => preserveWindowState=false);
    }
    // An unknown id (no virtual-desktop API) means the popout would show the current desktop.
    bool IsCurrentDesktop(Guid id) => id == Guid.Empty || id == desktops.Current;
    void SyncPopoutForDesktop()
    {
        var panel = popout;
        if (panel == null) return;
        if (IsCurrentDesktop(panel.DesktopId)) panel.SuspendForCurrentDesktop();
        else panel.ResumeAfterDesktopSwitch();
    }
    // Change background hands off to Windows' own Personalization > Background page.
    // Windows applies the chosen picture to whichever desktop is active when the picker
    // is used, so switch to the right-clicked desktop first. The picker then has to be
    // browsed like any clicked window: the overview is topmost and re-asserts that on
    // every foreground change, so without the handoff Settings opens behind the canvas
    // and the next reflow simply turns it into another tile.
    async void ChangeDesktopBackground(DesktopInfo desktop)
    {
        if (!session.Active) return;
        if (desktop.Id != Guid.Empty && desktop.Id != desktops.Current
            && !session.ChangeDesktop(() => desktops.Switch(desktop.Id))) return;
        int version = ++activationVersion;
        // Suppresses the overview's own z-order maintenance until the picker is up.
        activating = true;
        nint picker = 0;
        try
        {
            foreach (var chrome in overlays.Values) chrome.DropTopmost();
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:personalization-background")))
            { Log.Write("Windows declined ms-settings:personalization-background."); return; }
            // Wait for Settings to actually take the foreground; it may still be starting.
            for (int attempt = 0; attempt < 40 && picker == 0; attempt++)
            {
                await Task.Delay(100);
                if (version != activationVersion || !session.Active) return;
                var foreground = Native.GetForegroundWindow();
                if (IsSettingsWindow(foreground)) picker = foreground;
            }
            if (picker == 0) Log.Write("The Settings background page did not reach the foreground.");
        }
        catch (Exception ex) { Log.Write("Background settings: " + ex); }
        finally
        {
            if (version == activationVersion)
            {
                activating = false;
                if (session.Active)
                {
                    if (picker != 0) EnterBrowsing(picker);
                    // Nothing to browse: put the overview back the way it was.
                    else if (!browsing) { ClearBrowsed(); foreach (var chrome in overlays.Values) chrome.RaiseTopmost(); }
                }
            }
        }
    }
    // Settings is a packaged app: the frame can belong to ApplicationFrameHost with
    // SystemSettings owning the hosted content. Match on process, never on a window
    // title, which is localized.
    static bool IsSettingsWindow(nint h)
    {
        if (h == 0 || !Native.IsWindowVisible(h)) return false;
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName is "SystemSettings" or "ApplicationFrameHost"; }
        catch { return false; }
    }
    void SetFloatingPanel(nint handle)
    {
        handle=optionsWindow?.Handle ?? handle;
        primary.FloatingPanel = handle;
        foreach (var chrome in overlays.Values) chrome.FloatingPanel = handle;
    }
    void ClosePopout() { var p = popout; popout = null; SetFloatingPanel(0); try { p?.Close(); } catch (Exception ex) { Log.Write("Popout close: " + ex.Message); } }
    // Clicking a tile is the Task-View action: the overlays drop out of the way so the
    // real window comes to the front at true size and takes focus. The overview stays
    // resident; the hotkey re-summons the grid.
    async void EnterBrowsing(nint h)
    {
        Log.Write($"[browse] enter {h} pinned={IsPinned(h)} stable={IsCurrentDesktopStable(h)} active={session.Active}");
        if (!session.Active || h==0 || !Native.IsWindow(h) || (!IsPinned(h)&&!IsCurrentDesktopStable(h))) return;
        ShowTilePin(h);   // clicking a tile-only pin brings it in front as an ordinary pin
        var activationDesktop=desktops.Current;
        int version=++activationVersion;
        activating=true;
        // A NEW focus animation captures the target miniature's current visual position
        // itself, so don't finish an in-flight layout transition here first: doing so was
        // the dock -> canvas -> focus two-journey bug. Re-fronting an already browsed window
        // has no new visual journey and may safely settle any unrelated layout transition.
        if(browsed.Contains(h))CancelAnimations();
        // The overview must stop being topmost before SetForegroundWindow can succeed,
        // but don't commit browsing/suppression until activation has actually succeeded.
        try
        {
            if(!browsed.Contains(h))
            {
                // A miniature drag is the one overview gesture allowed to change the real
                // desktop location. Apply that saved target while the canvas is still
                // covering the HWND, then measure the actual DWM frame for a snap-free
                // expansion. A normal focus click performs no geometry change.
                session.PrepareActivationGeometry(h);
                if(!IsPinned(h))PlaceClearOfPins(h);
                session.EnsureActivationTopVisible(h);
                await AnimateWindowsAsync(WindowBounds(new[]{h}),true);
            }
            if(version!=activationVersion || !session.Active || (activationDesktop!=Guid.Empty && desktops.Current!=activationDesktop) || !Native.IsWindow(h) || (!IsPinned(h)&&!IsCurrentDesktopStable(h)))return;
            foreach (var chrome in overlays.Values) chrome.DropTopmost();
            for(int attempt=0;attempt<10;attempt++)
            {
                if(version!=activationVersion || !session.Active || (activationDesktop!=Guid.Empty && desktops.Current!=activationDesktop))return;
                if(!Native.IsWindow(h) || (!IsPinned(h)&&!IsCurrentDesktopStable(h)))break;
                if(session.Activate(h))
                {
                    browsing=true;
                    input.Browsing=true;
                    // The DWM miniature has reached the real window rectangle. Keep it
                    // there for the handoff instead of CancelAnimations snapping it back
                    // to the small tile for one frame just as the real HWND appears.
                    CommitAnimations();
                    Promote(h);
                    RaisePinsAboveOthers();
                    return;
                }
                await Task.Delay(50);
            }
            if(version!=activationVersion || !session.Active)return;
            Log.Write($"[activate] Window {h} ({Native.Class(h)}) did not become foreground; foreground={Native.GetForegroundWindow()}");
        }
        catch(Exception ex){Log.Write("Window activation: "+ex);}
        finally
        {
            if(version==activationVersion)
            {
                activating=false;
                CancelAnimations();
                if(session.Active && !browsing)
                    { ClearBrowsed(); foreach(var chrome in overlays.Values) chrome.RaiseTopmost(); }
                else if(session.Active)PublishBrowsed();
            }
        }
    }
    void HotkeyToggle()
    {
        if (session.Active && browsing)
        {
            // First return ordinary focused windows to their tiles. If only pinned
            // background windows remain, the next toggle dismisses the overview normally.
            if(browsed.Any(h=>!IsPinned(h)))ReturnToGrid();
            else session.Exit();
            return;
        }
        session.Toggle();
    }
    // The single authority for browse state. Called on every foreground change AND every
    // timer tick, so however the world drifts — a window focused directly, the desktop
    // switched (by StayView or Windows), a window closed or moved away — StayView's browse
    // state is reconciled against what is actually in front, within one tick at worst.
    // Browsing is valid ONLY while the selected window is the real foreground window on the
    // current desktop; anything else resolves to following the new foreground source or,
    // failing that, returning to the grid so no window is ever left hidden with the
    // overview dropped behind it.
    void ReconcileBrowse()
    {
        if(!browsing || !session.Active || activating) return;
        if(session.DragActive || input.IsDragging || nativeGestures.Count!=0 || overlays.Values.Any(c => c.IsDragging)) return; // never interrupt a gesture
        var minimized=browsed.Where(Native.IsIconic).ToList();
        foreach(var h in minimized)session.NoteUserMinimized(h);
        if(minimized.Count>0)PublishBrowsed();
        if(minimized.Count>0)
        {
            // Animate the already-registered DWM miniature from the last real window
            // rectangle down into its remembered tile BEFORE any reflow can snap the tile
            // straight to its small position. The real HWND remains genuinely minimized,
            // so this restores the visual transition without reintroducing the flash.
            if(minimized.Count==browsed.Count) { Log.Write("[reconcile] all browsed minimized -> grid"); ReturnToGrid(); return; }
            Log.Write($"[reconcile] demote minimized {minimized[0]}"); Demote(minimized[0]); return;
        }
        var available = BrowseReconciler.AvailableWindows(browsed, CanBrowse);
        if (!browsed.SequenceEqual(available))
        {
            browsed.Clear(); browsed.AddRange(available);
            PublishBrowsed();
        }
        // The browsed window left the current desktop (a switch by either StayView or
        // Windows): show the new desktop's grid rather than auto-browsing whatever is here.
        if(session.Selected == 0 || !browsed.Contains(session.Selected))
        {
            // Primary is gone (closed / moved desktop). If the second browsed window is still
            // here, it carries on alone rather than dropping both to the grid.
            var survivor = browsed.FirstOrDefault();
            browsed.Remove(session.Selected);
            Log.Write($"[reconcile] selected={session.Selected} not browsed; survivor={survivor}");
            if(survivor != 0) { EnterBrowsing(survivor); return; }
            ReturnToGrid(); return;
        }
        // A pinned-only browse has nothing to return to the grid: the pins stay in front
        // whatever holds focus (canvas, another app). Treating a canvas click here as
        // "back to grid" made ReturnToGrid a no-op that left this exact state in place,
        // so the reconciler re-took the same decision every tick (2026-09-22 livelock:
        // overlay re-rendered twice a second and swallowed desktop-card clicks).
        if(browsed.Count>0 && browsed.All(IsPinned)) return;
        var fg = Native.GetForegroundWindow();
        var root = fg == 0 ? 0 : Native.GetAncestor(fg, 3); // GA_ROOTOWNER
        // Our own non-overlay windows (Options panel, desktop popout, menus) taking focus is
        // not drift: leave the browse exactly as it is.
        if(fg != 0 && !overlays.Values.Any(c => c.Handle == fg || c.Handle == root))
        { Native.GetWindowThreadProcessId(fg, out var fgPid); if(fgPid == Environment.ProcessId) return; }
        if(fg == session.Selected || root == session.Selected) return; // still in front: valid, nothing to do
        // The other browsed window took the foreground: it becomes primary, nothing is demoted.
        var other = browsed.FirstOrDefault(b => b != session.Selected && (b == fg || b == root));
        if(other != 0) { if(session.Activate(other)) Promote(other); return; }
        // Focus moved off the browsed window. Classify what took it: our own canvas (a tile
        // gesture), another managed source, or anything else.
        bool ownCanvas = fg != 0 && overlays.Values.Any(c => c.Handle == fg || c.Handle == root);
        var target = ownCanvas ? 0 : catalog.Enumerate().FirstOrDefault(w => w.Handle == fg || w.Handle == root)?.Handle ?? 0;
        bool canvasGesture=ownCanvas && refrontAfterCanvasGesture;
        var browseTarget=BrowseReconciler.ClassifyForeground(session.Selected, fg, root, ownCanvas, canvasGesture, target);
        // This permission belongs to exactly one completed StayView drag handoff. Never let
        // it survive into a later foreground change such as an app's native X/Close.
        if(ownCanvas)refrontAfterCanvasGesture=false;
        Log.Write($"[reconcile] fg={fg} root={root} selected={session.Selected} ownCanvas={ownCanvas} target={target} -> {browseTarget}");
        switch(browseTarget)
        {
            case BrowseTarget.Keep:
                return;
            case BrowseTarget.Refront:
                // A tile press makes the overview the foreground window. Now that a tile
                // press no longer ends the browse, the browse is still live — put the
                // selected window back in front instead of dropping to the grid, which
                // would lower every source and take the browsed window with it.
                EnterBrowsing(session.Selected);
                return;
            case BrowseTarget.Follow:
                if(session.Activate(target)) { Promote(target); return; }
                break;
        }
        ReturnToGrid();
    }
    // Re-summon the grid on top of the focused window instead of dismissing. Shared by
    // the hotkey and by a click on the overview canvas while browsing: clicking the canvas
    // makes it the foreground window, which puts the opaque canvas over the browsed
    // window — so if the browsed tile stayed suppressed the window vanished from both
    // places at once. Back-on-the-grid is the only honest state after that click.
    async void ReturnToGrid()
    {
        // Invalidate any asynchronous activation before publishing the grid state.
        int version=++activationVersion;
        CancelAnimations();
        var pinned=browsed.Where(h=>IsPinned(h)&&CanBrowse(h)&&!session.IsMinimizedDocked(h)).ToList();
        var returning=browsed.Where(h=>!IsPinned(h)).ToList();
        Log.Write($"[grid] return pinned={pinned.Count} returning={returning.Count}");
        // Nothing to send back and the pins already define the state: leave it alone rather
        // than cancelling animations / bumping activationVersion for no visible change.
        if(returning.Count==0 && pinned.Count==browsed.Count){ activating=false; return; }
        var bounds=WindowBounds(returning);
        activating = true;
        browsing = pinned.Count>0;
        input.Browsing = browsing;
        var animation=AnimateWindowsAsync(bounds,false);
        // Restore the miniatures first so there is no one-frame hole when the browsed
        // real windows are sent back.
        browsed.Clear();browsed.AddRange(pinned);PublishBrowsed();
        // The overview cannot become topmost while pinned HWNDs must remain visible above
        // it. Explicitly bury only the ordinary windows whose miniatures were just restored.
        if(pinned.Count>0)
            foreach(var h in returning.Where(Native.IsWindow))Native.SetWindowPos(h,1,0,0,0,0,0x13);
        // If a canvas gesture is in progress (a tile was just pressed), stop here: a
        // SetWindowPos on the overview while the pointer is captured can cancel the
        // gesture, and the gesture's own end (drop/dock/undock/activate) reflows anyway,
        // which re-asserts the grid's z-order now that browsedSource is cleared.
        try
        {
            if (!overlays.Values.Any(c => c.IsDragging))
            {
                if(pinned.Count==0)foreach (var chrome in overlays.Values) chrome.RaiseTopmost();
                else foreach(var chrome in overlays.Values)chrome.ReassertBrowseZOrder();
                popout?.BringToFront();
            }
            await animation;
        }
        finally
        {
            if(version==activationVersion)
            {
                CancelAnimations(); activating=false;
                if(session.Active && !overlays.Values.Any(c=>c.IsDragging))session.Reflow();
            }
        }
    }
    string TopologySignature()
    {
        // Enumerate resolves the current desktop id for this pass; reuse it via
        // LastCurrent instead of a second broker round-trip for desktops.Current.
        var handles = catalog.Enumerate().Select(w => w.Handle.ToInt64()).OrderBy(h => h);
        // Docked windows can live on other virtual desktops now, so include their live
        // handles too. A foreign dock source closing must invalidate the layout even when
        // the current desktop's ordinary window set did not otherwise change.
        var docked = session.DockedSources.Where(Native.IsWindow).Select(h => h.ToInt64()).OrderBy(h => h);
        return catalog.LastCurrent + "|" + string.Join("|", handles) + "|D:" + string.Join(",", docked);
    }
    void ShowOptions(nint owner=0)
    {
        if(optionsWindow!=null){optionsWindow.Present();return;}
        var window=new OptionsWindow(settings,ApplyAppearance,ApplyLayoutSettings,()=>queue.TryEnqueue(ExitWithBanner),owner!=0?owner:primary.Handle);
        optionsWindow=window;
        SetFloatingPanel(window.Handle);
        window.Closed+=(_,_)=>{
            if(optionsWindow!=window)return;
            optionsWindow=null;
            SetFloatingPanel(popout?.Handle ?? 0);
        };
        window.Present();
    }
    // Safe to call repeatedly (Stop then Exit's Leaving); never throws into Exit's cleanup.
    void CloseOptions() { var window = optionsWindow; optionsWindow = null; SetFloatingPanel(popout?.Handle ?? 0); try { window?.Close(); } catch (Exception ex) { Log.Write("Options close: " + ex.Message); } }
    void ApplyAppearance()
    {
        foreach(var chrome in overlays.Values)chrome.ApplyAppearance();
        primary.ApplyAppearance();
    }
    void ApplyLayoutSettings()
    {
        ApplyAppearance();
        TrimBrowsed();
        session.ApplyAutoArrangeSetting();
        session.ApplyDockMinimizedSetting();
        if(session.Active&&!browsing&&!overlays.Values.Any(c=>c.IsDragging))session.Reflow();
    }
    // Exit shows the "Taskview++" banner, shuts down behind it (placements restored, helpers
    // stopped) once it has painted, then fades it and quits. Cosmetic: any banner failure
    // falls back to the plain immediate exit.
    bool exiting;
    void ExitWithBanner()
    {
        if(exiting)return;
        exiting=true;
        SplashWindow? banner=null;
        try{banner=new SplashWindow();banner.Activate();banner.KeepOnTop();}
        catch(Exception ex){Log.Write("Exit banner failed: "+ex.Message);banner=null;}
        if(banner==null){Stop();app.Exit();return;}
        // Backstop: exit must never hang on the banner.
        var backstop=new DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};
        backstop.Tick+=(_,_)=>{backstop.Stop();Log.Write("Exit banner backstop fired");try{Stop();}catch{}app.Exit();};
        backstop.Start();
        int frames=0;var painted=new FrameTimer();
        painted.Tick+=(_,_)=>{
            if(++frames<3)return;
            painted.Stop();
            try{Stop();}
            catch(Exception ex){Log.Write("Exit failed: "+ex);}
            try{banner.FadeOut(()=>app.Exit());}
            catch(Exception ex){Log.Write("Exit banner fade failed: "+ex.Message);app.Exit();}
        };
        painted.Start();
    }
    public void Stop() { if (stopped) return; stopped = true; CloseOptions(); timer.Stop(); session.Exit(); catalog.Dispose(); input.Dispose(); tray.Dispose(); emptySpace.Dispose(); app.ShutdownHelpers(); }
    public void EmergencyRestore() { try { if (session.Placements.Entries.Count > 0) session.Placements.Restore(); } catch (Exception ex) { Log.Write(ex.Message); } }
}
