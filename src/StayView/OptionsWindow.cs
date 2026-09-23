using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StayView.Core;

namespace StayView;

// Settings panel, styled after Windows 11 Settings: a branded header, titled sections of
// rounded cards, one row per setting (name + one-line description on the left, control on
// the right), native toggle switches for on/off settings and a segmented control for
// choices. Dropdowns are avoided on purpose: this transient panel closes when it loses
// activation, and a popup list could trigger that.
sealed class OptionsWindow : Window
{
    readonly Settings settings;
    readonly Action appearanceChanged;
    readonly Action layoutChanged;
    readonly Action exitApp;
    readonly Grid root=new();
    bool hasActivated;
    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    public void BringToFront()
    {
        if(!Native.IsWindowVisible(Handle))return;
        Native.SetWindowPos(Handle,-1,0,0,0,0,0x13); // TOPMOST, no activation or geometry changes
    }
    public void Present(){Activate();BringToFront();}

    static readonly FontFamily Display=new("Segoe UI Variable Display, Segoe UI");
    static readonly FontFamily Text=new("Segoe UI Variable Text, Segoe UI");
    static readonly FontFamily Icons=new("Segoe Fluent Icons, Segoe MDL2 Assets");
    const int CardOpacity=52;
    const int PillOpacity=70;
    const double PanelWidth=560;

    public OptionsWindow(Settings settings,Action appearanceChanged,Action layoutChanged,Action exitApp,nint owner)
    {
        this.settings=settings;this.appearanceChanged=appearanceChanged;this.layoutChanged=layoutChanged;this.exitApp=exitApp;
        Title="Taskview++ Settings";
        var handle=WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Keep Options structurally above the overview that launched it (see git history):
        // ownership stops the overview's z-order maintenance burying a fresh panel.
        if(owner!=0)Native.SetWindowLongPtr(handle,-8,owner); // GWLP_HWNDPARENT
        Native.DisableDwmBorder(handle);
        var anchor=owner!=0?owner:handle;
        double windowScale=Math.Max(1,Native.GetDpiForWindow(anchor)/96d);
        var work=Native.WorkArea(Native.MonitorFromWindow(anchor,2));
        if(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop=true;
            presenter.IsResizable=false;
            presenter.IsMaximizable=false;
            presenter.IsMinimizable=false;
            presenter.SetBorderAndTitleBar(false,false);
        }
        Activated+=(_,e)=>{
            if(e.WindowActivationState!=WindowActivationState.Deactivated){hasActivated=true;return;}
            // Transient settings panel: once genuinely active, an outside click dismisses it.
            if(hasActivated)Close();
        };

        root.RequestedTheme=ElementTheme.Dark;
        Content=root;

        var header=Header();
        var body=new StackPanel{Spacing=6,Padding=new Thickness(24,4,24,22)};
        var scroller=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        var layout=new Grid();
        layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        Grid.SetRow(scroller,1);
        layout.Children.Add(header);layout.Children.Add(scroller);
        root.Children.Add(layout);

        body.Children.Add(new TextBlock{Text="Changes apply instantly",FontFamily=Text,FontSize=12,Foreground=GlassAppearance.SecondaryBrush(),
            HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,0,4)});

        // APPEARANCE
        var appearance=Section(body,"Appearance");
        Setting(appearance,"Theme","Overview background colour",
            Segmented(new[]{"Blue-black","Darker","More blue","Light"},(int)settings.BackgroundTheme,i=>{settings.BackgroundTheme=(BackgroundTheme)i;SaveAppearance();},true),stacked:true);
        // GlassOpacity is backdrop OPACITY, so High transparency is the lowest value.
        Setting(appearance,"Transparency","How much of the desktop shows through",
            Segmented(new[]{"Low","Medium","High"},Nearest(settings.GlassOpacity,85,65,35),i=>{settings.GlassOpacity=new[]{85,65,35}[i];SaveAppearance();}));

        // LAYOUT
        var layoutCard=Section(body,"Layout");
        Setting(layoutCard,"Auto-arrange","Keep windows in the Task View layout",
            Toggle(settings.AutoArrange,v=>{settings.AutoArrange=v;settings.Save();layoutChanged();}));
        Setting(layoutCard,"Focused windows","How many windows can be brought forward at once",
            Segmented(new[]{"1","2","3","4","5"},Math.Clamp(settings.MaxBrowsedWindows,1,5)-1,i=>{settings.MaxBrowsedWindows=i+1;settings.Save();layoutChanged();}));

        // DOCK
        var dock=Section(body,"Dock");
        Setting(dock,"Dock minimized windows","Minimized windows sit in the desktop bar",
            Toggle(settings.DockMinimizedWindows,v=>{settings.DockMinimizedWindows=v;settings.Save();layoutChanged();}));
        Setting(dock,"Dock on bar contact","Pushing a window into the bar docks it",
            Toggle(settings.DockOnBarContact,v=>{settings.DockOnBarContact=v;settings.Save();}));
        Setting(dock,"Plasma effect","Electric arcs when a window meets the bar",
            Toggle(settings.PlasmaEffect,v=>{settings.PlasmaEffect=v;settings.Save();}));

