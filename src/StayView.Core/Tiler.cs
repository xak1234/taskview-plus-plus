namespace StayView.Core;
public enum TileRole { Hero,Satellite,Dock }
public sealed record Tile(AppWindow Window,Native.RECT Cell,Native.RECT Client,bool Preview,TileRole Role);
public static class Tiler
{
    public static IReadOnlyList<Native.RECT> Layout(int count,Native.RECT area,int gap,int minWidth,nint expanded,IReadOnlyList<AppWindow> windows) {
        if(count==0)return [];
        int selected=expanded==0?-1:windows.ToList().FindIndex(x=>x.Handle==expanded);
        if(selected>=0&&count>1&&area.Width>650) {
            int large=(int)(area.Width*.67);
            var side=new Native.RECT(area.Left+large+gap,area.Top,area.Width-large-gap,area.Height);
            var rest=Grid(count-1,side,gap,minWidth);var output=new List<Native.RECT>();int j=0;
            for(int i=0;i<count;i++)output.Add(i==selected?new(area.Left,area.Top,large,area.Height):rest[j++]);return output;
        }
        return Grid(count,area,gap,minWidth);
    }
    static List<Native.RECT> Grid(int n,Native.RECT a,int gap,int minWidth) {
        int best=1;double score=double.MaxValue;
        for(int cols=1;cols<=n;cols++) {int rows=(n+cols-1)/cols;double w=(a.Width-(cols-1)*gap)/(double)cols,h=(a.Height-(rows-1)*gap)/(double)rows;
            if(w<=0||h<=36)continue;
            double s=Math.Abs(Math.Log(w/(h-36)/1.6))+(cols*rows-n)*.12+Math.Max(0,minWidth-w)/minWidth;
            if(s<score){score=s;best=cols;}
        }
        int rowCount=(n+best-1)/best;int width=Math.Max(1,(a.Width-(best-1)*gap)/best),height=Math.Max(1,(a.Height-(rowCount-1)*gap)/rowCount);
        return Enumerable.Range(0,n).Select(i=>new Native.RECT(a.Left+(i%best)*(width+gap),a.Top+(i/best)*(height+gap),width,height)).ToList();
    }
    public static int CompactHeight(int count,int width,int gap,int preferredWidth,int rowHeight) {
        if(count<=0)return 0;
        int cols=Math.Min(count,Math.Max(1,(width+gap)/Math.Max(1,preferredWidth+gap)));
        int rows=(count+cols-1)/cols;
        return rows*rowHeight+(rows-1)*gap;
    }
    public static IReadOnlyList<Native.RECT> Compact(int count,Native.RECT area,int gap,int preferredWidth,int rowHeight) {
        if(count<=0)return [];
        int cols=Math.Min(count,Math.Max(1,(area.Width+gap)/Math.Max(1,preferredWidth+gap)));
        int rows=(count+cols-1)/cols;
        int width=Math.Max(1,(area.Width-(cols-1)*gap)/cols);
        int height=Math.Max(1,Math.Min(rowHeight,(area.Height-(rows-1)*gap)/rows));
        return Enumerable.Range(0,count).Select(i=>new Native.RECT(area.Left+(i%cols)*(width+gap),area.Top+(i/cols)*(height+gap),width,height)).ToList();
    }
    public static DockPosition ResolveDockPosition(DockPosition requested,Native.RECT area)=>requested==DockPosition.Auto?(area.Width>=area.Height*1.2?DockPosition.Right:DockPosition.Bottom):requested;
    public static Native.RECT OverviewArea(Native.RECT work,int gap,double dpi,DesktopStripPosition stripPosition) {
        int top=(int)Math.Round((stripPosition==DesktopStripPosition.Bottom?42:244)*dpi);
        int bottom=(int)Math.Round((stripPosition==DesktopStripPosition.Bottom?244:42)*dpi);
        return new Native.RECT(work.Left+gap,work.Top+top,Math.Max(1,work.Width-2*gap),Math.Max(1,work.Height-top-bottom));
    }
    public static Native.RECT CanvasArea(Native.RECT work,double dpi,DesktopStripPosition stripPosition) {
        int top=(int)Math.Round((stripPosition==DesktopStripPosition.Bottom?24:180)*dpi);
        int bottom=(int)Math.Round((stripPosition==DesktopStripPosition.Bottom?180:49)*dpi);
        return new Native.RECT(work.Left+8,work.Top+top,Math.Max(1,work.Width-16),Math.Max(1,work.Height-top-bottom));
    }
    public static (Native.RECT Hero,Native.RECT Dock) DockLayout(Native.RECT area,int gap,DockPosition position,int satelliteCount) {
        if(satelliteCount<=0)return(area,new Native.RECT(area.Right,area.Bottom,0,0));
        position=ResolveDockPosition(position,area);
        if(position==DockPosition.Right){
            int dock=Math.Clamp((int)(area.Width*.27),260,520);dock=Math.Min(dock,Math.Max(1,area.Width/2));
            return(new Native.RECT(area.Left,area.Top,Math.Max(1,area.Width-dock-gap),area.Height),new Native.RECT(area.Right-dock,area.Top,dock,area.Height));
        }
        int height=Math.Clamp((int)(area.Height*.29),180,340);height=Math.Min(height,Math.Max(1,area.Height/2));
        return(new Native.RECT(area.Left,area.Top,area.Width,Math.Max(1,area.Height-height-gap)),new Native.RECT(area.Left,area.Bottom-height,area.Width,height));
    }
    public static IReadOnlyList<Native.RECT> DockSlots(IReadOnlyList<Native.RECT> sources,Native.RECT dock,int gap,DockPosition position,bool sameSize=false,int minimumWidth=120,int minimumHeight=80) {
        if(sources.Count==0)return [];
        position=ResolveDockPosition(position,dock);
        var result=new List<Native.RECT>(sources.Count);
        if(sameSize){
            const double targetAspect=1.5;
            if(position==DockPosition.Bottom){
                int availableH=Math.Max(1,dock.Height-2*gap);int proposedW=Math.Max(1,(dock.Width-gap*(sources.Count+1))/sources.Count);
                int h=Math.Min(availableH,Math.Max(minimumHeight,(int)Math.Round(proposedW/targetAspect)));int w=Math.Max(minimumWidth,(int)Math.Round(h*targetAspect));
                if(w<minimumWidth){w=minimumWidth;h=Math.Max(minimumHeight,(int)Math.Round(w/targetAspect));}
                int y=dock.Top+(dock.Height-h)/2;for(int i=0;i<sources.Count;i++)result.Add(new Native.RECT(dock.Left+gap+i*(w+gap),y,w,h));
            }else{
                int availableW=Math.Max(1,dock.Width-2*gap);int proposedH=Math.Max(1,(dock.Height-gap*(sources.Count+1))/sources.Count);
                int w=Math.Min(availableW,Math.Max(minimumWidth,(int)Math.Round(proposedH*targetAspect)));int h=Math.Max(minimumHeight,(int)Math.Round(w/targetAspect));
                if(h<minimumHeight){h=minimumHeight;w=Math.Max(minimumWidth,(int)Math.Round(h*targetAspect));}
                int x=dock.Left+(dock.Width-w)/2;for(int i=0;i<sources.Count;i++)result.Add(new Native.RECT(x,dock.Top+gap+i*(h+gap),w,h));
            }
            return result;
        }
        int cursor=position==DockPosition.Bottom?dock.Left+gap:dock.Top+gap;
        for(int i=0;i<sources.Count;i++){
            var source=sources[i];double aspect=Math.Max(.15,source.Width/(double)Math.Max(1,source.Height));
            if(position==DockPosition.Bottom){int h=Math.Max(minimumHeight,dock.Height-2*gap);int w=Math.Max(minimumWidth,(int)Math.Round(h*aspect));result.Add(new Native.RECT(cursor,dock.Top+(dock.Height-h)/2,w,h));cursor+=w+gap;}
            else{int w=Math.Max(minimumWidth,dock.Width-2*gap);int h=Math.Max(minimumHeight,(int)Math.Round(w/aspect));result.Add(new Native.RECT(dock.Left+(dock.Width-w)/2,cursor,w,h));cursor+=h+gap;}
        }
        return result;
    }
    public static (Native.RECT Hero,Native.RECT SatelliteArea) SatelliteStage(Native.RECT area,int gap,int count) {
        if(count<=0)return(area,new Native.RECT(area.Right,area.Bottom,0,0));
        if(area.Width>=area.Height*1.15){int side=Math.Clamp((int)(area.Width*.31),260,620);return(new Native.RECT(area.Left,area.Top,Math.Max(1,area.Width-side-gap),area.Height),new Native.RECT(area.Right-side,area.Top,side,area.Height));}
        int bottom=Math.Clamp((int)(area.Height*.34),180,420);return(new Native.RECT(area.Left,area.Top,area.Width,Math.Max(1,area.Height-bottom-gap)),new Native.RECT(area.Left,area.Bottom-bottom,area.Width,bottom));
    }
    public static IReadOnlyList<Native.RECT> SatelliteLayout(IReadOnlyList<Native.RECT> sources,Native.RECT area,int gap,int percent,bool sameSize=false,int minimumWidth=120,int minimumHeight=80) {
        if(sources.Count==0)return [];
        percent=Math.Clamp(percent,15,40);
        bool horizontal=area.Width>=area.Height;
        int cols=horizontal?sources.Count:Math.Max(1,(int)Math.Ceiling(Math.Sqrt(sources.Count))),rows=(sources.Count+cols-1)/cols;
        int cellW=Math.Max(1,(area.Width-(cols-1)*gap)/cols),cellH=Math.Max(1,(area.Height-(rows-1)*gap)/rows);
        var result=new List<Native.RECT>(sources.Count);
        if(sameSize){
            var orderedW=sources.Select(r=>Math.Max(1,r.Width)).OrderBy(v=>v).ToArray();var orderedH=sources.Select(r=>Math.Max(1,r.Height)).OrderBy(v=>v).ToArray();
            int desiredW=Math.Max(minimumWidth,(int)Math.Round(orderedW[orderedW.Length/2]*(percent/100d)));int desiredH=Math.Max(minimumHeight,(int)Math.Round(orderedH[orderedH.Length/2]*(percent/100d)));
            double fit=Math.Min(1,Math.Min(cellW/(double)Math.Max(1,desiredW),cellH/(double)Math.Max(1,desiredH)));int w=Math.Max(1,(int)Math.Round(desiredW*fit)),h=Math.Max(1,(int)Math.Round(desiredH*fit));
            for(int i=0;i<sources.Count;i++){int x=area.Left+(i%cols)*(cellW+gap)+(cellW-w)/2,y=area.Top+(i/cols)*(cellH+gap)+(cellH-h)/2;result.Add(new Native.RECT(x,y,w,h));}
            return result;
        }
        for(int i=0;i<sources.Count;i++){
            var source=sources[i];double scale=percent/100d;int w=Math.Max(120,(int)Math.Round(source.Width*scale)),h=Math.Max(90,(int)Math.Round(source.Height*scale));
            double fit=Math.Min(1,Math.Min(cellW/(double)Math.Max(1,w),cellH/(double)Math.Max(1,h)));w=Math.Max(1,(int)(w*fit));h=Math.Max(1,(int)(h*fit));
            int x=area.Left+(i%cols)*(cellW+gap)+(cellW-w)/2,y=area.Top+(i/cols)*(cellH+gap)+(cellH-h)/2;result.Add(new Native.RECT(x,y,w,h));
        }
        return result;
    }
    public static bool Move(nint h,Native.RECT r) {
        if(Native.IsHungAppWindow(h))return false;
        if(Native.IsIconic(h)||Native.IsZoomed(h))Native.ShowWindow(h,9);
        bool ok=Native.SetWindowPos(h,0,r.Left,r.Top,r.Width,r.Height,0x14);
        Native.GetWindowRect(h,out var actual);
        return ok&&Math.Abs(actual.Left-r.Left)<16&&Math.Abs(actual.Top-r.Top)<16&&Math.Abs(actual.Width-r.Width)<16&&Math.Abs(actual.Height-r.Height)<16;
    }
}
