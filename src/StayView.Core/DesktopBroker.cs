using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace StayView.Core;

// Undocumented COM never runs in the UI process. An ABI access violation terminates
// only this pipe server; the client disables desktop controls and keeps tiling.
public static class DesktopBroker
{
    public static void Run(string pipeName) {
        try {
            using var pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.CurrentUserOnly);
            var connection=pipe.WaitForConnectionAsync();
            if(!connection.Wait(10000))return;
            using var reader=new StreamReader(pipe);using var writer=new StreamWriter(pipe){AutoFlush=true};
            var service=new DirectVirtualDesktopService();
            string? line;
            while((line=reader.ReadLine())!=null){
                try{
                    var request=JsonSerializer.Deserialize<DesktopRequest>(line)!;
                    object? result=request.Command switch {
                        "available"=>service.Available,
                        "list"=>service.List(),
                        "current"=>service.Current,
                        "switch"=>service.Switch(request.Id),
                        "create"=>service.Create(),
                        "remove"=>service.Remove(request.Id),
                        "moveDesktop"=>service.MoveDesktop(request.Id,request.Index),
                        "move"=>service.Move((nint)request.Handle,request.Id),
                        "pin"=>service.Pin((nint)request.Handle),
                        _=>false
                    };
                    writer.WriteLine(JsonSerializer.Serialize(result));
                }catch(IOException){break;}
                catch(Exception ex){
                    Log.Write("Desktop broker: "+ex.Message);
                    try{writer.WriteLine("null");}catch(IOException){break;}
                }
            }
        }catch(IOException){/* The UI process closed its pipe; broker shutdown is normal. */}
        catch(ObjectDisposedException){/* Process teardown raced pipe disposal. */}
    }
}
public sealed record DesktopRequest(string Command,Guid Id=default,long Handle=0,int Index=-1);
sealed class DesktopConnection : IDisposable
{
    NamedPipeClientStream? pipe;StreamReader? reader;StreamWriter? writer;Process? process;
    public bool Ready {get;private set;}
    public DesktopConnection(){
        if(Environment.OSVersion.Version.Build is <26100 or >26200)return;
        try{
            string name="StayView.Desktops."+Guid.NewGuid().ToString("N");
            process=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--desktop-broker "+name){UseShellExecute=false,CreateNoWindow=true});
            pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            pipe.Connect(1500);reader=new(pipe);writer=new(pipe){AutoFlush=true};Ready=true;
            Ready=Call<bool>(new("available"));
        }catch(Exception ex){Fail(ex);}
    }
    void Fail(Exception ex)
    {
        Ready=false;Log.Write("Desktop broker connection failed: "+ex.Message);
        try{writer?.Dispose();}catch{}try{reader?.Dispose();}catch{}try{pipe?.Dispose();}catch{}
        try{if(process!=null&&!process.HasExited)process.Kill();}catch{}
    }
    public T? Call<T>(DesktopRequest request){
        lock(this){
            if(!Ready)return default;
            try{
                writer!.WriteLine(JsonSerializer.Serialize(request));
                var read=reader!.ReadLineAsync();
                // Private shell calls should return in a few milliseconds. Bound a broken
                // broker tightly so the UI cannot appear frozen for several seconds; the
                // service wrapper will create a fresh broker on a later call.
                if(!read.Wait(600)||read.Result==null)throw new IOException("Desktop broker stopped responding.");
                return JsonSerializer.Deserialize<T>(read.Result);
            }catch(Exception ex){Fail(ex);return default;}
        }
    }
    public void Dispose()
    {
        lock(this)
        {
            Ready=false;
            try{writer?.Dispose();}catch{}
            try{reader?.Dispose();}catch{}
            try{pipe?.Dispose();}catch{}
            try
            {
                if(process!=null&&!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch{}
            try{process?.Dispose();}catch{}
            writer=null;reader=null;pipe=null;process=null;
        }
    }
}
public sealed class VirtualDesktopService
{
    static readonly object connectionGate=new();
    static DesktopConnection? connection;
    static long retryAfter;
    readonly IVirtualDesktopManager? standard;
    public VirtualDesktopService(){try{standard=(IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new("AA509086-5CA9-4C25-8F95-589D3C07B48A"))!)!;}catch{}}
    static DesktopConnection? Connection()
    {
        if(Environment.OSVersion.Version.Build is <26100 or >26200)return null;
        lock(connectionGate)
        {
            if(connection?.Ready==true)return connection;
            long now=Environment.TickCount64;if(now<retryAfter)return null;
            try{connection?.Dispose();}catch{}connection=null;
            var next=new DesktopConnection();
            if(!next.Ready){next.Dispose();retryAfter=now+1500;return null;}
            connection=next;retryAfter=0;return connection;
        }
    }
    static T? BrokerCall<T>(DesktopRequest request)
    {
        var c=Connection();if(c==null)return default;
        var result=c.Call<T>(request);
        if(!c.Ready)
        {
            lock(connectionGate)
            {
                if(ReferenceEquals(connection,c)){try{connection.Dispose();}catch{}connection=null;retryAfter=Environment.TickCount64+500;}
            }
        }
        return result;
    }
    public bool Available=>Connection()!=null;
    public string Status=>Available?"Windows virtual desktops":"Desktop controls unavailable on this Windows build; current-desktop tiling remains available.";
    public Guid Current=>BrokerCall<Guid>(new("current"));
    public bool IsCurrent(nint h){try{return standard?.IsWindowOnCurrentVirtualDesktop(h)??true;}catch{return true;}}
    public Guid WindowDesktop(nint h){try{return standard?.GetWindowDesktopId(h)??Guid.Empty;}catch{return Guid.Empty;}}
    public IReadOnlyList<DesktopInfo> List()=>BrokerCall<List<DesktopInfo>>(new("list"))??[];
    public bool PinOwnWindow(nint h)=>BrokerCall<bool>(new("pin",Handle:h.ToInt64()));
    public bool Switch(Guid id)=>BrokerCall<bool>(new("switch",id));
    public bool Create()=>BrokerCall<bool>(new("create"));
    public bool Remove(Guid id)=>BrokerCall<bool>(new("remove",id));
    public bool MoveDesktop(Guid id,int index)=>BrokerCall<bool>(new("moveDesktop",id,Index:index));
    public bool Move(nint h,Guid id)=>BrokerCall<bool>(new("move",id,h.ToInt64()));
    public void MoveOwnWindow(nint h,Guid id){try{if(id!=Guid.Empty)standard?.MoveWindowToDesktop(h,ref id);}catch(Exception ex){Log.Write(ex.Message);}}
    public static void ShutdownBroker(){lock(connectionGate){try{connection?.Dispose();}catch(Exception ex){Log.Write("Desktop broker shutdown: "+ex.Message);}connection=null;retryAfter=long.MaxValue;}}
}