        // DESKTOPS
        var desktops=Section(body,"Desktops");
        Setting(desktops,"Desktop bar position","Where the virtual desktop bar sits",
            Segmented(new[]{"Top","Bottom"},settings.DesktopStripPosition==DesktopStripPosition.Bottom?1:0,i=>{settings.DesktopStripPosition=i==1?DesktopStripPosition.Bottom:DesktopStripPosition.Top;settings.Save();layoutChanged();}));
        Setting(desktops,"Opening animation","Windows fly into place when the overview opens",
            Toggle(settings.AnimateLayout,v=>{settings.AnimateLayout=v;settings.Save();}));
        var transitionModes=new[]{DesktopTransitionMode.Slide,DesktopTransitionMode.Appear,DesktopTransitionMode.ShiftInRight,DesktopTransitionMode.ShiftInLeft,DesktopTransitionMode.Implode,DesktopTransitionMode.Explode};
        Setting(desktops,"Desktop transition","Animation when switching desktops",
            Segmented(new[]{"Slide","Appear","Shift right","Shift left","Implode","Explode"},Math.Max(0,Array.IndexOf(transitionModes,settings.DesktopTransition)),i=>{settings.DesktopTransition=transitionModes[i];settings.Save();},true),stacked:true);

        body.Children.Add(Footer());

