using System.Diagnostics;
using System.Runtime.InteropServices;
using StayView.Core;

static class InteractionChecks
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
    static Native.POINT Center(Native.RECT r)=>new(){X=r.Left+r.Width/2,Y=r.Top+r.Height/2};
    static void Button(bool down)
    {
        var input=new Native.INPUT { Type=0,Mouse=new Native.MOUSEINPUT { DwFlags=down?2u:4u } };
        if(Native.SendInput(1,new[]{input},Marshal.SizeOf<Native.INPUT>())!=1)throw new Exception("Mouse input was rejected");
    }
    static async Task Click(Native.POINT p)
    { SetCursorPos(p.X,p.Y); await Task.Delay(60); Button(true); await Task.Delay(60); Button(false); }
    static async Task Drag(Native.POINT from,int dx,int dy)
    {
        SetCursorPos(from.X,from.Y); await Task.Delay(80); Button(true); await Task.Delay(80);
        try { for(int i=1;i<=20;i++){SetCursorPos(from.X+dx*i/20,from.Y+dy*i/20);await Task.Delay(16);} }
        finally { Button(false); }
        await Task.Delay(600);
    }
    static Native.RECT Bounds(nint h) { Native.GetWindowRect(h,out var r); return r; }
    static void Assert(bool value,string message) { if(!value)throw new Exception(message);Console.WriteLine("PASS: "+message); }
    static async Task Toggle(nint overlay) { Native.PostMessage(overlay,0x312,1,0);await Task.Delay(1100); }
    public static async Task Run()
    {
        nint overlay=0;
        for(int i=0;i<50 && overlay==0;i++)
        { Native.EnumWindows((h,_)=>{if(Native.Title(h)=="Taskview++")overlay=h;return true;},0);if(overlay==0)await Task.Delay(100); }
        if(overlay==0 || Native.IsWindowVisible(overlay))throw new Exception("Start StayView with its overview closed before running interaction-checks");
        Native.GetCursorPos(out var cursor);
        using var fixtures=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixtures"){UseShellExecute=false,CreateNoWindow=true})!;
        var owned=new List<nint>();
        try
        {
            for(int i=0;i<50 && owned.Count<3;i++)
            {
                await Task.Delay(100);owned.Clear();
                Native.EnumWindows((h,_)=>{Native.GetWindowThreadProcessId(h,out var pid);if(pid==fixtures.Id && Native.Title(h).StartsWith("StayView fixture"))owned.Add(h);return true;},0);
            }
            var target=owned.Single(h=>Native.Title(h)=="StayView fixture normal");
            var original=Bounds(target);
            var settings=Settings.Load();
            using var catalog=new WindowCatalog(new VirtualDesktopService());
            var monitor=Native.MonitorFromWindow(target,2);
            var group=catalog.Enumerate().Where(w=>w.Monitor==monitor && !(settings.DockMinimizedWindows && w.Minimized)).ToList();
            var sourceRects=group.Select(w=>Native.Placement(w.Handle).NormalPosition).ToList();
            if(settings.KeepMinisSameSize)
            {
                var aspects=sourceRects.Select(r=>Math.Max(1,r.Width)/(double)Math.Max(1,r.Height)).Order().ToList();
                var common=new Native.RECT(0,0,(int)Math.Round(Math.Clamp(aspects[aspects.Count/2],.6,2.2)*1000),1000);
                sourceRects=Enumerable.Repeat(common,group.Count).ToList();
            }
            var work=Native.WorkArea(monitor);double dpi=Native.MonitorScale(monitor);
            var area=Tiler.OverviewArea(work,8,dpi,settings.DesktopStripPosition);
            int gap=(int)Math.Round(28*dpi),longEdge=(int)Math.Round(settings.SmallWindowSize*dpi);
            var slots=settings.AutoArrange
                ? ThumbnailLayout.GridCells(Tiler.OverviewArea(work,28,dpi,settings.DesktopStripPosition),settings.AutoArrangeGrid,group.Count,gap)
                    .Take(group.Count).Select((cell,i)=>ThumbnailLayout.FitInCell(sourceRects[i],cell,longEdge)).ToList()
                : ThumbnailLayout.ArrangeSources(sourceRects,area,gap,longEdge);
            var tile=slots[group.FindIndex(w=>w.Handle==target)];
            await Toggle(overlay);
            Assert(Native.IsWindowVisible(overlay),"overview opened");
            if(settings.AnimateLayout)
            {
                await Click(Center(tile));await Task.Delay(20);
                await Click(new Native.POINT { X=area.Left+1,Y=area.Top+1 });await Task.Delay(450);
                Assert((Native.GetWindowLongPtr(overlay,-20).ToInt64()&8)!=0,"a rapid canvas press cancels pending focus without hiding the overview");
            }
            await Click(Center(tile)); await Task.Delay(900);
            Assert(Native.GetForegroundWindow()==target && !Native.IsIconic(target),"tile click focuses its real window");
            Assert(Bounds(target).Equals(original),"focus preserves real window geometry");
            await Toggle(overlay);
            if(!settings.AutoArrange)
            {
                int dx=tile.Right+120<=area.Right?120:-120;
                await Drag(Center(tile),dx,0);
                tile=ThumbnailLayout.Clamp(new Native.RECT(tile.Left+dx,tile.Top,tile.Width,tile.Height),area);
                Assert(Bounds(target).Equals(original),"dragging a miniature does not move the real window");
            }
            await Click(Center(tile));await Task.Delay(900);
            Assert(Native.GetForegroundWindow()==target,"dragged tile remains clickable at its dropped position");
            var before=Bounds(target);
            var caption=new Native.POINT { X=before.Left+before.Width/2,Y=before.Top+(int)(15*dpi) };
            await Drag(caption,96,64);
            var moved=Bounds(target);
            Assert(Math.Abs(moved.Left-before.Left-96)<=3 && Math.Abs(moved.Top-before.Top-64)<=3,"native title-bar drag follows the pointer");
            await Toggle(overlay);
            await Click(Center(tile));await Task.Delay(900);
            Assert(Native.GetForegroundWindow()==target && Bounds(target).Equals(moved),"shrink and refocus preserve both tile and real-window positions");
            var movedCaption=new Native.POINT { X=moved.Left+moved.Width/2,Y=moved.Top+(int)(15*dpi) };
            await Click(movedCaption);await Click(movedCaption);await Task.Delay(750);
            Assert((Native.GetWindowLongPtr(overlay,-20).ToInt64()&8)!=0,"caption double-click shrinks back to the overview");
            await Click(Center(tile));await Task.Delay(900);
            Assert(Native.GetForegroundWindow()==target,"caption shrink leaves the remembered tile responsive");
            if(settings.MaxBrowsedWindows>1)
            {
                var secondary=owned.Single(h=>Native.Title(h)=="StayView fixture maximized");
                Native.ShowWindowAsync(secondary,9);await Task.Delay(200);
                Native.ForceForeground(secondary);await Task.Delay(750);
                Assert(Native.GetForegroundWindow()==secondary,"a second real window can take focus while browsing");
                Native.PostMessage(secondary,0x112,0xF020,0);await Task.Delay(1400);
                Assert(Native.GetForegroundWindow()==target && !Native.IsIconic(target),"minimising a second focused window preserves the surviving window");
                Assert(Bounds(target).Equals(moved),"secondary minimise does not relocate the surviving window");
            }
            Native.PostMessage(target,0x112,0xF020,0);await Task.Delay(1400); // native minimize
            Assert(Native.IsWindowVisible(overlay),"minimising the focused window keeps the overview available");
            Assert(Native.IsIconic(target),"user-minimised source stays truly minimised behind its overview tile");
            if(settings.DockMinimizedWindows)
            {
                // The journal must retain minimize intent although DWM needs a restored source.
                var journal=System.Text.Json.JsonSerializer.Deserialize<List<SavedPlacement>>(File.ReadAllText(PlacementStore.Journal),new System.Text.Json.JsonSerializerOptions{IncludeFields=true})!;
                Assert(journal.Single(s=>s.Handle==target).Placement.ShowCmd is 2 or 6 or 7,"native minimize intent is retained for dismissal");
            }
            // A user click on that minimized tile must restore/focus it even for apps that
            // ignore SW_RESTORE but honor the system-menu/taskbar SC_RESTORE command.
            await Click(Center(tile));await Task.Delay(900);
            Assert(Native.GetForegroundWindow()==target && !Native.IsIconic(target),"clicking a minimized tile restores and focuses it");
            await Toggle(overlay);
            await Toggle(overlay);
            Assert(!Native.IsWindowVisible(overlay),"overview dismisses after minimise");
            Assert(!Native.IsIconic(target),"explicitly refocused window stays restored on dismissal");
            Console.WriteLine("PASS: Windows interaction smoke checks completed.");
        }
        finally
        {
            if(Native.IsWindowVisible(overlay)){await Toggle(overlay);if(Native.IsWindowVisible(overlay))await Toggle(overlay);}
            foreach(var h in owned)if(Native.IsWindow(h))Native.PostMessage(h,0x10,0,0);
            await Task.WhenAny(fixtures.WaitForExitAsync(),Task.Delay(2000));
            SetCursorPos(cursor.X,cursor.Y);
        }
    }
}
