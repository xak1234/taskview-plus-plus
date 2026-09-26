namespace StayView.Core;
public sealed record Launch(string Exe,string Arguments,string WorkingDirectory);
public sealed record LiveWindow(nint Handle,string Exe,string Class,string Title);

public static class WorkspacePlan
{
    public const int MaxLaunches=25;
    // A title within this share of edits of the saved one is the same window (a changed
    // "unsaved" marker, a counter); a generic first title ("New Tab") is not.
    const double StrictTitleDistance=0.2;

    // A fresh sign-in that has not been restored yet. The marker stops a second relaunch in
    // the same sign-in when StayView restarts or crashes before its next save.
    public static bool IsFreshSignIn(WorkspaceFile file,SessionKey now)
        =>!file.Session.SameSession(now)&&!(file.RestoredFor is SessionKey restored&&restored.SameSession(now));

    // One launch per saved process whose app is not running with a window; browsers once per
    // exe, bare (they restore their own session); Explorer once per saved folder that is not
    // already open. Missing, transient (installers, temp, downloads) and packaged exes are
    // skipped. Arguments are replayed only when safe. Capped.
    public static IReadOnlyList<Launch> Plan(IReadOnlyList<SavedWindow> saved,IReadOnlySet<string> runningExes,
        IReadOnlySet<string> openExplorerFolders,Func<string,bool> exeExists)
    {
        var runningNames=new HashSet<string>(runningExes.Select(Path.GetFileName).Where(n=>n!=null)!,StringComparer.OrdinalIgnoreCase);
        bool Running(string exe)=>runningExes.Contains(exe)||runningNames.Contains(Path.GetFileName(exe));
        var launches=new List<Launch>();
        var seenProcess=new HashSet<string>();
        var seenBrowser=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFolder=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var w in saved.OrderBy(w=>w.Z))
        {
            if(launches.Count>=MaxLaunches)break;
            if(string.IsNullOrEmpty(w.Exe)||ProcessInfo.IsPackaged(w.Exe)||IsTransientExe(w.Exe)||!exeExists(w.Exe))continue;
            string dir=Path.GetDirectoryName(w.Exe)??"";
            if(ProcessInfo.IsExplorer(w.Exe))
            {
                if(string.IsNullOrEmpty(w.ExplorerFolder)||openExplorerFolders.Contains(w.ExplorerFolder)||!seenFolder.Add(w.ExplorerFolder))continue;
                launches.Add(new(w.Exe,"\""+w.ExplorerFolder+"\"",dir));
                continue;
            }
            if(Running(w.Exe))continue;
            if(ProcessInfo.IsBrowser(w.Exe)){if(seenBrowser.Add(Path.GetFileName(w.Exe)))launches.Add(new(w.Exe,"",dir));continue;}
            if(seenProcess.Add(w.ProcessKey))launches.Add(new(w.Exe,SafeArguments(ArgumentsOf(w.CommandLine,w.Exe)),dir));
        }
        return launches;
    }
    // Installers, updaters and anything run from temp or Downloads are one-off: never relaunched.
    public static bool IsTransientExe(string exe)
    {
        var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        if(exe.StartsWith(temp+"\\",StringComparison.OrdinalIgnoreCase)||exe.Contains(@"\Downloads\",StringComparison.OrdinalIgnoreCase))return true;
        var name=Path.GetFileNameWithoutExtension(exe);
        return name.Contains("setup",StringComparison.OrdinalIgnoreCase)||name.Contains("install",StringComparison.OrdinalIgnoreCase)
            ||name.Contains("update",StringComparison.OrdinalIgnoreCase)||name.Contains("uninst",StringComparison.OrdinalIgnoreCase);
    }
    // The command line after the executable. With the saved exe path, argv0 is stripped by
    // that path (quoted or not, spaces included) rather than at the first space.
    public static string ArgumentsOf(string commandLine,string? exe=null)
    {
        var s=commandLine.TrimStart();
        if(!string.IsNullOrEmpty(exe))
        {
            if(s.StartsWith("\""+exe+"\"",StringComparison.OrdinalIgnoreCase))return s[(exe.Length+2)..].Trim();
            if(s.StartsWith(exe,StringComparison.OrdinalIgnoreCase))return s[exe.Length..].Trim();
        }
        if(s.StartsWith('"')){int end=s.IndexOf('"',1);return end<0?"":s[(end+1)..].Trim();}
        int space=s.IndexOf(' ');
        return space<0?"":s[(space+1)..].Trim();
    }
    // Drop arguments that would replay a one-shot action (a URL or protocol link, a meeting
    // join) or an internal/transient launch mode. "--single-argument" takes the rest as a URL.
    static readonly string[] transientSwitches={"--url=","--single-argument","--type=","--squirrel","--no-startup-window","--win-session-start","--processstart"};
    public static string SafeArguments(string arguments)
    {
        var kept=new List<string>();
        foreach(var token in Tokens(arguments))
        {
            var bare=token.Trim('"');
            if(bare.StartsWith("--single-argument",StringComparison.OrdinalIgnoreCase))break;
            if(bare.Contains("://")||transientSwitches.Any(t=>bare.StartsWith(t,StringComparison.OrdinalIgnoreCase)))continue;
            kept.Add(token);
        }
        return string.Join(' ',kept);
    }
    // Whitespace-separated tokens; a quoted run (with its quotes) is one token.
    static IEnumerable<string> Tokens(string s)
    {
        int i=0;
        while(i<s.Length)
        {
            while(i<s.Length&&char.IsWhiteSpace(s[i]))i++;
            if(i>=s.Length)yield break;
            int start=i;bool quoted=false;
            while(i<s.Length&&(quoted||!char.IsWhiteSpace(s[i]))){if(s[i]=='"')quoted=!quoted;i++;}
            yield return s[start..i];
        }
    }
    // Saved windows paired with live ones: same exe (case-insensitive) and class, closest
    // title first, one-to-one. Strict: the titles must be close. Loose (the final pass only):
    // an exe+class group that is exactly one saved to one live window is paired whatever
    // the titles; anything ambiguous is left unmatched rather than guessed.
    public static IReadOnlyList<(SavedWindow Saved,nint Handle)> Match(IReadOnlyList<SavedWindow> saved,IReadOnlyList<LiveWindow> live,bool loose=false)
    {
        var pairs=new List<(double Score,SavedWindow Saved,LiveWindow Live)>();
        foreach(var s in saved)
            foreach(var l in live)
                if(string.Equals(s.Exe,l.Exe,StringComparison.OrdinalIgnoreCase)&&s.Class==l.Class)
                {
                    double score=TitleDistance(s.Title,l.Title)/(double)Math.Max(1,Math.Max(Math.Min(s.Title.Length,80),Math.Min(l.Title.Length,80)));
                    if(score<=StrictTitleDistance)pairs.Add((score,s,l));
                }
        var result=new List<(SavedWindow,nint)>();
        var usedSaved=new HashSet<SavedWindow>(ReferenceEqualityComparer.Instance);
        var usedLive=new HashSet<nint>();
        foreach(var p in pairs.OrderBy(p=>p.Score))
        {
            if(usedSaved.Contains(p.Saved)||usedLive.Contains(p.Live.Handle))continue;
            usedSaved.Add(p.Saved);usedLive.Add(p.Live.Handle);
            result.Add((p.Saved,p.Live.Handle));
        }
        if(loose)
        {
            var restSaved=saved.Where(s=>!usedSaved.Contains(s)).ToList();
            var restLive=live.Where(l=>!usedLive.Contains(l.Handle)).ToList();
            foreach(var group in restSaved.GroupBy(s=>(Exe:s.Exe.ToLowerInvariant(),s.Class)))
            {
                var candidates=restLive.Where(l=>string.Equals(l.Exe,group.Key.Exe,StringComparison.OrdinalIgnoreCase)&&l.Class==group.Key.Class).ToList();
                if(group.Count()==1&&candidates.Count==1)result.Add((group.First(),candidates[0].Handle));
            }
        }
        return result;
    }
    // Levenshtein distance, bounded to keep long titles cheap.
    static int TitleDistance(string a,string b)
    {
        a=a.Length>80?a[..80]:a;b=b.Length>80?b[..80]:b;
        var prev=new int[b.Length+1];var cur=new int[b.Length+1];
        for(int j=0;j<=b.Length;j++)prev[j]=j;
        for(int i=1;i<=a.Length;i++)
        {
            cur[0]=i;
            for(int j=1;j<=b.Length;j++)cur[j]=Math.Min(Math.Min(cur[j-1]+1,prev[j]+1),prev[j-1]+(a[i-1]==b[j-1]?0:1));
            (prev,cur)=(cur,prev);
        }
        return prev[b.Length];
    }
}

// Every WM_QUERYENDSESSION saves (apps are all still open then) and suppresses StayView's own
// exit save, which would only see the partial set of windows left during shutdown. A
// cancelled shutdown (WM_ENDSESSION, wParam FALSE) re-enables the normal saves.
public sealed class WorkspaceSaveGate
{
    public bool SessionEnding{get;private set;}
    public bool BeginShutdownSave(){SessionEnding=true;return true;}
    public void SessionEndCancelled()=>SessionEnding=false;
    public bool AllowExitSave()=>!SessionEnding;
}