        ApplyAppearance();
        FitToContent(header,body,scroller,windowScale,work);
    }

    // Brand mark, title and subtitle; a quiet close button top-right.
    Grid Header()
    {
        var header=new Grid{Padding=new Thickness(24,20,16,14),ColumnSpacing=14};
        header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var mark=new Border{Width=36,Height=36,CornerRadius=new CornerRadius(9),VerticalAlignment=VerticalAlignment.Center,
            Background=new LinearGradientBrush{StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(1,1),
                GradientStops={new GradientStop{Color=Windows.UI.Color.FromArgb(255,58,132,236),Offset=0},new GradientStop{Color=Windows.UI.Color.FromArgb(255,28,70,150),Offset=1}}},
            Child=new TextBlock{Text="",FontFamily=Icons,FontSize=17,Foreground=new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};
        header.Children.Add(mark);
        var titles=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Spacing=0};
        titles.Children.Add(new TextBlock{Text="Taskview++",FontFamily=Display,FontSize=20,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,Foreground=GlassAppearance.PrimaryBrush()});
        titles.Children.Add(new TextBlock{Text="Settings",FontFamily=Text,FontSize=12,Foreground=GlassAppearance.SecondaryBrush()});
        Grid.SetColumn(titles,1);header.Children.Add(titles);
        var close=new Button{Content=new TextBlock{Text="",FontFamily=Icons,FontSize=10},Width=34,Height=34,Padding=new Thickness(0),
            VerticalAlignment=VerticalAlignment.Top,CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(0),
            Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),Foreground=GlassAppearance.SecondaryBrush()};
        ToolTipService.SetToolTip(close,"Close");
        close.Click+=(_,_)=>Close();
        Grid.SetColumn(close,2);header.Children.Add(close);
        return header;
    }

    // A quiet Quit, centred at the bottom.
    Button Footer()
    {
        var quit=new Button{Height=34,Padding=new Thickness(14,0,16,0),CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1),
            HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,16,0,0),
            BorderBrush=GlassAppearance.EdgeBrush(),Background=GlassAppearance.SurfaceBrush(PillOpacity),Foreground=GlassAppearance.PrimaryBrush()};
        var content=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        content.Children.Add(new TextBlock{Text="",FontFamily=Icons,FontSize=12,VerticalAlignment=VerticalAlignment.Center,
            Foreground=new SolidColorBrush(Windows.UI.Color.FromArgb(255,255,140,140))});
        content.Children.Add(new TextBlock{Text="Quit Taskview++",FontFamily=Text,FontSize=13,VerticalAlignment=VerticalAlignment.Center});
        quit.Content=content;
        quit.Click+=(_,_)=>exitApp();
        return quit;
    }

    void FitToContent(Grid header,StackPanel body,ScrollViewer scroller,double scale,Native.RECT work)
    {
        int maxWidth=Math.Max(1,work.Width-48),maxHeight=Math.Max(1,work.Height-48);
        int width=Math.Min((int)Math.Round(PanelWidth*scale),maxWidth);
        double widthDip=width/scale;
        // Measure the actual controls rather than sizing the window to a fixed historical
        // height. On normal displays the panel grows to exactly contain its settings, so
        // there is no redundant vertical scrollbar. Small work areas retain scrolling as
        // a fallback instead of clipping the bottom controls.
        header.Measure(new Windows.Foundation.Size(widthDip,double.PositiveInfinity));
        body.Measure(new Windows.Foundation.Size(widthDip,double.PositiveInfinity));
        int contentHeight=(int)Math.Ceiling((header.DesiredSize.Height+body.DesiredSize.Height)*scale);
        bool constrained=contentHeight>maxHeight;
        scroller.VerticalScrollBarVisibility=constrained?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled;
        int height=Math.Min(contentHeight,maxHeight);
        int x=work.Left+(work.Width-width)/2;
        int y=work.Top+(work.Height-height)/2;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x,y,width,height));
    }

    // A titled group: the section name centred above a rounded card whose rows are split by hairlines.
    static StackPanel Section(StackPanel body,string title)
    {
        body.Children.Add(new TextBlock{Text=title,FontFamily=Text,FontSize=14,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground=GlassAppearance.PrimaryBrush(),HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,12,0,4)});
        var rows=new StackPanel();
        body.Children.Add(new Border{Child=rows,CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),
            BorderBrush=GlassAppearance.EdgeBrush(),Background=GlassAppearance.SurfaceBrush(CardOpacity)});
        return rows;
    }

    // One setting: name and description on the left, control on the right (or underneath
    // for wide option sets).
    static void Setting(StackPanel rows,string name,string description,FrameworkElement control,bool stacked=false)
    {
        if(rows.Children.Count>0)
            rows.Children.Add(new Border{Height=1,Background=GlassAppearance.EdgeBrush(),Opacity=.6,Margin=new Thickness(16,0,16,0)});
        var labels=new StackPanel{Spacing=1,VerticalAlignment=VerticalAlignment.Center};
        labels.Children.Add(new TextBlock{Text=name,FontFamily=Text,FontSize=14,Foreground=GlassAppearance.PrimaryBrush()});
        labels.Children.Add(new TextBlock{Text=description,FontFamily=Text,FontSize=12,Foreground=GlassAppearance.SecondaryBrush(),TextWrapping=TextWrapping.Wrap});
        if(stacked)
        {
            var column=new StackPanel{Spacing=10,Padding=new Thickness(16,12,16,14)};
            column.Children.Add(labels);
            column.Children.Add(control);
            rows.Children.Add(column);
            return;
        }
        var grid=new Grid{ColumnSpacing=16,Padding=new Thickness(16,11,12,11),MinHeight=60};
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        grid.Children.Add(labels);
        control.VerticalAlignment=VerticalAlignment.Center;
        Grid.SetColumn(control,1);grid.Children.Add(control);
        rows.Children.Add(grid);
    }

    // Native Windows toggle, compact (no On/Off caption beside it).
    static ToggleSwitch Toggle(bool value,Action<bool> changed)
    {
        var toggle=new ToggleSwitch{IsOn=value,OnContent=null,OffContent=null,MinWidth=0,Margin=new Thickness(0,0,-8,0)};
        toggle.Toggled+=(_,_)=>changed(toggle.IsOn);
        return toggle;
    }

    // Segmented choice: a recessed track with the selected option raised in accent blue.
    static Border Segmented(string[] labels,int selected,Action<int> changed,bool stretch=false)
    {
        var track=new Grid{ColumnSpacing=2};
        var buttons=new List<Button>();
        for(int i=0;i<labels.Length;i++)track.ColumnDefinitions.Add(new ColumnDefinition{Width=stretch?new GridLength(1,GridUnitType.Star):GridLength.Auto});
        void Paint()
        {
            for(int i=0;i<buttons.Count;i++)
            {
                bool on=i==selected;
                buttons[i].Background=on?GlassAppearance.ButtonBlueActiveBrush():new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                buttons[i].Foreground=on?GlassAppearance.PrimaryBrush():GlassAppearance.SecondaryBrush();
                buttons[i].FontWeight=on?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal;
            }
        }
        for(int i=0;i<labels.Length;i++)
        {
            int index=i;
            var b=new Button{Content=labels[i],Height=30,MinWidth=stretch?0:44,Padding=new Thickness(12,0,12,0),FontSize=12.5,FontFamily=Text,
                HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Center,
                CornerRadius=new CornerRadius(5),BorderThickness=new Thickness(0)};
            b.Click+=(_,_)=>{selected=index;Paint();changed(index);};
            Grid.SetColumn(b,i);track.Children.Add(b);buttons.Add(b);
        }
        Paint();
        return new Border{Child=track,Padding=new Thickness(3),CornerRadius=new CornerRadius(7),
            Background=GlassAppearance.SurfaceBrush(PillOpacity),BorderBrush=GlassAppearance.EdgeBrush(),BorderThickness=new Thickness(1),
            HorizontalAlignment=stretch?HorizontalAlignment.Stretch:HorizontalAlignment.Right};
    }

    static int Nearest(int value,params int[] choices)=>Enumerable.Range(0,choices.Length).OrderBy(i=>Math.Abs(choices[i]-value)).First();
    void SaveAppearance(){settings.Save();ApplyAppearance();appearanceChanged();}
    public void ApplyAppearance(){GlassAppearance.ApplyBackdrop(this,settings);root.Background=GlassAppearance.PanelBrush(settings);}
}
