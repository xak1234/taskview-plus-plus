using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using StayView.Core;

// Measures the real Windows 11 Task View so StayView's layout can be matched to it.
// Task View's layout is undocumented and its XAML content is not exposed to UI Automation
// (checked on 26200 with both the managed and the native UIA3 client), so it is measured
// from pixels: solid-colour borderless fixture windows are opened on a temporary virtual
// desktop, Task View is opened, the screen is captured and each colour's bounding box is
// the thumbnail rectangle. The user's own desktop is restored afterwards.
static class TaskViewCalibration
{
    [DllImport("user32.dll")] static extern void keybd_event(byte vk,byte scan,uint flags,nuint extra);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern nint GetDC(nint h);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint h,nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(nint dst,int x,int y,int w,int h,nint src,int sx,int sy,uint rop);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc,ref BitmapInfo info,uint usage,out nint bits,nint section,uint offset);
    [DllImport("gdi32.dll")] static extern nint CreateSolidBrush(uint colorref);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern ushort RegisterClass(ref WindowClass c);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint DefWindowProc(nint h,uint m,nint w,nint l);
    [DllImport("user32.dll")] static extern bool GetMessage(out Message m,nint h,uint min,uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message m);
    [DllImport("user32.dll")] static extern nint DispatchMessage(ref Message m);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint h);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct WindowClass{public uint Style;public Native.WndProc Proc;public int ClassExtra,WindowExtra;public nint Instance,Icon,Cursor,Background;public string? Menu;public string Name;}
    [StructLayout(LayoutKind.Sequential)] struct Message{public nint Hwnd;public uint Id;public nuint Wp;public nint Lp;public uint Time;public Native.POINT Point;public uint Private;}
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo{public int Size,Width,Height;public short Planes,BitCount;public int Compression,SizeImage,XPels,YPels,ClrUsed,ClrImportant;}

    const byte VK_LWIN=0x5B,VK_TAB=0x09,VK_ESCAPE=0x1B;
    const uint KEYUP=2,WM_APP_CLOSE=0x8001;
    static readonly Native.WndProc proc=Procedure;
    static readonly List<nint> live=[];

    // Saturated, mutually distant colours; the blurred/dimmed Task View backdrop never reaches them.
    static readonly (byte R,byte G,byte B)[] Palette=[
        (255,0,0),(0,255,0),(0,0,255),(255,255,0),(255,0,255),(0,255,255),(255,128,0),(128,0,255),
        (0,128,255),(255,0,128),(128,255,0),(0,255,128),(128,64,0),(0,128,64),(64,0,128),(255,255,255)];

    public sealed record Scenario(string Name,(int W,int H)[] Sizes);
    public sealed record Measured(int Index,string Colour,Native.RECT Source,Native.RECT Thumb,int Pixels,int HeaderTop);
    public sealed record Result(string Scenario,int ScreenWidth,int ScreenHeight,Native.RECT Work,double Scale,List<Measured> Windows);

    static Scenario[] Scenarios(int sw,int sh)
    {
        (int,int) R(double w,double h)=>((int)(sw*w),(int)(sh*h));
        var std=R(.42,.46);
        var list=new List<Scenario>();
        foreach(int n in new[]{1,2,3,4,5,6,7,8,9,12,16})list.Add(new($"uniform-{n}",Enumerable.Repeat(std,n).ToArray()));
        list.Add(new("tiny-1",[R(.10,.14)]));
        list.Add(new("tiny-3",[R(.10,.14),R(.10,.14),R(.10,.14)]));
        list.Add(new("huge-1",[R(.98,.94)]));
        list.Add(new("mixed-5",[R(.62,.60),R(.21,.55),R(.42,.42),R(.13,.16),R(.31,.55)]));
        list.Add(new("portrait-3",[R(.20,.70),R(.20,.70),R(.20,.70)]));
        list.Add(new("wide-3",[R(.80,.30),R(.80,.30),R(.80,.30)]));
        list.Add(new("mixed-8",[R(.42,.46),R(.20,.70),R(.80,.30),R(.10,.14),R(.62,.60),R(.31,.40),R(.42,.46),R(.25,.25)]));
        // Row-count rule discovery: more counts and shapes.
        foreach(int n in new[]{10,11,13,14,15})list.Add(new($"uniform-{n}",Enumerable.Repeat(std,n).ToArray()));
        var square=R(.28,.50);
        foreach(int n in new[]{2,3,4,5,6,8,10,12})list.Add(new($"square-{n}",Enumerable.Repeat(square,n).ToArray()));
        var tall=R(.20,.70);
        foreach(int n in new[]{4,5,6,8,10})list.Add(new($"portrait-{n}",Enumerable.Repeat(tall,n).ToArray()));
        var wide=R(.80,.30);
        foreach(int n in new[]{2,4,5,6})list.Add(new($"wide-{n}",Enumerable.Repeat(wide,n).ToArray()));
        var mid=R(.50,.40);
        foreach(int n in new[]{3,4,5,6,7})list.Add(new($"wide16-{n}",Enumerable.Repeat(mid,n).ToArray()));
        list.Add(new("mixed-4a",[R(.80,.30),R(.20,.70),R(.20,.70),R(.42,.46)]));
        list.Add(new("mixed-6a",[R(.20,.70),R(.80,.30),R(.42,.46),R(.20,.70),R(.28,.50),R(.62,.60)]));
        list.Add(new("mixed-7a",[R(.10,.14),R(.10,.14),R(.42,.46),R(.42,.46),R(.80,.30),R(.20,.70),R(.28,.50)]));
        return list.ToArray();
    }

    // Task View's desktop strip starts here on the calibration monitor (3840x2160, 100%,
    // 48 px taskbar); the window block is centred in the space above it.
    const int CalibrationStripTop=1928;

    // Holds TaskViewLayout to every recorded Task View layout.
    public static int CheckGolden(string dir)
    {
        int failures=0,files=0;double worst=0;
        foreach(var file in Directory.GetFiles(dir,"*.json").OrderBy(f=>f))
        {
            var result=JsonSerializer.Deserialize<Result>(File.ReadAllText(file),new JsonSerializerOptions{IncludeFields=true})!;
            files++;
            // Visual order (row-major) is Task View's MRU order by definition.
            var ordered=result.Windows.OrderBy(w=>w.Thumb.Top).ThenBy(w=>w.Thumb.Left).ToList();
            var monitor=new Native.RECT(0,0,result.ScreenWidth,result.ScreenHeight);
            var region=new Native.RECT(0,0,result.ScreenWidth,CalibrationStripTop);
            var predicted=TaskViewLayout.Layout(ordered.Select(w=>w.Source).ToList(),monitor,region,result.Scale);
            double error=0;
            for(int i=0;i<ordered.Count;i++)
            {
                var p=predicted[i];var t=ordered[i].Thumb;
                error=Math.Max(error,new[]{Math.Abs(p.Left-t.Left),Math.Abs(p.Top-t.Top),Math.Abs(p.Right-t.Right),Math.Abs(p.Bottom-t.Bottom)}.Max());
            }
            worst=Math.Max(worst,error);
            if(error>3){failures++;Console.WriteLine($"FAIL {result.Scenario}: max error {error}px");}
        }
        if(files==0){Console.WriteLine("FAIL: no Task View golden layouts found in "+dir);return 1;}
        Console.WriteLine(failures==0?$"PASS: TaskViewLayout matches all {files} recorded Task View layouts (worst {worst}px).":$"FAIL: {failures}/{files} layouts differ by more than 3px.");
        return failures==0?0:1;
    }

    public static async Task Run(string outDir,string? only)
    {
        Directory.CreateDirectory(outDir);
        int sw=GetSystemMetrics(0),sh=GetSystemMetrics(1);
        var origin=new Native.RECT(0,0,1,1);
        var primary=Native.MonitorFromRect(ref origin,1 /*PRIMARY*/);
        var work=Native.WorkArea(primary);double scale=Native.MonitorScale(primary);
        Console.WriteLine($"Primary {sw}x{sh} scale={scale} work={work.Left},{work.Top},{work.Width}x{work.Height} monitors={Native.Monitors().Count()}");
        var desktops=new VirtualDesktopService();
        var original=desktops.Current;
        if(original==Guid.Empty)throw new Exception("Virtual desktop broker unavailable; refusing to calibrate on the user's desktop.");
        if(!desktops.Create())throw new Exception("Could not create a temporary virtual desktop.");
        await Task.Delay(1200);
        var temp=desktops.Current;
        if(temp==original||temp==Guid.Empty)throw new Exception("Temporary desktop did not become current.");
        Console.WriteLine($"Temporary desktop {temp} (original {original})");
        try
        {
            foreach(var scenario in Scenarios(sw,sh).Where(s=>only==null||s.Name==only||(only.EndsWith('*')&&s.Name.StartsWith(only[..^1]))||(only=="new"&&!File.Exists(Path.Combine(outDir,s.Name+".json")))))
            {
                var result=await Measure(scenario,sw,sh,work,scale,outDir);
                File.WriteAllText(Path.Combine(outDir,scenario.Name+".json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{IncludeFields=true,WriteIndented=true}));
                Console.WriteLine($"{scenario.Name}: found {result.Windows.Count(w=>w.Pixels>0)}/{scenario.Sizes.Length}");
                foreach(var w in result.Windows)Console.WriteLine($"  #{w.Index} {w.Colour} src={w.Source.Width}x{w.Source.Height} thumb={w.Thumb.Left},{w.Thumb.Top},{w.Thumb.Width}x{w.Thumb.Height} header={w.Thumb.Top-w.HeaderTop} px={w.Pixels}");
            }
        }
        finally
        {
            desktops.Switch(original);
            await Task.Delay(800);
            desktops.Remove(temp);
            Console.WriteLine("Restored original desktop and removed the temporary one.");
        }
    }

    static async Task<Result> Measure(Scenario scenario,int sw,int sh,Native.RECT work,double scale,string outDir)
    {
        var windows=await Spawn(scenario.Sizes,work);
        try
        {
            await Task.Delay(600);
            var sources=windows.Select(h=>{Native.GetWindowRect(h,out var r);return r;}).ToList();
            Open();
            await Task.Delay(1600);
            var pixels=Capture(sw,sh);
            Close();
            await Task.Delay(700);
            if(scenario.Name is "uniform-4" or "mixed-5" or "mixed-8" or "uniform-9")SavePng(Path.Combine(outDir,scenario.Name+".png"),pixels,sw,sh);
            var measured=new List<Measured>();
            for(int i=0;i<windows.Count;i++)
            {
                var c=Palette[i];
                var (box,count)=Find(pixels,sw,sh,c);
                measured.Add(new(i,$"{c.R},{c.G},{c.B}",sources[i],box,count,count>0?HeaderTop(pixels,sw,box):0));
            }
            return new(scenario.Name,sw,sh,work,scale,measured);
        }
        finally{Destroy(windows);await Task.Delay(400);}
    }

    // Largest 4-connected region of the colour. Smaller matches (taskbar icons, the
    // desktop-strip preview of the same desktop) are ignored.
    static (Native.RECT Box,int Count) Find(byte[] bgra,int w,int h,(byte R,byte G,byte B) c)
    {
        const int tol=40;
        var match=new bool[w*h];
        for(int p=0;p<w*h;p++){int i=p*4;match[p]=Math.Abs(bgra[i+2]-c.R)<=tol&&Math.Abs(bgra[i+1]-c.G)<=tol&&Math.Abs(bgra[i]-c.B)<=tol;}
        var seen=new bool[w*h];var stack=new Stack<int>();
        Native.RECT best=default;int bestCount=0;
        for(int start=0;start<w*h;start++)
        {
            if(!match[start]||seen[start])continue;
            int minX=int.MaxValue,minY=int.MaxValue,maxX=-1,maxY=-1,count=0;
            seen[start]=true;stack.Push(start);
            while(stack.Count>0)
            {
                int p=stack.Pop();int x=p%w,y=p/w;count++;
                if(x<minX)minX=x;if(x>maxX)maxX=x;if(y<minY)minY=y;if(y>maxY)maxY=y;
                if(x>0&&match[p-1]&&!seen[p-1]){seen[p-1]=true;stack.Push(p-1);}
                if(x<w-1&&match[p+1]&&!seen[p+1]){seen[p+1]=true;stack.Push(p+1);}
                if(y>0&&match[p-w]&&!seen[p-w]){seen[p-w]=true;stack.Push(p-w);}
                if(y<h-1&&match[p+w]&&!seen[p+w]){seen[p+w]=true;stack.Push(p+w);}
            }
            if(count>bestCount){bestCount=count;best=new Native.RECT(minX,minY,maxX-minX+1,maxY-minY+1);}
        }
        return (best,bestCount);
    }
    // Task View draws an opaque title header directly above each thumbnail. Walk up from the
    // thumbnail's top edge (at 80% width, clear of the icon/title text) while the pixel stays
    // the header's colour, and report where that band starts.
    static int HeaderTop(byte[] bgra,int w,Native.RECT thumb)
    {
        int x=thumb.Left+thumb.Width*4/5,y=thumb.Top-2;
        if(y<1)return thumb.Top;
        int i0=(y*w+x)*4;
        int top=y;
        for(int yy=y-1;yy>=0;yy--)
        {
            int i=(yy*w+x)*4;
            if(Math.Abs(bgra[i]-bgra[i0])+Math.Abs(bgra[i+1]-bgra[i0+1])+Math.Abs(bgra[i+2]-bgra[i0+2])>18)break;
            top=yy;
        }
        return top;
    }

    // Borderless unowned app windows: the thumbnail is then exactly one solid colour.
    static Task<List<nint>> Spawn((int W,int H)[] sizes,Native.RECT work)
    {
        var ready=new TaskCompletionSource<List<nint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{
            try
            {
                var instance=Native.GetModuleHandle(null);
                var handles=new List<nint>();
                for(int i=0;i<sizes.Length;i++)
                {
                    var c=Palette[i];
                    var cls=new WindowClass{Proc=proc,Instance=instance,Background=CreateSolidBrush((uint)(c.R|(c.G<<8)|(c.B<<16))),Name=$"StayViewTaskViewCal{i}_{Environment.TickCount64}"};
                    RegisterClass(ref cls);
                    var (w,hgt)=sizes[i];
                    int x=work.Left+Math.Max(0,Math.Min(work.Width-w,40+i*37)),y=work.Top+Math.Max(0,Math.Min(work.Height-hgt,40+i*29));
                    var h=CreateWindowEx(0x40000 /*APPWINDOW*/,cls.Name,$"Calibration {i+1}",0x90000000 /*POPUP|VISIBLE*/,x,y,w,hgt,0,0,instance,0);
                    if(h==0)throw new Exception("Could not create calibration fixture");
                    SetForegroundWindow(h);
                    handles.Add(h);
                }
                lock(live)live.AddRange(handles);
                ready.SetResult(handles);
                while(GetMessage(out var m,0,0,0)){TranslateMessage(ref m);DispatchMessage(ref m);}
            }
            catch(Exception ex){ready.TrySetException(ex);}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
    static void Destroy(List<nint> windows){if(windows.Count>0)Native.PostMessage(windows[0],WM_APP_CLOSE,0,0);}
    static nint Procedure(nint h,uint message,nint wp,nint lp)
    {
        if(message==WM_APP_CLOSE)
        {
            List<nint> all;lock(live){all=[..live];live.Clear();}
            foreach(var w in all)DestroyWindow(w);
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProc(h,message,wp,lp);
    }

    public static void Open()
    {
        keybd_event(VK_LWIN,0,0,0);keybd_event(VK_TAB,0,0,0);
        keybd_event(VK_TAB,0,KEYUP,0);keybd_event(VK_LWIN,0,KEYUP,0);
    }
    public static void Close(){keybd_event(VK_ESCAPE,0,0,0);keybd_event(VK_ESCAPE,0,KEYUP,0);}

    static byte[] Capture(int w,int h)
    {
        var screen=GetDC(0);var dc=CreateCompatibleDC(screen);
        var info=new BitmapInfo{Size=40,Width=w,Height=-h,Planes=1,BitCount=32};
        var bitmap=CreateDIBSection(dc,ref info,0,out var bits,0,0);
        var old=SelectObject(dc,bitmap);
        try
        {
            BitBlt(dc,0,0,w,h,screen,0,0,0x00CC0020|0x40000000 /*SRCCOPY|CAPTUREBLT*/);
            var data=new byte[w*h*4];Marshal.Copy(bits,data,0,data.Length);return data;
        }
        finally{SelectObject(dc,old);DeleteObject(bitmap);DeleteDC(dc);ReleaseDC(0,screen);}
    }

    static void SavePng(string path,byte[] bgra,int w,int h)
    {
        using var raw=new MemoryStream();
        using(var z=new ZLibStream(raw,CompressionLevel.Fastest,true))
        {
            var line=new byte[1+w*3];
            for(int y=0;y<h;y++){for(int x=0;x<w;x++){int i=(y*w+x)*4;line[1+x*3]=bgra[i+2];line[2+x*3]=bgra[i+1];line[3+x*3]=bgra[i];}z.Write(line);}
        }
        using var file=File.Create(path);
        file.Write([137,80,78,71,13,10,26,10]);
        var ihdr=new byte[13];BE(ihdr,0,w);BE(ihdr,4,h);ihdr[8]=8;ihdr[9]=2;
        Chunk(file,"IHDR",ihdr);Chunk(file,"IDAT",raw.ToArray());Chunk(file,"IEND",[]);
    }
    static void BE(byte[] b,int o,int v){b[o]=(byte)(v>>24);b[o+1]=(byte)(v>>16);b[o+2]=(byte)(v>>8);b[o+3]=(byte)v;}
    static void Chunk(Stream s,string type,byte[] data)
    {
        var len=new byte[4];BE(len,0,data.Length);s.Write(len);
        var body=new byte[4+data.Length];System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body,0);data.CopyTo(body,4);s.Write(body);
        var crc=new byte[4];BE(crc,0,(int)Crc(body));s.Write(crc);
    }
    static uint Crc(byte[] data)
    {
        uint c=0xFFFFFFFF;
        foreach(var b in data){c^=b;for(int k=0;k<8;k++)c=(c&1)!=0?0xEDB88320^(c>>1):c>>1;}
        return c^0xFFFFFFFF;
    }
}
