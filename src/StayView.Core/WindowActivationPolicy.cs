namespace StayView.Core;

// Pure decision shared by the real activation path and regression checks.
public static class WindowActivationPolicy
{
    public static bool CanActivate(bool docked, bool minimizedDocked, bool pendingMinimize,
        bool requestForeground, bool restoreDocked=false)
        => requestForeground || restoreDocked || !(docked || minimizedDocked || pendingMinimize);

    public static bool IsNewTaskbarTarget(nint previous, nint current)
        => current!=0 && current!=previous;
}