using System.Runtime.InteropServices;
using System.Text.Json;
namespace StayView.Core;

// Identifies one Windows sign-in: the boot time (to the minute) plus the logon session
// LUID. It changes after a reboot and after every sign-out/sign-in, and is stable while
// StayView is merely restarted.
public readonly record struct SessionKey(long BootMinuteUtc,long LogonId)
{
    // The boot time is derived from the wall clock, which NTP and RTC corrections nudge;
    // a few minutes of drift is still the same sign-in. The logon LUID must match exactly.
    public bool SameSession(SessionKey other)=>LogonId==other.LogonId&&Math.Abs(BootMinuteUtc-other.BootMinuteUtc)<=5;
    public static SessionKey Current()
    {
        long boot=(DateTime.UtcNow-TimeSpan.FromMilliseconds(Environment.TickCount64)).Ticks/TimeSpan.TicksPerMinute;
        return new(boot,LogonLuid());
    }
    static long LogonLuid()
    {
        try
        {
            if(!OpenProcessToken(GetCurrentProcess(),0x0008,out var token))return 0; // TOKEN_QUERY
            try
            {
                int size=Marshal.SizeOf<TOKEN_STATISTICS>();var buffer=Marshal.AllocHGlobal(size);
                try{return GetTokenInformation(token,10,buffer,size,out _)?Marshal.PtrToStructure<TOKEN_STATISTICS>(buffer).AuthenticationId:0;} // TokenStatistics
                finally{Marshal.FreeHGlobal(buffer);}
            }
            finally{CloseHandle(token);}
        }
        catch{return 0;}
    }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_STATISTICS{public long TokenId,AuthenticationId,ExpirationTime;public int TokenType,ImpersonationLevel;public uint DynamicCharged,DynamicAvailable,GroupCount,PrivilegeCount;public long ModifiedId;}
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(nint process,uint access,out nint token);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(nint token,int infoClass,nint info,int length,out int returned);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
}

// One app window as it was when the state was saved.
public sealed record SavedWindow(
    string Exe,string CommandLine,string ProcessKey,string? ExplorerFolder,
    string Class,string Title,Guid DesktopId,Native.WINDOWPLACEMENT Placement,int Z,
    bool Docked,bool DockLeft,
    bool Pinned,bool PinTileOnly,Guid PinHomeDesktop,Native.RECT PinBounds,bool PinMaximized,bool PinAllDesktops);

public sealed record WorkspaceFile(SessionKey Session,List<SavedWindow> Windows)
{
    // Set once a sign-in's relaunch has started, so a StayView restart (or crash) in the
    // same sign-in never relaunches the apps a second time.
    public SessionKey? RestoredFor {get;init;}
}

public static class WorkspaceStore
{
    static readonly JsonSerializerOptions json=new(){IncludeFields=true,WriteIndented=true};
    public static string DefaultPath=>Path.Combine(Settings.Folder,"workspace.json");
    public static WorkspaceFile? Load(string? path=null)
    {
        path??=DefaultPath;
        try
        {
            var file=File.Exists(path)?JsonSerializer.Deserialize<WorkspaceFile>(File.ReadAllText(path),json):null;
            // Valid JSON can still be missing fields: never let a half-written or hand-edited
            // file crash the start-up restore.
            if(file?.Windows==null)return null;
            var valid=file.Windows.Where(w=>w!=null&&!string.IsNullOrEmpty(w.Exe)&&w.Class!=null&&w.Title!=null&&w.CommandLine!=null&&w.ProcessKey!=null).ToList();
            return file with {Windows=valid};
        }
        catch(Exception ex){Log.Write("[workspace] load failed: "+ex.Message);return null;}
    }
    public static bool Save(WorkspaceFile file,string? path=null)
    {
        path??=DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path+".tmp",JsonSerializer.Serialize(file,json));
            File.Move(path+".tmp",path,true);
            return true;
        }
        catch(Exception ex){Log.Write("[workspace] save failed: "+ex.Message);return false;}
    }
}
