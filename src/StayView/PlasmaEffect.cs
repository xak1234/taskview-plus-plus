using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace StayView;

// Electric "plasma" contact between a dragged tile and the desktop/dock bar. The tile is
// held off the bar by the no-drop gap, so arcs jump across that gap between the tile's
// edge and the bar's edge, and the bar edge crackles with a glow where the tile meets it.
// Drawn on the adornment layer in DIP; DWM thumbnails composite above XAML, so every
// stroke stays in the gap/bar and never under the tile itself.
sealed class PlasmaEffect
{
    readonly Canvas layer = new() { IsHitTestVisible = false };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly Random random = new();
    readonly Rectangle glow = new();
    readonly Rectangle surface = new() { RadiusX = 10, RadiusY = 10 };
    readonly List<(Polyline Halo, Polyline Core)> bolts = [];
    readonly List<(Polyline Halo, Polyline Core)> crackle = [];
    readonly List<(Polyline Halo, Polyline Core)> surfaceArcs = [];
    Rect bar;
    double barEdge, tileEdge, left, right, target, intensity;
    bool charged;

    static readonly Windows.UI.Color ElectricBlue = Windows.UI.Color.FromArgb(255, 110, 220, 255);

    public PlasmaEffect(Canvas host)
    {
        Canvas.SetZIndex(layer, 60);
        host.Children.Add(layer);
        layer.Children.Add(surface);
        layer.Children.Add(glow);
        for (int i = 0; i < 7; i++) bolts.Add(NewBolt());
        for (int i = 0; i < 4; i++) crackle.Add(NewBolt());
        for (int i = 0; i < 5; i++) surfaceArcs.Add(NewBolt());
        timer.Tick += (_, _) => Tick();
        layer.Visibility = Visibility.Collapsed;
    }

    (Polyline, Polyline) NewBolt()
    {
        var halo = new Polyline { StrokeThickness = 5, StrokeLineJoin = PenLineJoin.Round, Stroke = new SolidColorBrush(ElectricBlue), Opacity = 0 };
        var core = new Polyline { StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round, Stroke = new SolidColorBrush(ElectricBlue), Opacity = 0 };
        layer.Children.Add(halo); layer.Children.Add(core);
        return (halo, core);
    }

    // barRect: the whole desktop/dock bar; barAbove: the bar sits above the canvas (strip at
    // the top); tileEdgeY: the dragged tile's edge facing the bar; [x1,x2]: horizontal contact
    // span. strength 0..1 (approach -> pressed); charged: releasing now would dock the window.
    // The whole bar lights up; the arcs that jump the gap stay at the contact span.
    public void Show(Rect barRect, bool barAbove, double tileEdgeY, double x1, double x2, double strength, bool charged)
    {
        bar = barRect;
        barEdge = barAbove ? bar.Bottom : bar.Top; tileEdge = tileEdgeY; left = Math.Min(x1, x2); right = Math.Max(x1, x2);
        target = Math.Clamp(strength, 0, 1); this.charged = charged;
        if (target > 0 && !timer.IsEnabled) { layer.Visibility = Visibility.Visible; timer.Start(); Tick(); }
    }
    public void Hide() => target = 0;
    public void Stop()
    {
        timer.Stop(); target = intensity = 0;
        layer.Visibility = Visibility.Collapsed;
    }

