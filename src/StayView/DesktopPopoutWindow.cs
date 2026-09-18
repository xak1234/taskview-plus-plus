using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StayView.Core;
using Windows.Graphics;

namespace StayView;

// A double-clicked virtual desktop popped out as its own borderless, always-on-top
// window: a live scaled copy (wallpaper + DWM thumbnails of that desktop's windows)
// that can be dragged by its header, resized by the corner grip, and whose windows can
// be clicked to jump to that desktop. The thumbnails are visual copies (not embedded
// controls), so clicking one switches+focuses rather than interacting in place.
sealed class DesktopPopoutWindow : Window
{
    readonly OverviewSession session;
    readonly Guid desktopId;
    readonly Border frame;
    readonly Border header;
    readonly Border body = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    readonly Image wallpaper = new() { Stretch = Stretch.UniformToFill };
    readonly Button closeButton;
    readonly Border grip;
    readonly OverlappedPresenter presenter;
    // Keyed by source window so a refresh can reuse thumbnails instead of unregistering
    // and re-registering them all, which made the panel flicker whenever a window moved.
    readonly Dictionary<nint, nint> thumbBySource = [];
    readonly List<(nint Handle, Native.RECT Rect)> windowRects = []; // client physical px
    nint dragPreviewThumb;
    nint dragPreviewSource;
    public nint Handle { get; }
    public Guid DesktopId => desktopId;
    double scale = 1;
    string contentSig = "";
    bool suspended;
    bool pinned;
    // 0 = none, 1 = move (header), 2 = resize (grip)
    int gestureMode;
    uint gesturePointer;
    Native.POINT gestureStartCursor;
    RectInt32 gestureStartRect;
    bool bodyPressed, bodyMoved;
    bool bodyWindowMoved,bodyMoveFailedLogged;
    nint bodyWindow;
    Native.RECT bodyWindowStart;
    // A click on a window jumps to it, but only after the double-click interval, so a
    // second click can instead put the desktop back in the bar.
    readonly DispatcherTimer jumpTimer = new() { Interval = TimeSpan.FromMilliseconds(Native.GetDoubleClickTime()) };
    nint pendingJump;
    long lastBodyClick;
    Native.POINT lastBodyClickPoint;
    public event Action? Dismissed;
    public event Action<Guid>? UserCloseRequested;
    public event Action<Guid, nint>? JumpToWindow;
    public bool IsInteracting => gestureMode != 0 || bodyPressed;
    public bool Suspended => suspended;

    const double HeaderH = 30, SideInset = 16, GripSize = 16, GripReserve = 26, MinW = 260, MinH = 190;
    // A desktop popout is a preview panel. The resize range is bounded to a 40% work-area
    // footprint, even after it is moved to another monitor. It opens 25% smaller than that
    // cap by default (0.40 x 0.75), and the user can still drag it out to the full 40%.
    const double MaxWorkAreaFraction = 0.40;
    const double DefaultWorkAreaFraction = MaxWorkAreaFraction * 0.75;
    static readonly (double Margin, double Thickness, byte Alpha, double Radius)[] GlowRings =
        { (2, 3, 190, 7), (5, 4, 105, 6), (9, 5, 55, 5) };

    public DesktopPopoutWindow(OverviewSession session, Settings settings, DesktopInfo desktop, nint owner)
    {
        this.session = session; desktopId = desktop.Id;
        Title = "Taskview++ desktop";
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Native.DisableDwmBorder(Handle);
        // Make the popout a true owned top-level window of the overview that spawned it.
        // Windows then keeps it above that overview as a stable z-order relationship,
        // instead of us repeatedly shuffling two independent TOPMOST windows while a
        // tile is being dragged (which causes the visible flash/flicker).
        if (owner != 0) Native.SetWindowLongPtr(Handle, -8, owner); // GWLP_HWNDPARENT
        presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        try { AppWindow.IsShownInSwitchers = false; } catch (NotImplementedException) { }
        // Stay hidden until Present has sized, positioned and composed the panel,
        // otherwise a default-sized unpainted window flashes up first.
        AppWindow.Hide();
        Native.SetWindowLongPtr(Handle, -20, (nint)(Native.GetWindowLongPtr(Handle, -20).ToInt64() | 0x80)); // WS_EX_TOOLWINDOW

        var wp = desktop.Wallpaper;
        if (string.IsNullOrWhiteSpace(wp))
            wp = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", "") as string ?? "";
        if (System.IO.File.Exists(wp))
            try { wallpaper.Source = new BitmapImage(new Uri(wp)); } catch (Exception ex) { Log.Write("Popout wallpaper: " + ex.Message); }

        var title = new TextBlock { Text = desktop.Name.ToUpperInvariant(), FontSize = 11,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), CharacterSpacing = 60,
            Foreground = GlassAppearance.PrimaryBrush(), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        closeButton = new Button { Content = "✕", Width = 24, Height = 22, Padding = new Thickness(0), FontSize = 12,
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(210, 40, 70, 120)),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        closeButton.Click += (_, _) => { UserCloseRequested?.Invoke(desktopId); Close(); };
        var headerGrid = new Grid();
        headerGrid.Children.Add(title); headerGrid.Children.Add(closeButton);
        header = new Border { Height = HeaderH, Child = headerGrid, Background = GlassAppearance.StripBrush(settings) };

        grip = new Border { Width = GripSize, Height = GripSize, CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(170, 150, 190, 255)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 4, 4) };

