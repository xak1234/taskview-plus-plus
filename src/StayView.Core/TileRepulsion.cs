namespace StayView.Core;

// Neighbouring tiles move out of the way of a dragged tile, keeping Task View's spacing.
//
// Each tile's footprint is its thumbnail plus the title-header band Task View reserves
// above it. Two footprints are "too close" when they are nearer than the Task View gaps
// (horizontal gap side by side, vertical gap above/below).
//
// Only tiles the drag actually reaches move: a tile is pushed when it is too close to the
// dragged tile or to a tile that has already been pushed, so a push cascades outward
// (A pushes B pushes C) while tiles the user left close together elsewhere stay put.
// The dragged tile and pinned tiles never move.
//
// The solve always starts from the tiles' home positions, never from the previous frame,
// so neighbours slide back as the dragged tile moves away and repeated frames cannot drift.
// `previous` (the last frame's result) only chooses the side a tile keeps escaping to, so
// a neighbour does not swing across when the pointer wobbles over its centre.
public static class TileRepulsion
{
    public static Dictionary<nint, Native.RECT> Resolve(
        nint dragged, Native.RECT draggedRect,
        IReadOnlyDictionary<nint, Native.RECT> home, IReadOnlySet<nint> fixedTiles,
        Native.RECT canvas, int horizontalGap, int verticalGap, int header,
        IReadOnlyDictionary<nint, Native.RECT>? previous = null, Native.RECT? vacated = null)
    {
        // A full canvas can leave a neighbour nowhere to go. Rather than shoving it into
        // another neighbour (one then draws behind the other), such a tile stays in its own
        // slot - under the held tile only - and the solve is repeated with it held still, so
        // the others route around it. Neighbours therefore never end up overlapping.
        var held = new HashSet<nint>(fixedTiles);
        Dictionary<nint, Native.RECT> result = [];
        // Each round holds at least one more tile still, so this terminates. The first rounds
        // hold one tile at a time (the best-looking result); after that every blocked tile is
        // held at once so a crowded canvas converges in a few rounds (runs once per frame).
        for (int round = 0; round <= home.Count; round++)
        {
            result = ResolveOnce(dragged, draggedRect, home, held, canvas, horizontalGap, verticalGap, header, previous, vacated);
            var moved = result.Where(p => home.TryGetValue(p.Key, out var h0) && !h0.Equals(p.Value)).ToList();
            var others = result.ToList();
            var failed = moved.Where(m => others.Any(o => o.Key != m.Key && TooClose(m.Value, o.Value, horizontalGap, verticalGap, header))).Select(m => m.Key).ToList();
            if (failed.Count == 0) break;
            // Hold the failed tile nearest the drag first; it is the one everything else pushed.
            var dc = Centre(draggedRect);
            if (round < 3) held.Add(failed.OrderBy(f => Distance(Centre(home[f]), dc)).First());
            else held.UnionWith(failed);
        }
        return result;
    }

    // Where a dropped tile finally lands: where it was released when that is clear, else the
    // slot it came from, else the nearest spot that keeps the Task View gaps. Null when the
    // canvas has no room at all - the caller then cancels the drop (everything snaps back).
    public static Native.RECT? Settle(Native.RECT dropped, Native.RECT? origin, IEnumerable<Native.RECT> others,
        Native.RECT canvas, int horizontalGap, int verticalGap, int header)
    {
        var list = others.ToList();
        bool Free(Native.RECT c) => Inside(c, canvas) && !list.Any(o => TooClose(c, o, horizontalGap, verticalGap, header));
        if (Free(dropped)) return dropped;
        if (origin is { } o0 && Free(o0)) return o0;
        return NearestFree(dropped, list, [], canvas, horizontalGap, verticalGap, header, Free);
    }

