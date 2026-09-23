using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using StayView.Core;

namespace StayView;

// The whole thumbnail canvas. Tiles are DWM thumbnails (not XAML children), so we
// hit-test their rectangles ourselves. A click focuses the real window; a drag moves
// the tile; a drop on the desktop bar docks it. Docked tiles also expose a small grey
// right-click menu so they can be closed without first undocking them.
sealed class DockView : IDisposable
{
    enum DragPhase { Idle, Pressed, Dragging, Dropping }
    enum PositionWriter { Layout, Drag }
    sealed class Item
    {
        public required nint Source;
        public nint Thumbnail;
        public Native.RECT Cell;
        public Native.RECT VisualCell;
        public Native.POINT SourceSize;
        public bool Docked;
    }
    readonly nint host;
    readonly Canvas canvas;
    readonly Canvas adornmentCanvas;
    readonly OverviewSession session;
    readonly Settings settings;
    readonly List<Item> items = [];
    // Blue frames drawn just outside docked thumbnails. Keep one XAML object per source
    // on a persistent adornment layer so unrelated reflows do not tear the frame down.
    readonly Dictionary<nint, Border> dockBorders = [];
    // Browsed real windows sit above this full-screen overlay. Their outline is therefore
    // drawn just OUTSIDE the real DWM frame on this persistent layer: the real window hides
    // the inner edge while the ring remains visible around it. The actual foreground window
    // gets a substantially thicker ring than the other browsed window.
    readonly Dictionary<nint, Border> browseBorders = [];
    readonly Dictionary<nint, Border> pinDots = [];
    readonly DispatcherTimer pinHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    // Frame-locked (see FrameTimer): layout/desktop transitions, repel easing, drag work.
    readonly FrameTimer transitionTimer = new();
    readonly FrameTimer dragFrame = new();
    bool dragFramePending;
    Native.RECT dragRawRect;
    readonly DispatcherTimer transitionDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(55) };
    // Neighbours easing out of (or back into) the way of a dragged tile.
    readonly FrameTimer repelTimer = new();
    readonly Dictionary<nint, (Native.RECT From, Native.RECT To, long Started)> repel = [];
    const double RepelDuration = 140;
    // Electric arcs between a dragged tile and the desktop/dock bar when they meet.
    readonly PlasmaEffect plasma;
    // Contact must last this long (plasma visible) before "Dock on bar contact" docks.
    readonly DispatcherTimer contactTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    Pointer? dragPointer;
    readonly Dictionary<nint, Native.RECT> transitionStarts = [];
    readonly Dictionary<nint, Native.RECT> transitionTargets = [];
    TaskCompletionSource? transitionCompletion;
    double transitionDuration = 420;
    long transitionStarted;
    bool transitionActive;
    // Hover glow: several faint accent rings just outside the hovered tile, fading
    // outward so it reads as a soft glow rather than a hard border line.
    static readonly (double Inset, double Thickness, byte Alpha, double Radius)[] GlowRings =
        { (13, 10, 20, 13), (10, 8, 34, 11), (7, 6, 54, 9), (4, 4, 96, 7) };
    readonly List<Border> hoverGlow = [];
    nint dragSource;
    nint hoverSource;
    // The iconic source hover last asked the session to restore, so it asks once per
    // hover-entry rather than on every pointer-move sample.
    nint hoverRestoreAsked;
    readonly HashSet<nint> suppressed = new();
    readonly HashSet<nint> presentedBrowsed = new();
    // Exact miniature rectangle captured immediately before a focus expansion. Shrink uses
    // the same rectangle even if a background reflow recalculates Item.Cell while focused.
    readonly Dictionary<nint, Native.RECT> browseHomes = [];
    Item? externalDragPreview;
    uint pointerId;
    Native.POINT pressed, lastPoint;
    Native.RECT original, lastDragRect, work;
    double scale;
    DragPhase dragPhase;
    bool pressedDocked, overStrip;
    nint pinHoldSource;
    uint pinHoldPointerId;
    Native.POINT pinHoldStart;
    bool pinHoldTriggered;
    // Screen-px rects of the whole desktop bar (dock drop target) and the dock lane
    // either side of the desktop cards.
    Native.RECT stripBar, leftDock, rightDock;
    public event Action<bool>? DockHover;
    // A left press on empty canvas (no tile). TrayApp returns to the grid on it while
    // browsing, so a tile press can instead drag the tile without ending the browse.
    public event Action? BackgroundPressed;
    public event Action? InteractionStarted;
    public event Action? TileDragStarted;
    public event Action<nint>? TileActivated;
    // A click on a docked miniature. TrayApp undocks it clear of any pinned panel.
    public event Action<nint>? DockedTileClicked;
    public event Action<nint>? TileClose;
    public event Action<nint>? TilePinToggle;
    // Fired on the pointer-up that ends a completed pin hold, after the canvas releases
    // capture. Focusing during the hold loses to that release and the pin cannot take text.
    public event Action<nint>? PinGestureCompleted;
    public event Action<nint, Native.POINT>? TileDragMoved;
    public event Action? TileDragEnded;
    public Func<nint, Native.POINT, bool>? ExternalDrop;
    public bool IsDragging => dragPhase is DragPhase.Pressed or DragPhase.Dragging;
    public IEnumerable<nint> Sources => items.Select(i=>i.Source);
    public DockView(nint host, Canvas canvas, Canvas adornmentCanvas, OverviewSession session, Settings settings)
    {
        this.host = host; this.canvas = canvas; this.adornmentCanvas = adornmentCanvas; this.session = session; this.settings = settings;
        // Hover shows a soft glow only; there is no on-tile close button. A window is
        // closed from the overview by middle-clicking its tile (see PointerPressed).
        for (int i = 0; i < GlowRings.Length; i++)
        {
            var (_, thickness, alpha, radius) = GlowRings[i];
            var ring = new Border {
                BorderThickness = new Thickness(thickness), CornerRadius = new CornerRadius(radius),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, 96, 176, 255)),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            Canvas.SetZIndex(ring, 40 + i);
            hoverGlow.Add(ring);
            adornmentCanvas.Children.Add(ring);
        }
        canvas.PointerPressed += PointerPressed;
        canvas.PointerMoved += PointerMoved;
        canvas.PointerReleased += PointerReleased;
        canvas.PointerCanceled += PointerCanceled;
        canvas.PointerCaptureLost += PointerCaptureLost;
        canvas.PointerExited += (_, _) => ClearHover();
        transitionTimer.Tick += (_, _) => TransitionTick();
        repelTimer.Tick += (_, _) => RepelTick();
        zoomTimer.Tick += (_, _) => ZoomTick();
        dragFrame.Tick += (_, _) => DragFrame();
        plasma = new PlasmaEffect(adornmentCanvas);
        contactTimer.Tick += (_, _) => {
            contactTimer.Stop();
            // Still held in contact after the delay: dock where it is.
            if(dragPhase!=DragPhase.Dragging||dragPointer==null||!settings.DockOnBarContact)return;
            SetOverStrip(true);
            FinishPointerGesture(dragPointer,true);
        };
        transitionDelayTimer.Tick += (_, _) => {
            transitionDelayTimer.Stop();
            if(!transitionActive || transitionStarts.Count==0)return;
            transitionStarted=Environment.TickCount64;
            transitionTimer.Start();
        };
        pinHoldTimer.Tick += (_, _) => {
            pinHoldTimer.Stop();
            if(pinHoldSource==0||!Native.IsWindow(pinHoldSource)){StayView.Core.Log.Write($"[pin] tile hold expired source={pinHoldSource}");return;}
            pinHoldTriggered=true;
            StayView.Core.Log.Write($"[pin] tile hold completed for {pinHoldSource} iconic={Native.IsIconic(pinHoldSource)}");
            TilePinToggle?.Invoke(pinHoldSource);
        };
    }
    public void Render(IReadOnlyList<Tile> tiles, Native.RECT work, double scale, DesktopTransitionMode transition=DesktopTransitionMode.Appear)
    {
        // Preserve the last COMPOSITED rectangles before the reflow rewrites Cell/Docked.
        // In particular, clicking a docked mini calls UndockTile -> Reflow; without this
        // snapshot the item is immediately drawn at its canvas destination and Animate
        // layout has no dock-side origin to animate from.
        var previous = items.ToDictionary(i => i.Source, i => (i.Docked, i.VisualCell));
        this.work = work; this.scale = scale;
        StopRepel();
        StopZoom();
        StopTransition(false);
        ClearHover();
        var wanted = tiles.Select(t => t.Window.Handle).ToHashSet();
        foreach (var stale in items.Where(i => !wanted.Contains(i.Source)).ToList())
        {
            Native.DwmUnregisterThumbnail(stale.Thumbnail);
            items.Remove(stale);
            RemoveDockBorder(stale.Source);
        }
        var docked = new List<Item>();
        foreach (var tile in tiles)
        {
            var item = items.FirstOrDefault(i => i.Source == tile.Window.Handle);
            bool hadPrevious = previous.TryGetValue(tile.Window.Handle, out var before);
            if (item == null)
            {
                if (Native.DwmRegisterThumbnail(host, tile.Window.Handle, out var thumbnail) != 0)
                { Log.Write("Cannot register DWM thumbnail: " + tile.Window.Title); continue; }
                item = new Item { Source = tile.Window.Handle, Thumbnail = thumbnail };
                items.Add(item);
            }
            item.Cell = tile.Cell; item.Docked = tile.Role == TileRole.Dock;
            Native.DwmQueryThumbnailSourceSize(item.Thumbnail,out item.SourceSize);
            if (item.Docked) docked.Add(item);
            else if(settings.AnimateLayout && hadPrevious && before.Docked &&
                    before.VisualCell.Width>0 && before.VisualCell.Height>0)
            {
                // Undocking is a normal layout transition, not a desktop transition. Start
                // at the exact dock rectangle that was visible on the previous frame; the
                // existing prepared transition then eases to item.Cell after Render/Show
                // and z-order work has settled.
                transitionStarts[item.Source]=before.VisualCell;
                PositionRect(item,before.VisualCell,PositionWriter.Layout);
            }
            else if(transition==DesktopTransitionMode.Appear)Position(item, PositionWriter.Layout);
            else
            {
                // FlyIn (soft animation on open): start at the window's REAL screen rect so
                // the miniature visibly shrinks/travels from where the window sits into its
                // slot, exactly Task View's opening move. A minimized or off-screen window
                // has no sensible origin, so its miniature simply appears in place.
                Native.RECT start;
                if (transition==DesktopTransitionMode.FlyIn)
                {
                    if (!Native.IsIconic(item.Source) && Native.TryGetVisualBounds(item.Source, out var real) && real.Intersects(work)) start=real;
                    else { Position(item, PositionWriter.Layout); continue; }
                }
                else start=TransitionStart(item.Cell,transition);
                transitionStarts[item.Source]=start;
                PositionRect(item,start,PositionWriter.Layout);
            }
        }
        // Docked windows are equal-size 16:9 live views in the lanes either side of the
        // desktop cards, with a wider gap between them and a persistent blue frame around each.
        // They alternate sides in dock order - first left, second right, and so on - so
        // the bar stays balanced however many windows are docked.
        var leftItems = new List<Item>();
        var rightItems = new List<Item>();
        for (int i = 0; i < docked.Count; i++) (i % 2 == 0 ? leftItems : rightItems).Add(docked[i]);
        int dockGap = (int)Math.Round(20 * scale);
        int perLane = Math.Max(leftItems.Count, rightItems.Count);
        var leftSlots = ThumbnailLayout.DockLaneSlots(perLane, leftDock, dockGap, true);
        var rightSlots = ThumbnailLayout.DockLaneSlots(perLane, rightDock, dockGap, false);
        for (int i = 0; i < leftItems.Count; i++) { leftItems[i].Cell = leftSlots[i]; Position(leftItems[i], PositionWriter.Layout); AddDockBorder(leftItems[i].Source, leftItems[i].Cell); }
        for (int i = 0; i < rightItems.Count; i++) { rightItems[i].Cell = rightSlots[i]; Position(rightItems[i], PositionWriter.Layout); AddDockBorder(rightItems[i].Source, rightItems[i].Cell); }
        RefreshDockBorders();
        UpdatePinDots(presentedBrowsed,null);
        Select(session.Selected);
        // A full-width slide reads best a little quicker than the other deliveries.
        transitionDuration = transition==DesktopTransitionMode.Slide ? 340 : 420;
        if(transitionStarts.Count>0)transitionActive=true;
    }
    public Task AnimateWindowsAsync(IReadOnlyDictionary<nint, Native.RECT> bounds, bool expanding)
    {
        // Capture the requested miniature(s) BEFORE finishing any older layout transition.
        // This is important when focus is requested mid-move (for example dock -> canvas):
        // unrelated thumbnails may finish normally, but the focused miniature must continue
        // directly from the pixels currently on screen rather than taking two journeys.
        var currentStarts = expanding
            ? items.Where(i => bounds.ContainsKey(i.Source) && i.VisualCell.Width > 0 && i.VisualCell.Height > 0)
                .ToDictionary(i => i.Source, i => i.VisualCell)
            : new Dictionary<nint, Native.RECT>();
        if(expanding)
            foreach(var pair in currentStarts)browseHomes[pair.Key]=pair.Value;
        // Finish unrelated movement, but never write the focused miniature to an
        // intermediate layout Cell. It stays exactly at its captured VisualCell until
        // the focus transition is seeded, so there is no compositable second journey.
        StopTransition(true, expanding ? currentStarts : null);
        if (!settings.AnimateLayout || !session.Active) return Task.CompletedTask;
        ClearHover();
        foreach (var item in items)
        {
            if (!bounds.TryGetValue(item.Source, out var real) || !real.Intersects(work)) continue;
            transitionStarts[item.Source] = expanding
                ? currentStarts.GetValueOrDefault(item.Source, item.Cell)
                : real;
            transitionTargets[item.Source] = expanding ? real : browseHomes.GetValueOrDefault(item.Source,item.Cell);
        }
        if (transitionStarts.Count == 0) return Task.CompletedTask;
        transitionCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transitionDuration = 280;
        transitionActive = true;
        foreach (var item in items)
            if (transitionStarts.TryGetValue(item.Source, out var start)) PositionRect(item, start, PositionWriter.Layout);
        transitionStarted = Environment.TickCount64;
        transitionTimer.Start();
        return transitionCompletion.Task;
    }
    public void CancelWindowAnimation() => StopTransition(true);
    // A completed expand hands off to the real HWND at the animation's full-size target.
    // Clear transition bookkeeping WITHOUT snapping the DWM thumbnail back to its tile;
    // SetBrowsed then hides it in place once the real window is confirmed foreground.
    public void CommitWindowAnimation() => StopTransition(false);
    public void StartPreparedTransition()
    {
        if(!transitionActive || transitionStarts.Count==0)return;
        // Render() deliberately leaves the new desktop thumbnails in their start pose.
        // Wait until OverlayChrome has completed AppWindow.Show/z-order work, then hold
        // that pose for one visible frame before beginning the DWM movement.
        transitionDelayTimer.Stop();
        transitionDelayTimer.Start();
    }
    public void SetStrip(Native.RECT bar, Native.RECT left, Native.RECT right) { stripBar = bar; leftDock = left; rightDock = right; }
    public void SuppressSource(nint source) => SetSuppressed(source==0?Array.Empty<nint>():new[]{source});
    // Hide the tiles of the browsed windows (up to two) and restore any previously hidden.
    public void SetSuppressed(IReadOnlyList<nint> sources)
    {
        var next=new HashSet<nint>(sources.Where(s=>s!=0));
        if(next.SetEquals(suppressed))return;
        // Once a real window is taking over there must be no canvas adornment left for
        // that tile. Clear hover immediately (rather than waiting for pointer movement)
        // and hide dock frames in the same transaction as thumbnail suppression; otherwise
        // a docked tile could leave a blue "zombie" border behind after its pixels vanish.
        if(next.Count>0)ClearHover();
        foreach(var prev in suppressed.Where(s=>!next.Contains(s)).ToList())SetSourceVisible(prev,true);
        foreach(var s in next.Where(s=>!suppressed.Contains(s)).ToList())SetSourceVisible(s,false);
        suppressed.Clear(); suppressed.UnionWith(next);
        RefreshDockBorders();
    }
    // Keep hidden browse thumbnails parked at the real HWND rectangle. If z-order ever
    // becomes unsafe, OverlayChrome can reveal the thumbnail immediately and it occupies
    // the same pixels rather than falling back to an old small-tile location.
    void UpdateBrowsedVisuals(IReadOnlyList<nint> sources)
    {
        foreach(var source in sources)
        {
            if(Native.IsIconic(source)||!Native.TryGetVisualBounds(source,out var real)||!real.Intersects(work))continue;
            var item=items.FirstOrDefault(i=>i.Source==source);
            if(item!=null)PositionRect(item,real,PositionWriter.Layout);
        }
    }
    public void SetBrowsePresentation(IReadOnlyList<nint> orderedSources,IReadOnlyList<nint> suppressibleSources,nint focusedSource,IReadOnlySet<nint>? pinnedSources=null)
    {
        // A parked full-size fallback is presentation state, never the tile's home.
        // Demotion by the browse cap has no shrink animation/reflow to reset it for us.
        foreach(var item in items)
            if(presentedBrowsed.Contains(item.Source) && !orderedSources.Contains(item.Source)
                && !transitionTargets.ContainsKey(item.Source))Position(item,PositionWriter.Layout);
        presentedBrowsed.Clear(); presentedBrowsed.UnionWith(orderedSources);
        UpdateBrowsedVisuals(orderedSources);
        SetSuppressed(suppressibleSources);
        UpdateBrowseBorders(orderedSources,focusedSource,pinnedSources);
        UpdatePinDots(orderedSources,pinnedSources);
    }
    public void ShowBrowseFallback(IReadOnlyList<nint> orderedSources,nint focusedSource,IReadOnlySet<nint>? pinnedSources=null)
    {
        SetBrowsePresentation(orderedSources,Array.Empty<nint>(),focusedSource,pinnedSources);
    }
    public void RefreshBrowseGeometry(IReadOnlyList<nint> orderedSources,nint focusedSource,IReadOnlySet<nint>? pinnedSources=null)
    {
        UpdateBrowsedVisuals(orderedSources);
        UpdateBrowseBorders(orderedSources,focusedSource,pinnedSources);
        UpdatePinDots(orderedSources,pinnedSources);
    }
    void SetSourceVisible(nint source,bool visible)
    {
        var item=items.FirstOrDefault(i=>i.Source==source);
        if(item==null||item.Thumbnail==0)return;
        // Tiles under a zoomed card / dock preview stay hidden (the 500 ms prune calls this).
        var props=new Native.THUMBNAIL{Flags=8,Visible=(visible || transitionTargets.ContainsKey(source)) && !Covered(source,item.VisualCell)}; // DWM_TNP_VISIBLE
        Native.DwmUpdateThumbnailProperties(item.Thumbnail,ref props);
    }
    void SetOverStrip(bool over) { if (over == overStrip) return; overStrip = over; DockHover?.Invoke(over); }
    public void Select(nint source)
    {
        // Miniatures are the window pixels themselves; there is deliberately no
        // selection card, background plate, padding, or second title treatment.
    }
    void Position(Item item, PositionWriter writer)
        => PositionRect(item,item.Cell,writer);
    void PositionRect(Item item, Native.RECT cell, PositionWriter writer)
    {
        item.VisualCell = cell;
        // Keep cover bookkeeping in step with what this write makes visible.
        if (Covered(item.Source, cell)) coverHidden.Add(item.Source); else coverHidden.Remove(item.Source);
        var props = new Native.THUMBNAIL { Flags = writer == PositionWriter.Drag ? 1u : 1u | 4u | 8u | 16u,
            Destination = new(cell.Left - work.Left, cell.Top - work.Top, cell.Width, cell.Height),
            Opacity = 255, Visible = (!suppressed.Contains(item.Source) || transitionTargets.ContainsKey(item.Source)) && !Covered(item.Source, cell), SourceClientOnly = false };
        Native.DwmUpdateThumbnailProperties(item.Thumbnail, ref props);
    }
    // A hover-zoomed desktop card (XAML) is drawn over the canvas; DWM thumbnails would
    // otherwise composite on top of it, so tiles under it are hidden while it is enlarged.
    // The same applies to a hover-zoomed dock mini and its frame (zoomCover), except the
    // zoomed mini itself.
    Native.RECT? coverArea, zoomCover;
    readonly HashSet<nint> coverHidden = [];
    bool Covered(nint source, Native.RECT cell) =>
        (coverArea is { } a && cell.Intersects(a)) || (zoomCover is { } z && source != zoomSource && cell.Intersects(z));
    public void SetCoverArea(Native.RECT? area)
    {
        if (Nullable.Equals(area, coverArea)) return;
        coverArea = area;
        RefreshCover();
    }
    // Re-show/hide only the tiles whose covered state changed.
    void RefreshCover()
    {
        foreach (var item in items)
        {
            bool hide = Covered(item.Source, item.VisualCell);
            if (hide == coverHidden.Contains(item.Source)) continue;
            if (hide) coverHidden.Add(item.Source); else coverHidden.Remove(item.Source);
            PositionRect(item, item.VisualCell, PositionWriter.Layout);
        }
    }
    Native.RECT TransitionStart(Native.RECT target,DesktopTransitionMode mode)
    {
        int cx=work.Left+work.Width/2,cy=work.Top+work.Height/2;
        return mode switch
        {
            DesktopTransitionMode.ShiftInRight => new(
                work.Right-Math.Max(8,target.Width/6),target.Top,target.Width,target.Height),
            DesktopTransitionMode.ShiftInLeft => new(
                work.Left-target.Width+Math.Max(8,target.Width/6),target.Top,target.Width,target.Height),
            DesktopTransitionMode.Explode => new(cx-Math.Max(1,target.Width/12)/2,cy-Math.Max(1,target.Height/12)/2,
                Math.Max(1,target.Width/12),Math.Max(1,target.Height/12)),
            DesktopTransitionMode.Implode => ImplodeStart(target,cx,cy),
            // Task View's switch: the whole layout moves together by one screen width,
            // entering from the side of the desktop being moved to.
            DesktopTransitionMode.Slide => new(target.Left+session.DesktopTransitionDirection*work.Width,target.Top,target.Width,target.Height),
            _ => target
        };
    }
    Native.RECT ImplodeStart(Native.RECT target,int cx,int cy)
    {
        int tcx=target.Left+target.Width/2,tcy=target.Top+target.Height/2;
        int w=Math.Min(work.Width,Math.Max(1,(int)Math.Round(target.Width*1.65)));
        int h=Math.Min(work.Height,Math.Max(1,(int)Math.Round(target.Height*1.65)));
        int scx=cx+(int)Math.Round((tcx-cx)*1.35),scy=cy+(int)Math.Round((tcy-cy)*1.35);
        return ThumbnailLayout.Clamp(new Native.RECT(scx-w/2,scy-h/2,w,h),work);
    }
    static Native.RECT Lerp(Native.RECT a,Native.RECT b,double t)=>new(
        (int)Math.Round(a.Left+(b.Left-a.Left)*t),(int)Math.Round(a.Top+(b.Top-a.Top)*t),
        Math.Max(1,(int)Math.Round(a.Width+(b.Width-a.Width)*t)),Math.Max(1,(int)Math.Round(a.Height+(b.Height-a.Height)*t)));
    void TransitionTick()
    {
        if(!transitionActive){transitionTimer.Stop();return;}
        double t=Math.Clamp((Environment.TickCount64-transitionStarted)/transitionDuration,0,1);
        double eased=1-Math.Pow(1-t,3);
        foreach(var item in items)
            if(transitionStarts.TryGetValue(item.Source,out var start))
                PositionRect(item,Lerp(start,transitionTargets.GetValueOrDefault(item.Source,item.Cell),eased),PositionWriter.Layout);
        if(t>=1)
        {
            transitionTimer.Stop(); transitionActive=false;
            if (transitionCompletion != null)
            { var completion=transitionCompletion; transitionCompletion=null; completion.TrySetResult(); }
            else StopTransition(true);
        }
    }
    void StopTransition(bool finish, IReadOnlyDictionary<nint, Native.RECT>? preserveVisual = null)
    {
        transitionDelayTimer.Stop();
        transitionTimer.Stop();
        var completion=transitionCompletion; transitionCompletion=null;
        transitionTargets.Clear();
        if(finish)
            foreach(var item in items)
                if(transitionStarts.ContainsKey(item.Source)
                    && (preserveVisual==null || !preserveVisual.ContainsKey(item.Source)))
                    Position(item,PositionWriter.Layout);
        transitionStarts.Clear();transitionActive=false;
        completion?.TrySetResult();
    }
    void AddDockBorder(nint source, Native.RECT cell)
    {
        // DWM thumbnails composite ABOVE XAML, so the frame must sit fully OUTSIDE the
        // thumbnail rect or the live pixels hide it. Draw a fatter ring a small gap away
        // from every edge so it reads as a complete border all the way round.
        const double gap = 2, thickness = 3;
        double inset = gap + thickness;
        double left = (cell.Left - work.Left) / scale - inset;
        double top = (cell.Top - work.Top) / scale - inset;
        double w = cell.Width / scale + 2 * inset, h = cell.Height / scale + 2 * inset;
        if(!dockBorders.TryGetValue(source,out var frame))
        {
            frame = new Border {
                BorderThickness = new Thickness(thickness),
                BorderBrush = DockBorderBrush(source),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false };
            Canvas.SetZIndex(frame, 50);
            adornmentCanvas.Children.Add(frame);
            dockBorders[source]=frame;
        }
        frame.BorderBrush=DockBorderBrush(source);
        frame.Width = Math.Max(1, w); frame.Height = Math.Max(1, h);
        Canvas.SetLeft(frame, left); Canvas.SetTop(frame, top);
        frame.Visibility=ShouldShowDockBorder(source)?Visibility.Visible:Visibility.Collapsed;
    }
    // Pinned docked windows get a white frame; every other docked window is standard blue.
    SolidColorBrush DockBorderBrush(nint source)
        => session.IsPinned(source)
            ? new SolidColorBrush(Microsoft.UI.Colors.White)
            : new SolidColorBrush(Windows.UI.Color.FromArgb(245, 30, 70, 150));
    bool ShouldShowDockBorder(nint source)
    {
        var item=items.FirstOrDefault(i=>i.Source==source);
        // Read the authoritative SESSION dock role as well as the last rendered item role.
        // A browsed window can be minimized before the next Reflow updates Item.Docked.
        // In that interval the old XAML frame used to survive by itself as an empty border.
        // Legitimate minimized-on-entry dock residents remain in session.docked, so they
        // still keep their blue frame even though their real HWND is iconic.
        return item?.Docked==true && session.IsDocked(source)
            && Native.IsWindow(source) && !suppressed.Contains(source)
            && source != zoomSource; // a hover-zoomed mini has left its frame in the bar
    }

    // Hover zoom for docked minis: after a short dwell the mini lifts above everything and
    // eases out to a larger live preview, growing away from the bar; leaving it eases it back.
    // A click while zoomed still undocks (hit-testing uses the visible rectangle).
    readonly FrameTimer zoomTimer = new();
    nint zoomSource, zoomPending;
    Spring zoom;
    double zoomTarget;
    long zoomPendingSince, zoomLastTick;
    const double ZoomScale = 2.6, ZoomDelayMs = 120;

    void UpdateDockZoom(Native.POINT p)
    {
        var over = items.LastOrDefault(i => i.Docked && !suppressed.Contains(i.Source)
            && (Contains(i.Source == zoomSource ? i.VisualCell : i.Cell, p)));
        nint want = over?.Source ?? 0;
        if (want != 0 && want == zoomSource) { zoomTarget = 1; zoomPending = 0; }
        else
        {
            if (zoomSource != 0) zoomTarget = 0;              // shrink the current one first
            if (want != zoomPending) { zoomPending = want; zoomPendingSince = Environment.TickCount64; }
        }
        // Reset the frame clock only when (re)starting: resetting on every pointer sample
        // starved the spring of time while the mouse moved.
        if ((zoomSource != 0 || zoomPending != 0) && !zoomTimer.IsEnabled) { zoomLastTick = Environment.TickCount64; zoomTimer.Start(); }
    }
    void ZoomTick()
    {
        long now = Environment.TickCount64;
        double dt = Math.Max(0, now - zoomLastTick); zoomLastTick = now;
        if (zoomSource != 0)
        {
            var item = items.FirstOrDefault(i => i.Source == zoomSource && i.Docked);
            if (item == null) { EndZoom(); return; }   // pruned mid-zoom: still clear frame + cover
            zoom.Step(zoomTarget, dt / 1000);
            double amount = Math.Clamp(zoom.Value, 0, 1);
            var shown = Lerp(item.Cell, ZoomRect(item.Cell), amount);
            PositionRect(item, shown, PositionWriter.Drag);
            PlaceZoomFrame(shown, amount, DockBorderBrush(item.Source));
            if (zoomTarget == 0 && zoom.Settled(0)) EndZoom();
            else if (zoomTarget == 1 && zoom.Settled(1) && zoomPending == 0) { zoom.Value = 1; zoom.Velocity = 0; zoomTimer.Stop(); }
            return;
        }
        if (zoomPending == 0) { zoomTimer.Stop(); return; }
        if (now - zoomPendingSince < ZoomDelayMs) return;
        BeginZoom(zoomPending);
        zoomPending = 0;
    }
    void BeginZoom(nint source)
    {
        var item = items.FirstOrDefault(i => i.Source == source && i.Docked);
        if (item == null) return;
        // DWM paints in registration order: re-register once so the preview sits above
        // canvas tiles it grows over (same lift as a drag).
        if (Native.DwmRegisterThumbnail(host, item.Source, out var raised) == 0)
        {
            var old = item.Thumbnail; item.Thumbnail = raised;
            PositionRect(item, item.Cell, PositionWriter.Layout);
            Native.DwmUnregisterThumbnail(old);
            items.Remove(item); items.Add(item);
        }
        zoomSource = source; zoom.Reset(); zoomTarget = 1;
        RefreshDockBorders();
    }
    // Thicker frame just outside the zoomed preview (XAML sits under DWM, so it must stay
    // outside the thumbnail); it fades with the zoom. Tiles under preview+frame are hidden.
    Border? zoomFrame;
    const double ZoomFrameGap = 3, ZoomFrameThickness = 4;
    void PlaceZoomFrame(Native.RECT shown, double amount, Brush brush)
    {
        if (zoomFrame?.Parent == null)
        {
            zoomFrame = new Border { BorderThickness = new Thickness(ZoomFrameThickness), CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), IsHitTestVisible = false };
            Canvas.SetZIndex(zoomFrame, 55);
            adornmentCanvas.Children.Add(zoomFrame);
        }
        double inset = ZoomFrameGap + ZoomFrameThickness;
        zoomFrame.BorderBrush = brush;
        zoomFrame.Width = Math.Max(1, shown.Width / scale + 2 * inset);
        zoomFrame.Height = Math.Max(1, shown.Height / scale + 2 * inset);
        Canvas.SetLeft(zoomFrame, (shown.Left - work.Left) / scale - inset);
        Canvas.SetTop(zoomFrame, (shown.Top - work.Top) / scale - inset);
        zoomFrame.Opacity = amount;
        zoomFrame.Visibility = amount > .01 ? Visibility.Visible : Visibility.Collapsed;
        int pad = (int)Math.Ceiling(inset * scale);
        zoomCover = amount > .01 ? new Native.RECT(shown.Left - pad, shown.Top - pad, shown.Width + 2 * pad, shown.Height + 2 * pad) : null;
        RefreshCover();
    }
    void EndZoom()
    {
        var item = items.FirstOrDefault(i => i.Source == zoomSource);
        if (zoomFrame != null) zoomFrame.Visibility = Visibility.Collapsed;
        zoomCover = null;
        zoomSource = 0; zoom.Reset();
        RefreshCover();
        if (item != null) PositionRect(item, item.Cell, PositionWriter.Layout);
        RefreshDockBorders();
    }
    void StopZoom()
    {
        zoomTimer.Stop(); zoomPending = 0;
        EndZoom();   // always: frame, cover and state, even if the zoomed item already left
    }
    // Larger preview of a docked mini, centred on it and growing away from the bar.
    Native.RECT ZoomRect(Native.RECT cell)
    {
        int w = (int)Math.Min(cell.Width * ZoomScale, work.Width * .42);
        int h = (int)Math.Round(w * cell.Height / (double)Math.Max(1, cell.Width));
        int x = cell.Left + cell.Width / 2 - w / 2;
        int y = settings.DesktopStripPosition == DesktopStripPosition.Bottom ? cell.Bottom - h : cell.Top;
        return ThumbnailLayout.Clamp(new Native.RECT(x, y, w, h), work);
    }
    void RefreshDockBorders()
    {
        foreach(var (source,frame) in dockBorders)
        {
            frame.BorderBrush=DockBorderBrush(source);
            frame.Visibility=ShouldShowDockBorder(source)?Visibility.Visible:Visibility.Collapsed;
        }
    }
    void RemoveDockBorder(nint source)
    {
        if(!dockBorders.Remove(source,out var frame))return;
        adornmentCanvas.Children.Remove(frame);
    }
    void UpdateBrowseBorders(IReadOnlyList<nint> sources,nint focusedSource,IReadOnlySet<nint>? pinnedSources)
    {
        var wanted=sources.Where(s=>s!=0&&Native.IsWindow(s)).ToHashSet();
        foreach(var stale in browseBorders.Keys.Where(s=>!wanted.Contains(s)).ToList())
        {
            adornmentCanvas.Children.Remove(browseBorders[stale]);
            browseBorders.Remove(stale);
        }
        foreach(var source in wanted)
        {
            if(Native.IsIconic(source)||!Native.IsWindowVisible(source)||
               !Native.TryGetVisualBounds(source,out var real)||!real.Intersects(work))
            {
                if(browseBorders.TryGetValue(source,out var hidden))hidden.Visibility=Visibility.Collapsed;
                continue;
            }
            bool pinned=pinnedSources?.Contains(source)==true;
            bool focused=source==focusedSource;
            // Pin state is called out by the corner dot below. Keep the established focus
            // outline thickness so pinning does not turn the whole window into a heavy box.
            double thickness=focused?6:2;
            double gap=1;
            double inset=gap+thickness;
            if(!browseBorders.TryGetValue(source,out var frame))
            {
                frame=new Border {
                    Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    IsHitTestVisible=false,
                    CornerRadius=new CornerRadius(4) };
                Canvas.SetZIndex(frame,60);
                adornmentCanvas.Children.Add(frame);
                browseBorders[source]=frame;
            }
            frame.BorderThickness=new Thickness(thickness);
            frame.BorderBrush=pinned||focused?GlassAppearance.ActiveBrush():GlassAppearance.EdgeBrush();
            frame.Width=Math.Max(1,real.Width/scale+2*inset);
            frame.Height=Math.Max(1,real.Height/scale+2*inset);
            Canvas.SetLeft(frame,(real.Left-work.Left)/scale-inset);
            Canvas.SetTop(frame,(real.Top-work.Top)/scale-inset);
            frame.Visibility=Visibility.Visible;
        }
    }
    void UpdatePinDots(IEnumerable<nint> browsedSources,IReadOnlySet<nint>? pinnedSources)
    {
        // Pin state also decides the dock frame colour (white = pinned).
        RefreshDockBorders();
        var tilePins=items.Where(i=>!suppressed.Contains(i.Source)&&session.IsPinned(i.Source)).ToDictionary(i=>i.Source);
        var browsePins=browsedSources.Where(s=>Native.IsWindow(s)&&session.IsPinned(s)&&(pinnedSources==null||pinnedSources.Contains(s))).ToHashSet();
        var wanted=tilePins.Keys.Concat(browsePins).ToHashSet();
        foreach(var stale in pinDots.Keys.Where(s=>!wanted.Contains(s)).ToList())
        {
            adornmentCanvas.Children.Remove(pinDots[stale]);
            pinDots.Remove(stale);
        }
        foreach(var source in wanted)
        {
            if(!pinDots.TryGetValue(source,out var dot))
            {
                const double dotSize=18;
                dot=new Border {
                    Width=dotSize,Height=dotSize,CornerRadius=new CornerRadius(dotSize/2),
                    Background=GlassAppearance.ActiveBrush(),
                    BorderBrush=GlassAppearance.DesktopCloseGlowBrush(),
                    BorderThickness=new Thickness(3),
                    IsHitTestVisible=false
                };
                Canvas.SetZIndex(dot,70);
                adornmentCanvas.Children.Add(dot);pinDots[source]=dot;
            }
            if(tilePins.TryGetValue(source,out var item))
            {
                // Just outside the tile's top-left corner, like a pinned real window: the
                // tile is a DWM thumbnail drawn over all XAML, so a dot inside it was hidden
                // (only a sliver showed at the rounded corner, looking like a stray dot).
                const double dotSize=18,dotGap=4;
                Canvas.SetLeft(dot,Math.Max(1,(item.VisualCell.Left-work.Left)/scale-dotSize-dotGap));
                Canvas.SetTop(dot,Math.Max(1,(item.VisualCell.Top-work.Top)/scale-dotSize-dotGap));
                dot.Visibility=Visibility.Visible;
            }
            else if(Native.IsWindowVisible(source)&&!Native.IsIconic(source)&&Native.TryGetVisualBounds(source,out var real)&&real.Intersects(work))
            {
                const double dotSize=18,dotGap=5;
                Canvas.SetLeft(dot,Math.Max(1,(real.Left-work.Left)/scale-dotSize-dotGap));
                Canvas.SetTop(dot,Math.Max(1,(real.Top-work.Top)/scale-dotSize-dotGap));
                dot.Visibility=Visibility.Visible;
            }
            else dot.Visibility=Visibility.Collapsed;
        }
    }
    void UpdateHover(Native.POINT p)
    {
        if (dragPhase != DragPhase.Idle || transitionActive) return;
        if (coverArea is { } covered && Contains(covered, p)) { ClearHover(); return; }
        UpdateDockZoom(p);
        // Tiles hidden under a zoomed dock preview get no glow.
        if (zoomCover is { } zc && Contains(zc, p)) { HideGlow(); hoverSource = 0; return; }
        var item = items.LastOrDefault(i => !suppressed.Contains(i.Source) && Contains(i.Cell, p));
        // If a source was minimized after its thumbnail was registered, DWM may leave
        // an empty tile while this XAML glow still renders. Drop the glow immediately
        // and ask the session to restore the real window behind the overview so the live
        // thumbnail can resume instead of presenting a clickable "ghost" rectangle.
        if (item != null && Native.IsIconic(item.Source))
        {
            ClearHover();
            // Ask ONCE per hover-entry, not on every pointer-move sample. A source that
            // refuses to stay restored (e.g. a background updater that re-minimizes
            // itself) otherwise turned this into a ~120/sec SetWindowPlacement storm that
            // flapped the real window in and out behind the canvas. The 500 ms timer's
            // RestoreMinimizedSourcesForOverview handles any periodic retry; hover only
            // needs to kick it once when the pointer arrives on the ghost tile.
            if (hoverRestoreAsked != item.Source)
            {
                hoverRestoreAsked = item.Source;
                session.RestoreMinimizedSourceForOverview(item.Source);
            }
            return;
        }
        hoverRestoreAsked = 0;
        nint next = item?.Source ?? 0;
        if (next == hoverSource) return;
        hoverSource = next;
        // Docked tiles already carry a white frame; only non-docked tiles get the glow.
        if (item == null || item.Docked) { HideGlow(); hoverSource = 0; return; }
        // Soft glow around the hovered tile to show it is active.
        ShowGlow(item.Cell);
    }
    void ShowGlow(Native.RECT cell)
    {
        for (int i = 0; i < hoverGlow.Count; i++)
        {
            double inset = GlowRings[i].Inset;
            var ring = hoverGlow[i];
            ring.Width = Math.Max(1, cell.Width / scale + 2 * inset);
            ring.Height = Math.Max(1, cell.Height / scale + 2 * inset);
            Canvas.SetLeft(ring, (cell.Left - work.Left) / scale - inset);
            Canvas.SetTop(ring, (cell.Top - work.Top) / scale - inset);
            ring.Visibility = Visibility.Visible;
        }
    }
    void HideGlow() { foreach (var ring in hoverGlow) ring.Visibility = Visibility.Collapsed; }
    public void ClearHover()
    {
        hoverSource = 0;
        HideGlow();
        // Pointer gone (left the canvas, pressed elsewhere, overview hiding): ease any
        // hover-zoomed dock mini back into the bar.
        zoomPending = 0;
        if (zoomSource != 0) { zoomTarget = 0; if (!zoomTimer.IsEnabled) { zoomLastTick = Environment.TickCount64; zoomTimer.Start(); } }
    }
    Native.POINT ScreenPoint(PointerRoutedEventArgs e)
    {
        var p=e.GetCurrentPoint(canvas).Position;
        return new Native.POINT {
            X=work.Left+(int)Math.Round(p.X*scale),
            Y=work.Top +(int)Math.Round(p.Y*scale)
        };
    }
    void ShowDockMenu(Item item, Windows.Foundation.Point position)
    {
        var menu=new MenuFlyout();
        GlassAppearance.StyleGreyMenu(menu);
        var close=new MenuFlyoutItem {
            Text="Close",
            Foreground=GlassAppearance.MenuWhiteBrush()
        };
        close.Click+=(_,_)=>{
            ClearHover();
            TileClose?.Invoke(item.Source);
        };
        menu.Items.Add(close);
        menu.ShowAt(canvas,new FlyoutShowOptions{Position=position});
    }
    public nint HitSource(Native.POINT screen)
    {
        var item=items.LastOrDefault(i=>!suppressed.Contains(i.Source)&&(Contains(i.VisualCell,screen)||Contains(i.Cell,screen)));
        return item?.Source??0;
    }
    void PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if(!session.Active || dragPhase!=DragPhase.Idle || !Native.IsWindowVisible(host))return;
        var point=e.GetCurrentPoint(canvas);
        var p=ScreenPoint(e);
        // A hover-zoomed desktop card owns this area; its own Tapped handles the click.
        if(coverArea is {} covered && Contains(covered,p))return;
        // The zoom frame ring hides tiles under it; a press there must not hit them.
        if(zoomCover is {} ring && Contains(ring,p) && !items.Any(i=>i.Source==zoomSource&&Contains(i.VisualCell,p)))return;
        var item=items.LastOrDefault(i=>!suppressed.Contains(i.Source)&&(Contains(i.VisualCell,p)||Contains(i.Cell,p)));
        InteractionStarted?.Invoke();
        // Settle the animation after hit-testing its visible pixels, so a press is never lost.
        StopTransition(true);
        // Middle-click a tile to close its window (replaces the old on-tile ✕ button).
        if(point.Properties.IsMiddleButtonPressed)
        {
            if(item!=null){ClearHover();TileClose?.Invoke(item.Source);e.Handled=true;}
            return;
        }
        // Docked windows are global workspace shortcuts. Right-clicking one opens a
        // deliberately minimal grey context menu with Close; the real HWND receives a
        // normal WM_CLOSE through TrayApp/OverviewSession, so apps can still present any
        // native save/discard confirmation before they actually disappear.
        if(point.Properties.IsRightButtonPressed)
        {
            if(item!=null)
            {
                ClearHover();
                pinHoldSource=item.Source;
                pinHoldPointerId=e.Pointer.PointerId;
                pinHoldStart=p;
                pinHoldTriggered=false;
                pinHoldTimer.Stop();pinHoldTimer.Start();
                if(!canvas.CapturePointer(e.Pointer)){StayView.Core.Log.Write($"[pin] tile right-down capture failed {item.Source}");pinHoldTimer.Stop();pinHoldSource=0;pinHoldPointerId=0;return;}
                e.Handled=true;
            }
            return;
        }
        if(!point.Properties.IsLeftButtonPressed)return;
        // Empty canvas: not a tile. While browsing this is the "put the grid back" gesture
        // (handled by TrayApp); a press ON a tile below is handled here so tiles can be
        // dragged even while a window is focused.
        if(item==null){BackgroundPressed?.Invoke();return;}

        // The tile rectangle is the hit target. A click focuses the window; a drag past
        // the 8 px threshold moves the tile. The decision is made on release.
        dragSource=item.Source;
        pressedDocked=item.Docked;
        pointerId=e.Pointer.PointerId;
        pressed=lastPoint=p;
        original=lastDragRect=item.Cell;
        dragPhase=DragPhase.Pressed;
        if (!canvas.CapturePointer(e.Pointer))
        { dragSource=0; pointerId=0; dragPhase=DragPhase.Idle; return; }
        e.Handled=true;
    }
    void PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if(pinHoldSource!=0&&e.Pointer.PointerId==pinHoldPointerId)
        {
            var current=ScreenPoint(e);
            // IsRightButtonPressed is often false for a held right button in WinUI, which
            // cancelled every tile pin immediately. Movement is the only cancel.
            if(Math.Max(Math.Abs(current.X-pinHoldStart.X),Math.Abs(current.Y-pinHoldStart.Y))>=8)
                CancelPinHold(e.Pointer);
            e.Handled=true;
            return;
        }
        if(dragPhase==DragPhase.Idle) { UpdateHover(ScreenPoint(e)); return; }
        if(dragSource==0 || e.Pointer.PointerId!=pointerId)return;
        var p=ScreenPoint(e);
        lastPoint=p;
        // Docked minis are click-to-undock only; they never start a drag.
        // Pinned tiles are locked in place: they never start a drag (a still press+release
        // is still a click that brings the pinned window forward).
        if(dragPhase==DragPhase.Pressed && !pressedDocked && !session.IsPinned(dragSource) && Math.Max(Math.Abs(p.X-pressed.X),Math.Abs(p.Y-pressed.Y))>=8)
        {
            dragPhase=DragPhase.Dragging;
            ClearHover();
            // DWM's registration order is its paint order. Lift just the dragged copy
            // once, so overlapping tiles follow the same visual and mouse-hit order.
            var item=items.FirstOrDefault(i=>i.Source==dragSource);
            if(item!=null && Native.DwmRegisterThumbnail(host,item.Source,out var raised)==0)
            {
                var old=item.Thumbnail; item.Thumbnail=raised;
                Position(item,PositionWriter.Layout);
                Native.DwmUnregisterThumbnail(old);
                items.Remove(item); items.Add(item);
            }
            // A tile still sliding home from the previous drag now follows the pointer only.
            repel.Remove(dragSource);
            session.BeginTileDrag(dragSource);
            TileDragStarted?.Invoke();
        }
        if(dragPhase==DragPhase.Dragging)
        {
            var rect=new Native.RECT(original.Left+p.X-pressed.X,original.Top+p.Y-pressed.Y,original.Width,original.Height);
            // The tile itself follows every pointer sample; the heavier per-move work
            // (repel solve, plasma, dock contact) is coalesced to once per frame.
            lastDragRect=MoveVisual(dragSource,rect);
            TileDragMoved?.Invoke(dragSource,p);
            SetOverStrip(Contains(stripBar,p));
            dragPointer=e.Pointer;
            dragRawRect=rect;
            dragFramePending=true;
            dragFrame.Start();
        }
        e.Handled=true;
    }
    void PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if(pinHoldSource!=0&&e.Pointer.PointerId==pinHoldPointerId)
        {
            var source=pinHoldSource;
            bool held=pinHoldTriggered;
            var point=e.GetCurrentPoint(canvas);
            var current=ScreenPoint(e);
            bool stationary=Math.Max(Math.Abs(current.X-pinHoldStart.X),Math.Abs(current.Y-pinHoldStart.Y))<8;
            CancelPinHold(e.Pointer);
            var item=items.FirstOrDefault(i=>i.Source==source);
            if(!held&&stationary&&item?.Docked==true)ShowDockMenu(item,point.Position);
            if(held)PinGestureCompleted?.Invoke(source);
            e.Handled=true;
            return;
        }
        if(dragPhase==DragPhase.Idle || dragSource==0 || e.Pointer.PointerId!=pointerId)return;
        if(e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed)return;
        lastPoint=ScreenPoint(e);
        if (dragPhase==DragPhase.Dragging)
        {
            lastDragRect=MoveVisual(dragSource,new Native.RECT(original.Left+lastPoint.X-pressed.X,
                original.Top+lastPoint.Y-pressed.Y,original.Width,original.Height));
            SetOverStrip(Contains(stripBar,lastPoint));
        }
        FinishPointerGesture(e.Pointer,true);
        e.Handled=true;
    }
    void PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if(pinHoldSource!=0&&e.Pointer.PointerId==pinHoldPointerId){CancelPinHold(e.Pointer);return;}
        if(dragPhase==DragPhase.Idle || e.Pointer.PointerId!=pointerId)return;
        FinishPointerGesture(e.Pointer,false);
    }
    void PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if(pinHoldSource!=0&&e.Pointer.PointerId==pinHoldPointerId){CancelPinHold(null);return;}
        if(dragPhase!=DragPhase.Idle && e.Pointer.PointerId==pointerId)
            FinishPointerGesture(e.Pointer,false);
    }
    void CancelPinHold(Pointer? pointer)
    {
        pinHoldTimer.Stop();pinHoldSource=0;pinHoldPointerId=0;pinHoldTriggered=false;
        if(pointer!=null)canvas.ReleasePointerCapture(pointer);
    }
    void FinishPointerGesture(Pointer pointer,bool clicked)
    {
        var source=dragSource;
        var moved=dragPhase==DragPhase.Dragging;
        var wasDocked=pressedDocked;
        var dock=moved&&clicked&&overStrip;
        dragPhase=DragPhase.Dropping;
        contactTimer.Stop(); dragPointer=null;
        dragFrame.Stop(); dragFramePending=false;
        plasma.Hide();
        SetOverStrip(false);
        try
        {
            if(moved)
            {
                var item=items.FirstOrDefault(i=>i.Source==source);
                if(item!=null)
                {
                    session.EndTileDrag(source);
                    dragSource=0; pointerId=0; dragPhase=DragPhase.Idle;
                    // External drop targets (currently a popped-out virtual desktop) get
                    // first refusal. If they consume the drop, the window leaves this
                    // desktop and no local canvas/dock placement should be applied.
                    bool external = clicked && (ExternalDrop?.Invoke(source,lastPoint) ?? false);
                    // Released over the bar docks the tile; anywhere else is a canvas drop
                    // clamped out of the bar.
                    if(!external && !(dock&&session.DockTile(source)))
                    {
                        item.Cell=MoveVisual(source,clicked?lastDragRect:original);
                        if(clicked)session.DropTile(source,item.Cell,original);
                    }
                    // A canvas drop commits pushed neighbours (and reflows). Anything else -
                    // cancel, dock, or a drop into a popped-out desktop - lets them slide home.
                    // Always settle neighbours on their (possibly newly committed) cells: the
                    // release point can differ from the last per-frame preview.
                    UpdateRepel(new Dictionary<nint,Native.RECT>());
                }
            }
            else if(clicked)
            {
                dragSource=0; pointerId=0; dragPhase=DragPhase.Idle;
                // A docked press that wandered off is not a click; only act in place.
                if(Math.Max(Math.Abs(lastPoint.X-pressed.X),Math.Abs(lastPoint.Y-pressed.Y))<8)
                {
                    // Drop the hover glow before the overlay goes behind the activated
                    // window, or the rings stay on the canvas as a ghost outline.
                    ClearHover();
                    if(wasDocked)DockedTileClicked?.Invoke(source);
                    else TileActivated?.Invoke(source);
                }
            }
        }
        finally
        {
            session.EndTileDrag(source);
            pressedDocked=false;
            dragSource=0; pointerId=0; dragPhase=DragPhase.Idle;
            canvas.ReleasePointerCapture(pointer);
            TileDragEnded?.Invoke();
        }
    }
    // Once per rendered frame while dragging: repel neighbours, plasma, and the
    // "Dock on bar contact" countdown (plasma for ContactDockDelay, then dock; leaving
    // contact cancels it).
    void DragFrame()
    {
        if(!dragFramePending||dragPhase!=DragPhase.Dragging||dragSource==0){dragFrame.Stop();dragFramePending=false;return;}
        dragFramePending=false;
        UpdateRepel(session.PreviewDrag(dragSource,lastDragRect));
        if(UpdatePlasma(dragRawRect)&&settings.DockOnBarContact)
        {
            if(!contactTimer.IsEnabled)contactTimer.Start();
        }
        else contactTimer.Stop();
    }
    // Ease every canvas neighbour toward its repelled rectangle (or back to its own cell
    // when it has none). Retargeting mid-ease starts from the pixels currently shown.
    void UpdateRepel(IReadOnlyDictionary<nint, Native.RECT> targets)
    {
        long now = Environment.TickCount64;
        foreach (var item in items)
        {
            // Browsed windows (including a parked full-size fallback) and focus animations
            // are not canvas tiles at the moment; leave their pixels alone.
            if (item.Docked || item.Source == dragSource || suppressed.Contains(item.Source)
                || presentedBrowsed.Contains(item.Source) || transitionTargets.ContainsKey(item.Source)) continue;
            var to = targets.TryGetValue(item.Source, out var target) ? target : item.Cell;
            var current = repel.TryGetValue(item.Source, out var running) ? running.To : item.VisualCell;
            if (to.Equals(current)) continue;
            repel[item.Source] = (item.VisualCell, to, now);
        }
        if (repel.Count > 0 && !repelTimer.IsEnabled) repelTimer.Start();
    }
    void RepelTick()
    {
        long now = Environment.TickCount64;
        foreach (var (source, (from, to, started)) in repel.ToList())
        {
            var item = items.FirstOrDefault(i => i.Source == source);
            if (item == null) { repel.Remove(source); continue; }
            double t = Math.Clamp((now - started) / RepelDuration, 0, 1);
            PositionRect(item, Lerp(from, to, 1 - Math.Pow(1 - t, 3)), PositionWriter.Drag);
            if (t >= 1) repel.Remove(source);
        }
        if (repel.Count == 0) repelTimer.Stop();
    }
    void StopRepel()
    {
        repelTimer.Stop();
        repel.Clear();
    }
    // The dragged tile is held off the bar by the no-drop gap. As its edge nears that limit
    // arcs start to jump the gap; pressing past it (the tile stays put, the pointer does
    // not) strengthens them, and a pointer over the bar - release docks - charges them fully.
    // Returns true on contact: the tile is pressed into the bar's limit, which docks it.
    bool UpdatePlasma(Native.RECT raw)
    {
        if(stripBar.Width<=0||stripBar.Height<=0){plasma.Hide();return false;}
        bool top=settings.DesktopStripPosition!=DesktopStripPosition.Bottom;
        var limit=Tiler.OverviewArea(work,8,scale,settings.DesktopStripPosition);
        var tile=lastDragRect;
        double reach=36*scale,push=70*scale;
        double toLimit=top?raw.Top-limit.Top:limit.Bottom-raw.Bottom;
        double strength=toLimit>=reach?0:toLimit>0?.55*(1-toLimit/reach):.55+.45*Math.Min(1,-toLimit/push);
        if(overStrip)strength=1;
        int x1=Math.Max(tile.Left,stripBar.Left),x2=Math.Min(tile.Right,stripBar.Right);
        if(strength<=0||x2<=x1){plasma.Hide();return false;}
        // Contact: the pointer has pushed the tile a little past its stop against the bar.
        // With "Dock on bar contact" off, only a pointer over the bar (release docks) counts.
        bool contact=overStrip||(settings.DockOnBarContact&&toLimit<=-8*scale);
        double Dip(int screen,int origin)=>(screen-origin)/scale;
        // "Plasma effect" off: contact still counts (Dock on bar contact), nothing is drawn.
        if(!settings.PlasmaEffect){plasma.Hide();return contact;}
        var barDip=new Windows.Foundation.Rect(Dip(stripBar.Left,work.Left),Dip(stripBar.Top,work.Top),stripBar.Width/scale,stripBar.Height/scale);
        plasma.Show(barDip,top,Dip(top?tile.Top:tile.Bottom,work.Top),
            Dip(x1,work.Left),Dip(x2,work.Left),contact?1:strength,contact||overStrip);
        return contact;
    }
    Native.RECT MoveVisual(nint source, Native.RECT rect)
    {
        var item = items.FirstOrDefault(i => i.Source == source);
        if (item == null || !session.Active) return rect;
        // The tile visual is always kept out of the desktop bar, so it bumps against the
        // strip edge instead of sliding under it — both while dragging and on drop. Docking
        // is unaffected: it triggers on the CURSOR entering the bar (SetOverStrip), not on
        // the tile rectangle, so the pointer can still reach the bar to dock.
        var area=Tiler.OverviewArea(work,8,scale,settings.DesktopStripPosition);
        item.Cell = ThumbnailLayout.Clamp(rect, area);
        Position(item, PositionWriter.Drag);
        return item.Cell;
    }
    // A source window that has closed leaves a dead tile: the thumbnail stops updating but
    // its rectangle is still hit-tested and can still raise the hover glow. Drop those as
    // soon as the window is gone. This also runs while browsing, when no reflow happens.
    public void PruneDead()
    {
        if (dragPhase != DragPhase.Idle) return;
        // PointerExited is not guaranteed when another HWND covers the canvas or
        // the source disappears. Validate the glow independently of pointer events.
        if(hoverSource!=0)
        {
            Native.GetCursorPos(out var cursor);
            var hovered=items.LastOrDefault(i=>i.Source==hoverSource);
            Native.DwmGetWindowAttribute(hoverSource,14,out var cloaked,4);
            if(hovered==null || !Native.IsWindowVisible(hoverSource) || Native.IsIconic(hoverSource) || cloaked!=0 ||
                !Contains(hovered.Cell,cursor) || Native.GetAncestor(Native.WindowFromPoint(cursor),2)!=host)
                ClearHover();
        }
        RefreshDockBorders();
        suppressed.RemoveWhere(s=>!Native.IsWindow(s));
        foreach(var source in browseBorders.Keys.Where(s=>!Native.IsWindow(s)).ToList())
        {
            adornmentCanvas.Children.Remove(browseBorders[source]);
            browseBorders.Remove(source);
        }
        foreach(var source in pinDots.Keys.Where(s=>!Native.IsWindow(s)).ToList())
        {
            adornmentCanvas.Children.Remove(pinDots[source]);
            pinDots.Remove(source);
        }
        bool removed = false;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (Native.IsWindow(items[i].Source))
            {
                SetSourceVisible(items[i].Source,Native.IsWindowVisible(items[i].Source) && !suppressed.Contains(items[i].Source));
                continue;
            }
            // A pruned tile only comes back on the next full reflow, so a window that is
            // merely hidden for a moment would vanish here and reappear later.
            if (hoverSource == items[i].Source) ClearHover();
            Native.DwmUnregisterThumbnail(items[i].Thumbnail);
            items.RemoveAt(i);
            removed = true;
        }
        if (!removed) return;
        foreach(var source in dockBorders.Keys.Where(s=>!Native.IsWindow(s)).ToList())
            RemoveDockBorder(source);
    }
    public void Clear()
    {
        CancelPinHold(null);
        ClearExternalDragPreview();
        StopTransition(false);
        StopRepel();
        plasma.Stop();
        contactTimer.Stop();
        dragFrame.Stop();
        StopZoom();
        coverArea = null; zoomCover = null; coverHidden.Clear();
        session.EndTileDrag();
        dragSource = 0; pointerId=0; dragPhase = DragPhase.Idle; pressedDocked=false;
        canvas.ReleasePointerCaptures();
        hoverSource = 0; HideGlow();
        SetOverStrip(false);
        foreach (var item in items) Native.DwmUnregisterThumbnail(item.Thumbnail);
        items.Clear();
        presentedBrowsed.Clear(); suppressed.Clear();browseHomes.Clear();
        foreach (var b in dockBorders.Values) adornmentCanvas.Children.Remove(b);
        dockBorders.Clear();
        foreach (var b in browseBorders.Values) adornmentCanvas.Children.Remove(b);
        browseBorders.Clear();
        foreach (var b in pinDots.Values) adornmentCanvas.Children.Remove(b);
        pinDots.Clear();
    }
    public static Native.RECT Intersect(Native.RECT a, Native.RECT b)
    {
        int l = Math.Max(a.Left, b.Left), t = Math.Max(a.Top, b.Top), r = Math.Min(a.Right, b.Right), bottom = Math.Min(a.Bottom, b.Bottom);
        return new(l, t, Math.Max(0, r - l), Math.Max(0, bottom - t));
    }
    public void ShowExternalDragPreview(nint source,Native.POINT point)
    {
        if(!Contains(work,point)||!Native.IsWindow(source)){ClearExternalDragPreview();return;}
        if(externalDragPreview?.Source!=source)
        {
            ClearExternalDragPreview();
            if(Native.DwmRegisterThumbnail(host,source,out var thumbnail)!=0)return;
            externalDragPreview=new Item{Source=source,Thumbnail=thumbnail};
        }
        Native.GetWindowRect(source,out var bounds);
        var (w,h)=ThumbnailLayout.SizeForSource(bounds,(int)Math.Round(Settings.SmallWindowSizeDefault*scale));
        var area=Tiler.OverviewArea(work,8,scale,settings.DesktopStripPosition);
        PositionRect(externalDragPreview,DesktopDropGeometry.AtPoint(new(0,0,w,h),point,area),PositionWriter.Layout);
    }
    public void ClearExternalDragPreview()
    {
        if(externalDragPreview!=null)Native.DwmUnregisterThumbnail(externalDragPreview.Thumbnail);
        externalDragPreview=null;
    }
    static bool Contains(Native.RECT r, Native.POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    public void Dispose()
    {
        transitionDelayTimer.Stop();
        StopTransition(false);
        StopRepel();
        plasma.Stop();
        contactTimer.Stop();
        dragFrame.Stop();
        zoomTimer.Stop();
        canvas.PointerPressed -= PointerPressed;
        canvas.PointerMoved -= PointerMoved;
        canvas.PointerReleased -= PointerReleased;
        canvas.PointerCanceled -= PointerCanceled;
        canvas.PointerCaptureLost -= PointerCaptureLost;
        Clear();
    }
}
