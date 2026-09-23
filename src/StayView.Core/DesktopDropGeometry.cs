namespace StayView.Core;

public static class DesktopDropGeometry
{
    public static Native.POINT MapPoint(Native.POINT point, Native.RECT preview, Native.RECT desktop)
        => new() {
            X=desktop.Left+(int)Math.Round(Math.Clamp((point.X-preview.Left)/(double)Math.Max(1,preview.Width),0,1)*desktop.Width),
            Y=desktop.Top+(int)Math.Round(Math.Clamp((point.Y-preview.Top)/(double)Math.Max(1,preview.Height),0,1)*desktop.Height)
        };

    // Preserve size even when a window is larger than the destination work area.
    public static Native.RECT AtPoint(Native.RECT source, Native.POINT point, Native.RECT area)
        => new(Math.Clamp(point.X-source.Width/2,area.Left,Math.Max(area.Left,area.Right-source.Width)),
            Math.Clamp(point.Y-source.Height/2,area.Top,Math.Max(area.Top,area.Bottom-source.Height)),
            source.Width,source.Height);
}
