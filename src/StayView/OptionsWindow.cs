using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StayView.Core;

namespace StayView;

// Options panel. Visual language matches the overview: acrylic panel, glass "cards" per
// section, tiny uppercase monospace eyebrow labels (same as the desktop-card labels), one
// segmented pill control per setting (selected segment filled dark blue), and a quiet
// outlined Exit at the bottom.
sealed class OptionsWindow : Window
{
    readonly Settings settings;
    readonly Action appearanceChanged;
    readonly Action layoutChanged;
    readonly Action tileSizeChanged;
    readonly Action exitApp;
    readonly Grid root=new();
    readonly DispatcherTimer sizeCommitTimer=new(){Interval=TimeSpan.FromMilliseconds(120)};
    bool hasActivated;
    bool sizeDirty;
    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    public void BringToFront()
    {
        if(!Native.IsWindowVisible(Handle))return;
        Native.SetWindowPos(Handle,-1,0,0,0,0,0x13); // TOPMOST, no activation or geometry changes
    }
    public void Present(){Activate();BringToFront();}

    static readonly FontFamily Mono=new("Cascadia Mono, Consolas");
    const int CardOpacity=58;
    const int PillOpacity=78;

    public OptionsWindow(Settings settings,Action appearanceChanged,Action layoutChanged,Action tileSizeChanged,Action exitApp,nint owner)
    {
        this.settings=settings;this.appearanceChanged=appearanceChanged;this.layoutChanged=layoutChanged;this.tileSizeChanged=tileSizeChanged;this.exitApp=exitApp;
        Title="Taskview++ Options";
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
        sizeCommitTimer.Tick+=(_,_)=>CommitSize();
        Closed+=(_,_)=>CommitSize();

        root.RequestedTheme=ElementTheme.Dark;
        Content=root;

        // Header: eyebrow + title, close at top-right.
        var header=new Grid{Padding=new Thickness(22,18,14,6)};
        header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var titles=new StackPanel{Spacing=1};
        titles.Children.Add(Eyebrow("TASKVIEW++"));
        titles.Children.Add(new TextBlock{Text="Options",FontSize=22,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,Foreground=GlassAppearance.PrimaryBrush()});
        header.Children.Add(titles);
        var close=new Button{Content="\u2715",Width=30,Height=30,Padding=new Thickness(0),FontSize=12,VerticalAlignment=VerticalAlignment.Top,
            CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),BorderBrush=GlassAppearance.EdgeBrush(),
            Background=GlassAppearance.SurfaceBrush(PillOpacity),Foreground=GlassAppearance.SecondaryBrush()};
        close.Click+=(_,_)=>Close();
        Grid.SetColumn(close,1);header.Children.Add(close);

        var body=new StackPanel{Spacing=12,Padding=new Thickness(22,6,22,20)};
        var scroller=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        var layout=new Grid();
        layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        Grid.SetRow(scroller,1);
        layout.Children.Add(header);layout.Children.Add(scroller);
        root.Children.Add(layout);

        // APPEARANCE
        var appearance=Card("APPEARANCE");
        Row(appearance,"Theme",new[]{"Blue-black","Darker","More blue","Light"},(int)settings.BackgroundTheme,i=>{settings.BackgroundTheme=(BackgroundTheme)i;SaveAppearance();},stacked:true);
        // GlassOpacity is backdrop OPACITY, so High transparency is the lowest value.
        Row(appearance,"Transparency",new[]{"Low","Medium","High"},Nearest(settings.GlassOpacity,85,65,35),i=>{settings.GlassOpacity=new[]{85,65,35}[i];SaveAppearance();});
        body.Children.Add(appearance.Card);

        // LAYOUT
        var layoutCard=Card("LAYOUT");
        Row(layoutCard,"Auto-arrange",new[]{"On","Off"},settings.AutoArrange?0:1,i=>{settings.AutoArrange=i==0;settings.Save();layoutChanged();});
        Row(layoutCard,"Grid",new[]{"2\u00D72","4\u00D74","5\u00D75"},settings.AutoArrangeGrid==2?0:settings.AutoArrangeGrid==5?2:1,i=>{settings.AutoArrangeGrid=new[]{2,4,5}[i];settings.Save();layoutChanged();});
        Row(layoutCard,"Focused windows",new[]{"1","2","3","4","5"},Math.Clamp(settings.MaxBrowsedWindows,1,5)-1,i=>{settings.MaxBrowsedWindows=i+1;settings.Save();layoutChanged();});
        SliderRow(layoutCard);
        Row(layoutCard,"Dock minimized windows",new[]{"On","Off"},settings.DockMinimizedWindows?0:1,i=>{settings.DockMinimizedWindows=i==0;settings.Save();layoutChanged();});
        body.Children.Add(layoutCard.Card);

        // DESKTOPS
        var desktops=Card("DESKTOPS");
        Row(desktops,"Desktop bar",new[]{"Top","Bottom"},settings.DesktopStripPosition==DesktopStripPosition.Bottom?1:0,i=>{settings.DesktopStripPosition=i==1?DesktopStripPosition.Bottom:DesktopStripPosition.Top;settings.Save();layoutChanged();});
        Row(desktops,"Soft animation",new[]{"On","Off"},settings.AnimateLayout?0:1,i=>{settings.AnimateLayout=i==0;settings.Save();});
        Row(desktops,"Desktop transition",new[]{"Appear","Shift right","Shift left","Implode","Explode"},(int)settings.DesktopTransition,i=>{settings.DesktopTransition=(DesktopTransitionMode)i;settings.Save();},stacked:true);
        body.Children.Add(desktops.Card);

