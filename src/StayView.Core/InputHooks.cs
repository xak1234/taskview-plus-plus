using System.Runtime.InteropServices;
namespace StayView.Core;

public sealed class InputHooks : IDisposable
{
    readonly nint hwnd;
    readonly Native.HookProc keyCallback;
    readonly Native.HookProc mouseCallback;
    nint hook, mouseHook;
    bool fallback, spaceHeld, escapeHeld, active;
    volatile bool winKeyDown; // Win held, tracked by the keyboard hook for Win+drag
    // Only while browsing (a real window focused in front of the overview) does the mouse
    // hook watch the focused window. Title bars shrink directly; client double-clicks
    // are checked asynchronously for empty background. Controls keep their native input.
    bool browsing;
    public bool Browsing { get => browsing; set {
        if(browsing!=value){lastDown=0; GestureVersion++; pendingClient=0;}
        if(!value && dragArmed && dragButton==2) swallowRightRelease=true;
        browsing = value;
        if (!value) { dragArmed = false; dragging = false; dragButton=0; }
    } }
    public long GestureVersion { get; private set; }
    bool lastWasCaption, lastWasClient;
    nint pendingClient;
    Native.POINT pendingFirst, pendingSecond;
    public event Action<nint, Native.POINT, Native.POINT, long>? ClientDoubleClick;
    long lastDown;
    nint lastDownTarget;
    bool swallowRelease;
    Native.POINT lastDownPoint;
    public Func<bool>? IsActive;
    // The window a left-drag may move (the one currently browsed).
    public Func<nint>? BrowsedWindow;
    public event Action<nint>? WindowDragged;
    public bool IsDragging => dragArmed;
    static readonly nuint DragSentinel = 0x53565744; // "SVWD": marks our own replayed click
    const int DragThreshold = 6;
    nint dragTarget;
    Native.POINT dragStart;
    Native.RECT dragOrigin;
    bool dragArmed, dragging, dragLogged;
    // 1 = explicit Win+left drag, 2 = right-button hold drag. Right-drag deliberately
    // ignores the child HWND under the pointer so a focused host can still be moved while
    // the cursor is over an embedded app owned by another process.
    int dragButton;
    bool swallowRightRelease;
    public event Action? Escape;
    public event Action? Toggle;
    public event Action<nint>? DoubleClick; // the browsed/foreground window that was double-clicked
    public event Action<int>? DesktopDirection;
    public string HotkeyText { get; private set; } = "Ctrl + Win + Space";
    public InputHooks(nint hwnd, Settings settings)
    {
        this.hwnd = hwnd; keyCallback = Keyboard; mouseCallback = Mouse; Register(settings);
        hook = Native.SetWindowsHookEx(13, keyCallback, Native.GetModuleHandle(null), 0);
        mouseHook = Native.SetWindowsHookEx(14, mouseCallback, Native.GetModuleHandle(null), 0);
        if (hook == 0 && fallback) HotkeyText = "Use tray (hotkey unavailable)";
    }
    nint Mouse(int code, nint wp, nint lp)
    {
        if (code < 0) return Native.CallNextHookEx(mouseHook, code, wp, lp);
        uint msg = (uint)wp;
        var mouse = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lp);
        if (mouse.ExtraInfo == DragSentinel)
            return Native.CallNextHookEx(mouseHook, code, wp, lp);
        // The overview may have reopened between the second down and its up.
        // Consume the matching release even after browsing was disabled.
        if(msg==0x202 && swallowRelease){swallowRelease=false;return 1;}
        if(msg==0x205 && swallowRightRelease){swallowRightRelease=false;return 1;}
        if(!browsing)return Native.CallNextHookEx(mouseHook, code, wp, lp);
        if (msg is 0x204 or 0x207 or 0x20B or 0x20A or 0x20E)
        { GestureVersion++; lastDown = 0; pendingClient = 0; }
        if (msg == 0x200) // WM_MOUSEMOVE
        {
            if ((lastDown != 0 || pendingClient != 0)
                && Math.Max(Math.Abs(mouse.Point.X - lastDownPoint.X), Math.Abs(mouse.Point.Y - lastDownPoint.Y)) >= DragThreshold)
            { lastDown = 0; pendingClient = 0; GestureVersion++; }
            if (!dragArmed) return Native.CallNextHookEx(mouseHook, code, wp, lp);
            var mp = mouse.Point;
            if (!dragging)
            {
                if (Math.Max(Math.Abs(mp.X - dragStart.X), Math.Abs(mp.Y - dragStart.Y)) < DragThreshold)
                    return Native.CallNextHookEx(mouseHook, code, wp, lp);
                dragging = true;
                lastDown = 0;
                // A maximized window cannot be moved by SetWindowPos. Restore it first (as
                // Windows does when you drag a maximized window) and continue from there.
                if (Native.IsZoomed(dragTarget))
                {
                    Native.ShowWindow(dragTarget, 9); // SW_RESTORE
                    if (Native.GetWindowRect(dragTarget, out var restored))
                    { dragOrigin = new Native.RECT(mp.X - restored.Width / 2, mp.Y - 20, restored.Width, restored.Height); dragStart = mp; }
                }
            }
            int dx = mp.X - dragStart.X, dy = mp.Y - dragStart.Y;
            int wantX = dragOrigin.Left + dx, wantY = dragOrigin.Top + dy;
            // Move only: never resize, re-order or activate.
            bool ok = Native.SetWindowPos(dragTarget, 0, wantX, wantY, 0, 0, 0x15 | 0x4000);
            // Only a failure is worth a line, once per drag (err 5 = the target is
            // elevated and we are not, so it cannot be moved).
            if (!ok && !dragLogged)
            {
                dragLogged = true;
                Log.Write($"Focused window drag failed for {dragTarget}: {Marshal.GetLastWin32Error()}");
            }
            // NEVER swallow a mouse move. A WH_MOUSE_LL hook that returns 1 for
            // WM_MOUSEMOVE blocks the movement itself, so the cursor freezes and
            // GetCursorPos stops changing - which stalls the drag it is meant to drive.
            // Blocking the button-down already stops the app from starting its own drag.
            return Native.CallNextHookEx(mouseHook, code, wp, lp);
        }
        if(msg==0x204) // WM_RBUTTONDOWN
        {
            GestureVersion++;lastDown=0;pendingClient=0;
            if(!TryArmRightDrag(mouse.Point))
                return Native.CallNextHookEx(mouseHook,code,wp,lp);
            // Suppress the app's right-down while the gesture is undecided. If the pointer
            // never moves past the drag threshold, replay a normal right click on release.
            return 1;
        }
        if(msg==0x205) // WM_RBUTTONUP
        {
            if(!dragArmed||dragButton!=2)
                return Native.CallNextHookEx(mouseHook,code,wp,lp);
            dragArmed=false;dragButton=0;
            if(dragging){dragging=false;WindowDragged?.Invoke(dragTarget);return 1;}
            ReplayClick(true);
            return 1;
        }
        if (msg != 0x201 && msg != 0x202) return Native.CallNextHookEx(mouseHook, code, wp, lp);
        if (msg == 0x201) // WM_LBUTTONDOWN
        {
            GestureVersion++;
            pendingClient = 0;
            var p = mouse.Point;
            // Caption and client gestures never combine. Only confirmed caption clicks
            // are consumed here; empty-client classification happens outside the hook.
            bool claimed = TryArmDrag(p, out var pressed, out var canShrink, out var isClient);
            long now = Environment.TickCount64; int dt = (int)Native.GetDoubleClickTime();
            int tolX = Math.Max(2, Native.GetSystemMetrics(36) / 2), tolY = Math.Max(2, Native.GetSystemMetrics(37) / 2);
            bool doubleClick = pressed != 0 && lastDown != 0 && lastDownTarget == pressed && now - lastDown <= dt && Math.Abs(p.X - lastDownPoint.X) <= tolX && Math.Abs(p.Y - lastDownPoint.Y) <= tolY;
            if (canShrink && lastWasCaption && doubleClick)
            {
                lastDown = 0; dragArmed = false; dragging = false; dragButton=0;
                swallowRelease = true;
                DoubleClick?.Invoke(pressed); return 1;
            }
            if (isClient && lastWasClient && doubleClick && !claimed)
            {
                // Let the app receive its complete click sequence. Only after mouse-up
                // may an asynchronous accessibility check recognize empty background.
                pendingClient = pressed; pendingFirst = lastDownPoint; pendingSecond = p;
            }
            lastDown = !doubleClick && pressed != 0 && (canShrink || isClient) ? now : 0;
            lastWasCaption = canShrink; lastWasClient = isClient && !claimed;
            lastDownPoint = p; lastDownTarget = pressed;
            if (!claimed) return Native.CallNextHookEx(mouseHook, code, wp, lp);
            // A press on the focused window's title bar arms a move of it. The press is
            // swallowed so the app never starts its own drag; a press that never moves is
            // replayed on release so ordinary title-bar clicks still reach the app.
            return 1;
        }
        // WM_LBUTTONUP
        if (pendingClient != 0)
        {
            var target = pendingClient; pendingClient = 0;
            ClientDoubleClick?.Invoke(target, pendingFirst, pendingSecond, GestureVersion);
        }
        if (!dragArmed || dragButton!=1) return Native.CallNextHookEx(mouseHook, code, wp, lp);
        dragArmed = false;dragButton=0;
        if (dragging) { dragging = false; WindowDragged?.Invoke(dragTarget); return 1; }
        ReplayClick(false);
        return 1;
    }
    // Arm a move of the window under the cursor, if the press landed on its title bar.
    // It must be a real top-level window of another process (never our own overlay or a
    // shell surface) and either the browsed window or the current foreground one.
    // Native hit testing determines whether a press may participate in a shrink gesture.
    bool TryArmDrag(Native.POINT p, out nint pressed, out bool canShrink, out bool isClient)
    {
        pressed = 0;
        canShrink = false;
        isClient = false;
        var target = Native.GetAncestor(Native.WindowFromPoint(p), 2); // GA_ROOT
        if (target == 0) return false;
        Native.GetWindowThreadProcessId(target, out var pid);
        if (pid == Environment.ProcessId) return false; // our overlay: leave it to XAML
        var cls = Native.Class(target);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return false;
        var browsed = BrowsedWindow?.Invoke() ?? 0;
        var foreground = Native.GetForegroundWindow();
        if (target != browsed && target != foreground)
        {
            // Geometry alone is insufficient: an unrelated window may overlap the
            // browsed one. Hosted roots must also belong to the same process.
            Native.GetWindowThreadProcessId(browsed,out var browsedPid);
            Native.GetWindowThreadProcessId(foreground,out var foregroundPid);
            if (pid==browsedPid && PointInsideWindow(browsed, p)) target = browsed;
            else if (pid==foregroundPid && PointInsideWindow(foreground, p)) target = foreground;
            else return false;
        }
        pressed = target;
        if (!Native.GetWindowRect(target, out var r)) return false;
        // A positive client hit belongs to the app, even in the top strip: that strip
        // can contain tabs, buttons or editable text. Win+drag is the explicit override.
        bool winHeld = winKeyDown || Native.GetAsyncKeyState(0x5B) < 0 || Native.GetAsyncKeyState(0x5C) < 0;
        var hit = HitTest(target, p, r);
        bool fallbackCaption = hit == null && InFallbackCaption(p, r);
        isClient = hit == 1;
        canShrink = FocusedClickPolicy.CanShrink(hit, fallbackCaption);
        // Let Windows own ordinary caption drags, snap, restore-under-pointer and capture.
        // Only the explicit Win+drag gesture needs synthetic window movement.
        bool claim = winHeld && FocusedClickPolicy.CanDrag(hit, winHeld, false);
        if (!claim) return false;
        dragTarget = target; dragStart = p; dragOrigin = r; dragArmed = true; dragging = false; dragLogged = false;dragButton=1;
        return true;
    }
    bool TryArmRightDrag(Native.POINT p)
    {
        var target=BrowsedWindow?.Invoke()??0;
        var foreground=Native.GetForegroundWindow();
        bool selectedFocused=target!=0&&(foreground==target||Native.GetAncestor(foreground,3)==target);
        if(!selectedFocused||!Native.IsWindow(target)||Native.IsIconic(target)||!PointInsideWindow(target,p))
        {
            // Fall back to the foreground root only when it is a real external window.
            target=foreground;
            if(target!=0)target=Native.GetAncestor(target,2);
            if(target==0||!Native.IsWindow(target)||Native.IsIconic(target)||!PointInsideWindow(target,p))return false;
            Native.GetWindowThreadProcessId(target,out var pid);
            if(pid==Environment.ProcessId)return false;
        }
        if(!Native.GetWindowRect(target,out var r))return false;
        dragTarget=target;dragStart=p;dragOrigin=r;dragArmed=true;dragging=false;dragLogged=false;dragButton=2;
        return true;
    }
    // Ask the window itself (WM_NCHITTEST) so each app's real chrome is respected, with a
    // short timeout so a hung app cannot stall the mouse hook. HTGROWBOX and the eight
    // border/corner codes are the resize frame; HTCAPTION is the title bar.
    static int? HitTest(nint h, Native.POINT p, Native.RECT r)
    {
        nint packed = (nint)(((uint)(p.Y & 0xFFFF) << 16) | (uint)(p.X & 0xFFFF));
        // SMTO_ABORTIFHUNG | SMTO_BLOCK, 30 ms so a genuinely hung app cannot stall the hook.
        if (Native.SendMessageTimeout(h, 0x0084, 0, packed, 0x0002 | 0x0001, 30, out var hit) != 0)
        {
            return (int)hit.ToInt64();
        }
        return null; // Uncertain hit tests must never swallow an app double-click.
    }
    static bool InFallbackCaption(Native.POINT p, Native.RECT r)
    {
        // No answer in 30 ms. A busy console (conhost running output) is regularly flagged
        // "hung" by SMTO_ABORTIFHUNG, so this path is common, not rare — the old code fell
        // straight to the resize-frame geometry and NEVER returned Caption, which is why a
        // console window could not be dragged by its title bar. Reconstruct the standard
        // non-client bands from system metrics: a resize border around every edge, then a
        // caption strip below the top border. This is what Windows itself lets you do with
        // a not-responding window (its title bar still drags).
        int border = Math.Max(4, Native.GetSystemMetrics(32) + Native.GetSystemMetrics(92)); // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
        int caption = Math.Max(1, Native.GetSystemMetrics(4)); // SM_CYCAPTION
        bool edge = p.X - r.Left < border || r.Right - p.X <= border
            || p.Y - r.Top < border || r.Bottom - p.Y <= border;
        return !edge && p.Y - r.Top < border + caption;
    }
    static bool PointInsideWindow(nint h, Native.POINT p)
    {
        if (h == 0 || !Native.IsWindow(h) || !Native.GetWindowRect(h, out var r)) return false;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }
    // The press was a click, not a drag: send it on so the app still receives it. The
    // sentinel keeps this hook from treating the replay as a fresh press.
    static void ReplayClick(bool right)
    {
        var inputs = new Native.INPUT[2];
        inputs[0].Type = 0; inputs[0].Mouse = new Native.MOUSEINPUT { DwFlags = right?0x0008u:0x0002u, ExtraInfo = DragSentinel };
        inputs[1].Type = 0; inputs[1].Mouse = new Native.MOUSEINPUT { DwFlags = right?0x0010u:0x0004u, ExtraInfo = DragSentinel };
        Native.SendInput(2, inputs, Marshal.SizeOf<Native.INPUT>());
    }
    public void Register(Settings settings)
    {
        Native.UnregisterHotKey(hwnd, 1);
        fallback = !Native.RegisterHotKey(hwnd, 1, 10 | 0x4000, 32);
        // Windows can reserve this chord. The keyboard hook handles that exact
        // chord when RegisterHotKey fails; never silently substitute another one.
    }
    public void SetOverview(bool value) => active = value;
    nint Keyboard(int code, nint wp, nint lp)
    {
        if (code < 0) return Native.CallNextHookEx(hook, code, wp, lp);
        int key = Marshal.ReadInt32(lp);
        bool down = wp == 0x100 || wp == 0x104, up = wp == 0x101 || wp == 0x105;
        if (down) { GestureVersion++; lastDown = 0; pendingClient = 0; }
        // Track the Win key here (this hook sees every key up/down) so the mouse hook has
        // an authoritative held-state for Win+drag. GetAsyncKeyState proved unreliable
        // when queried from inside the mouse hook.
        if (key is 0x5B or 0x5C) { if (down) winKeyDown = true; else if (up) winKeyDown = false; }
        bool ctrl = Native.GetAsyncKeyState(0x11) < 0;
        bool win = Native.GetAsyncKeyState(0x5B) < 0 || Native.GetAsyncKeyState(0x5C) < 0;
        if (fallback && key == 32)
        {
            if (up && spaceHeld) { spaceHeld = false; return 1; }
            if (down && ctrl && win && Native.GetAsyncKeyState(0x12) >= 0)
            { if (!spaceHeld) { spaceHeld = true; Toggle?.Invoke(); } return 1; }
        }
        if (key == 27 && up && escapeHeld) { escapeHeld = false; return 1; }
        if (active && IsActive?.Invoke() == true && down)
        {
            if (key == 27) { if (!escapeHeld) { escapeHeld = true; Escape?.Invoke(); } return 1; }
            if ((key == 37 || key == 39) && ctrl && win) { DesktopDirection?.Invoke(key == 37 ? -1 : 1); return 1; }
        }
        return Native.CallNextHookEx(hook, code, wp, lp);
    }
    public void Dispose() { if (hook != 0) Native.UnhookWindowsHookEx(hook); if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook); Native.UnregisterHotKey(hwnd, 1); GC.KeepAlive(keyCallback); GC.KeepAlive(mouseCallback); }
}