    void Tick()
    {
        // Ease up quickly, fade out a little slower.
        intensity += (target - intensity) * (target > intensity ? .45 : .22);
        if (target == 0 && intensity < .02) { Stop(); return; }
        double width = Math.Max(1, right - left);
        double dir = Math.Sign(tileEdge - barEdge); if (dir == 0) dir = 1;
        // The whole bar surface flickers with charge. Colour stays electric blue
        // whether or not the tile is pressed hard enough to dock.
        surface.Width = Math.Max(1, bar.Width); surface.Height = Math.Max(1, bar.Height);
        Canvas.SetLeft(surface, bar.Left); Canvas.SetTop(surface, bar.Top);
        surface.Fill = new SolidColorBrush(ElectricBlue);
        surface.Opacity = intensity * (.06 + .10 * random.NextDouble()) * (charged ? 1.6 : 1);

        // Glow band along the full length of the bar edge, flickering.
        double band = 10 + 14 * intensity;
        glow.Width = Math.Max(1, bar.Width); glow.Height = band;
        Canvas.SetLeft(glow, bar.Left);
        Canvas.SetTop(glow, dir > 0 ? barEdge - band : barEdge);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, dir > 0 ? 1 : 0), EndPoint = new Point(0, dir > 0 ? 0 : 1) };
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(230, ElectricBlue.R, ElectricBlue.G, ElectricBlue.B), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, ElectricBlue.R, ElectricBlue.G, ElectricBlue.B), Offset = 1 });
        glow.Fill = brush;
        glow.Opacity = intensity * (.55 + .45 * random.NextDouble());

        // Arcs across the gap; more of them the harder the tile presses.
        int active = (int)Math.Round(1 + intensity * (bolts.Count - 1) * (charged ? 1 : .75));
        for (int i = 0; i < bolts.Count; i++)
        {
            var (halo, core) = bolts[i];
            if (i >= active || random.NextDouble() < .18) { halo.Opacity = core.Opacity = 0; continue; }
            double x0 = left + width * random.NextDouble();
            double xEnd = Math.Clamp(x0 + (random.NextDouble() - .5) * 60, left, right);
            var points = Bolt(new Point(x0, barEdge), new Point(xEnd, tileEdge), 18 + 10 * intensity);
            Apply(halo, core, points, ElectricBlue, intensity * (.6 + .4 * random.NextDouble()));
        }
        // Crackle running along the entire bar edge, in segments so each stays jagged.
        double segment = Math.Max(1, bar.Width) / crackle.Count;
        for (int i = 0; i < crackle.Count; i++)
        {
            var from = new Point(bar.Left + segment * i, barEdge);
            var to = new Point(bar.Left + segment * (i + 1), barEdge);
            Apply(crackle[i].Halo, crackle[i].Core, Bolt(from, to, 5 + 5 * intensity), ElectricBlue, intensity * (.6 + .35 * random.NextDouble()));
        }
        // Arcs skittering across the bar surface; more when charged.
        int surfaceActive = (int)Math.Round(intensity * surfaceArcs.Count * (charged ? 1 : .7));
        for (int i = 0; i < surfaceArcs.Count; i++)
        {
            var (halo, core) = surfaceArcs[i];
            if (i >= surfaceActive || bar.Width < 2 || random.NextDouble() < .25) { halo.Opacity = core.Opacity = 0; continue; }
            double length = Math.Min(bar.Width, 120 + random.NextDouble() * bar.Width * .35);
            double x0 = bar.Left + random.NextDouble() * Math.Max(1, bar.Width - length);
            double y0 = bar.Top + bar.Height * (.15 + .7 * random.NextDouble());
            double y1 = Math.Clamp(y0 + (random.NextDouble() - .5) * bar.Height * .6, bar.Top + 2, bar.Bottom - 2);
            Apply(halo, core, Bolt(new Point(x0, y0), new Point(x0 + length, y1), 10 + 12 * intensity), ElectricBlue, intensity * (.35 + .4 * random.NextDouble()));
        }
    }

    static void Apply(Polyline halo, Polyline core, PointCollection points, Windows.UI.Color colour, double opacity)
    {
        halo.Points = points;
        core.Points = Copy(points);
        ((SolidColorBrush)halo.Stroke).Color = colour;
        halo.Opacity = opacity * .45;
        core.Opacity = opacity;
    }
    static PointCollection Copy(PointCollection source) { var c = new PointCollection(); foreach (var p in source) c.Add(p); return c; }

    // Midpoint displacement: a jagged lightning path between two points.
    PointCollection Bolt(Point a, Point b, double roughness)
    {
        var path = new List<Point> { a, b };
        // Enough subdivisions for ~20 DIP segments, so long bar-length bolts stay jagged.
        double span = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        int levels = Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(2, span / 20))), 3, 8);
        for (int level = 0; level < levels; level++, roughness *= .55)
        {
            var next = new List<Point>(path.Count * 2) { path[0] };
            for (int i = 1; i < path.Count; i++)
            {
                var p = path[i - 1]; var q = path[i];
                double dx = q.X - p.X, dy = q.Y - p.Y, len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                double offset = (random.NextDouble() - .5) * roughness;
                next.Add(new Point((p.X + q.X) / 2 - dy / len * offset, (p.Y + q.Y) / 2 + dx / len * offset));
                next.Add(q);
            }
            path = next;
        }
        var points = new PointCollection();
        foreach (var p in path) points.Add(p);
        return points;
    }
}
