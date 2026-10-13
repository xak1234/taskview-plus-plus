namespace StayView.Core;

public static class WindowHitTest
{
    // Modern Explorer reports HTCLIENT on its root, while its actual title-bar
    // child reports HTCAPTION. Ask the HWND under the pointer too, but only when
    // it belongs to this root. Tabs and toolbar controls still report HTCLIENT.
    public static int? At(nint root, Native.POINT point)
    {
        int? hit=Query(root,point);
        if(hit is not (null or 1))return hit;
        var child=Native.WindowFromPoint(point);
        if(child==0||child==root||Native.GetAncestor(child,2)!=root)return hit;
        int? childHit=Query(child,point);
        return childHit is >0 ? childHit : hit;
    }

    static int? Query(nint window,Native.POINT point)
    {
        nint packed=(nint)(((uint)(point.Y&0xFFFF)<<16)|(uint)(point.X&0xFFFF));
        return Native.SendMessageTimeout(window,0x0084,0,packed,0x0002|0x0001,30,out var hit)!=0
            ? (int)hit.ToInt64() : null;
    }
}
