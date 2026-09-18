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
    // hook watch the focused window. Title-bar/top-strip presses can arm a drag; a deliberate
    // product gesture also treats a double-click anywhere on the browsed window as
    // "shrink back to the grid", so the second click is intentionally consumed.
    bool browsing;
    public bool Browsing { get => browsing; set { if(browsing!=value)lastDown=0; browsing = value; if (!value) { dragArmed = false; dragging = false; } } }
    long lastDown;
    nint lastDownTarget;
    bool swallowRelease;
    Native.POINT lastDownPoint;
    public Func<bool>? IsActive;
    // The window a left-drag may move (the one currently browsed).
    public Func<nint>? BrowsedWindow;
    public event Action<nint>? WindowDragged;
    static readonly nuint DragSentinel = 0x53565744; // "SVWD": marks our own replayed click
    const int DragThreshold = 6;
    nint dragTarget;
    Native.POINT dragStart;
    Native.RECT dragOrigin;
    bool dragArmed, dragging, dragLogged;
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
        if(!browsing)return Native.CallNextHookEx(mouseHook, code, wp, lp);
        if (msg == 0x200) // WM_MOUSEMOVE
        {
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
        if (msg != 0x201 && msg != 0x202) return Native.CallNextHookEx(mouseHook, code, wp, lp);
        if (msg == 0x201) // WM_LBUTTONDOWN
        {
            var p = mouse.Point;
            // A double-click ANYWHERE on a browsed window (title bar or content) shrinks it
            // back to the grid. The first press still reaches the app (or arms a move);
            // only the second press is swallowed, so a single click behaves as normal.
            bool claimed = TryArmDrag(p, out var pressed);
            long now = Environment.TickCount64; int dt = (int)Native.GetDoubleClickTime();
            int tolX = Math.Max(2, Native.GetSystemMetrics(36) / 2), tolY = Math.Max(2, Native.GetSystemMetrics(37) / 2);
            if (pressed != 0 && lastDown != 0 && lastDownTarget == pressed && now - lastDown <= dt && Math.Abs(p.X - lastDownPoint.X) <= tolX && Math.Abs(p.Y - lastDownPoint.Y) <= tolY)
            {
                lastDown = 0; dragArmed = false; dragging = false;
                swallowRelease = true;
                DoubleClick?.Invoke(pressed); return 1;
            }
            lastDown = pressed != 0 ? now : 0; lastDownPoint = p; lastDownTarget = pressed;
            if (!claimed) return Native.CallNextHookEx(mouseHook, code, wp, lp);
            // A press on the focused window's title bar arms a move of it. The press is
            // swallowed so the app never starts its own drag; a press that never moves is
            // replayed on release so ordinary title-bar clicks still reach the app.
            return 1;
        }
        // WM_LBUTTONUP
        if (!dragArmed) return Native.CallNextHookEx(mouseHook, code, wp, lp);
        dragArmed = false;
        if (dragging) { dragging = false; WindowDragged?.Invoke(dragTarget); return 1; }
        ReplayClick();
        return 1;
    }
    // Arm a move of the window under the cursor, if the press landed on its title bar.
    // It must be a real top-level window of another process (never our own overlay or a
    // shell surface) and either the browsed window or the current foreground one.
    // `pressed` is the browsed/foreground window under the pointer even when the press is
    // not claimed (content), so the caller can still detect a double-click on it.
    bool TryArmDrag(Native.POINT p, out nint pressed)
    {
        pressed = 0;
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
        // Only the window's own title bar/top strip belongs to StayView for DRAGGING. A
        // press on the resize frame or ordinary content is left to Windows/the app unless
        // it completes the separate double-click-to-shrink gesture handled by the caller.
        // Holding Win turns the whole window into a drag handle, so custom-chrome apps
        // (Chrome, Electron) that report their title bar as client area can still be moved
        // from anywhere. Without the modifier only the real title bar (HTCAPTION) is a
        // StayView move, and the resize frame and content are left to the app.
        bool winHeld = winKeyDown || Native.GetAsyncKeyState(0x5B) < 0 || Native.GetAsyncKeyState(0x5C) < 0;
        var hit = HitTest(target, p, r);
        // The top strip of the focused window is a drag handle, even when the app draws its
        // own title bar there and reports it as client area (Chrome, Electron). A press on
        // Content is claimed only inside that band and only becomes a move once the pointer
        // passes the drag threshold; a plain click is replayed to the app, so tabs and
        // toolbar buttons in the strip keep working. The very top edge stays Frame (resize).
        uint dpi = Native.GetDpiForWindow(target); if (dpi == 0) dpi = 96;
        int band = (int)Math.Round(44.0 * dpi / 96);
        bool inTopBand = hit == Hit.Content && p.Y - r.Top >= 0 && p.Y - r.Top < band;
        // Holding Win extends that to anywhere on the window, for dragging below the strip.
        bool claim = winHeld || hit == Hit.Caption || inTopBand;
        if (!claim) return false;
        dragTarget = target; dragStart = p; dragOrigin = r; dragArmed = true; dragging = false; dragLogged = false;
        return true;
    }
    enum Hit { Content, Caption, Frame }
    // Ask the window itself (WM_NCHITTEST) so each app's real chrome is respected, with a
    // short timeout so a hung app cannot stall the mouse hook. HTGROWBOX and the eight
    // border/corner codes are the resize frame; HTCAPTION is the title bar.
    static Hit HitTest(nint h, Native.POINT p, Native.RECT r)
    {
        nint packed = (nint)(((uint)(p.Y & 0xFFFF) << 16) | (uint)(p.X & 0xFFFF));
        // SMTO_ABORTIFHUNG | SMTO_BLOCK, 30 ms so a genuinely hung app cannot stall the hook.
        if (Native.SendMessageTimeout(h, 0x0084, 0, packed, 0x0002 | 0x0001, 30, out var hit) != 0)
        {
            long code = hit.ToInt64();
            // The window answered: trust its own non-client hit-test exactly.
            if (code == 2) return Hit.Caption;
            return code == 4 || (code >= 10 && code <= 17) ? Hit.Frame : Hit.Content;
        }
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
        if (edge) return Hit.Frame;
        if (p.Y - r.Top < border + caption) return Hit.Caption;
        return Hit.Content;
    }
    static bool PointInsideWindow(nint h, Native.POINT p)
    {
        if (h == 0 || !Native.IsWindow(h) || !Native.GetWindowRect(h, out var r)) return false;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }
    // The press was a click, not a drag: send it on so the app still receives it. The
    // sentinel keeps this hook from treating the replay as a fresh press.
    static void ReplayClick()
    {
        var inputs = new Native.INPUT[2];
        inputs[0].Type = 0; inputs[0].Mouse = new Native.MOUSEINPUT { DwFlags = 0x0002, ExtraInfo = DragSentinel };
        inputs[1].Type = 0; inputs[1].Mouse = new Native.MOUSEINPUT { DwFlags = 0x0004, ExtraInfo = DragSentinel };
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
