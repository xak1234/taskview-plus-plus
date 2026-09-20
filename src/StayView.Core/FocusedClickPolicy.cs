namespace StayView.Core;

// Trust the application's non-client hit test. Client areas include custom tabs,
// toolbars and editors; their clicks must not become overview gestures.
public static class FocusedClickPolicy
{
    public static bool IsPageSizedHandler(System.Windows.Rect handler, System.Windows.Rect document)
        => !handler.IsEmpty && !document.IsEmpty && handler.Width > 0 && handler.Height > 0
            && Math.Abs(handler.Left-document.Left)<=2 && Math.Abs(handler.Top-document.Top)<=2
            && Math.Abs(handler.Right-document.Right)<=2 && Math.Abs(handler.Bottom-document.Bottom)<=2;

    public static bool IsOutsideText(double x, double y, double[] bounds)
    {
        if (bounds.Length % 4 != 0 || !double.IsFinite(x) || !double.IsFinite(y)) return false;
        for (int i = 0; i < bounds.Length; i += 4)
        {
            double left=bounds[i], top=bounds[i+1], width=bounds[i+2], height=bounds[i+3];
            if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width)
                || !double.IsFinite(height) || width<0 || height<0) return false;
            if (x>=left && x<=left+width && y>=top && y<=top+height) return false;
        }
        return true;
    }

    // UIA container types: List, Tree, Group, DataGrid, Table, Window, Pane.
    // Text, Document, Custom and individual items are deliberately not empty space.
    public static bool IsBackgroundContainer(int controlType)
        => controlType is 50008 or 50023 or 50026 or 50028 or 50036 or 50032 or 50033;

    // Invoke, Value, RangeValue, ExpandCollapse, SelectionItem, Text, Toggle, Text2.
    public static bool IsInteractivePattern(int pattern)
        => pattern is 10000 or 10002 or 10003 or 10005 or 10010 or 10014 or 10015 or 10024;

    // HTCAPTION is authoritative. If the target did not answer WM_NCHITTEST before the
    // bounded timeout, a point proven to lie in the standard caption band is also safe:
    // there is no confirmed client/control hit to steal, and Windows itself still treats
    // that non-client band as draggable on a busy window.
    public static bool CanShrink(int? hit, bool fallbackCaption = false)
        => hit == 2 || (hit == null && fallbackCaption);

    public static bool CanDrag(int? hit, bool winHeld, bool fallbackCaption)
        => hit == 2 || (hit == 1 && winHeld)
            || (hit == null && (winHeld || fallbackCaption));
}
