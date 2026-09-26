using System.Runtime.InteropServices;
using System.Text;
namespace StayView.Core;
// Reads what is needed to relaunch an app: its image path, full command line (fast, no
// WMI, safe during shutdown) and a pid:start key that groups windows of one process.
// Same-user elevated processes CAN be read with limited access; IsElevated tells them apart
// so they are not relaunched (each would raise a UAC prompt at sign-in).
public static class ProcessInfo
{
    public static bool IsElevated(uint pid)
    {
        var h=OpenProcess(0x1000,false,pid); // QUERY_LIMITED_INFORMATION
        if(h==0)return false;
        try
        {
            if(!OpenProcessToken(h,0x0008,out var token))return false; // TOKEN_QUERY
            try{return GetTokenInformation(token,20,out int elevated,4,out _)&&elevated!=0;} // TokenElevation
            finally{CloseHandle(token);}
        }
        finally{CloseHandle(h);}
    }
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(nint process,uint access,out nint token);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(nint token,int infoClass,out int info,int length,out int returned);
    static readonly HashSet<string> browsers=new(StringComparer.OrdinalIgnoreCase){"chrome","msedge","firefox","brave","opera","vivaldi"};
    public static bool IsBrowser(string exe)=>browsers.Contains(Path.GetFileNameWithoutExtension(exe));
    public static bool IsExplorer(string exe)=>string.Equals(Path.GetFileName(exe),"explorer.exe",StringComparison.OrdinalIgnoreCase);
    public static bool IsPackaged(string exe)=>exe.Contains(@"\WindowsApps\",StringComparison.OrdinalIgnoreCase)
        ||string.Equals(Path.GetFileName(exe),"ApplicationFrameHost.exe",StringComparison.OrdinalIgnoreCase);

    public static bool TryRead(uint pid,out string exe,out string commandLine,out string processKey)
    {
        exe=commandLine=processKey="";
        var h=OpenProcess(0x1000|0x0010,false,pid); // QUERY_LIMITED_INFORMATION | VM_READ
        if(h==0)h=OpenProcess(0x1000,false,pid);
        if(h==0)return false;
        try
        {
            var path=new StringBuilder(1024);int size=path.Capacity;
            if(!QueryFullProcessImageName(h,0,path,ref size))return false;
            exe=path.ToString();
            processKey=GetProcessTimes(h,out long created,out _,out _,out _)?$"{pid}:{created}":$"{pid}:0";
            commandLine=ReadCommandLine(h)??"\""+exe+"\"";
            return true;
        }
        finally{CloseHandle(h);}
    }
    // NtQueryInformationProcess(ProcessCommandLineInformation = 60) returns a UNICODE_STRING
    // followed by its buffer. Needs only PROCESS_QUERY_LIMITED_INFORMATION on Windows 8.1+.
    static string? ReadCommandLine(nint h)
    {
        int length=0;
        NtQueryInformationProcess(h,60,0,0,ref length);
        if(length<=0)return null;
        var buffer=Marshal.AllocHGlobal(length);
        try
        {
            if(NtQueryInformationProcess(h,60,buffer,length,ref length)!=0)return null;
            ushort bytes=(ushort)Marshal.ReadInt16(buffer);
            var text=Marshal.ReadIntPtr(buffer,IntPtr.Size); // UNICODE_STRING.Buffer (after Length, MaximumLength + padding)
            return text==0?null:Marshal.PtrToStringUni(text,bytes/2);
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    [DllImport("kernel32.dll",SetLastError=true)] static extern nint OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(nint h,int flags,StringBuilder name,ref int size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(nint h,out long creation,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint h,int infoClass,nint info,int length,ref int returned);
}