    static Dictionary<nint, Native.RECT> ResolveOnce(
        nint dragged, Native.RECT draggedRect,
        IReadOnlyDictionary<nint, Native.RECT> home, IReadOnlySet<nint> fixedTiles,
        Native.RECT canvas, int horizontalGap, int verticalGap, int header,
        IReadOnlyDictionary<nint, Native.RECT>? previous, Native.RECT? vacated)
    {
        var result = home.Where(p => p.Key != dragged).ToDictionary(p => p.Key, p => p.Value);
        // The slot the dragged tile left: a blocked neighbour can take it (a swap).
        if (vacated is null && home.TryGetValue(dragged, out var draggedHome)) vacated = draggedHome;
        var immovable = home.Where(p => p.Key != dragged && fixedTiles.Contains(p.Key)).Select(p => p.Value).ToList();
        // Obstacles a pushed tile must clear: the dragged tile, pinned tiles, and every tile pushed so far.
        var obstacles = new List<Native.RECT>(immovable) { draggedRect };
        var active = new List<Native.RECT> { draggedRect };
        var dc = Centre(draggedRect);
        var pending = home.Where(p => p.Key != dragged && !fixedTiles.Contains(p.Key))
            .OrderBy(p => Distance(Centre(p.Value), dc)).ThenBy(p => p.Key).ToList();
        bool changed = true;
        while (changed && pending.Count > 0)
        {
            changed = false;
            for (int i = 0; i < pending.Count; i++)
            {
                var (h, start) = pending[i];
                if (!active.Any(o => TooClose(start, o, horizontalGap, verticalGap, header))) continue;
                // Tiles that have not moved (yet): landing on them is allowed for a push (they
                // are pushed in turn) but not for a relocation, which must be a truly free spot.
                var stationary = pending.Where(p => p.Key != h).Select(p => p.Value).ToList();
                bool Free(Native.RECT c) =>
                    Inside(c, canvas) && !obstacles.Any(o => TooClose(c, o, horizontalGap, verticalGap, header))
                    && !stationary.Any(o => TooClose(c, o, horizontalGap, verticalGap, header));
                (int X, int Y) bias = default;
                Native.RECT? last = previous != null && previous.TryGetValue(h, out var p0) && !p0.Equals(start) ? p0 : null;
                if (last is { } l) bias = (Math.Sign(l.Left - start.Left), Math.Sign(l.Top - start.Top));
                Native.RECT r;
                // Same-size slots (Task View rounding differs by a pixel) are taken exactly.
                Native.RECT? swapSlot = vacated is not { } vs ? null
                    : Math.Abs(vs.Width - start.Width) <= 2 && Math.Abs(vs.Height - start.Height) <= 2 ? vs
                    : ThumbnailLayout.Clamp(new Native.RECT(vs.Left + (vs.Width - start.Width) / 2, vs.Top + (vs.Height - start.Height) / 2, start.Width, start.Height), canvas);
                if (last is { } keep && Free(keep)) r = keep;   // stay where it went last frame (no flicker)
                // Dropped squarely onto a neighbour: the two swap slots.
                else if (swapSlot is { } sq && CoveredFraction(start, draggedRect) >= .4 && Free(sq)) { r = sq; vacated = null; }
                else
                {
                    r = start;
                    for (int attempt = 0; attempt < 24; attempt++)
                    {
                        int blocker = obstacles.FindIndex(o => TooClose(r, o, horizontalGap, verticalGap, header));
                        if (blocker < 0) break;
                        r = PushClear(r, obstacles[blocker], obstacles, canvas, horizontalGap, verticalGap, header, bias);
                    }
                    if (obstacles.Any(o => TooClose(r, o, horizontalGap, verticalGap, header)) || !Inside(r, canvas))
                    {
                        // Boxed in (a full Task View canvas): take the dragged tile's old slot,
                        // else the nearest free spot. Only if neither exists keep the best push.
                        if (vacated is not null && swapSlot is { } s && Free(s)) { r = s; vacated = null; }
                        else if (NearestFree(start, obstacles, stationary, canvas, horizontalGap, verticalGap, header, Free) is { } spot) r = spot;
                    }
                }
                result[h] = r;
                obstacles.Add(r);
                active.Add(r);
                pending.RemoveAt(i--);
                changed = true;
            }
        }
        return result;
    }

    public static bool TooClose(Native.RECT a, Native.RECT b, int horizontalGap, int verticalGap, int header)
    {
        // Footprints include the header band above each thumbnail.
        long aTop = a.Top - header, bTop = b.Top - header;
        return a.Left < b.Right + horizontalGap && b.Left < a.Right + horizontalGap
            && aTop < b.Bottom + verticalGap && bTop < a.Bottom + verticalGap;
    }

