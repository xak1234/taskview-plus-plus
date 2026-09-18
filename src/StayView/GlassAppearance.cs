using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using StayView.Core;
using Windows.UI;

namespace StayView;

static class GlassAppearance
{
    static readonly Color Base=Color.FromArgb(255,2,6,14);
    static readonly Color Tint=Color.FromArgb(255,10,22,40);
    static readonly Color Tint2=Color.FromArgb(255,13,27,42);
    static readonly Color Edge=Color.FromArgb(56,140,190,255);
    static readonly Color Specular=Color.FromArgb(30,210,230,255);
    static readonly Color Primary=Color.FromArgb(255,232,238,248);
    static readonly Color Secondary=Color.FromArgb(255,154,168,194);
    static readonly Color Active=Color.FromArgb(255,74,163,255);
    // Darker-blue buttons for the Options panel: a deep base and a slightly brighter
    // (but still dark) blue for the selected state.
    static readonly Color ButtonBlue=Color.FromArgb(255,18,38,68);
    static readonly Color ButtonBlueActive=Color.FromArgb(255,32,72,132);
    static readonly Color MenuGrey=Color.FromArgb(255,54,54,54);
    static readonly Color MenuGreyEdge=Color.FromArgb(255,78,78,78);
    // Captured from the current Windows Task View look on this machine: neutral smoked
    // acrylic, not a white/light sheet. The wallpaper should remain visible as a heavily
    // blurred colour field underneath it.
    static readonly Color TaskViewGrey=Color.FromArgb(255,55,55,55);

