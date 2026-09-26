using Microsoft.UI.Dispatching;
using StayView.Core;
using System.Diagnostics;
namespace StayView;
// Relaunches missing apps on the first start after sign-in, then places their windows.
// Launching and polling run off the UI thread; placement runs on it (via the queue).
sealed class WorkspaceRestorer
{
    readonly DispatcherQueue queue;readonly WindowCatalog catalog;readonly Action<List<(SavedWindow Saved,nint Handle)>> place;
    public WorkspaceRestorer(DispatcherQueue queue,WindowCatalog catalog,Action<List<(SavedWindow Saved,nint Handle)>> place)
    {this.queue=queue;this.catalog=catalog;this.place=place;}

    public void Start(WorkspaceFile file,bool relaunch)
    {
        var pending=file.Windows.Where(w=>!ProcessInfo.IsPackaged(w.Exe)).ToList();
        Task.Run(async()=>{
            try
            {
                var used=new HashSet<nint>();
                var lastTitle=new Dictionary<nint,string>();
                // The catalog is not thread-safe: enumerate on the UI thread.
                Task<List<LiveWindow>> Live()=>OnUi(()=>catalog.Enumerate(true).Select(w=>{
                    Native.GetWindowThreadProcessId(w.Handle,out var pid);
                    return ProcessInfo.TryRead(pid,out var exe,out _,out _)?new LiveWindow(w.Handle,exe,Native.Class(w.Handle),w.Title):null;
                }).Where(l=>l!=null).Cast<LiveWindow>().ToList());
                void Take(List<(SavedWindow Saved,nint Handle)> matched)
                {
                    if(matched.Count==0)return;
                    foreach(var m in matched){pending.Remove(m.Saved);used.Add(m.Handle);}
                    queue.TryEnqueue(()=>place(matched));
                }
                // Only windows whose title held still since the previous poll: a launching app
                // shows generic titles first ("New Tab", "Visual Studio Code").
                async Task MatchStable(bool loose)
                {
                    var live=(await Live()).Where(l=>!used.Contains(l.Handle)).ToList();
                    var stable=live.Where(l=>lastTitle.TryGetValue(l.Handle,out var t)&&t==l.Title).ToList();
                    lastTitle=live.ToDictionary(l=>l.Handle,l=>l.Title);
                    Take(WorkspacePlan.Match(pending,stable,loose).ToList());
                }
                // Windows already open have settled titles: one strict pass, then (no relaunch)
                // one loose pass, and stop. No polling, so a window the user opens later is
                // never taken for a saved one.
                var existing=await Live();
                foreach(var l in existing)lastTitle[l.Handle]=l.Title;
                Take(WorkspacePlan.Match(pending,existing).ToList());
                if(!relaunch)
                {
                    Take(WorkspacePlan.Match(pending,existing.Where(l=>!used.Contains(l.Handle)).ToList(),loose:true).ToList());
                    return;
                }
                {
                    // Running = has a visible window. A background-only process (Edge startup
                    // boost, a tray app) still needs its window brought back.
                    var running=new HashSet<string>(existing.Select(l=>l.Exe),StringComparer.OrdinalIgnoreCase);
                    // Folders Explorer already reopened by itself are not launched again.
                    var openFolders=new HashSet<string>(await OnUi(()=>WorkspaceCapture.ExplorerFolders().Values.ToList()),StringComparer.OrdinalIgnoreCase);
                    foreach(var w in pending.Where(w=>!File.Exists(w.Exe)))Log.Write($"[workspace] skipped missing exe {w.Exe}");
                    var plan=WorkspacePlan.Plan(pending,running,openFolders,File.Exists);
                    foreach(var launch in plan)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(launch.Exe,launch.Arguments){UseShellExecute=true,WorkingDirectory=launch.WorkingDirectory})?.Dispose();
                            Log.Write($"[workspace] launched {launch.Exe} {launch.Arguments}");
                        }
                        catch(Exception ex){Log.Write($"[workspace] launch failed {launch.Exe}: {ex.Message}");}
                        await Task.Delay(300);
                    }
                }
                var until=Environment.TickCount64+30000;
                while(pending.Count>0&&Environment.TickCount64<until){await Task.Delay(500);await MatchStable(false);}
                // Last chance: pair any exe+class group that is exactly one saved to one live.
                if(pending.Count>0)await MatchStable(true);
                foreach(var w in pending)Log.Write($"[workspace] not matched: {w.Exe} '{w.Title}'");
            }
            catch(Exception ex){Log.Write("[workspace] restore failed: "+ex);}
        });
    }
    Task<T> OnUi<T>(Func<T> work)
    {
        var done=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!queue.TryEnqueue(()=>{try{done.SetResult(work());}catch(Exception ex){done.SetException(ex);}}))done.SetException(new InvalidOperationException("UI queue unavailable"));
        return done.Task;
    }
}
