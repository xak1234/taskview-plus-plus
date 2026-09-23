using System.Runtime.InteropServices;

// Minimal native UI Automation (UIA3) client. The managed System.Windows.Automation
// wrapper cannot see inside XAML islands such as Task View; CUIAutomation8 can.
// Only the vtable slots actually used are declared; the rest are ordered placeholders.
[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomation
{
    void _0(); void _1(); void _2();
    IUIAutomationElement ElementFromHandle(nint hwnd);
    IUIAutomationElement ElementFromPoint(Native3.POINT pt);
    void _5(); void _6(); void _7(); void _8(); void _9(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
    [return: MarshalAs(UnmanagedType.IUnknown)] object CreateTrueCondition();
}
[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomationElement
{
    void _0(); void _1(); void _2();
    IUIAutomationElementArray FindAll(int scope, [MarshalAs(UnmanagedType.IUnknown)] object condition);
    void _4(); void _5(); void _6();
    [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
}
[ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomationElementArray
{
    int Length { get; }
    IUIAutomationElement GetElement(int index);
}
static class Native3 { public struct POINT { public int X, Y; } }

sealed record UiaNode(string ControlType, string ClassName, string AutomationId, string Name, double Left, double Top, double Width, double Height);

static class Uia3
{
    const int BoundingRectangle = 30001, ControlType = 30003, Name = 30005, AutomationId = 30011, ClassName = 30012;
    static readonly IUIAutomation automation = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("e22ad333-b25f-460c-83d0-0581107395c9"))!)!;

    public static List<UiaNode> Descendants(nint hwnd)
    {
        var root = automation.ElementFromHandle(hwnd);
        var array = root.FindAll(4 /* TreeScope_Descendants */, automation.CreateTrueCondition());
        var result = new List<UiaNode>(array.Length);
        for (int i = 0; i < array.Length; i++) result.Add(Read(array.GetElement(i)));
        return result;
    }
    public static UiaNode FromPoint(int x, int y) => Read(automation.ElementFromPoint(new Native3.POINT { X = x, Y = y }));

    static UiaNode Read(IUIAutomationElement e)
    {
        var r = e.GetCurrentPropertyValue(BoundingRectangle) as double[] ?? [0, 0, 0, 0];
        return new(ControlTypeName(e.GetCurrentPropertyValue(ControlType)), e.GetCurrentPropertyValue(ClassName) as string ?? "",
            e.GetCurrentPropertyValue(AutomationId) as string ?? "", e.GetCurrentPropertyValue(Name) as string ?? "",
            r[0], r[1], r[2], r[3]);
    }
    static string ControlTypeName(object? id) => id is int v ? v switch
    {
        50000 => "Button", 50004 => "Edit", 50007 => "ListItem", 50008 => "List", 50020 => "Text", 50025 => "Custom",
        50026 => "Group", 50030 => "Document", 50032 => "Window", 50033 => "Pane", 50006 => "Image", 50003 => "ComboBox",
        _ => v.ToString()
    } : "?";
}
