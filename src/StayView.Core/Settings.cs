using System.Text.Json;
namespace StayView.Core;
public enum DockPosition { Auto,Right,Bottom }
public enum DesktopStripPosition { Top=1,Bottom=2 }
public enum BackgroundTheme { DarkBlueBlack,Darker,MoreBlue,Light }
// FlyIn is internal: the Task View-style opening animation (each miniature flies from
// its window's real screen position into its grid slot). It is never offered in the
// Desktop transition picker and never saved as the user's DesktopTransition.
public enum DesktopTransitionMode { Appear,ShiftInRight,ShiftInLeft,Implode,Explode,FlyIn }
public sealed class Settings
{
    public const int SmallWindowSizeMin=200;
    public const int SmallWindowSizeMax=900;
    // Default miniature long edge, sized to match Task View's generous thumbnails. The
    // slider in Options still adjusts it freely within the min/max range.
    public const int SmallWindowSizeDefault=700;
    // Bumped when a default changes in a way a one-shot migration should apply to
    // settings saved under an older default. Never migrates a value the user chose later.
    public int SettingsVersion {get;set;}
    public uint HotkeyModifiers {get;set;}=10;
    public uint HotkeyKey {get;set;}=32;
    public bool ShowAllDesktops {get;set;}
    public bool SpanAllMonitors {get;set;}
    public int Padding {get;set;}=18;
    public int MinimumTileWidth {get;set;}=300;
    public int SmallWindowSize {get;set;}=SmallWindowSizeDefault;
    public bool ShowTitles {get;set;}=true;
    // Soft animation: on opening, each miniature flies from its window's real screen
    // position into its slot, as Task View does. Exposed in Options.
    public bool AnimateLayout {get;set;}=true;
    public DesktopTransitionMode DesktopTransition {get;set;}=DesktopTransitionMode.Appear;
    public bool AutoArrange {get;set;}
    public int AutoArrangeGrid {get;set;}=4;
    // How many windows may be focused (browsed) in front of the grid at once, 1..5.
    public int MaxBrowsedWindows {get;set;}=2;
    public const int MaxBrowsedWindowsMin=1, MaxBrowsedWindowsMax=5;
    public BackgroundTheme BackgroundTheme {get;set;}=BackgroundTheme.DarkBlueBlack;
    public bool StartWithWindows {get;set;}
    public bool DockEnabled {get;set;}=true;
    public DockPosition DockPosition {get;set;}=DockPosition.Auto;
    public int SatelliteScale {get;set;}=25;
    public int GlassOpacity {get;set;}=65;
    public bool UseSameChromeOpacity {get;set;}=true;
    public int DockOpacity {get;set;}=55;
    public int DesktopStripOpacity {get;set;}=55;
    public bool ReduceBlur {get;set;}
    public DesktopStripPosition DesktopStripPosition {get;set;}=DesktopStripPosition.Top;
    public bool KeepMinisSameSize {get;set;}=true;
    // A window that is already minimized when the overview opens is parked, not in use,
    // so it starts as a docked live view beside the desktop cards instead of a tile.
    public bool DockMinimizedWindows {get;set;}=true;
    public static string Folder=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"StayView");
    public static Settings Load() {
        try {
            var settings=JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Folder,"settings.json")))??new();
            // Older builds used 0 for "CenterTop". The bar is always centered now,
            // so migrate that legacy value to the new Top choice.
            bool legacyStrip=settings.DesktopStripPosition is not DesktopStripPosition.Top and not DesktopStripPosition.Bottom;
            if(legacyStrip)
                settings.DesktopStripPosition=DesktopStripPosition.Top;
            if(legacyStrip&&settings.GlassOpacity==55)settings.GlassOpacity=65;
            settings.GlassOpacity=Closest(settings.GlassOpacity,35,65,85);
            // Migrate the known historical preset generations once, then preserve any
            // custom slider value. Older code snapped every unknown value back to one of
            // three presets, which would destroy a user's custom size on restart.
            settings.SmallWindowSize=settings.SmallWindowSize switch {
                240=>300,360=>450,560=>700,
                120=>300,180=>450,280=>700,
                _=>Math.Clamp(settings.SmallWindowSize,SmallWindowSizeMin,SmallWindowSizeMax)
            };
            // One-shot: settings saved under the old 450 default adopt the Task View-sized
            // default. Keyed on SettingsVersion so a 450 the user picks later via the
            // slider is never migrated again.
            if(settings.SettingsVersion<1){if(settings.SmallWindowSize==450)settings.SmallWindowSize=SmallWindowSizeDefault;settings.SettingsVersion=1;}
            if(settings.AutoArrangeGrid is not 2 and not 4 and not 5)settings.AutoArrangeGrid=4;
            settings.MaxBrowsedWindows=Math.Clamp(settings.MaxBrowsedWindows,MaxBrowsedWindowsMin,MaxBrowsedWindowsMax);
            if(!Enum.IsDefined(settings.BackgroundTheme))settings.BackgroundTheme=BackgroundTheme.DarkBlueBlack;
            // FlyIn is internal to the opening animation and is never a user choice here.
            if(!Enum.IsDefined(settings.DesktopTransition)||settings.DesktopTransition==DesktopTransitionMode.FlyIn)settings.DesktopTransition=DesktopTransitionMode.Appear;
            return settings;
        } catch { return new(); }
    }
    static int Closest(int value,params int[] choices)=>choices.OrderBy(x=>Math.Abs(x-value)).First();
    public void Save() {Directory.CreateDirectory(Folder);var path=Path.Combine(Folder,"settings.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));File.Move(path+".tmp",path,true);}
}
