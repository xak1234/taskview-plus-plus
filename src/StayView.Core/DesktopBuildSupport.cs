namespace StayView.Core;

// Keep the UI, pipe client and private COM broker on the same verified shell builds.
// Unknown builds retain the documented current-desktop functionality.
public static class DesktopBuildSupport
{
    public static bool IsSupported(int build) => build is 26100 or 26200 or 26300;
}
