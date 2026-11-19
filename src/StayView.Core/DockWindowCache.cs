namespace StayView.Core;

// A shell desktop switch may temporarily cloak or omit an otherwise live HWND.
// Retain the last valid thumbnail metadata for explicitly docked windows until
// the user undocks them or their HWND actually closes.
public sealed class DockWindowCache
{
    readonly Dictionary<nint, AppWindow> known = [];

    public void Remember(AppWindow window) => known[window.Handle] = window;

    public IReadOnlyList<AppWindow> Resolve(IEnumerable<AppWindow> enumerated,
        IReadOnlyCollection<nint> docked, Func<nint, bool> isWindow)
    {
        var liveDock = docked.Where(isWindow).ToHashSet();
        foreach (var handle in known.Keys.Where(h => !liveDock.Contains(h)).ToList())
            known.Remove(handle);
        foreach (var window in enumerated)
            if (liveDock.Contains(window.Handle)) known[window.Handle] = window;
        return docked.Where(liveDock.Contains).Where(known.ContainsKey)
            .Select(h => known[h]).ToList();
    }

    public void Clear() => known.Clear();
}