    // Candidate moves that clear `obstacle`: the side the tile escaped to last frame, else the
    // natural side (away from the obstacle's centre, along the axis of least travel), then the
    // other options. The first candidate that stays on the canvas and clears every obstacle
    // wins; otherwise the one that clears this obstacle with the fewest remaining conflicts.
    static Native.RECT PushClear(Native.RECT r, Native.RECT obstacle, List<Native.RECT> obstacles, Native.RECT canvas,
        int hGap, int vGap, int header, (int X, int Y) bias)
    {
        int right = obstacle.Right + hGap - r.Left;                       // move r right
        int left = r.Right + hGap - obstacle.Left;                        // move r left
        int down = obstacle.Bottom + vGap - (r.Top - header);             // move r down
        int up = r.Bottom + vGap - (obstacle.Top - header);               // move r up
        var rc = Centre(r); var oc = Centre(obstacle);
        bool hasBias = bias != default;
        double Cost(int travel, bool natural, bool biased) =>
            hasBias ? travel + (biased ? 0 : 1e7) : travel + (natural ? 0 : 1e6);
        var options = new List<(int Dx, int Dy, double Cost)>
        {
            (right, 0, Cost(right, rc.X >= oc.X, bias.X > 0)),
            (-left, 0, Cost(left, rc.X < oc.X, bias.X < 0)),
            (0, down, Cost(down, rc.Y >= oc.Y, bias.Y > 0)),
            (0, -up, Cost(up, rc.Y < oc.Y, bias.Y < 0)),
        };
        Native.RECT best = r; int bestConflicts = int.MaxValue; bool bestClears = false;
        foreach (var (dx, dy, _) in options.OrderBy(o => o.Cost))
        {
            var moved = ThumbnailLayout.Clamp(new Native.RECT(r.Left + dx, r.Top + dy, r.Width, r.Height), canvas);
            bool clears = !TooClose(moved, obstacle, hGap, vGap, header);
            int conflicts = obstacles.Count(o => TooClose(moved, o, hGap, vGap, header));
            if (clears && conflicts == 0) return moved;
            if ((clears && !bestClears) || (clears == bestClears && conflicts < bestConflicts))
            { best = moved; bestConflicts = conflicts; bestClears = clears; }
        }
        return best;
    }

    // Share of `tile` covered by `over`.
    static double CoveredFraction(Native.RECT tile, Native.RECT over)
    {
        long w = Math.Max(0, Math.Min(tile.Right, over.Right) - Math.Max(tile.Left, over.Left));
        long h = Math.Max(0, Math.Min(tile.Bottom, over.Bottom) - Math.Max(tile.Top, over.Top));
        return w * h / (double)Math.Max(1L, (long)tile.Width * tile.Height);
    }
    static bool Inside(Native.RECT r, Native.RECT canvas) =>
        r.Left >= canvas.Left && r.Top >= canvas.Top && r.Right <= canvas.Right && r.Bottom <= canvas.Bottom;

    // Nearest spot for `tile` (same size) that keeps the Task View gaps from everything.
    // Candidate edges come from every tile's edges offset by the gaps, plus the canvas edges.
    static Native.RECT? NearestFree(Native.RECT tile, List<Native.RECT> obstacles, List<Native.RECT> stationary,
        Native.RECT canvas, int hGap, int vGap, int header, Func<Native.RECT, bool> free)
    {
        int w = tile.Width, h = tile.Height;
        var all = obstacles.Concat(stationary).ToList();
        var xs = all.SelectMany(o => new[] { o.Right + hGap, o.Left - hGap - w, o.Left })
            .Append(canvas.Left).Append(canvas.Right - w).Append(tile.Left)
            .Where(x => x >= canvas.Left && x + w <= canvas.Right).Distinct().ToList();
        var ys = all.SelectMany(o => new[] { o.Bottom + vGap + header, o.Top - header - vGap - h, o.Top })
            .Append(canvas.Top + header).Append(canvas.Bottom - h).Append(tile.Top)
            .Where(y => y >= canvas.Top && y + h <= canvas.Bottom).Distinct().ToList();
        Native.RECT? best = null; long bestDistance = long.MaxValue;
        foreach (var y in ys)
            foreach (var x in xs)
            {
                long d = (long)(x - tile.Left) * (x - tile.Left) + (long)(y - tile.Top) * (y - tile.Top);
                if (d >= bestDistance) continue;
                var c = new Native.RECT(x, y, w, h);
                if (free(c)) { best = c; bestDistance = d; }
            }
        return best;
    }

    static (double X, double Y) Centre(Native.RECT r) => (r.Left + r.Width / 2d, r.Top + r.Height / 2d);
    static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
