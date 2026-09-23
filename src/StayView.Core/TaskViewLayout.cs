namespace StayView.Core;

// Windows 11 Task View's window layout, reproduced from measurement.
//
// The algorithm is undocumented, so it was measured on build 26200 (3840x2160, 100%):
// `StayView.Checks taskview-calibration` opens solid-colour fixture windows on a temporary
// virtual desktop, opens the real Task View and reads every thumbnail rectangle back from
// the screen. The 48 recorded layouts live in tests/StayView.Checks/taskview-golden and
// `taskview-layout-checks` holds this implementation to them (all within 3 px, which is the
// anti-aliased edge of the measured colour fill).
//
// Measured rules:
//  * Order is most-recently-used first (z-order), filled left to right, top to bottom.
//  * Each window gets a natural size: its real size, never enlarged, capped at 22% of the
//    monitor height and 33% of the monitor width, aspect preserved.
//  * Rows are an optimal split of that ordered sequence that minimises the widest row,
//    filled front-heavy (so 10 identical windows in 3 rows are 4,4,2).
//  * Row count is the fewest rows r for which a row holds at most r+3 windows and the
//    average row width (total natural width + (n-r) gaps) / r stays within 67.2% of the
//    monitor width for one row and 101.6% for two (extrapolated linearly beyond that).
//    Rows never exceed what fits at full size unless that yields bigger thumbnails.
//  * One common scale (<= 1) then fits the widest row into the available width and all
//    rows into the available height.
//  * Every row reserves scale*capHeight + header height, whatever its tallest window; the
//    thumbnails are top-aligned under their title header. Rows are centred horizontally
//    and the whole block is centred vertically in the region.
//  * Horizontal gap 24 px, vertical gap 26 px, header 39 px (at 100% scaling); the region is
//    inset by 4.4% of the monitor width at each side and 7.73% of the monitor height at the
//    top and bottom.
public static class TaskViewLayout
{
    const double CapHeightFraction = .22, CapWidthFraction = .33;
    const double MarginXFraction = .044, MarginYFraction = 167d / 2160;
    const double OneRowWidthFraction = .672, TwoRowWidthFraction = 1.016;
    const double HorizontalGapDip = 24, VerticalGapDip = 26, HeaderDip = 39;
    const int ExtraPerRow = 3;

    public static int HorizontalGap(double scale) => (int)Math.Round(HorizontalGapDip * scale);
    public static int VerticalGap(double scale) => (int)Math.Round(VerticalGapDip * scale);
    public static int HeaderHeight(double scale) => (int)Math.Round(HeaderDip * scale);
    // Smallest whole-pixel spacing the layout ever produces between neighbours: fractional
    // gaps (e.g. 45.5 px at 175%) snap to either side, so spacing checks use the floor.
    public static (int Horizontal, int Vertical, int Header) MinimumSpacing(double scale) =>
        ((int)Math.Floor(HorizontalGapDip * scale), (int)Math.Floor(VerticalGapDip * scale), (int)Math.Floor(HeaderDip * scale));

    // The inner area Task View lays windows out in, for a region that excludes its desktop strip.
    public static Native.RECT ContentArea(Native.RECT monitor, Native.RECT region)
    {
        int mx = (int)Math.Round(monitor.Width * MarginXFraction), my = (int)Math.Round(monitor.Height * MarginYFraction);
        return new(region.Left + mx, region.Top + my, Math.Max(1, region.Width - 2 * mx), Math.Max(1, region.Height - 2 * my));
    }

