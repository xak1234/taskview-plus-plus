namespace StayView.Core;

public static class StableTileLayout
{
    // Only new windows need a free slot. Existing windows retain both location and size.
    public static Native.RECT PlaceNew(Native.RECT preferred, IReadOnlyList<Native.RECT> occupied, Native.RECT area, int gap)
    {
        var candidate = ThumbnailLayout.Clamp(preferred, area);
        if (occupied.Count == 0) return candidate;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            int w = candidate.Width, h = candidate.Height;
            var xs = occupied.SelectMany(r => new[] { r.Left, r.Right + gap, r.Left - gap - w })
                .Append(preferred.Left).Append(area.Left).Append(area.Right - w).Distinct();
            var ys = occupied.SelectMany(r => new[] { r.Top, r.Bottom + gap, r.Top - gap - h })
                .Append(preferred.Top).Append(area.Top).Append(area.Bottom - h).Distinct();
            var slots = from y in ys from x in xs
                        where x >= area.Left && y >= area.Top && x + w <= area.Right && y + h <= area.Bottom
                        orderby Math.Abs((long)x - preferred.Left) + Math.Abs((long)y - preferred.Top)
                        select new Native.RECT(x, y, w, h);
            foreach (var slot in slots)
            {
                var padded = new Native.RECT(slot.Left-gap, slot.Top-gap, slot.Width+2*gap, slot.Height+2*gap);
                if (!occupied.Any(r => r.Intersects(padded))) return slot;
            }
            if (w == 1 && h == 1) break;
            candidate = new(candidate.Left, candidate.Top, Math.Max(1, (int)(w*.85)), Math.Max(1, (int)(h*.85)));
        }
        // A completely occupied canvas cannot fit another slot without moving old tiles.
        // Keep the new tile usable at its normal size rather than reducing it to one pixel.
        return ThumbnailLayout.Clamp(preferred, area);
    }
}
