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
        if(argv.Length>2&&argv[1]=="--empty-space-probe"){EmptySpaceProbeServer.Run(argv[2]);Exit();return;}
        if(argv.Length>2&&argv[1]=="--guardian"){
            try{var parent=Process.GetProcessById(int.Parse(argv[2]));parent.WaitForExit();}catch{}
            using var gate=new Mutex(false,"Local\\StayView.Singleton");
            bool acquired=false;try{try{acquired=gate.WaitOne(1000);}catch(AbandonedMutexException){acquired=true;}if(acquired)PlacementStore.Recover();}finally{if(acquired)gate.ReleaseMutex();}
            Exit();return;
        }
        mutex=new Mutex(true,"Local\\StayView.Singleton",out bool first);if(!first){Exit();return;}
        // Launch splash first; the (synchronous) load continues once it has painted.
        // The splash is cosmetic: if it cannot be created, start without it.
        SplashWindow? splash=null;
        try{splash=new SplashWindow();splash.Activate();}catch(Exception ex){Log.Write("Splash failed: "+ex.Message);splash=null;}
        var startup=Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        startup.Interval=TimeSpan.FromMilliseconds(60);
        startup.IsRepeating=false;
        startup.Tick+=(_,_)=>{
            bool started=false;
            try
            {
                PlacementStore.Recover();
                tray=new TrayApp(this);
                try{guardian=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--guardian "+Environment.ProcessId){UseShellExecute=false,CreateNoWindow=true});}catch(Exception ex){Log.Write("Guardian failed: "+ex);}
                tray.OpenOnLaunch();
                started=true;
            }
            finally
            {
                // Keep "Taskview++" up until the app is fully started: the overview (with its
                // desktop bar and broker connection) is open AND has presented its first
                // frames. A failed start drops the splash straight away.
                if(splash!=null)
                {
                    if(!started)FadeSplash(splash);
                    else
                    {
                        int frames=0;var presented=new FrameTimer();
                        presented.Tick+=(_,_)=>{if(++frames<3)return;presented.Stop();FadeSplash(splash);};
                        presented.Start();
                    }
                }
            }
        };
        startup.Start();
    }
    static void FadeSplash(SplashWindow splash){try{splash.FadeOut();}catch(Exception ex){Log.Write("Splash fade failed: "+ex.Message);}}
    internal void ShutdownHelpers()
    {
        if(helpersStopped)return;
        helpersStopped=true;
        // Normal shutdown has already restored placements, so the crash guardian is no
        // longer needed. Helper owners shut down their own children. Do not sweep every
        // same-name process here: an unkillable UIAutomation helper can otherwise make a
        // clean UI exit wait on stale processes and look hung.
        VirtualDesktopService.ShutdownBroker();
        try
        {
            if(guardian!=null&&!guardian.HasExited){guardian.Kill();guardian.WaitForExit(1000);}
            guardian?.Dispose();guardian=null;
        }
        catch(Exception ex){Log.Write("Guardian shutdown: "+ex.Message);}
    }
}