    public static bool TransparencyEnabled
    {
        get
        {
            try{return Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","EnableTransparency",1))!=0;}
            catch{return true;}
        }
    }

    public static int ClampOpacity(int value)=>Math.Clamp(value,20,90);
    public static int EffectiveDockOpacity(Settings settings)=>settings.UseSameChromeOpacity?ClampOpacity(settings.GlassOpacity):ClampOpacity(settings.DockOpacity);
    public static int EffectiveStripOpacity(Settings settings)=>settings.UseSameChromeOpacity?ClampOpacity(settings.GlassOpacity):ClampOpacity(settings.DesktopStripOpacity);
    static byte Alpha(int percent)=>checked((byte)Math.Round(ClampOpacity(percent)*255/100d));

    public static SolidColorBrush BackdropBrush(int percent)=>new(Color.FromArgb(Alpha(percent),Base.R,Base.G,Base.B));
    public static Brush OverviewBackdropBrush(Settings settings)
    {
        // Acrylic supplies the even frost; this single translucent gradient is the
        // only full-work-area tint above it. The opaque fallback prevents any raw
        // desktop flash when Windows transparency effects are disabled.
        byte alpha=TransparencyEnabled&&!settings.ReduceBlur?Alpha(settings.GlassOpacity):(byte)255;
        var (start,end)=ThemeColors(settings,alpha);
        return new LinearGradientBrush {
            StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(1,1),
            GradientStops={
                new GradientStop{Offset=0,Color=start},
                new GradientStop{Offset=1,Color=end}
            }
        };
    }
    // The theme's backdrop tint pair (start = lighter, end = darker).
    static (Color start,Color end) ThemeColors(Settings settings,byte alpha)=>settings.BackgroundTheme switch {
        BackgroundTheme.Darker => (Color.FromArgb(alpha,3,8,16),Color.FromArgb(alpha,0,1,4)),
        BackgroundTheme.MoreBlue => (Color.FromArgb(alpha,8,28,58),Color.FromArgb(alpha,1,7,18)),
        BackgroundTheme.Light => (Color.FromArgb(alpha,76,76,76),Color.FromArgb(alpha,45,45,45)),
        _ => (Color.FromArgb(alpha,7,20,38),Color.FromArgb(alpha,1,4,10))
    };
    static Color Scale(Color c,double k,byte alpha)=>Color.FromArgb(alpha,(byte)Math.Round(c.R*k),(byte)Math.Round(c.G*k),(byte)Math.Round(c.B*k));
    public static Brush StripBrush(Settings settings)
    {
        // The desktop strip is the same tint family as the page but a shade darker, so it
        // reads as a bar rather than a lighter grey block, with a left-to-right shading
        // (slightly lighter on the left, darkest on the right). Sits over the acrylic
        // backdrop, so partial alpha reads as frosted; opaque when transparency is off.
        byte alpha=TransparencyEnabled&&!settings.ReduceBlur?Alpha(Math.Min(100,EffectiveStripOpacity(settings)+20)):(byte)255;
        var (start,end)=ThemeColors(settings,alpha);
        return new LinearGradientBrush {
            StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(1,0),
            GradientStops={
                new GradientStop{Offset=0,Color=Scale(start,0.75,alpha)},
                new GradientStop{Offset=1,Color=Scale(end,0.55,alpha)}
            }
        };
    }
    // No visible outline around the desktop/dock bar at rest; the bar reads by its glass
    // fill alone. The blue ActiveBrush still marks it as a drop target on dock hover.
    public static SolidColorBrush StripEdgeBrush()=>new(Color.FromArgb(0,255,255,255));
    public static SolidColorBrush SurfaceBrush(int percent)=>new(Color.FromArgb(Alpha(percent),Tint.R,Tint.G,Tint.B));
    public static SolidColorBrush SurfaceBrush2(int percent)=>new(Color.FromArgb(Alpha(percent),Tint2.R,Tint2.G,Tint2.B));
    public static SolidColorBrush EdgeBrush()=>new(Edge);
    public static SolidColorBrush SpecularBrush()=>new(Specular);
    public static SolidColorBrush PrimaryBrush()=>new(Primary);
    public static SolidColorBrush SecondaryBrush()=>new(Secondary);
    public static SolidColorBrush ActiveBrush()=>new(Active);
    public static SolidColorBrush DesktopCloseGlowBrush()=>new(Color.FromArgb(255,178,220,255));
    public static SolidColorBrush ButtonBlueBrush()=>new(ButtonBlue);
    public static SolidColorBrush ButtonBlueActiveBrush()=>new(ButtonBlueActive);
    public static SolidColorBrush MenuGreyBrush()=>new(MenuGrey);
    public static SolidColorBrush MenuWhiteBrush()=>new(Microsoft.UI.Colors.White);
    // Slightly-transparent panel background (Options window). Sits over the acrylic
    // backdrop so it reads as frosted; opaque fallback when transparency is disabled.
    public static Brush PanelBrush(Settings settings)
    {
        byte alpha=TransparencyEnabled&&!settings.ReduceBlur?(byte)205:(byte)255;
        return new LinearGradientBrush {
            StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(1,1),
            GradientStops={
                new GradientStop{Offset=0,Color=Color.FromArgb(alpha,7,20,38)},
                new GradientStop{Offset=1,Color=Color.FromArgb(alpha,1,4,10)}
            }
        };
    }

    public static void ApplyBackdrop(Window window,Settings settings)
    {
        if(TransparencyEnabled&&!settings.ReduceBlur){
            bool taskView=settings.BackgroundTheme==BackgroundTheme.Light;
            if(window.SystemBackdrop is not ActiveAcrylicBackdrop acrylic || acrylic.TaskView!=taskView)
                window.SystemBackdrop=new ActiveAcrylicBackdrop(taskView);
        }else window.SystemBackdrop=null;
    }

    // DesktopAcrylicBackdrop follows window activation and paints its solid fallback
    // colour while inactive. The overview is deliberately never activated (clicks
    // focus source windows), so it always showed the opaque fallback. This backdrop
    // pins the configuration to input-active so the blur stays see-through.
    sealed class ActiveAcrylicBackdrop:SystemBackdrop
    {
        public bool TaskView {get;}
        Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController? controller;
        public ActiveAcrylicBackdrop(bool taskView){TaskView=taskView;}
        protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget,XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget,xamlRoot);
            controller=new Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController{
                Kind=Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicKind.Thin,
                TintColor=TaskView?TaskViewGrey:Base,TintOpacity=TaskView?.12f:0f,
                LuminosityOpacity=TaskView?.34f:.2f,FallbackColor=TaskView?TaskViewGrey:Base};
            controller.SetSystemBackdropConfiguration(new Microsoft.UI.Composition.SystemBackdrops.SystemBackdropConfiguration{
                IsInputActive=true,Theme=Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Dark});
            controller.AddSystemBackdropTarget(connectedTarget);
        }
        protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            base.OnTargetDisconnected(disconnectedTarget);
            controller?.RemoveSystemBackdropTarget(disconnectedTarget);
            controller?.Dispose();controller=null;
        }
    }

    public static Border Surface(UIElement child,int opacity,double radius=10,Thickness? padding=null)
    {
        var layers=new Grid();layers.Children.Add(child);layers.Children.Add(new Border{Height=1,Background=SpecularBrush(),VerticalAlignment=VerticalAlignment.Top,IsHitTestVisible=false,CornerRadius=new CornerRadius(radius)});
        var border=new Border{Child=layers,Background=SurfaceBrush(opacity),BorderBrush=EdgeBrush(),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(radius),Padding=padding??new Thickness(0)};
        return border;
    }

    public static Button Button(string text,Action click,int opacity)
    {
        var b=new Button{Content=text,Padding=new Thickness(11,5,11,5),CornerRadius=new CornerRadius(7),Background=SurfaceBrush2(opacity),Foreground=PrimaryBrush(),BorderBrush=EdgeBrush(),BorderThickness=new Thickness(1)};
        b.Click+=(_,_)=>click();return b;
    }

    public static void StyleMenu(MenuFlyout menu,int opacity)
    {
        var style=new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty,SurfaceBrush2(opacity)));
        style.Setters.Add(new Setter(Control.ForegroundProperty,PrimaryBrush()));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,EdgeBrush()));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty,new CornerRadius(9)));
        menu.MenuFlyoutPresenterStyle=style;
    }
    public static void StyleGreyMenu(MenuFlyout menu)
    {
        var style=new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty,MenuGreyBrush()));
        style.Setters.Add(new Setter(Control.ForegroundProperty,MenuWhiteBrush()));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,new SolidColorBrush(MenuGreyEdge)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty,new CornerRadius(7)));
        menu.MenuFlyoutPresenterStyle=style;
    }
}
