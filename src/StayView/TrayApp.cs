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
    readonly TrayIcon tray;
    readonly DispatcherQueue queue;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    string signature = "";
    bool stopped;
    // While browsing, a clicked window sits in front of the overview. The overlays have
    // dropped topmost; the timer and foreground events must not yank them back on top.
    bool browsing;
    // The windows currently browsed in front of the grid, primary (session.Selected /
    // foreground) first, at most two. A third promotion demotes the oldest back under the
    // canvas so its tile reappears.
    readonly List<nint> browsed = new();
    int MaxBrowsed => Math.Clamp(settings.MaxBrowsedWindows, Settings.MaxBrowsedWindowsMin, Settings.MaxBrowsedWindowsMax);
    void Promote(nint h)
    {
        browsed.Remove(h); browsed.Insert(0, h);
        browsed.RemoveAll(b => b != h && (!Native.IsWindow(b) || !desktops.IsCurrent(b)));
        var demoted = browsed.Skip(MaxBrowsed).ToList();
        if (demoted.Count > 0) browsed.RemoveRange(MaxBrowsed, browsed.Count - MaxBrowsed);
        foreach (var chrome in overlays.Values) chrome.SetBrowsed(browsed);
        foreach (var d in demoted) Native.SetWindowPos(d, 1, 0, 0, 0, 0, 0x13); // HWND_BOTTOM
    }
    void ClearBrowsed() { browsed.Clear(); foreach (var chrome in overlays.Values) chrome.SetBrowsed(browsed); }
    // Settings lowered the cap while more windows were browsed: demote the oldest extras.
    void TrimBrowsed() { if (browsing && browsed.Count > MaxBrowsed && browsed.Count > 0) Promote(browsed[0]); }
    // Send one browsed window back under the canvas, keeping the other in front (and focused).
    void Demote(nint h)
    {
        browsed.Remove(h);
        var keep = browsed.FirstOrDefault();
        if (keep == 0) { ReturnToGrid(); return; }
        // Push the demoted window under the canvas first so it can never sit over the kept
        // one, then hand focus to the kept window (EnterBrowsing retries; on failure it
        // falls back to the grid itself).
        Native.SetWindowPos(h, 1, 0, 0, 0, 0, 0x13); // HWND_BOTTOM
        foreach (var chrome in overlays.Values) chrome.SetBrowsed(browsed);
        EnterBrowsing(keep);
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
        // Left-drag on the focused window's title bar moves it; keep the moved placement.
        input.BrowsedWindow = () => session.Selected;
        input.WindowDragged += h => queue.TryEnqueue(() => session.NoteUserMoved(h));
        input.Toggle += () => queue.TryEnqueue(HotkeyToggle);
        // Double-clicking anywhere on a browsed window shrinks it back to the grid. This is
        // an intentional gesture: the second click is consumed even when it lands in app content.
        input.DoubleClick += h => queue.TryEnqueue(() => {
            if (!session.Active || !browsing) return;
            // With two windows browsed, only the double-clicked one shrinks back; the other
            // stays in front. The last one out returns the grid.
            if (browsed.Count > 1 && browsed.Contains(h)) Demote(h); else HotkeyToggle();
        });
        input.Escape += () => queue.TryEnqueue(() => session.Exit());
        input.DesktopDirection += direction => queue.TryEnqueue(() => {
            var list = desktops.List(); int i = list.ToList().FindIndex(d => d.Current) + direction;
            if (i >= 0 && i < list.Count) session.ChangeDesktop(() => desktops.Switch(list[i].Id));
        });
        WireOverlay(primary);
        primary.ToggleRequested += () => queue.TryEnqueue(HotkeyToggle);
        primary.TrayMessage += message => {
            if (message == 0x202) HotkeyToggle();
            else if (message == 0x205) {
                switch (tray.Menu()) { case 1: session.Enter(); break; case 2: ShowOptions(); break; case 3: Stop(); app.Exit(); break; }
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
            }
            // Render re-asserts the overlay's topmost, which would bury an open popout.
            if(popout?.IsInteracting != true)popout?.BringToFront();
            optionsWindow?.BringToFront();
            // Seed the topology signature from the layout that was just rendered.
            // This prevents the timer's first tick from immediately repacking a
            // canvas the user may already have started arranging.
            signature = TopologySignature();
        };
        session.DesktopRemoved += id => { if(popout?.DesktopId==id)ClosePopout(); };
        session.Leaving += () => { activationVersion++; activating=false; ClosePopout(); CloseOptions(); browsing = false; browsed.Clear(); input.Browsing = false; input.SetOverview(false); foreach (var chrome in overlays.Values) chrome.Hide(); signature = ""; };
        // A desktop switch while browsing ends the browse: the focused window is on the
        // desktop we left. Clear the flags only — the switch's own reflow renders the new
        // grid, and with browsedSource cleared that render re-asserts the overview topmost.
        session.DesktopSwitched += () => {
            SyncPopoutForDesktop();
            if(browsing)
            {
                browsing = false; input.Browsing = false; activating = false; activationVersion++;
                ClearBrowsed();
            }
        };
        catalog.ForegroundChanged += () => queue.TryEnqueue(() => {
            if(!session.Active || activating) return;
            // OS-level desktop switches do not raise OverviewSession.DesktopSwitched.
            // Synchronize before any z-order work so a pinned popout representing the new
            // current desktop cannot be raised briefly before the timer notices the switch.
            SyncPopoutForDesktop();
            if(browsing) { ReconcileBrowse(); return; }
            // The overview keeps itself above source windows; an open popout is then put
            // back on top of it, rather than suppressing the overview's own z-order work.
            foreach(var chrome in overlays.Values) chrome.KeepAbove();
            if(popout?.IsInteracting != true)popout?.BringToFront();
            optionsWindow?.BringToFront();
        });
        timer.Tick += (_, _) => {
            if (!session.Active) return;
            // Keep this ahead of all interaction early-outs. A Win+Ctrl+Arrow / Task View
            // switch must hide or restore the pinned popout even during browse/drag state.
            SyncPopoutForDesktop();
            // Runs even while browsing (no reflow happens then), so a window closed from
            // the app itself never leaves a clickable ghost tile behind on the canvas.
            foreach (var chrome in overlays.Values) chrome.PruneDeadTiles();
            // Runs while browsing, which is exactly when the focused window is being
            // moved, resized, maximized or snapped by the user.
            if (browsing) { foreach (var b in browsed.ToList()) session.SyncUserGeometry(b); ReconcileBrowse(); }
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
    OverlayChrome NewOverlay() { var chrome = new OverlayChrome(session, settings, ShowOptions); WireOverlay(chrome); return chrome; }
    void WireOverlay(OverlayChrome chrome)
    {
        chrome.FloatingPanel = popout?.Handle ?? 0;
        chrome.TileActivated += h => queue.TryEnqueue(() => EnterBrowsing(h));
        chrome.TileClose += h => queue.TryEnqueue(() => session.Close(h));
        chrome.TileDragMoved += (h,p) => {
            var target = popout;
            if (target == null) return;
            bool over = target.ContainsScreenPoint(p);
            target.SetDropTarget(over);
            if (over) target.ShowDragPreview(h,p);
        };
        chrome.TileDragEnded += () => { popout?.SetDropTarget(false); popout?.ClearDragPreview(); };
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
        if (!session.MoveToDesktop(h, target.DesktopId)) return false;
        target.Refresh();
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
        w.UserCloseRequested += id => { foreach(var chrome in overlays.Values)chrome.FlashDesktopCard(id); };
        w.Dismissed += () => { if (popout == w) { popout = null; SetFloatingPanel(0); } };
        w.JumpToWindow += (id, h) => queue.TryEnqueue(() => JumpToDesktopWindow(id, h));
        popout = w;
        w.Present(work);
        SetFloatingPanel(w.Handle);
    }
    // An unknown id (no virtual-desktop API) means the popout would show the current desktop.
    bool IsCurrentDesktop(Guid id) => id == Guid.Empty || id == desktops.Current;
    // Clicking a window in the popout jumps to that desktop and browses the window.
    void JumpToDesktopWindow(Guid id, nint h)
    {
        if (!session.Active) return;
        if (id != Guid.Empty && id != desktops.Current && !session.ChangeDesktop(() => desktops.Switch(id))) return;
        EnterBrowsing(h);
    }

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
        if (!session.Active || h==0 || !Native.IsWindow(h)) return;
        int version=++activationVersion;
        activating=true;
        // The overview must stop being topmost before SetForegroundWindow can succeed,
        // but don't commit browsing/suppression until activation has actually succeeded.
        try
        {
            foreach (var chrome in overlays.Values) chrome.DropTopmost();
            for(int attempt=0;attempt<10;attempt++)
            {
                if(version!=activationVersion || !session.Active)return;
                if(!Native.IsWindow(h))break;
                if(session.Activate(h))
                {
                    browsing=true;
                    input.Browsing=true;
                    Promote(h);
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
                if(session.Active && !browsing)
                    { ClearBrowsed(); foreach(var chrome in overlays.Values) chrome.RaiseTopmost(); }
            }
        }
    }
    void HotkeyToggle()
    {
        if (session.Active && browsing) { ReturnToGrid(); return; }
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
        if(session.DragActive || overlays.Values.Any(c => c.IsDragging)) return; // never interrupt a gesture
        // The browsed window left the current desktop (a switch by either StayView or
        // Windows): show the new desktop's grid rather than auto-browsing whatever is here.
        if(session.Selected == 0 || !Native.IsWindow(session.Selected) || !desktops.IsCurrent(session.Selected))
        {
            // Primary is gone (closed / moved desktop). If the second browsed window is still
            // here, it carries on alone rather than dropping both to the grid.
            var survivor = browsed.FirstOrDefault(b => b != session.Selected && Native.IsWindow(b) && desktops.IsCurrent(b));
            browsed.Remove(session.Selected);
            if(survivor != 0) { EnterBrowsing(survivor); return; }
            ReturnToGrid(); return;
        }
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
        switch(BrowseReconciler.ClassifyForeground(session.Selected, fg, root, ownCanvas, target))
        {
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
    void ReturnToGrid()
    {
        browsing = false;
        input.Browsing = false;
        // Restore the miniatures first so there is no one-frame hole when the browsed
        // real windows are sent back.
        ClearBrowsed();
        // If a canvas gesture is in progress (a tile was just pressed), stop here: a
        // SetWindowPos on the overview while the pointer is captured can cancel the
        // gesture, and the gesture's own end (drop/dock/undock/activate) reflows anyway,
        // which re-asserts the grid's z-order now that browsedSource is cleared.
        if (overlays.Values.Any(c => c.IsDragging)) return;
        foreach (var chrome in overlays.Values) chrome.RaiseTopmost();
        popout?.BringToFront();
        session.Reflow();
    }
    string TopologySignature()
    {
        // Enumerate resolves the current desktop id for this pass; reuse it via
        // LastCurrent instead of a second broker round-trip for desktops.Current.
        var handles = catalog.Enumerate().Select(w => w.Handle.ToInt64()).OrderBy(h => h);
        return catalog.LastCurrent + "|" + string.Join("|", handles);
    }
    void ShowOptions(nint owner=0)
    {
        if(optionsWindow!=null){optionsWindow.Present();return;}
        var window=new OptionsWindow(settings,ApplyAppearance,ApplyLayoutSettings,ApplySmallWindowSize,()=>queue.TryEnqueue(()=>{Stop();app.Exit();}),owner!=0?owner:primary.Handle);
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
    void ApplySmallWindowSize()
    {
        session.ApplySmallWindowSizeSetting();
        if(session.Active&&!browsing&&!overlays.Values.Any(c=>c.IsDragging))session.Reflow();
    }
    public void Stop() { if (stopped) return; stopped = true; CloseOptions(); timer.Stop(); session.Exit(); catalog.Dispose(); input.Dispose(); tray.Dispose(); app.ShutdownHelpers(); }
    public void EmergencyRestore() { try { if (session.Placements.Entries.Count > 0) session.Placements.Restore(); } catch (Exception ex) { Log.Write(ex.Message); } }
}
