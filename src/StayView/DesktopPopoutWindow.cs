using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StayView.Core;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace StayView;

// A right-clicked virtual desktop popped out as its own borderless, always-on-top
// window: a live scaled copy (wallpaper + DWM thumbnails of that desktop's windows)
// that can be dragged by its header and resized by the corner grip. Window thumbnails
// can be dragged onto the current desktop. A click in this panel is not a focus
// gesture: it must not browse, unbrowse, or move keyboard focus. The thumbnails are
// visual copies, not the real windows.
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
    readonly Native.WndProc procedure;
    readonly nint originalProc;
    const uint RestoreFocusMessage = 0x8004;
    nint focusRestore;
    // Keyed by source window so a refresh can reuse thumbnails instead of unregistering
    // and re-registering them all, which made the panel flicker whenever a window moved.
    readonly Dictionary<nint, nint> thumbBySource = [];
    // Windows whose frame attributes were changed so a popout thumbnail does not
    // paint that frame on the current desktop.
    readonly HashSet<nint> frameSuppressed = [];
    readonly List<(nint Handle, Native.RECT Rect)> windowRects = []; // client physical px
    nint dragPreviewThumb;
    nint dragPreviewSource;
    public nint Handle { get; }
    public Guid DesktopId => desktopId;
    double scale = 1;
    string contentSig = "";
    bool suspended;

    // The popout is a view of the monitor it was created from, not whichever monitor
    // the floating panel happens to be sitting on now. Re-resolving the source monitor
    // from Handle while dragging the panel across a monitor boundary made every source
    // window on the original monitor fail Snapshot's intersection test and disappear.
    Native.RECT sourceScreen;
    // 0 = none, 1 = move (header), 2 = resize (grip)
    int gestureMode;
    uint gesturePointer;
    Native.POINT gestureStartCursor;
    RectInt32 gestureStartRect;
    bool bodyPressed, bodyMoved;
    nint bodyWindow;
    Native.RECT bodyWindowStart;
    Native.RECT bodyWindowTarget;
    Guid bodyStartDesktop;
    // Empty-wallpaper double-click puts the desktop back in the bar. A click on a
    // thumbnail does not, and it does not focus that window either.
    long lastBodyClick;
    Native.POINT lastBodyClickPoint;
    public event Action? Dismissed;
    public event Action<Guid>? UserCloseRequested;
    public event Action<nint, Native.POINT>? WindowDragMoved;
    public event Action? WindowDragEnded;
    // A pointer press/release on the panel (header, grip or body). TrayApp re-asserts pins.
    public event Action<bool>? PressChanged;
    public Func<nint, Native.POINT, bool>? ExternalWindowDrop { get; set; }
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
        // This is an interactive preview, not an application surface. Let it receive mouse
        // input without ever becoming foreground: activating the popout makes the overview
        // re-run its foreground/z-order maintenance and can blank DWM thumbnails for a frame.
        // WS_EX_NOACTIVATE is the durable rule; WM_MOUSEACTIVATE below is a second guard for
        // WinUI's island/native handoff while still allowing the click itself through.
        Native.SetWindowLongPtr(Handle, -20, (nint)(Native.GetWindowLongPtr(Handle, -20).ToInt64() | 0x80 | 0x08000000)); // TOOLWINDOW|NOACTIVATE
        procedure = WndProc;
        originalProc = Native.SetWindowLongPtr(Handle, -4, Marshal.GetFunctionPointerForDelegate(procedure));

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
            if(!bodyPressed)return;
            // A cancelled drag only moved previews, so there is no placement to undo.
            bodyPressed = false; bodyWindow = 0; contentSig="";
            WindowDragEnded?.Invoke(); Refresh();
        };
        SizeChanged += (_, _) => Layout();
        Closed += (_, _) => { WindowDragEnded?.Invoke(); ClearDragPreview(); ClearThumbs(); Dismissed?.Invoke(); };
    }

    nint WndProc(nint h,uint msg,nint wp,nint lp)
    {
        // MA_NOACTIVATE: deliver the mouse message to XAML (buttons, body drag, resize grip)
        // but keep the existing foreground window untouched. WinUI can still activate this
        // window or its owner after the callback. The posted pass puts that window back,
        // so a popout click cannot focus or unfocus the window the user is working in.
        if(msg==0x21)
        {
            var fg=Native.GetForegroundWindow();
            if(fg!=0 && fg!=h) focusRestore=fg;
            Native.PostMessage(h, RestoreFocusMessage, focusRestore, 0);
            return 3; // WM_MOUSEACTIVATE / MA_NOACTIVATE
        }
        if(msg==RestoreFocusMessage)
        {
            var keep=wp;
            if(keep!=0 && Native.IsWindow(keep))
            {
                var fg=Native.GetForegroundWindow();
                if(fg!=keep)
                {
                    Native.GetWindowThreadProcessId(fg, out var pid);
                    if(pid==(uint)Environment.ProcessId) Native.SetForegroundWindow(keep);
                }
            }
            return 0;
        }
        return Native.CallWindowProc(originalProc,h,msg,wp,lp);
    }

    public void Present(Native.RECT work)
    {
        var sourceProbe=work;
        var sourceMonitor=Native.MonitorFromRect(ref sourceProbe,2);
        sourceScreen=sourceMonitor!=0?Native.MonitorBounds(sourceMonitor):work;
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
        AppWindow.Show(false);
        // Do not pin this panel to every desktop. A pinned host is what makes Windows
        // draw the previewed windows' own frames on the desktop the user is looking at.
        // The panel is moved onto whichever desktop is current instead.
        FollowCurrentDesktop();
        BringToFront();
    }

    // When this panel's desktop becomes current, showing a miniature of that same desktop
    // is redundant and can include the overview composition behind it. Hide the existing
    // panel instead of closing it so its desktop identity, position and resized dimensions
    // survive the round trip. It is shown again when the user leaves that desktop.
    public void SuspendForCurrentDesktop()
    {
        if (suspended) return;
        suspended = true;
        ClearDragPreview();
        bodyPressed=false;bodyWindow=0;body.ReleasePointerCaptures();WindowDragEnded?.Invoke();
        RestoreSuppressedFrames();
        AppWindow.Hide();
    }

    public void ResumeAfterDesktopSwitch()
    {
        if (!suspended) return;
        suspended = false;
        AppWindow.Show(false);
        FollowCurrentDesktop();
        BringToFront();
        Refresh();
    }

    public void FollowCurrentDesktop()
    {
        var id = session.Desktops.Current;
        if (id == Guid.Empty || id == desktopId) return;
        session.Desktops.MoveOwnWindow(Handle, id);
    }

    void StartGesture(int mode, PointerRoutedEventArgs e, UIElement el)
    {
        if (!e.GetCurrentPoint(el).Properties.IsLeftButtonPressed) return;
        lastBodyClick = 0;
        Native.GetCursorPos(out gestureStartCursor);
        gestureStartRect = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        gestureMode = mode; gesturePointer = e.Pointer.PointerId; el.CapturePointer(e.Pointer); e.Handled = true;
        PressChanged?.Invoke(true);
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
        if (gestureMode != 0 && e.Pointer.PointerId == gesturePointer) { gestureMode = 0; el.ReleasePointerCapture(e.Pointer); e.Handled = true; PressChanged?.Invoke(false); }
    }
    // A left-press on empty wallpaper drags the panel. A press on a thumbnail drags
    // that window's preview. A click that does not move does not change focus.
    void BodyPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(body).Properties.IsLeftButtonPressed) return;
        Native.GetCursorPos(out gestureStartCursor);
        gestureStartRect = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        // A press on one of the live desktop thumbnails arms a move of that real
        // window. Empty wallpaper still drags the popout itself.
        var local = new Native.POINT { X = gestureStartCursor.X - AppWindow.Position.X, Y = gestureStartCursor.Y - AppWindow.Position.Y };
        bodyWindow = 0;
        for (int i = windowRects.Count - 1; i >= 0; i--)
            if (Contains(windowRects[i].Rect, local)) { bodyWindow = windowRects[i].Handle; break; }
        if (bodyWindow != 0 && InCloseCorner(local))
        {
            session.Close(bodyWindow);
            e.Handled = true;
            return;
        }
        if (bodyWindow != 0 && !Native.GetWindowRect(bodyWindow, out bodyWindowStart)) bodyWindow = 0;
        if(bodyWindow!=0 && (Native.IsIconic(bodyWindow)||Native.IsZoomed(bodyWindow)))
            bodyWindowStart=Native.Placement(bodyWindow).NormalPosition;
        bodyWindowTarget=bodyWindowStart;
        bodyStartDesktop=session.Desktops.Current;
        bodyPressed = true; bodyMoved = false; gesturePointer = e.Pointer.PointerId;
        if(!body.CapturePointer(e.Pointer)){bodyPressed=false;bodyWindow=0;return;}
        e.Handled = true;
        PressChanged?.Invoke(true);
    }
    void BodyMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!bodyPressed || e.Pointer.PointerId != gesturePointer) return;
        Native.GetCursorPos(out var c);
        int dx = c.X - gestureStartCursor.X, dy = c.Y - gestureStartCursor.Y;
        if (!bodyMoved && Math.Max(Math.Abs(dx), Math.Abs(dy)) < 6) { e.Handled = true; return; }
        // A pinned focused window is geometry-locked. A popout drag must not start a
        // transfer; a click here also must not focus or unfocus that window.
        if(bodyWindow!=0 && session.IsPinned(bodyWindow)){e.Handled=true;return;}
        bodyMoved = true;
        if (bodyWindow != 0)
        {
            // Move only the preview during capture. A foreign maximized HWND must never
            // receive SW_RESTORE here: that activates it and switches desktops mid-drag.
            var screen = SourceScreen();
            var content = PopoutContentRect();
            int realDx = (int)Math.Round(dx * screen.Width / (double)Math.Max(1, content.Width));
            int realDy = (int)Math.Round(dy * screen.Height / (double)Math.Max(1, content.Height));
            int left = bodyWindowStart.Width >= screen.Width ? screen.Left : Math.Clamp(bodyWindowStart.Left + realDx, screen.Left, screen.Right - bodyWindowStart.Width);
            int top = bodyWindowStart.Height >= screen.Height ? screen.Top : Math.Clamp(bodyWindowStart.Top + realDy, screen.Top, screen.Bottom - bodyWindowStart.Height);
            bodyWindowTarget=new(left,top,bodyWindowStart.Width,bodyWindowStart.Height);
            if(ContainsScreenPoint(c))UpdateWindowVisual(bodyWindow,bodyWindowTarget);
            WindowDragMoved?.Invoke(bodyWindow,c);
            contentSig="";
        }
        else AppWindow.Move(new PointInt32(gestureStartRect.X + dx, gestureStartRect.Y + dy));
        e.Handled = true;
    }
    void BodyReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!bodyPressed || e.Pointer.PointerId!=gesturePointer) return;
        bodyPressed = false; body.ReleasePointerCapture(e.Pointer); e.Handled = true;
        PressChanged?.Invoke(false);
        if (bodyMoved)
        {
            Native.GetCursorPos(out var drop);
            var source=bodyWindow;bodyWindow=0;lastBodyClick=0;
            WindowDragEnded?.Invoke();
            if(source!=0 && bodyStartDesktop!=Guid.Empty && session.Desktops.Current==bodyStartDesktop)
            {
                RestoreSuppressedFrame(source);
                if(ContainsScreenPoint(drop))session.MoveToDesktop(source,desktopId,bodyWindowTarget);
                else ExternalWindowDrop?.Invoke(source,drop);
            }
            contentSig="";Refresh(); return;
        } // a drag cannot complete a double-click
        var clickedWindow=bodyWindow;
        bodyWindow = 0;
        // A thumbnail click is not the overview's focus gesture. Leave the focused
        // window, the grid, and keyboard focus exactly as they were. Only empty
        // wallpaper participates in the double-click that dismisses this panel.
        if(clickedWindow!=0){lastBodyClick=0;return;}
        Native.GetCursorPos(out var c);
        long now = Environment.TickCount64;
        int tolX = Math.Max(2, Native.GetSystemMetrics(36) / 2), tolY = Math.Max(2, Native.GetSystemMetrics(37) / 2);
        if (lastBodyClick != 0 && now - lastBodyClick <= Native.GetDoubleClickTime()
            && Math.Abs(c.X - lastBodyClickPoint.X) <= tolX && Math.Abs(c.Y - lastBodyClickPoint.Y) <= tolY)
        {
            lastBodyClick = 0;
            UserCloseRequested?.Invoke(desktopId);
            Close();
            return;
        }
        lastBodyClick = now; lastBodyClickPoint = c;
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
        var windows = Snapshot();
        var sig = Signature(windows);
        if (sig == contentSig) { HideLeakedFrames(); return; }
        Compose(windows);
        HideLeakedFrames();
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

    List<(nint Handle, Native.RECT Visible, Native.RECT Full)> Snapshot()
    {
        var screen = SourceScreen();
        var list = new List<(nint Handle, Native.RECT Visible, Native.RECT Full)>();
        foreach (var h in DesktopWindows())
        {
            if (!Native.GetWindowRect(h, out var full) || full.Width < 1 || full.Height < 1) continue;
            // GetWindowRect includes the invisible resize margin. Drawing that margin
            // is the empty blue border that sticks out above or below the next window.
            var visible = Native.TryGetVisualBounds(h, out var frame) && frame.Width > 1 && frame.Height > 1 ? frame : full;
            if (!visible.Intersects(screen)) continue;
            list.Add((h, visible, full));
        }
        return list;
    }
    public Native.RECT DropBounds(nint source,Native.POINT point)
    {
        var content=PopoutContentRect();
        content=new(content.Left+AppWindow.Position.X,content.Top+AppWindow.Position.Y,content.Width,content.Height);
        var screen=SourceScreen();
        var location=DesktopDropGeometry.MapPoint(point,content,screen);
        Native.GetWindowRect(source,out var bounds);
        if(Native.IsIconic(source)||Native.IsZoomed(source))bounds=Native.Placement(source).NormalPosition;
        return DesktopDropGeometry.AtPoint(bounds,location,screen);
    }
    static string Signature(IEnumerable<(nint Handle, Native.RECT Visible, Native.RECT Full)> windows) =>
        string.Join("|", windows.Select(w => $"{w.Handle}:{w.Visible.Left},{w.Visible.Top},{w.Visible.Width},{w.Visible.Height}"));

    void Compose(List<(nint Handle, Native.RECT Visible, Native.RECT Full)> windows)
    {
        windowRects.Clear();
        var live = new HashSet<nint>();
        var content = PopoutContentRect();
        var screen = SourceScreen();
        // Back-to-front so overlapping windows composite in the right order.
        foreach (var w in windows.AsEnumerable().Reverse())
        {
            // Reuse the existing registration; re-registering every pass is what flickered.
            if (!thumbBySource.TryGetValue(w.Handle, out var thumb))
            {
                int reg = Native.DwmRegisterThumbnail(Handle, w.Handle, out thumb);
                if (reg != 0) { Log.Write($"Popout thumbnail registration failed 0x{reg:X} for {Native.Title(w.Handle)} ({w.Handle})"); continue; }
                thumbBySource[w.Handle] = thumb;
            }
            if (TryPositionWindowThumb(w.Handle, thumb, w.Visible, w.Full, content, screen, out var clip))
            { live.Add(w.Handle); windowRects.Add((w.Handle, clip)); }
        }
        // Unregister only windows that have left this desktop; everything else keeps its
        // registration, so a window moving just repositions the thumbnail it already has.
        foreach (var gone in thumbBySource.Keys.Where(k => !live.Contains(k)).ToList())
        {
            Native.DwmUnregisterThumbnail(thumbBySource[gone]);
            thumbBySource.Remove(gone);
            RestoreSuppressedFrame(gone);
        }
        // Retry transient DWM registration/update failures on the next refresh.
        contentSig = live.Count == windows.Count ? Signature(windows) : "";
        HideLeakedFrames();
    }

    Native.RECT PopoutContentRect()
    {
        var client = AppWindow.ClientSize;
        int headerPx = (int)Math.Round(HeaderH * scale), sidePx = (int)Math.Round(SideInset * scale), gripPx = (int)Math.Round(GripReserve * scale);
        return new Native.RECT(sidePx, headerPx, Math.Max(1, client.Width - 2 * sidePx), Math.Max(1, client.Height - headerPx - gripPx));
    }
    void UpdateWindowVisual(nint h, Native.RECT fullTarget)
    {
        if (!thumbBySource.TryGetValue(h, out var thumb)) return;
        var content = PopoutContentRect();
        var screen = SourceScreen();
        var visible = fullTarget;
        if (Native.GetWindowRect(h, out var fullNow) && Native.TryGetVisualBounds(h, out var visibleNow)
            && fullNow.Width > 1 && visibleNow.Width > 1)
        {
            int left = visibleNow.Left - fullNow.Left, top = visibleNow.Top - fullNow.Top;
            int right = fullNow.Right - visibleNow.Right, bottom = fullNow.Bottom - visibleNow.Bottom;
            visible = new Native.RECT(fullTarget.Left + left, fullTarget.Top + top,
                Math.Max(1, fullTarget.Width - left - right), Math.Max(1, fullTarget.Height - top - bottom));
        }
        if (!TryPositionWindowThumb(h, thumb, visible, fullTarget, content, screen, out var clip)) return;
        SuppressFrame(h);
        int i = windowRects.FindIndex(x => x.Handle == h);
        if (i >= 0) windowRects[i] = (h, clip); else windowRects.Add((h, clip));
    }
    Native.RECT SourceScreen()
    {
        if(sourceScreen.Width>0&&sourceScreen.Height>0)return sourceScreen;
        var monitor=Native.MonitorFromWindow(Handle,2);
        return monitor!=0?Native.MonitorBounds(monitor):new Native.RECT(0,0,1,1);
    }
    static bool TryPositionWindowThumb(nint h, nint thumb, Native.RECT visible, Native.RECT full, Native.RECT content, Native.RECT screen, out Native.RECT clip)
    {
        var dest = new Native.RECT(
            content.Left + (int)Math.Round((visible.Left - screen.Left) * content.Width / (double)Math.Max(1, screen.Width)),
            content.Top + (int)Math.Round((visible.Top - screen.Top) * content.Height / (double)Math.Max(1, screen.Height)),
            Math.Max(1, (int)Math.Round(visible.Width * content.Width / (double)Math.Max(1, screen.Width))),
            Math.Max(1, (int)Math.Round(visible.Height * content.Height / (double)Math.Max(1, screen.Height))));
        clip = DockView.Intersect(dest, content);
        if (clip.Width < 1 || clip.Height < 1) return false;
        if (Native.DwmQueryThumbnailSourceSize(thumb, out var size) != 0 || size.X < 1 || size.Y < 1) return false;
        // The thumbnail bitmap covers the outer window rectangle. Crop to the visible
        // frame so the invisible margin is not drawn as an empty border.
        double u0 = (clip.Left - dest.Left) / (double)Math.Max(1, dest.Width);
        double v0 = (clip.Top - dest.Top) / (double)Math.Max(1, dest.Height);
        double u1 = (clip.Right - dest.Left) / (double)Math.Max(1, dest.Width);
        double v1 = (clip.Bottom - dest.Top) / (double)Math.Max(1, dest.Height);
        double fullW = Math.Max(1, full.Width), fullH = Math.Max(1, full.Height);
        int srcX = (int)Math.Round((visible.Left + u0 * visible.Width - full.Left) / fullW * size.X);
        int srcY = (int)Math.Round((visible.Top + v0 * visible.Height - full.Top) / fullH * size.Y);
        int srcR = (int)Math.Round((visible.Left + u1 * visible.Width - full.Left) / fullW * size.X);
        int srcB = (int)Math.Round((visible.Top + v1 * visible.Height - full.Top) / fullH * size.Y);
        srcX = Math.Clamp(srcX, 0, size.X - 1);
        srcY = Math.Clamp(srcY, 0, size.Y - 1);
        srcR = Math.Clamp(srcR, srcX + 1, size.X);
        srcB = Math.Clamp(srcB, srcY + 1, size.Y);
        var crop = new Native.RECT(srcX, srcY, srcR - srcX, srcB - srcY);
        var props = new Native.THUMBNAIL { Flags = 31, Destination = clip, Source = crop, Visible = true, Opacity = 255, SourceClientOnly = false };
        bool ok = Native.DwmUpdateThumbnailProperties(thumb, ref props) == 0;
        if (!ok) Log.Write($"Popout thumbnail update failed for {Native.Title(h)} ({h})");
        return ok;
    }

    void ClearThumbs()
    {
        foreach (var t in thumbBySource.Values) Native.DwmUnregisterThumbnail(t);
        thumbBySource.Clear();
        RestoreSuppressedFrames();
    }
    void HideLeakedFrames()
    {
        foreach (var h in thumbBySource.Keys) SuppressFrame(h);
    }
    void SuppressFrame(nint h)
    {
        if (!Native.IsWindow(h) || frameSuppressed.Contains(h) || session.Desktops.IsWindowPinned(h)) return;
        var current = session.Desktops.Current;
        var owner = session.Desktops.WindowDesktop(h);
        if (owner == Guid.Empty || current == Guid.Empty || owner == current) return;
        Native.SuppressThumbnailGhostFrame(h);
        frameSuppressed.Add(h);
    }
    void RestoreSuppressedFrame(nint h)
    {
        if (!frameSuppressed.Remove(h) || !Native.IsWindow(h)) return;
        Native.RestoreThumbnailGhostFrame(h);
    }
    void RestoreSuppressedFrames()
    {
        foreach (var h in frameSuppressed.ToList()) RestoreSuppressedFrame(h);
    }
    static bool Contains(Native.RECT r, Native.POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    bool InCloseCorner(Native.POINT local)
    {
        if (bodyWindow == 0 || (Native.GetWindowLongPtr(bodyWindow, -16).ToInt64() & 0x00C00000L) == 0) return false;
        for (int i = windowRects.Count - 1; i >= 0; i--)
        {
            if (windowRects[i].Handle != bodyWindow) continue;
            var cell = windowRects[i].Rect;
            if (cell.Width < 48 || cell.Height < 36) return false;
            int bw = Math.Clamp(cell.Width / 7, 14, 48);
            int bh = Math.Clamp(cell.Height / 8, 12, 32);
            return local.X >= cell.Right - bw && local.X < cell.Right && local.Y >= cell.Top && local.Y < cell.Top + bh;
        }
        return false;
    }
}
