using System.Diagnostics;
using System.Runtime.InteropServices;
namespace StayView.Core;

// A Windows job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Helper processes (desktop
// broker, empty-space probe) are assigned to it, so they end whenever the Taskview++ UI
// process ends: a clean exit, a crash, or being killed. Before this, a helper stuck in a
// COM or UI Automation call never noticed its pipe had closed and was left orphaned.
// The crash guardian is deliberately NOT tracked: it must outlive a crash to restore windows.
public sealed class ChildProcessJob : IDisposable
{
    // The UI process's job. Its handle is never closed explicitly; the OS closes it when
    // the process ends, which is exactly when the helpers must go.
    public static readonly ChildProcessJob Helpers = new();

    nint handle;
    public ChildProcessJob()
    {
        handle = CreateJobObject(0, null);
        if (handle == 0) { Log.Write("[job] CreateJobObject failed: " + Marshal.GetLastWin32Error()); return; }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            Log.Write("[job] SetInformationJobObject failed: " + Marshal.GetLastWin32Error());
            CloseHandle(handle); handle = 0;
        }
    }

    // Returns false if the process could not be tracked (it is still usable, only not
    // guaranteed to die with the owner).
    public bool Track(Process? process)
    {
        if (handle == 0 || process == null) return false;
        try
        {
            if (AssignProcessToJobObject(handle, process.Handle)) return true;
            Log.Write($"[job] could not track helper {process.Id}: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Log.Write("[job] track failed: " + ex.Message); }
        return false;
    }

    // Terminates every process still in the job.
    public void Dispose()
    {
        var h = handle; handle = 0;
        if (h != 0) CloseHandle(h);
    }

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(nint job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(nint handle);
}