        // Quiet outlined Exit (the tray menu also exits).
        var exit=new Button{Content="Exit Taskview++",HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Center,
            Height=36,Margin=new Thickness(0,4,0,0),CornerRadius=new CornerRadius(8),FontSize=13,BorderThickness=new Thickness(1),
            BorderBrush=GlassAppearance.EdgeBrush(),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),Foreground=GlassAppearance.SecondaryBrush()};
        exit.Click+=(_,_)=>exitApp();
        body.Children.Add(exit);

        ApplyAppearance();
        FitToContent(header,body,scroller,windowScale,work);
    }

    void FitToContent(Grid header,StackPanel body,ScrollViewer scroller,double scale,Native.RECT work)
    {
        int maxWidth=Math.Max(1,work.Width-48),maxHeight=Math.Max(1,work.Height-48);
        int width=Math.Min((int)Math.Round(540*scale),maxWidth);
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

    static TextBlock Eyebrow(string text)=>new(){Text=text,FontSize=9,FontFamily=Mono,CharacterSpacing=60,Foreground=GlassAppearance.SecondaryBrush()};

    // A glass card with an eyebrow title and a list of rows separated by hairlines.
    sealed record Section(Border Card,StackPanel Rows);
    static Section Card(string title)
    {
        var rows=new StackPanel{Spacing=0};
        var inner=new StackPanel{Spacing=8};
        inner.Children.Add(Eyebrow(title));
        inner.Children.Add(rows);
        return new(GlassAppearance.Surface(inner,CardOpacity,12,new Thickness(16,12,16,10)),rows);
    }
    void AddRow(Section section,UIElement row)
    {
        if(section.Rows.Children.Count>0)
            section.Rows.Children.Add(new Border{Height=1,Background=GlassAppearance.EdgeBrush(),Opacity=0.55,Margin=new Thickness(0,8,0,8)});
        section.Rows.Children.Add(row);
    }

    // Label left, segmented pill right; `stacked` puts a full-width pill under the label
    // for option sets too long to sit inline.
    void Row(Section section,string label,string[] labels,int selected,Action<int> changed,bool stacked=false)
    {
        var pill=Segmented(labels,selected,changed,stacked);
        if(stacked)
        {
            var col=new StackPanel{Spacing=7};
            col.Children.Add(new TextBlock{Text=label,FontSize=13,Foreground=GlassAppearance.PrimaryBrush()});
            col.Children.Add(pill);
            AddRow(section,col);
            return;
        }
        var grid=new Grid{ColumnSpacing=12};
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        grid.Children.Add(new TextBlock{Text=label,FontSize=13,Foreground=GlassAppearance.PrimaryBrush(),VerticalAlignment=VerticalAlignment.Center});
        Grid.SetColumn(pill,1);grid.Children.Add(pill);
        AddRow(section,grid);
    }

    static Border Segmented(string[] labels,int selected,Action<int> changed,bool stretch)
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
            }
        }
        for(int i=0;i<labels.Length;i++)
        {
            int index=i;
            var b=new Button{Content=labels[i],Height=28,MinWidth=stretch?0:60,Padding=new Thickness(12,0,12,0),FontSize=12,
                HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Center,
                CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(0)};
            b.Click+=(_,_)=>{selected=index;Paint();changed(index);};
            Grid.SetColumn(b,i);track.Children.Add(b);buttons.Add(b);
        }
        Paint();
        return new Border{Child=track,Padding=new Thickness(2),CornerRadius=new CornerRadius(8),
            Background=GlassAppearance.SurfaceBrush(PillOpacity),BorderBrush=GlassAppearance.EdgeBrush(),BorderThickness=new Thickness(1),
            HorizontalAlignment=stretch?HorizontalAlignment.Stretch:HorizontalAlignment.Right};
    }

    void SliderRow(Section section)
    {
        var col=new StackPanel{Spacing=2};
        var header=new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        header.Children.Add(new TextBlock{Text="Small window size",FontSize=13,Foreground=GlassAppearance.PrimaryBrush()});
        var valueText=new TextBlock{Text=$"{settings.SmallWindowSize} DIP",FontSize=11,FontFamily=Mono,Foreground=GlassAppearance.SecondaryBrush(),VerticalAlignment=VerticalAlignment.Center};
        Grid.SetColumn(valueText,1);header.Children.Add(valueText);
        col.Children.Add(header);
        var slider=new Slider{
            Minimum=Settings.SmallWindowSizeMin,Maximum=Settings.SmallWindowSizeMax,
            StepFrequency=10,TickFrequency=50,
            SnapsTo=Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues,
            Value=settings.SmallWindowSize,HorizontalAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(0,-4,0,-6)};
        slider.ValueChanged+=(_,e)=>{
            int next=(int)Math.Round(e.NewValue/10d)*10;
            next=Math.Clamp(next,Settings.SmallWindowSizeMin,Settings.SmallWindowSizeMax);
            valueText.Text=$"{next} DIP";
            if(settings.SmallWindowSize==next)return;
            settings.SmallWindowSize=next;
            sizeDirty=true;
            sizeCommitTimer.Stop();sizeCommitTimer.Start();
        };
        col.Children.Add(slider);
        AddRow(section,col);
    }

    static int Nearest(int value,params int[] choices)=>Enumerable.Range(0,choices.Length).OrderBy(i=>Math.Abs(choices[i]-value)).First();
    void CommitSize()
    {
        sizeCommitTimer.Stop();
        if(!sizeDirty)return;
        sizeDirty=false;settings.Save();tileSizeChanged();
    }
    void SaveAppearance(){settings.Save();ApplyAppearance();appearanceChanged();}
    public void ApplyAppearance(){GlassAppearance.ApplyBackdrop(this,settings);root.Background=GlassAppearance.PanelBrush(settings);}
}
