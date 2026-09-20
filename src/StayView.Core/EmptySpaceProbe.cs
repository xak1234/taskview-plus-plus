using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace StayView.Core;

// UI Automation never runs in the global mouse hook or on the UI thread. Keep at
// most one provider call in flight: a hung provider cannot accumulate workers.
public sealed class EmptySpaceProbe
{
    int busy;
    public string LastDecision { get; private set; } = "not checked";
    bool Reject(string reason) { LastDecision=reason; return false; }
    public async Task<bool> IsEmptyAsync(nint window, Native.POINT first, Native.POINT second)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        var query = Task.Run(() =>
        {
            var dpi = Native.SetThreadDpiAwarenessContext(-4);
            try { return IsEmpty(window, first) && ((first.X==second.X && first.Y==second.Y) || IsEmpty(window, second)); }
            catch (Exception ex) { return Reject(ex.GetType().Name); } // Unknown accessibility preserves clicks.
            finally { if(dpi!=0)Native.SetThreadDpiAwarenessContext(dpi); Volatile.Write(ref busy, 0); }
        });
        return await Task.WhenAny(query, Task.Delay(400)) == query && await query;
    }

    bool IsEmpty(nint window, Native.POINT point)
    {
        if (Native.GetAncestor(Native.WindowFromPoint(point), 2) != window) return Reject("different window under pointer");
        Native.GetWindowThreadProcessId(window, out var pid);
        bool browser = Native.Class(window) == "Chrome_WidgetWin_1";
        if (browser)
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            browser = process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase);
        }
        var element = AutomationElement.FromPoint(new Point(point.X, point.Y));
        if (element == null) return Reject("no element");
        var info = element.Current;
        // Chromium's provider can initially return a cached hit from another point.
        // Give its asynchronous hit test a bounded chance to settle; never trust an
        // out-of-bounds leaf as proof of either whitespace or an interactive control.
        for (int retry=0; retry<4 && !info.BoundingRectangle.Contains(point.X,point.Y); retry++)
        {
            Thread.Sleep(15);
            element=AutomationElement.FromPoint(new Point(point.X,point.Y));
            if(element==null)return Reject("no element");
            info=element.Current;
        }
        if (browser && !info.BoundingRectangle.Contains(point.X,point.Y))
        {
            int budget=256;
            element=FindAtPoint(AutomationElement.FromHandle(window),point,0,ref budget);
            if(element==null)return Reject("no bounded browser element");
            info=element.Current;
        }
        if (info.ProcessId != pid || info.IsOffscreen || !info.IsEnabled
            || !info.BoundingRectangle.Contains(point.X, point.Y)) return Reject("element not visible/enabled/in bounds");
        bool sawDocument = false, verifiedReadOnlyText = false;
        var pageHandlers = new List<Rect>();
        var documents = new List<Rect>();

        // A generic pane nested inside an editor/button still belongs to that control.
        // Reach the actual target HWND before accepting; incomplete ancestry is unknown.
        for (int depth = 0; element != null && depth < 96; depth++)
        {
            info = element.Current;
            bool document = browser && info.ControlType == ControlType.Document;
            if (!FocusedClickPolicy.IsBackgroundContainer(info.ControlType.Id) && !document) return Reject("control: " + info.ControlType.ProgrammaticName);
            if (document)
            {
                sawDocument = true;
                documents.Add(info.BoundingRectangle);
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                    && !((ValuePattern)value).Current.IsReadOnly) return Reject("editable document");
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text))
                {
                    var range = ((TextPattern)text).RangeFromPoint(new Point(point.X, point.Y));
                    range.ExpandToEnclosingUnit(TextUnit.Character);
                    // Chrome exposes Text/Value on the whole read-only webpage, including
                    // its whitespace. RangeFromPoint chooses the NEAREST character, so
                    // only its actual screen rectangle indicates a click on text.
                    if (range.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not true) return Reject("editable/unknown text");
                    var bounds = range.GetBoundingRectangles().SelectMany(r => new[] { r.X, r.Y, r.Width, r.Height }).ToArray();
                    if (!FocusedClickPolicy.IsOutsideText(point.X, point.Y, bounds)) return Reject("text under pointer");
                    verifiedReadOnlyText = true;
                }
            }
            foreach (var pattern in element.GetSupportedPatterns())
            {
                // Sites often put a delegated click handler on their entire page. Chrome
                // exposes that as Invoke on an unnamed group, even over empty whitespace.
                // Defer only these groups, then require a matching document-sized box.
                if (browser && pattern.Id == 10000 && info.ControlType == ControlType.Group
                    && !info.IsKeyboardFocusable && string.IsNullOrWhiteSpace(info.Name))
                { pageHandlers.Add(info.BoundingRectangle); continue; }
                if (FocusedClickPolicy.IsInteractivePattern(pattern.Id)
                    && !(document && pattern.Id is 10002 or 10014 or 10024)) return Reject("interactive pattern " + pattern.Id);
            }
            if ((nint)info.NativeWindowHandle == window)
            {
                LastDecision="reached target window";
                return (!sawDocument || verifiedReadOnlyText)
                    && pageHandlers.All(h => verifiedReadOnlyText && documents.Any(d => FocusedClickPolicy.IsPageSizedHandler(h, d)));
            }
            element = TreeWalker.RawViewWalker.GetParent(element);
        }
        return Reject("incomplete ancestry");
    }

    static AutomationElement? FindAtPoint(AutomationElement parent, Native.POINT point, int depth, ref int budget)
    {
        if(depth>=96 || --budget<0)throw new InvalidOperationException("Browser point search budget exhausted");
        var info=parent.Current;
        if(info.IsOffscreen || !info.BoundingRectangle.Contains(point.X,point.Y))return null;
        // Chromium sometimes returns a hit with stale scroll coordinates. Walk only
        // branches whose actual rectangles contain the point, within a fixed budget.
        var best=parent;
        var walker=TreeWalker.RawViewWalker;
        for(var child=walker.GetFirstChild(parent);child!=null;child=walker.GetNextSibling(child))
        {
            var candidate=FindAtPoint(child,point,depth+1,ref budget);
            if(candidate!=null)best=candidate;
        }
        return best;
    }
}
