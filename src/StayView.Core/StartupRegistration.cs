using Microsoft.Win32;
namespace StayView.Core;
// "Start with Windows": a per-user Run entry pointing at the running build. StayView.exe
// is a windowed app, so starting it this way shows no console.
public static class StartupRegistration
{
    const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void Apply(bool enabled,string exePath,string valueName="Taskview++")
    {
        try
        {
            using var key=Registry.CurrentUser.CreateSubKey(RunKey,true);
            if(enabled)key.SetValue(valueName,"\""+exePath+"\"",RegistryValueKind.String);
            else if(key.GetValue(valueName)!=null)key.DeleteValue(valueName,false);
        }
        catch(Exception ex){Log.Write("[startup] Run entry update failed: "+ex.Message);}
    }
    public static string? Current(string valueName="Taskview++")
    {
        try{using var key=Registry.CurrentUser.OpenSubKey(RunKey,false);return key?.GetValue(valueName) as string;}
        catch{return null;}
    }
}
