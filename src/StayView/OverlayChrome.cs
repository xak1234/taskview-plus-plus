using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StayView.Core;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace StayView;

sealed class OverlayChrome : Window
{
    readonly Grid root = new();
    readonly Border backdropLayer = new(){IsHitTestVisible=false};
    readonly Border stripBar = new(){IsHitTestVisible=false,CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1)};
    readonly Canvas canvas = new();
    readonly OverviewSession session;
    readonly Settings settings;
    readonly Action<nint> showOptions;
    readonly Native.WndProc procedure;
    readonly nint originalProc;
    readonly DockView tilesView;
    readonly OverlappedPresenter presenter;
    // Desktop-card preview thumbnails, keyed by source window so a repositioning pass
    // reuses each registration instead of unregistering and re-registering it (which
    // blinks every preview whenever any window moves). Each source belongs to exactly
    // one desktop, so it maps to at most one card thumbnail.
    readonly Dictionary<nint, nint> desktopThumbBySource = [];
    readonly List<(DesktopInfo Desktop, Native.RECT Picture)> pictures = [];
    Native.RECT work;
    double scale = 1;
    bool pinned;
    string pictureSignature = "";
    int transitionVersionSeen;
    public nint Handle { get; }
    // Keep the overview underneath its floating panel, including during reflows.
    public nint FloatingPanel { get; set; }
    nint ZOrderTarget => FloatingPanel != 0 && Native.IsWindowVisible(FloatingPanel) ? FloatingPanel : -1;
    public bool IsDragging => tilesView.IsDragging;
    public event Action? ToggleRequested;
    public event Action<int>? TrayMessage;
    public event Action<nint>? TileActivated;
    public event Action<nint>? TileClose;
    public event Action<nint, Native.POINT>? TileDragMoved;
    public event Action? TileDragEnded;
    public event Action? OverviewPointerDown;
    // A left press on empty overview canvas (no tile hit) — used to return to the grid
    // while browsing, without stealing presses that land on a tile (so tiles stay draggable).
    public event Action? BackgroundPressed;
    public Func<nint, Native.POINT, bool>? ExternalTileDrop { get => tilesView.ExternalDrop; set => tilesView.ExternalDrop = value; }
    // A desktop card was double-clicked: pop that desktop out (with the work rect for placement).
    public event Action<DesktopInfo, Native.RECT>? DesktopPopoutRequested;
    // Change background was chosen for a desktop card. The handoff to Windows' own
    // picker needs the overview's z-order and foreground state, which TrayApp owns.
    public event Action<DesktopInfo>? DesktopBackgroundRequested;
    // Single vs double click on a desktop card. Add a short grace period after the
    // Windows double-click interval because WinUI can deliver the second tap slightly
    // after the native threshold under load.
    readonly DispatcherTimer switchTimer = new() { Interval = TimeSpan.FromMilliseconds(Math.Max(500d, Native.GetDoubleClickTime() + 150d)) };
    Guid pendingSwitch;
    readonly Dictionary<Guid,Border> desktopCards = [];

    public OverlayChrome(OverviewSession session, Settings settings, Action<nint> showOptions)
    {
        this.session = session; this.settings=settings;this.showOptions=showOptions;
        Title = "Taskview++";
        canvas.RequestedTheme = ElementTheme.Dark;
        canvas.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        root.Children.Add(backdropLayer);root.Children.Add(canvas);Content = root;
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Native.DisableDwmScreenFrame(Handle);
        presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        // SetBorderAndTitleBar(false,false) still leaves a classic dialog-frame style on
        // some Windows App SDK builds. Remove those native frame bits while still hidden.
        Native.MakeOverviewTrueBorderless(Handle);
        // Cosmetic only. Some Windows App SDK / shell combinations return
        // E_NOTIMPL here even though the HWND already has WS_EX_TOOLWINDOW.
        // Never let that optional switcher hint prevent StayView from starting.
        try { AppWindow.IsShownInSwitchers = false; }
        catch (NotImplementedException) { }
        Native.SetWindowLongPtr(Handle, -20, (nint)(Native.GetWindowLongPtr(Handle, -20).ToInt64() | 0x80));
        procedure = WndProc;
        originalProc = Native.SetWindowLongPtr(Handle, -4, Marshal.GetFunctionPointerForDelegate(procedure));
        tilesView = new DockView(Handle, canvas, session, settings);
        tilesView.DockHover += over => stripBar.BorderBrush = over ? GlassAppearance.ActiveBrush() : GlassAppearance.StripEdgeBrush();
        tilesView.TileActivated += h => TileActivated?.Invoke(h);
        tilesView.BackgroundPressed += () => BackgroundPressed?.Invoke();
        tilesView.TileClose += h => TileClose?.Invoke(h);
        tilesView.TileDragMoved += (h,p) => TileDragMoved?.Invoke(h,p);
        tilesView.TileDragEnded += () => TileDragEnded?.Invoke();
        switchTimer.Tick += (_, _) => { switchTimer.Stop(); var id = pendingSwitch; if (id != Guid.Empty) session.ChangeDesktop(() => session.Desktops.Switch(id)); };
        ApplyAppearance();
        Closed += (_, _) => { switchTimer.Stop(); tilesView.Dispose(); ClearDesktopThumbnails(); if (session.Active) session.Exit(); };
    }
    nint WndProc(nint h, uint msg, nint wp, nint lp)
    {
        if (TrayIcon.HandleOwnerDraw(msg,lp,out var menuResult)) return menuResult;
        if (msg == 0x312) { ToggleRequested?.Invoke(); return 0; }
        if (msg == 0x8001) { TrayMessage?.Invoke((int)(lp.ToInt64() & 0xffff)); return 0; }
        if(msg is 0x201 or 0x204 or 0x207)OverviewPointerDown?.Invoke();
        if (msg == 0x21) return 3; // MA_NOACTIVATE: clicking a thumbnail may focus its source.
        // WM_ACTIVATE while browsing: MA_NOACTIVATE only stops the click itself. WinUI's
        // island still focuses (and so activates) this HWND on a tile press, and activation
        // raises the opaque canvas over the browsed window whose tile is suppressed — the
        // "focused window vanishes when you drag another tile" bug. Put the canvas straight
        // back underneath the browsed window; the reconciler re-focuses it after the gesture.
        if (msg == 0x06 && (wp.ToInt64() & 0xffff) != 0) KeepBelowBrowsed();
        if (msg == 0x14)
        {
            Native.GetClientRect(h, out var r);
            Native.FillRect(wp, ref r, Native.GetStockObject(4)); // Solid black even before XAML paints.
            return 1;
        }
        return Native.CallWindowProc(originalProc, h, msg, wp, lp);
    }
    public void Render(nint monitor, IReadOnlyList<Tile> tiles, string hotkey)
    {
        if (IsDragging) return;
        work = Native.WorkArea(monitor);
        AppWindow.MoveAndResize(new RectInt32(work.Left, work.Top, work.Width, work.Height));
        scale = Math.Max(1, Native.GetDpiForWindow(Handle) / 96d);
        // One full work-area surface only. Never use a cut-out region: moving a
        // thumbnail must not expose a raw-desktop hole beneath it.
        Native.SetWindowRgn(Handle, 0, true);
        tilesView.Clear();
        // Deliberately NOT ClearDesktopThumbnails() here. The desktop-card previews are DWM
        // registrations against this HWND, not XAML children, so they survive the canvas
        // rebuild below. Tearing all of them down on every reflow and re-registering them
        // blinked every preview on each tile drop/dock/undock/grid return. Resetting the
        // signature makes UpdateDesktopPictures reposition every preview onto the rebuilt
        // cards, reusing each registration and unregistering only sources that have left.
        pictureSignature = "";
        canvas.Children.Clear();
        canvas.Width = work.Width / scale; canvas.Height = work.Height / scale;
        backdropLayer.Width=canvas.Width;backdropLayer.Height=canvas.Height;
        ApplyAppearance();
        BuildDesktopStrip();
        var options = new Button { Content = "⚙", Width=36, Height=32, Padding=new Thickness(0),
            Background = GlassAppearance.SurfaceBrush2(settings.GlassOpacity), Foreground = GlassAppearance.PrimaryBrush(),
            CornerRadius = new CornerRadius(7), BorderThickness=new Thickness(0) };
        options.Click += (_, _) => showOptions(Handle);
        Add(options, 14, 14);
        // No on-canvas exit button: Esc dismisses the overview, and the tray menu (or the
        // Exit button in Options) quits the app.
        var transition=session.DesktopTransitionVersion!=transitionVersionSeen?session.DesktopTransition:DesktopTransitionMode.Appear;
        transitionVersionSeen=session.DesktopTransitionVersion;
        tilesView.Render(tiles, work, scale, transition);
        root.UpdateLayout();
        AppWindow.Show(false);
        // WinUI may rewrite presenter styles during Show. Reassert the real borderless
        // HWND state, not just DWM colouring, so no 1 px perimeter survives.
        Native.MakeOverviewTrueBorderless(Handle);
        if (!pinned) pinned = session.Desktops.PinOwnWindow(Handle);
        if (browsed.Count == 0)
        {
            // Grid is up: WinUI can drop the native TOPMOST bit when a previously hidden
            // AppWindow is shown. Reassert the presenter state after Show so the overview
            // grid remains above every source HWND until a tile is clicked.
            if (!presenter.IsAlwaysOnTop) presenter.IsAlwaysOnTop = true;
            Native.SetWindowPos(Handle, ZOrderTarget, 0, 0, 0, 0, 0x13);
        }
        // While browsing, the overview deliberately stays NOTOPMOST behind the focused
        // window. Re-asserting topmost here — which every mid-browse reflow used to do —
        // put the opaque canvas over that window while its tile was still suppressed, so
        // the window the user was working in simply vanished. Pin it directly beneath.
        else KeepBelowBrowsed();
        // Start any desktop-delivery transition only after the refreshed overview has
        // actually been shown and its z-order/composition state is settled. Starting it
        // inside Render() made short animations race the Show() pass and appear invisible.
        tilesView.StartPreparedTransition();
        LowerSmallSources();
        UpdateDesktopPictures();
    }
    // Browsing: a clicked window must sit in front of the overview, so the overlay
    // drops its always-on-top and stops holding sources at the bottom.
    public void DropTopmost()
    {
        // The canvas is about to sit behind the focused window; any hover glow left
        // showing would be stranded there as a ghost outline.
        tilesView.ClearHover();
        presenter.IsAlwaysOnTop = false;
        Native.SetWindowPos(Handle, -2, 0, 0, 0, 0, 0x13); // HWND_NOTOPMOST
    }
    // The window currently browsed in front of this overview (0 when the grid is up).
    // Render consults it so a reflow that happens mid-browse — a tile dropped, docked or
    // undocked on the canvas still visible around the focused window — never re-raises
    // the overview over that window or lowers it to the bottom of the z-order.
    // Up to two windows browsed in front of this overview, primary (foreground) first.
    readonly List<nint> browsed = new();
    public void SetBrowsedSource(nint source) => SetBrowsed(source == 0 ? Array.Empty<nint>() : new[] { source });
    public void SetBrowsed(IReadOnlyList<nint> sources)
    {
        browsed.Clear(); browsed.AddRange(sources.Where(s => s != 0).Distinct().Take(Settings.MaxBrowsedWindowsMax));
        tilesView.SetSuppressed(browsed);
        KeepBelowBrowsed();
    }
    // Enforce primary > secondary > canvas in the z-order without changing activation.
    // Idempotent; a no-op when the grid is up.
    void KeepBelowBrowsed()
    {
        if (browsed.Count == 0 || !Native.IsWindowVisible(Handle)) return;
        var live = browsed.Where(Native.IsWindow).ToList();
        if (live.Count == 0) return;
        if (presenter.IsAlwaysOnTop) presenter.IsAlwaysOnTop = false;
        for (int i = 1; i < live.Count; i++) Native.SetWindowPos(live[i], live[i - 1], 0, 0, 0, 0, 0x13); // secondary directly under primary
        Native.SetWindowPos(Handle, live[^1], 0, 0, 0, 0, 0x13); // canvas directly under the lowest browsed
    }
    // Re-summon the grid on top and push every source back to the bottom of the z-order.
    public void RaiseTopmost()
    {
        if (!Native.IsWindowVisible(Handle)) return;
        if (!presenter.IsAlwaysOnTop) presenter.IsAlwaysOnTop = true;
        Native.SetWindowPos(Handle, ZOrderTarget, 0, 0, 0, 0, 0x13);
        LowerSmallSources();
    }
    void BuildDesktopStrip()
    {
        pictures.Clear();
        desktopCards.Clear();
        var desktops = session.Desktops.List();
        if (desktops.Count == 0) desktops = [new(session.Desktops.Current, "Current desktop", true)];
        bool canCreateDesktop=session.Desktops.Available;
        int cardCount=desktops.Count+(canCreateDesktop?1:0);
        double gap = 28, available = Math.Max(1, canvas.Width - 48);
        double width = Math.Min(242, Math.Max(28, (available - (cardCount - 1) * gap) / cardCount));
        double height = width * 9 / 16, left = (canvas.Width - cardCount * width - (cardCount - 1) * gap) / 2;
        double cardTop=settings.DesktopStripPosition==DesktopStripPosition.Bottom?Math.Max(20,canvas.Height-height-22):20;
        double labelTop=settings.DesktopStripPosition==DesktopStripPosition.Bottom?cardTop-16:cardTop+height+7;
        // Full-width glass bar behind the cards, inside the strip's no-drop reservation.
        double barTop=settings.DesktopStripPosition==DesktopStripPosition.Bottom?labelTop-9:8;
        double barBottom=settings.DesktopStripPosition==DesktopStripPosition.Bottom?canvas.Height-8:labelTop+20;
        stripBar.Width=Math.Max(1,canvas.Width-16);stripBar.Height=Math.Max(1,barBottom-barTop);
        Add(stripBar,8,barTop);
        // Dock lanes: one either side of the desktop cards, clear of the ⚙ button when the
        // bar is on top. The same outer margin is reserved on both sides so the two lanes
        // are equally wide and a docked mini is the same size on either of them. CardGap
        // is the clear channel that keeps the nearest docked mini off the cards.
        const double CardGap=96;
        double dockOuter=settings.DesktopStripPosition==DesktopStripPosition.Top?62:20;
        double cardsRight=left+cardCount*width+(cardCount-1)*gap;
        int laneTop=work.Top+(int)Math.Round(cardTop*scale),laneHeight=(int)Math.Round(height*scale);
        tilesView.SetStrip(
            new Native.RECT(work.Left+(int)Math.Round(8*scale),work.Top+(int)Math.Round(barTop*scale),(int)Math.Round(stripBar.Width*scale),(int)Math.Round(stripBar.Height*scale)),
            new Native.RECT(work.Left+(int)Math.Round(dockOuter*scale),laneTop,Math.Max(0,(int)Math.Round((left-CardGap-dockOuter)*scale)),laneHeight),
            new Native.RECT(work.Left+(int)Math.Round((cardsRight+CardGap)*scale),laneTop,Math.Max(0,(int)Math.Round((canvas.Width-dockOuter-CardGap-cardsRight)*scale)),laneHeight));
        for (int desktopIndex=0; desktopIndex<desktops.Count; desktopIndex++)
        {
            var desktop=desktops[desktopIndex];
            var picture = new Grid {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 32, 44)), CornerRadius = new CornerRadius(8) };
            var wallpaper = desktop.Wallpaper;
            if (string.IsNullOrWhiteSpace(wallpaper))
                wallpaper = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", "") as string ?? "";
            if (System.IO.File.Exists(wallpaper))
            {
                try { picture.Children.Add(new Image { Source = new BitmapImage(new Uri(wallpaper)), Stretch = Stretch.UniformToFill }); }
                catch (Exception ex) { Log.Write("Wallpaper: " + ex.Message); }
            }
            // A Border has no Button focus/pressed chrome, so the only blue mark
            // is this tight rounded selection outline on the active miniature.
            var card = new Border { Width = width, Height = height, Child = picture,
                CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(desktop.Current ? Windows.UI.Color.FromArgb(255, 109, 175, 255) : Microsoft.UI.Colors.Transparent),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 32, 44)) };
            AutomationPropertiesName(card, desktop.Name);
            var target = desktop;
            if(target.Id!=Guid.Empty)desktopCards[target.Id]=card;
            // Single click switches after the double-click window; this lets a second click
            // cancel the switch and open the desktop popout instead.
            card.Tapped += (_, _) => { if (target.Current || target.Id == session.Desktops.Current) return; pendingSwitch = target.Id; switchTimer.Stop(); switchTimer.Start(); };
            // Double click pops out alternate desktops only; the current one would nest the overview.
            card.DoubleTapped += (_, _) => { switchTimer.Stop(); pendingSwitch = Guid.Empty; if (!target.Current) DesktopPopoutRequested?.Invoke(target, work); };
            if (session.Desktops.Available && target.Id != Guid.Empty)
                card.ContextFlyout = DesktopMenu(target,desktopIndex,desktops.Count);
            Add(card, left, cardTop);
            // Tiny uppercase monospace (terminal) label under/over each desktop card.
            Add(new TextBlock { Text = desktop.Name.ToUpperInvariant(), FontSize = 7,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), CharacterSpacing = 60,
                Foreground = GlassAppearance.SecondaryBrush(),
                Width = width, TextAlignment = TextAlignment.Center }, left, labelTop);
            pictures.Add((desktop, new Native.RECT(work.Left + (int)((left + 2) * scale), work.Top + (int)((cardTop+2) * scale),
                Math.Max(1, (int)((width - 4) * scale)), Math.Max(1, (int)((height - 4) * scale)))));
            left += width + gap;
        }
        if(canCreateDesktop)
        {
            var plus=new TextBlock {
                Text="+",FontSize=Math.Clamp(width*.28,22,52),FontWeight=Microsoft.UI.Text.FontWeights.Light,
                Foreground=GlassAppearance.PrimaryBrush(),HorizontalAlignment=HorizontalAlignment.Center,
                VerticalAlignment=VerticalAlignment.Center,TextAlignment=TextAlignment.Center
            };
            var addCard=new Border {
                Width=width,Height=height,Child=plus,CornerRadius=new CornerRadius(9),
                BorderThickness=new Thickness(2),BorderBrush=GlassAppearance.EdgeBrush(),
                Background=GlassAppearance.SurfaceBrush2(Math.Max(35,settings.GlassOpacity-20))
            };
            AutomationPropertiesName(addCard,"New desktop");
            addCard.Tapped+=(_,e)=>{
                switchTimer.Stop();pendingSwitch=Guid.Empty;e.Handled=true;
                session.ChangeDesktop(()=>session.Desktops.Create());
            };
            Add(addCard,left,cardTop);
            Add(new TextBlock { Text="NEW DESKTOP",FontSize=7,
                FontFamily=new FontFamily("Cascadia Mono, Consolas"),CharacterSpacing=60,
                Foreground=GlassAppearance.SecondaryBrush(),Width=width,TextAlignment=TextAlignment.Center },left,labelTop);
        }
    }
    public void FlashDesktopCard(Guid id)
    {
        if(id==Guid.Empty||!desktopCards.TryGetValue(id,out var card))return;
        var normalBrush=card.BorderBrush;
        var normalThickness=card.BorderThickness;
        card.BorderBrush = GlassAppearance.DesktopCloseGlowBrush();
        card.BorderThickness = new Thickness(3);
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(180)};
        timer.Tick+=(_,_)=>{
            timer.Stop();
            // A reflow may have rebuilt the strip while the flash was active. Only restore
            // this Border if it is still the current visual for that desktop id.
            if(desktopCards.TryGetValue(id,out var current)&&ReferenceEquals(current,card))
            {
                card.BorderBrush=normalBrush;
                card.BorderThickness=normalThickness;
            }
        };
        timer.Start();
    }

    MenuFlyout DesktopMenu(DesktopInfo desktop,int index,int count)
    {
        var menu=new MenuFlyout();GlassAppearance.StyleGreyMenu(menu);
        MenuFlyoutItem Item(string text,Action action)
        {
            var item=new MenuFlyoutItem{Text=text,Foreground=GlassAppearance.MenuWhiteBrush()};
            item.Click+=(_,_)=>action();return item;
        }
        // A desktop can only be closed when Windows has somewhere else to put its
        // windows. Left/right actions exist only when a neighbour exists in that direction.
        if(count>1)menu.Items.Add(Item("Close",()=>{
            switchTimer.Stop();pendingSwitch=Guid.Empty;
            session.RemoveDesktop(desktop.Id);
        }));
        if(!desktop.Current)menu.Items.Add(Item("Pop out",()=>DesktopPopoutRequested?.Invoke(desktop,work)));
        menu.Items.Add(Item("Change background",()=>ChangeDesktopBackground(desktop)));
        if(index<count-1)menu.Items.Add(Item("Move right",()=>MoveDesktop(desktop.Id,index+1)));
        if(index>0)menu.Items.Add(Item("Move left",()=>MoveDesktop(desktop.Id,index-1)));
        return menu;
    }
    void MoveDesktop(Guid id,int index)
    {
        switchTimer.Stop();pendingSwitch=Guid.Empty;
        if(session.Desktops.MoveDesktop(id,index))session.Reflow();
    }
    void ChangeDesktopBackground(DesktopInfo desktop)
    {
        switchTimer.Stop();pendingSwitch=Guid.Empty;
        DesktopBackgroundRequested?.Invoke(desktop);
    }
    static void AutomationPropertiesName(DependencyObject target, string name)
        => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(target, name);

    public void UpdateDesktopPictures()
    {
        if (!Native.IsWindowVisible(Handle) || IsDragging) return;
        var windows = session.DesktopWindows();
        var next = string.Join("|", windows.Select(w => {
            Native.GetWindowRect(w.Handle, out var r);
            return $"{w.Handle}:{w.Minimized}:{r.Left},{r.Top},{r.Width},{r.Height}:{session.Desktops.WindowDesktop(w.Handle)}";
        }));
        if(next == pictureSignature) return;
        pictureSignature = next;
        var screen = Native.MonitorBounds(Native.MonitorFromWindow(Handle, 2));
        var live = new HashSet<nint>();
        foreach (var (desktop, picture) in pictures)
        {
            // Reverse EnumWindows' front-to-back order for correct composition.
            foreach (var w in windows.Reverse())
            {
                if (Native.IsIconic(w.Handle)) continue;
                var id = session.Desktops.WindowDesktop(w.Handle);
                if (desktop.Id != Guid.Empty)
                {
                    // Realtek Audio Console was observed returning Guid.Empty at
                    // (40,32); treating that as "all desktops" produced Desktop 1's
                    // solid blue top-left blob. Unknown desktop ownership is now
                    // omitted rather than composited into every real desktop card.
                    if (id == Guid.Empty || id != desktop.Id) continue;
                }
                else if (!session.Desktops.IsCurrent(w.Handle)) continue;
                if (!Native.GetWindowRect(w.Handle, out var source) || !source.Intersects(screen)) continue;
                // Reuse this source's existing registration; only register once. Tearing
                // every thumbnail down and rebuilding it each pass blinked all previews
                // whenever any single window moved or animated.
                if (!desktopThumbBySource.TryGetValue(w.Handle, out var thumb))
                {
                    if (Native.DwmRegisterThumbnail(Handle, w.Handle, out thumb) != 0) continue;
                    desktopThumbBySource[w.Handle] = thumb;
                }
                var dest = new Native.RECT(
                    picture.Left + (int)Math.Round((source.Left - screen.Left) * picture.Width / (double)screen.Width),
                    picture.Top + (int)Math.Round((source.Top - screen.Top) * picture.Height / (double)screen.Height),
                    Math.Max(1, (int)Math.Round(source.Width * picture.Width / (double)screen.Width)),
                    Math.Max(1, (int)Math.Round(source.Height * picture.Height / (double)screen.Height)));
                var clip = DockView.Intersect(dest, picture);
                if (clip.Width < 1 || clip.Height < 1) continue;
                Native.DwmQueryThumbnailSourceSize(thumb, out var size);
                if (size.X < 1 || size.Y < 1) continue;
                var crop = new Native.RECT((int)((clip.Left - dest.Left) * size.X / (double)dest.Width),
                    (int)((clip.Top - dest.Top) * size.Y / (double)dest.Height),
                    Math.Max(1, (int)(clip.Width * size.X / (double)dest.Width)), Math.Max(1, (int)(clip.Height * size.Y / (double)dest.Height)));
                var props = new Native.THUMBNAIL { Flags = 31, Destination = new(clip.Left - work.Left, clip.Top - work.Top, clip.Width, clip.Height),
                    Source = crop, Visible = true, Opacity = 255, SourceClientOnly = false };
                if (Native.DwmUpdateThumbnailProperties(thumb, ref props) == 0) live.Add(w.Handle);
            }
        }
        // Drop only the previews whose source is no longer shown (closed, minimized, moved
        // to another desktop, or off-screen), leaving every reused registration in place.
        foreach (var gone in desktopThumbBySource.Keys.Where(k => !live.Contains(k)).ToList())
        { Native.DwmUnregisterThumbnail(desktopThumbBySource[gone]); desktopThumbBySource.Remove(gone); }
    }
    void ClearDesktopThumbnails() { foreach (var t in desktopThumbBySource.Values) Native.DwmUnregisterThumbnail(t); desktopThumbBySource.Clear(); }
    public void ApplyAppearance() {
        GlassAppearance.ApplyBackdrop(this,settings);
        backdropLayer.Background=GlassAppearance.OverviewBackdropBrush(settings);
        stripBar.Background=GlassAppearance.StripBrush(settings);
        stripBar.BorderBrush=GlassAppearance.StripEdgeBrush();
    }
    public void KeepAbove() {
        // Z-order maintenance is unnecessary during a tile drag and can force extra
        // composition work in the middle of the pointer stream.
        if(!Native.IsWindowVisible(Handle) || IsDragging || session.DragActive)return;
        if (!presenter.IsAlwaysOnTop) presenter.IsAlwaysOnTop = true;
        Native.SetWindowPos(Handle,ZOrderTarget,0,0,0,0,0x13);
        LowerSmallSources();
        var foreground=Native.GetForegroundWindow();
        if(foreground!=Handle)tilesView.Select(foreground);
    }
    void LowerSmallSources()
    {
        // DWM thumbnails are the interactive small windows. Keep the real source
        // HWNDs at the bottom of the normal z-order while Overview is active so a
        // stationary source can never cover/fight the moving thumbnail. This is
        // z-order only: no source position or size changes, and PlacementStore
        // restores the original order/topmost state when Overview exits.
        // The browsed window is the one the user is working in, in front of the overview;
        // it is never sent to the bottom by a reflow that happens while it is focused.
        foreach(var source in tilesView.Sources)
            if(!browsed.Contains(source) && Native.IsWindow(source))
                Native.SetWindowPos(source,1,0,0,0,0,0x13);
    }
    // Drop tiles whose source window has closed, so no clickable ghost is left behind.
    public void PruneDeadTiles() => tilesView.PruneDead();
    public void ApplyRegion() { } // No holes, ever.
    public void Hide() { tilesView.SuppressSource(0); tilesView.Clear(); ClearDesktopThumbnails(); AppWindow.Hide(); }
    void Add(UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element); }
}