        body.Child = wallpaper;
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0); content.Children.Add(header);
        Grid.SetRow(body, 1); content.Children.Add(body);
        // Glowing blue border: faint accent rings hugging the panel edge, fading inward.
        foreach (var (margin, thickness, alpha, radius) in GlowRings)
        {
            var ring = new Border { Margin = new Thickness(margin), BorderThickness = new Thickness(thickness),
                CornerRadius = new CornerRadius(radius), IsHitTestVisible = false,
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, 74, 163, 255)),
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            Grid.SetRowSpan(ring, 2); content.Children.Add(ring);
        }
        Grid.SetRowSpan(grip, 2); content.Children.Add(grip);

        frame = new Border { Child = content, RequestedTheme = ElementTheme.Dark,
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 74, 163, 255)), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 16, 28)) };
        Content = frame;

        header.PointerPressed += (_, e) => StartGesture(1, e, header);
        header.PointerMoved += GestureMove;
        header.PointerReleased += (_, e) => EndGesture(header, e);
        header.PointerCaptureLost += (_, _) => gestureMode = 0;
        grip.PointerPressed += (_, e) => StartGesture(2, e, grip);
        grip.PointerMoved += GestureMove;
        grip.PointerReleased += (_, e) => EndGesture(grip, e);
        grip.PointerCaptureLost += (_, _) => gestureMode = 0;
        body.PointerPressed += BodyPressed;
        body.PointerMoved += BodyMoved;
        body.PointerReleased += BodyReleased;
        body.PointerCaptureLost += (_, _) => {
            if (bodyPressed && bodyMoved && bodyWindow != 0&&bodyWindowMoved) session.NoteUserMoved(bodyWindow);
            bodyPressed = false; bodyWindow = 0;
        };
        jumpTimer.Tick += (_, _) => { jumpTimer.Stop(); var h = pendingJump; pendingJump = 0; if (h != 0) JumpToWindow?.Invoke(desktopId, h); };
        SizeChanged += (_, _) => Layout();
        Closed += (_, _) => { jumpTimer.Stop(); pendingJump = 0; ClearDragPreview(); ClearThumbs(); Dismissed?.Invoke(); };
    }

    public void Present(Native.RECT work)
    {
        scale = Math.Max(1, Native.GetDpiForWindow(Handle) / 96d);
        var (minW,maxW,minH,maxH)=ResizeBounds(work);
        int w = Math.Clamp((int)Math.Round(work.Width * DefaultWorkAreaFraction), minW, maxW);
        int h = Math.Clamp((int)Math.Round(work.Height * DefaultWorkAreaFraction), minH, maxH);
        int x = work.Left + (work.Width - w) / 2, y = work.Top + (work.Height - h) / 2;
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        // Compose the live thumbnails at the final size while still hidden, then show.
        // Showing first (or before the move) flashes an unpainted default-sized window.
        Layout();
        frame.UpdateLayout();
        AppWindow.Show();
        if (!pinned) pinned = session.Desktops.PinOwnWindow(Handle);
        Activate();
    }

    // When this panel's desktop becomes current, showing a miniature of that same desktop
    // is redundant and can include the overview composition behind it. Hide the existing
    // panel instead of closing it so its desktop identity, position and resized dimensions
    // survive the round trip. It is shown again when the user leaves that desktop.
    public void SuspendForCurrentDesktop()
    {
        if (suspended) return;
        suspended = true;
        jumpTimer.Stop(); pendingJump = 0;
        ClearDragPreview();
        AppWindow.Hide();
    }

    public void ResumeAfterDesktopSwitch()
    {
        if (!suspended) return;
        suspended = false;
        AppWindow.Show(false);
        if (!pinned) pinned = session.Desktops.PinOwnWindow(Handle);
        BringToFront();
        Refresh();
    }

    void StartGesture(int mode, PointerRoutedEventArgs e, UIElement el)
    {
        if (!e.GetCurrentPoint(el).Properties.IsLeftButtonPressed) return;
        jumpTimer.Stop(); pendingJump = 0; lastBodyClick = 0;
        Native.GetCursorPos(out gestureStartCursor);
        gestureStartRect = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        gestureMode = mode; gesturePointer = e.Pointer.PointerId; el.CapturePointer(e.Pointer); e.Handled = true;
    }
    void GestureMove(object sender, PointerRoutedEventArgs e)
    {
        if (gestureMode == 0 || e.Pointer.PointerId != gesturePointer) return;
        Native.GetCursorPos(out var c);
        int dx = c.X - gestureStartCursor.X, dy = c.Y - gestureStartCursor.Y;
        if (gestureMode == 1)
            AppWindow.Move(new PointInt32(gestureStartRect.X + dx, gestureStartRect.Y + dy));
        else
        {
            var monitor=Native.MonitorFromWindow(Handle,2);
            var resizeWork=monitor!=0?Native.WorkArea(monitor):new Native.RECT(0,0,gestureStartRect.Width,gestureStartRect.Height);
            var (minW,maxW,minH,maxH)=ResizeBounds(resizeWork);
            AppWindow.Resize(new SizeInt32(
                Math.Clamp(gestureStartRect.Width + dx,minW,maxW),
                Math.Clamp(gestureStartRect.Height + dy,minH,maxH)));
        }
        e.Handled = true;
    }
    void EndGesture(UIElement el, PointerRoutedEventArgs e)
    {
        if (gestureMode != 0 && e.Pointer.PointerId == gesturePointer) { gestureMode = 0; el.ReleasePointerCapture(e.Pointer); e.Handled = true; }
    }
    // Task-View style: a left-press anywhere on the panel body drags the whole panel.
    // A press released without moving is a click, which jumps to that window instead.
    void BodyPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(body).Properties.IsLeftButtonPressed) return;
        jumpTimer.Stop(); pendingJump = 0;
        Native.GetCursorPos(out gestureStartCursor);
        gestureStartRect = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        // A press on one of the live desktop thumbnails arms a move of that real
        // window. Empty wallpaper still drags the popout itself.
        var local = new Native.POINT { X = gestureStartCursor.X - AppWindow.Position.X, Y = gestureStartCursor.Y - AppWindow.Position.Y };
        bodyWindow = 0;
        for (int i = windowRects.Count - 1; i >= 0; i--)
            if (Contains(windowRects[i].Rect, local)) { bodyWindow = windowRects[i].Handle; break; }
        if (bodyWindow != 0 && !Native.GetWindowRect(bodyWindow, out bodyWindowStart)) bodyWindow = 0;
        bodyPressed = true; bodyMoved = false; gesturePointer = e.Pointer.PointerId;
        bodyWindowMoved=false;bodyMoveFailedLogged=false;
        body.CapturePointer(e.Pointer); e.Handled = true;
    }
    void BodyMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!bodyPressed || e.Pointer.PointerId != gesturePointer) return;
        Native.GetCursorPos(out var c);
        int dx = c.X - gestureStartCursor.X, dy = c.Y - gestureStartCursor.Y;
        if (!bodyMoved && Math.Max(Math.Abs(dx), Math.Abs(dy)) < 6) { e.Handled = true; return; }
        bodyMoved = true;
        if (bodyWindow != 0)
        {
            if(!bodyWindowMoved&&Native.IsZoomed(bodyWindow))
            {
                Native.ShowWindow(bodyWindow,9);
                if(Native.GetWindowRect(bodyWindow,out var restored))
                {
                    bodyWindowStart=restored;
                    gestureStartCursor=c;dx=0;dy=0;
                }
            }
            // The popout is a scaled map of one physical monitor. Convert movement in
            // panel pixels back into desktop pixels and move the real HWND without
            // activating or re-ordering it. Its existing DWM registration is then moved
            // directly, avoiding a full popout rebuild on every pointer sample.
            var screen = Native.MonitorBounds(Native.MonitorFromWindow(Handle, 2));
            var content = PopoutContentRect();
            int realDx = (int)Math.Round(dx * screen.Width / (double)Math.Max(1, content.Width));
            int realDy = (int)Math.Round(dy * screen.Height / (double)Math.Max(1, content.Height));
            int left = bodyWindowStart.Width >= screen.Width ? screen.Left : Math.Clamp(bodyWindowStart.Left + realDx, screen.Left, screen.Right - bodyWindowStart.Width);
            int top = bodyWindowStart.Height >= screen.Height ? screen.Top : Math.Clamp(bodyWindowStart.Top + realDy, screen.Top, screen.Bottom - bodyWindowStart.Height);
            if(Native.SetWindowPos(bodyWindow, 0, left, top, 0, 0, 0x15 | 0x4000))
            {
                bodyWindowMoved=true;
                var moved = new Native.RECT(left, top, bodyWindowStart.Width, bodyWindowStart.Height);
                UpdateWindowVisual(bodyWindow, moved);
                contentSig = "";
            }
            else if(!bodyMoveFailedLogged)
            {
                bodyMoveFailedLogged=true;
                Log.Write("Popout window move failed for "+bodyWindow+": "+System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
        }
        else AppWindow.Move(new PointInt32(gestureStartRect.X + dx, gestureStartRect.Y + dy));
        e.Handled = true;
    }
    void BodyReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!bodyPressed) return;
        bodyPressed = false; body.ReleasePointerCapture(e.Pointer); e.Handled = true;
        if (bodyMoved)
        {
            if (bodyWindow != 0&&bodyWindowMoved) session.NoteUserMoved(bodyWindow);
            bodyWindow = 0; lastBodyClick = 0; Refresh(); return;
        } // a drag cannot complete a double-click
        var clickedWindow=bodyWindow;
        bodyWindow = 0;
        Native.GetCursorPos(out var c);
        long now = Environment.TickCount64;
        int tolX = Math.Max(2, Native.GetSystemMetrics(36) / 2), tolY = Math.Max(2, Native.GetSystemMetrics(37) / 2);
        // Window clicks always jump to that window. Only empty wallpaper participates in
        // the double-click gesture that returns/dismisses the desktop popout.
        if(clickedWindow!=0)
        {
            lastBodyClick=0;jumpTimer.Stop();pendingJump=0;
            JumpToWindow?.Invoke(desktopId,clickedWindow);
            return;
        }
        // Second EMPTY click in the same spot: put this desktop back in the bar.
        if (lastBodyClick != 0 && now - lastBodyClick <= Native.GetDoubleClickTime()
            && Math.Abs(c.X - lastBodyClickPoint.X) <= tolX && Math.Abs(c.Y - lastBodyClickPoint.Y) <= tolY)
        {
            lastBodyClick = 0; jumpTimer.Stop(); pendingJump = 0;
            UserCloseRequested?.Invoke(desktopId);
            Close();
            return;
        }
        lastBodyClick = now; lastBodyClickPoint = c;
        // A click on a window jumps to it, but only once the double-click interval has
        // passed, so that a second click closes the panel instead of jumping.
        var p = new Native.POINT { X = c.X - AppWindow.Position.X, Y = c.Y - AppWindow.Position.Y };
        for (int i = windowRects.Count - 1; i >= 0; i--)
            if (Contains(windowRects[i].Rect, p))
            { pendingJump = windowRects[i].Handle; jumpTimer.Stop(); jumpTimer.Start(); return; }
    }

    // Keep above the overview and pick up window changes; skips work while the user is
    // dragging/resizing and re-composes only when the desktop's windows actually change.
    // Put the panel back above the overview. The overlay re-asserts its own topmost on
    // every render/raise, which would otherwise bury this window until the next refresh.
    public void BringToFront()
    {
        if (suspended) return;
        Native.SetWindowPos(Handle, -1, 0, 0, 0, 0, 0x13); // HWND_TOPMOST
    }
    public bool ContainsScreenPoint(Native.POINT p)
    {
        if (!Native.IsWindowVisible(Handle) || !Native.GetWindowRect(Handle, out var r)) return false;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }
    public void SetDropTarget(bool active)
    {
        frame.BorderThickness = new Thickness(active ? 5 : 2);
        frame.BorderBrush = new SolidColorBrush(active
            ? Windows.UI.Color.FromArgb(255, 145, 205, 255)
            : Windows.UI.Color.FromArgb(255, 74, 163, 255));
        if (!active) ClearDragPreview();
    }
    public void ShowDragPreview(nint source, Native.POINT screenPoint)
    {
        if (source == 0 || !Native.IsWindow(source) || !ContainsScreenPoint(screenPoint))
        { ClearDragPreview(); return; }
        if (dragPreviewSource != source || dragPreviewThumb == 0)
        {
            ClearDragPreview();
            if (Native.DwmRegisterThumbnail(Handle, source, out dragPreviewThumb) != 0)
            { dragPreviewThumb = 0; return; }
            dragPreviewSource = source;
        }
        if (Native.DwmQueryThumbnailSourceSize(dragPreviewThumb, out var size) != 0 || size.X < 1 || size.Y < 1)
            return;
        var client = AppWindow.ClientSize;
        int headerPx = (int)Math.Round(HeaderH * scale), gripPx = (int)Math.Round(GripReserve * scale);
        int contentTop = headerPx, contentBottom = Math.Max(contentTop + 1, client.Height - gripPx);
        int maxW = Math.Max(80, (int)Math.Round(client.Width * 0.46));
        int maxH = Math.Max(60, (int)Math.Round((contentBottom - contentTop) * 0.52));
        double fit = Math.Min(maxW / (double)size.X, maxH / (double)size.Y);
        int w = Math.Max(1, (int)Math.Round(size.X * fit));
        int h = Math.Max(1, (int)Math.Round(size.Y * fit));
        int localX = screenPoint.X - AppWindow.Position.X;
        int localY = screenPoint.Y - AppWindow.Position.Y;
        int left = Math.Clamp(localX - w / 2, 6, Math.Max(6, client.Width - w - 6));
        int top = Math.Clamp(localY - h / 2, contentTop + 4, Math.Max(contentTop + 4, contentBottom - h - 4));
        var props = new Native.THUMBNAIL {
            Flags = 1u | 4u | 8u | 16u,
            Destination = new Native.RECT(left, top, w, h),
            Opacity = 235,
            Visible = true,
            SourceClientOnly = false
        };
        Native.DwmUpdateThumbnailProperties(dragPreviewThumb, ref props);
    }
    public void ClearDragPreview()
    {
        if (dragPreviewThumb != 0) Native.DwmUnregisterThumbnail(dragPreviewThumb);
        dragPreviewThumb = 0;
        dragPreviewSource = 0;
    }
    public void Refresh()
    {
        if (suspended) return;
        // Do not touch z-order while a pointer gesture owns capture. SetWindowPos can
        // cause Windows to send PointerCaptureLost, which used to stop panel dragging
        // after the refresh timer ticked during a held mouse button.
        if (gestureMode != 0 || bodyPressed) return;
        BringToFront();
        var windows = Snapshot();
        var sig = Signature(windows);
        if (sig == contentSig) return;
        Compose(windows);
    }

    IEnumerable<nint> DesktopWindows() => session.DesktopWindows()
        .Where(w => !Native.IsIconic(w.Handle) &&
            (desktopId != Guid.Empty ? session.Desktops.WindowDesktop(w.Handle) == desktopId : session.Desktops.IsCurrent(w.Handle)))
        .Select(w => w.Handle);

    void Layout()
    {
        scale = Math.Max(1, Native.GetDpiForWindow(Handle) / 96d);
        Compose(Snapshot());
    }

    (int MinWidth,int MaxWidth,int MinHeight,int MaxHeight) ResizeBounds(Native.RECT work)
    {
        int minWidth=Math.Max(1,(int)Math.Round(MinW*scale));
        int minHeight=Math.Max(1,(int)Math.Round(MinH*scale));
        int maxWidth=Math.Max(minWidth,(int)Math.Round(work.Width*MaxWorkAreaFraction));
        int maxHeight=Math.Max(minHeight,(int)Math.Round(work.Height*MaxWorkAreaFraction));
        return (minWidth,maxWidth,minHeight,maxHeight);
    }

    List<(nint Handle, Native.RECT Bounds)> Snapshot()
    {
        var screen = Native.MonitorBounds(Native.MonitorFromWindow(Handle, 2));
        return DesktopWindows().Select(h => (Handle: h, Bounds: Native.GetWindowRect(h, out var r) ? r : default))
            .Where(w => w.Bounds.Width > 0 && w.Bounds.Height > 0 && w.Bounds.Intersects(screen)).ToList();
    }
    static string Signature(IEnumerable<(nint Handle, Native.RECT Bounds)> windows) =>
        string.Join("|", windows.Select(w => $"{w.Handle}:{w.Bounds.Left},{w.Bounds.Top},{w.Bounds.Width},{w.Bounds.Height}"));

    void Compose(List<(nint Handle, Native.RECT Bounds)> windows)
    {
        windowRects.Clear();
        var live = new HashSet<nint>();
        var content = PopoutContentRect();
        var screen = Native.MonitorBounds(Native.MonitorFromWindow(Handle, 2));
        // Back-to-front so overlapping windows composite in the right order.
        foreach (var w in windows.AsEnumerable().Reverse())
        {
            var source = w.Bounds;
            // Reuse the existing registration; re-registering every pass is what flickered.
            if (!thumbBySource.TryGetValue(w.Handle, out var thumb))
            {
                int reg = Native.DwmRegisterThumbnail(Handle, w.Handle, out thumb);
                if (reg != 0) { Log.Write($"Popout thumbnail registration failed 0x{reg:X} for {Native.Title(w.Handle)} ({w.Handle})"); continue; }
                thumbBySource[w.Handle] = thumb;
            }
            if (TryPositionWindowThumb(w.Handle, thumb, source, content, screen, out var clip))
            { live.Add(w.Handle); windowRects.Add((w.Handle, clip)); }
        }
        // Unregister only windows that have left this desktop; everything else keeps its
        // registration, so a window moving just repositions the thumbnail it already has.
        foreach (var gone in thumbBySource.Keys.Where(k => !live.Contains(k)).ToList())
        { Native.DwmUnregisterThumbnail(thumbBySource[gone]); thumbBySource.Remove(gone); }
        // Retry transient DWM registration/update failures on the next refresh.
        contentSig = live.Count == windows.Count ? Signature(windows) : "";
    }

    Native.RECT PopoutContentRect()
    {
        var client = AppWindow.ClientSize;
        int headerPx = (int)Math.Round(HeaderH * scale), sidePx = (int)Math.Round(SideInset * scale), gripPx = (int)Math.Round(GripReserve * scale);
        return new Native.RECT(sidePx, headerPx, Math.Max(1, client.Width - 2 * sidePx), Math.Max(1, client.Height - headerPx - gripPx));
    }
    void UpdateWindowVisual(nint h, Native.RECT source)
    {
        if (!thumbBySource.TryGetValue(h, out var thumb)) return;
        var content = PopoutContentRect();
        var screen = Native.MonitorBounds(Native.MonitorFromWindow(Handle, 2));
        if (!TryPositionWindowThumb(h, thumb, source, content, screen, out var clip)) return;
        int i = windowRects.FindIndex(x => x.Handle == h);
        if (i >= 0) windowRects[i] = (h, clip); else windowRects.Add((h, clip));
    }
    static bool TryPositionWindowThumb(nint h, nint thumb, Native.RECT source, Native.RECT content, Native.RECT screen, out Native.RECT clip)
    {
        var dest = new Native.RECT(
            content.Left + (int)Math.Round((source.Left - screen.Left) * content.Width / (double)Math.Max(1, screen.Width)),
            content.Top + (int)Math.Round((source.Top - screen.Top) * content.Height / (double)Math.Max(1, screen.Height)),
            Math.Max(1, (int)Math.Round(source.Width * content.Width / (double)Math.Max(1, screen.Width))),
            Math.Max(1, (int)Math.Round(source.Height * content.Height / (double)Math.Max(1, screen.Height))));
        clip = DockView.Intersect(dest, content);
        if (clip.Width < 1 || clip.Height < 1) return false;
        if (Native.DwmQueryThumbnailSourceSize(thumb, out var size) != 0 || size.X < 1 || size.Y < 1) return false;
        var crop = new Native.RECT((int)((clip.Left - dest.Left) * size.X / (double)dest.Width), (int)((clip.Top - dest.Top) * size.Y / (double)dest.Height),
            Math.Max(1, (int)(clip.Width * size.X / (double)dest.Width)), Math.Max(1, (int)(clip.Height * size.Y / (double)dest.Height)));
        var props = new Native.THUMBNAIL { Flags = 31, Destination = clip, Source = crop, Visible = true, Opacity = 255, SourceClientOnly = false };
        bool ok = Native.DwmUpdateThumbnailProperties(thumb, ref props) == 0;
        if (!ok) Log.Write($"Popout thumbnail update failed for {Native.Title(h)} ({h})");
        return ok;
    }

    void ClearThumbs() { foreach (var t in thumbBySource.Values) Native.DwmUnregisterThumbnail(t); thumbBySource.Clear(); }
    static bool Contains(Native.RECT r, Native.POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
}