    // sources: real window rectangles in most-recently-used order. Returns the thumbnail
    // rectangle (without its title header) for each, in the same order.
    public static IReadOnlyList<Native.RECT> Layout(IReadOnlyList<Native.RECT> sources, Native.RECT monitor, Native.RECT region, double scale)
    {
        int n = sources.Count;
        if (n == 0) return [];
        scale = scale > 0 ? scale : 1;
        var area = ContentArea(monitor, region);
        double hGap = HorizontalGapDip * scale, vGap = VerticalGapDip * scale, header = HeaderDip * scale;
        double capH = monitor.Height * CapHeightFraction, capW = monitor.Width * CapWidthFraction;
        var widths = new double[n];
        var heights = new double[n];
        for (int i = 0; i < n; i++)
        {
            double w = Math.Max(1, sources[i].Width), h = Math.Max(1, sources[i].Height);
            double s = Math.Min(1, Math.Min(capH / h, capW / w));
            widths[i] = w * s; heights[i] = h * s;
        }

        // Rows that fit vertically at full size (small tolerance: 3 rows fill 1080p-scaled space exactly).
        int fullRows = Math.Max(1, (int)Math.Floor((area.Height + vGap + 1) / (capH + header + vGap)));
        double total = widths.Sum();
        (int[] Shape, double Scale)? chosen = null;
        for (int r = 1; r <= Math.Min(n, fullRows); r++)
        {
            if ((n + r - 1) / r > r + ExtraPerRow) continue;
            if ((total + (n - r) * hGap) / r <= RowWidthLimit(r, monitor.Width)) { chosen = Fit(r); break; }
        }
        if (chosen is null)
        {
            for (int r = Math.Min(n, fullRows); r <= n; r++)
            {
                // The height fit only shrinks with more rows; once it is below the best
                // scale found, no larger row count can do better.
                if (chosen is not null && HeightScale(r) <= chosen.Value.Scale) break;
                var candidate = Fit(r);
                if (chosen is null || candidate.Scale > chosen.Value.Scale + 1e-9) chosen = candidate;
                // More rows than the split actually used gives the same split again.
                if (candidate.Shape.Length < r) break;
            }
        }
        var (shape, fit) = chosen!.Value;

        double slot = fit * capH + header;
        double blockH = shape.Length * slot + (shape.Length - 1) * vGap;
        double y = area.Top + (area.Height - blockH) / 2;
        var result = new Native.RECT[n];
        int index = 0;
        foreach (int count in shape)
        {
            double rowW = (count - 1) * hGap;
            for (int j = 0; j < count; j++) rowW += widths[index + j] * fit;
            double x = area.Left + (area.Width - rowW) / 2;
            for (int j = 0; j < count; j++, index++)
            {
                double w = widths[index] * fit, h = heights[index] * fit;
                result[index] = Snap(x, y + header, w, h);
                x += w + hGap;
            }
            y += slot + vGap;
        }
        return result;

        (int[] Shape, double Scale) Fit(int r)
        {
            var rows = MinMaxRows(widths, r, hGap);
            double s = 1;
            int start = 0;
            foreach (int count in rows)
            {
                double sum = 0;
                for (int j = 0; j < count; j++) sum += widths[start + j];
                s = Math.Min(s, (area.Width - (count - 1) * hGap) / Math.Max(1, sum));
                start += count;
            }
            s = Math.Min(s, HeightScale(rows.Length));
            return (rows, Math.Max(.01, s));
        }
        double HeightScale(int r) => ((area.Height - (r - 1) * vGap) / r - header) / capH;
    }

    static double RowWidthLimit(int rows, int monitorWidth) =>
        monitorWidth * (rows == 1 ? OneRowWidthFraction : TwoRowWidthFraction + (TwoRowWidthFraction - OneRowWidthFraction) * (rows - 2));

    // Split the ordered widths into at most `rows` consecutive rows so the widest row is as
    // narrow as possible; rows are then filled greedily to that width, which keeps earlier
    // rows full (front-heavy) when several splits tie.
    public static int[] MinMaxRows(IReadOnlyList<double> widths, int rows, double gap)
    {
        int n = widths.Count;
        var candidates = new List<double>(n * (n + 1) / 2);
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int j = i; j < n; j++) { sum += widths[j] + (j > i ? gap : 0); candidates.Add(sum); }
        }
        candidates.Sort();
        // Rows needed only falls as the width limit grows: binary-search the smallest limit.
        int lo = 0, hi = candidates.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Greedy(candidates[mid]).Count <= rows) hi = mid; else lo = mid + 1;
        }
        return Greedy(candidates[lo]).ToArray();

        List<int> Greedy(double max)
        {
            var output = new List<int>();
            double current = -1; int count = 0;
            foreach (var w in widths)
            {
                if (w > max + 1e-6) return Enumerable.Repeat(1, n + 1).ToList();
                if (count == 0) { current = w; count = 1; }
                else if (current + gap + w <= max + 1e-6) { current += gap + w; count++; }
                else { output.Add(count); current = w; count = 1; }
            }
            output.Add(count);
            return output;
        }
    }

    static Native.RECT Snap(double x, double y, double w, double h)
    {
        int left = (int)Math.Round(x), top = (int)Math.Round(y);
        return new(left, top, Math.Max(1, (int)Math.Round(x + w) - left), Math.Max(1, (int)Math.Round(y + h) - top));
    }
}
