using Microsoft.UI.Xaml;
using StayView.Core;
using System.Diagnostics;
namespace StayView;
public partial class App:Application
{
    TrayApp? tray;Mutex? mutex;Process? guardian;bool helpersStopped;
    public App(){InitializeComponent();UnhandledException+=(_,e)=>{Log.Write(e.Exception.ToString());tray?.Stop();};AppDomain.CurrentDomain.ProcessExit+=(_,_)=>tray?.EmergencyRestore();}
    protected override void OnLaunched(LaunchActivatedEventArgs args){
        var argv=Environment.GetCommandLineArgs();
        if(argv.Length>2&&argv[1]=="--desktop-broker"){DesktopBroker.Run(argv[2]);Exit();return;}
        if(argv.Length>2&&argv[1]=="--guardian"){
            try{var parent=Process.GetProcessById(int.Parse(argv[2]));parent.WaitForExit();}catch{}
            using var gate=new Mutex(false,"Local\\StayView.Singleton");
            bool acquired=false;try{try{acquired=gate.WaitOne(1000);}catch(AbandonedMutexException){acquired=true;}if(acquired)PlacementStore.Recover();}finally{if(acquired)gate.ReleaseMutex();}
            Exit();return;
        }
        mutex=new Mutex(true,"Local\\StayView.Singleton",out bool first);if(!first){Exit();return;}
        PlacementStore.Recover();
        tray=new TrayApp(this);
        try{guardian=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--guardian "+Environment.ProcessId){UseShellExecute=false,CreateNoWindow=true});}catch(Exception ex){Log.Write("Guardian failed: "+ex);}
    }
    internal void ShutdownHelpers()
    {
        if(helpersStopped)return;
        helpersStopped=true;
        // Normal shutdown has already restored placements, so the crash guardian is no
        // longer needed. Stop the broker first, then the guardian, then sweep any stale
        // StayView helper/UI processes left in this Windows session by older builds.
        VirtualDesktopService.ShutdownBroker();
        try
        {
            if(guardian!=null&&!guardian.HasExited){guardian.Kill();guardian.WaitForExit(1000);}
            guardian?.Dispose();guardian=null;
        }
        catch(Exception ex){Log.Write("Guardian shutdown: "+ex.Message);}
        try
        {
            using var self=Process.GetCurrentProcess();
            foreach(var p in Process.GetProcessesByName(self.ProcessName))
            {
                using(p)
                {
                    if(p.Id==self.Id)continue;
                    try
                    {
                        if(p.SessionId!=self.SessionId)continue;
                        p.Kill();
                        p.WaitForExit(1000);
                    }
                    catch(Exception ex){Log.Write($"Stale process cleanup {p.Id}: {ex.Message}");}
                }
            }
        }
        catch(Exception ex){Log.Write("StayView process cleanup: "+ex.Message);}
    }
}
