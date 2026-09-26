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
        // Frosted glass: the theme's tint at about 45% (the blurred wallpaper under the
        // window's acrylic shows through), lifted by a faint white frost (about 8%), with the
        // same left-to-right shading as before. Opaque when transparency is off.
        bool glass=TransparencyEnabled&&!settings.ReduceBlur;
        byte alpha=glass?Alpha(StripGlassPercent):(byte)255;
        var (start,end)=ThemeColors(settings,alpha);
        return new LinearGradientBrush {
            StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(1,0),
            GradientStops={
                new GradientStop{Offset=0,Color=Frost(Scale(start,0.75,alpha),glass?StripFrost:0)},
                new GradientStop{Offset=1,Color=Frost(Scale(end,0.55,alpha),glass?StripFrost:0)}
            }
        };
    }
    public const int StripGlassPercent=45;
    const double StripFrost=0.08;
    // Mix a fraction of white into a colour (keeps its alpha): the frost of the glass.
    static Color Frost(Color c,double white)=>Color.FromArgb(c.A,
        (byte)Math.Round(c.R+(255-c.R)*white),(byte)Math.Round(c.G+(255-c.G)*white),(byte)Math.Round(c.B+(255-c.B)*white));
    // The glass edge: a light 1 px rim, brightest along the top (catching the light) and
    // fading down the sides. The blue ActiveBrush still replaces it on dock hover.
    public static Brush StripEdgeBrush()=>new LinearGradientBrush {
        StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(0,1),
        GradientStops={
            new GradientStop{Offset=0,Color=Color.FromArgb(72,255,255,255)},
            new GradientStop{Offset=0.35,Color=Color.FromArgb(22,255,255,255)},
            new GradientStop{Offset=1,Color=Color.FromArgb(14,255,255,255)}
        }
    };
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
    // Half-transparent panel background (Options window): 50% tint over the acrylic
    // backdrop, so the blurred desktop shows through; opaque when transparency is disabled.
    public static Brush PanelBrush(Settings settings)
    {
        byte alpha=TransparencyEnabled&&!settings.ReduceBlur?(byte)128:(byte)255;
        return new LinearGradientBrush {
            StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(1,1),
            GradientStops={
                new GradientStop{Offset=0,Color=Color.FromArgb(alpha,7,20,38)},
                new GradientStop{Offset=1,Color=Color.FromArgb(alpha,1,4,10)}
            }
        };
    }

    // Pin head: a glossy sphere lit from the top-left, like a push-pin seen from above.
    // Layers: contact shadow, shaded body, dark rim, bottom rim light, specular highlight.
    public const double PinHeadSize=20;
    public static UIElement PinHead()
    {
        const double s=PinHeadSize;
        static Windows.Foundation.Point P(double x,double y)=>new(x,y);
        static GradientStop Stop(double o,byte a,byte r,byte g,byte b)=>new(){Offset=o,Color=Color.FromArgb(a,r,g,b)};
        var shadow=new Microsoft.UI.Xaml.Shapes.Ellipse{Width=s,Height=s,
            Fill=new RadialGradientBrush{Center=P(.5,.5),GradientOrigin=P(.5,.5),RadiusX=.5,RadiusY=.5,
                GradientStops={Stop(0,150,0,4,12),Stop(.6,90,0,4,12),Stop(1,0,0,4,12)}},
            RenderTransform=new TranslateTransform{X=1.5,Y=2.5}};
        var body=new Microsoft.UI.Xaml.Shapes.Ellipse{Width=s,Height=s,
            Fill=new RadialGradientBrush{Center=P(.42,.38),GradientOrigin=P(.34,.28),RadiusX=.7,RadiusY=.7,
                GradientStops={Stop(0,255,196,232,255),Stop(.28,255,96,178,255),Stop(.62,255,30,104,208),Stop(.9,255,10,44,104),Stop(1,255,6,26,64)}},
            Stroke=new SolidColorBrush(Color.FromArgb(230,4,16,40)),StrokeThickness=1};
        // Light bouncing off the canvas onto the lower edge; gives the sphere its underside.
        var rimLight=new Microsoft.UI.Xaml.Shapes.Ellipse{Width=s-2,Height=s-2,Margin=new Thickness(1),
            Fill=new RadialGradientBrush{Center=P(.5,.5),GradientOrigin=P(.62,.78),RadiusX=.5,RadiusY=.5,
                GradientStops={Stop(0,0,110,220,255),Stop(.78,0,110,220,255),Stop(1,120,110,220,255)}}};
        var specular=new Microsoft.UI.Xaml.Shapes.Ellipse{Width=s*.42,Height=s*.28,
            HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,
            Margin=new Thickness(s*.2,s*.13,0,0),
            Fill=new RadialGradientBrush{Center=P(.5,.5),GradientOrigin=P(.45,.4),RadiusX=.5,RadiusY=.5,
                GradientStops={Stop(0,235,255,255,255),Stop(.55,110,255,255,255),Stop(1,0,255,255,255)}}};
        return new Grid{Width=s,Height=s,IsHitTestVisible=false,Children={shadow,body,rimLight,specular}};
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
