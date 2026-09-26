using System.Diagnostics;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace StayView.Core;

// UI Automation never runs in the UI process. A provider call that hangs inside
// UIAutomationCore's pipe transport (seen against Chrome) leaves the calling thread
// in a kernel wait that process exit cannot interrupt: the process then lingers as
// an unkillable zombie. Running the probe in a helper process keeps that risk out
// of Taskview++ itself; a hung helper is abandoned and a fresh one is spawned.
public sealed class EmptySpaceProbe : IDisposable
{
    const int Budget = 400;          // ms the UI waits for a verdict
    const int RespawnDelay = 1500;   // ms before a failed helper is replaced
    int busy;
    Process? process; NamedPipeClientStream? pipe; StreamReader? reader; StreamWriter? writer;
    long retryAfter;
    bool disabled;
    public string LastDecision { get; private set; } = "not checked";
    bool Reject(string reason) { LastDecision = reason; return false; }

    bool Connect()
    {
        if(disabled)return false;
        if (process != null && !process.HasExited && pipe?.IsConnected == true) return true;
        if(!Drop())return false;
        long now = Environment.TickCount64;
        if (now < retryAfter) return false;
        try
        {
            string name = "StayView.EmptySpace." + Guid.NewGuid().ToString("N");
            process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--empty-space-probe " + name) { UseShellExecute = false, CreateNoWindow = true });
            ChildProcessJob.Helpers.Track(process); // dies with the UI process, however it ends
            pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(1500);
            reader = new(pipe); writer = new(pipe) { AutoFlush = true };
            retryAfter = 0;
            return true;
        }
        catch (Exception ex) { Log.Write("[probe] helper start failed: " + ex.Message); Drop(); retryAfter = now + RespawnDelay; return false; }
    }

    bool Drop()
    {
        try { writer?.Dispose(); } catch { } try { reader?.Dispose(); } catch { } try { pipe?.Dispose(); } catch { }
        bool exited=true;
        try
        {
            if(process!=null&&!process.HasExited)
            {
                process.Kill();
                exited=process.WaitForExit(250);
                if(!exited)
                {
                    disabled=true;
                    Log.Write("[probe] helper could not terminate; disabling empty-space probing for this session");
                }
            }
        }
        catch(Exception ex)
        {
            exited=false;disabled=true;
            Log.Write("[probe] helper shutdown failed; disabling probing: "+ex.Message);
        }
        try { process?.Dispose(); } catch { }
        writer = null; reader = null; pipe = null; process = null;
        return exited;
    }

    // Spawning and JIT-warming a helper costs ~300 ms; do it before the first click so a
    // cold helper never eats the user's first empty-space double-click.
    public void Warm() => _ = Task.Run(async () =>
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        try
        {
            if (!Connect()) return;
            await writer!.WriteLineAsync("0 0 0 0 0");
            var read = reader!.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(3000)) != read) { if(Drop())retryAfter = Environment.TickCount64 + RespawnDelay; }
        }
        catch (Exception ex) { Log.Write("[probe] warm-up failed: " + ex.Message); Drop(); }
        finally { Volatile.Write(ref busy, 0); }
    });

    public async Task<bool> IsEmptyAsync(nint window, Native.POINT first, Native.POINT second)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        try
        {
            if (!Connect()) return Reject("probe helper unavailable");
            Task<string?> read;
            try
            {
                await writer!.WriteLineAsync($"{window} {first.X} {first.Y} {second.X} {second.Y}");
                read = reader!.ReadLineAsync();
            }
            catch (Exception ex) { if(Drop())retryAfter = Environment.TickCount64 + RespawnDelay; return Reject("probe pipe: " + ex.GetType().Name); }
            if (await Task.WhenAny(read, Task.Delay(Budget)) != read || read.Result == null)
            {
                Log.Write("[probe] helper timed out; terminating " + process?.Id);
                bool stopped=Drop();
                if(stopped)
                {
                    retryAfter = Environment.TickCount64 + RespawnDelay;
                    _ = Task.Delay(RespawnDelay + 50).ContinueWith(_ => Warm());
                }
                return Reject("provider timeout");
            }
            var line = read.Result;
            int sep = line.IndexOf('|');
            LastDecision = sep >= 0 ? line[(sep + 1)..] : line;
            return line.StartsWith("1", StringComparison.Ordinal);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    public void Dispose() => Drop();
}

// Helper-process side. Each request runs on its own thread with the same budget the
// UI uses; a request that overruns is abandoned inside this process, which keeps
// serving later requests. Nothing here runs in the Taskview++ UI process.
public static class EmptySpaceProbeServer
{
    public static void Run(string pipeName)
    {
        try
        {
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
            var connection = pipe.WaitForConnectionAsync();
            if (!connection.Wait(10000)) return;
            using var reader = new StreamReader(pipe); using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var probe = new Probe();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string reply;
                try
                {
                    var p = line.Split(' ');
                    nint window = (nint)long.Parse(p[0]);
                    var first = new Native.POINT { X = int.Parse(p[1]), Y = int.Parse(p[2]) };
                    var second = new Native.POINT { X = int.Parse(p[3]), Y = int.Parse(p[4]) };
                    var query = Task.Run(() =>
                    {
                        var dpi = Native.SetThreadDpiAwarenessContext(-4);
                        try { return probe.IsEmpty(window, first) && ((first.X == second.X && first.Y == second.Y) || probe.IsEmpty(window, second)); }
                        catch (Exception ex) { probe.LastDecision = ex.GetType().Name; return false; }
                        finally { if (dpi != 0) Native.SetThreadDpiAwarenessContext(dpi); }
                    });
                    // Slightly longer than the client's budget so a fast answer always wins.
                    reply = query.Wait(600) ? (query.Result ? "1|" : "0|") + probe.LastDecision : "0|provider timeout (helper)";
                }
                catch (Exception ex) { reply = "0|" + ex.GetType().Name; }
                try { writer.WriteLine(reply); } catch (IOException) { break; }
            }
        }
        catch (IOException) { /* UI process closed the pipe. */ }
        catch (ObjectDisposedException) { }
        // Do not return into WinUI teardown; a thread stuck in UIAutomationCore may keep
        // this helper alive as a zombie, which is harmless here and never touches the UI process.
        Environment.Exit(0);
    }
}

sealed class Probe
{
    public string LastDecision = "not checked";
    bool Reject(string reason) { LastDecision = reason; return false; }
    public bool IsEmpty(nint window, Native.POINT point)
    {
        if (Native.GetAncestor(Native.WindowFromPoint(point), 2) != window) return Reject("different window under pointer");
        Native.GetWindowThreadProcessId(window, out var pid);
        bool browser = Native.Class(window) == "Chrome_WidgetWin_1";
        if (browser)
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            browser = process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase);
        }
        var element = AutomationElement.FromPoint(new Point(point.X, point.Y));
        if (element == null) return Reject("no element");
        var info = element.Current;
        // Chromium's provider can initially return a cached hit from another point.
        // Give its asynchronous hit test a bounded chance to settle; never trust an
        // out-of-bounds leaf as proof of either whitespace or an interactive control.
        for (int retry=0; retry<4 && !info.BoundingRectangle.Contains(point.X,point.Y); retry++)
        {
            Thread.Sleep(15);
            element=AutomationElement.FromPoint(new Point(point.X,point.Y));
            if(element==null)return Reject("no element");
            info=element.Current;
        }
        if (browser && !info.BoundingRectangle.Contains(point.X,point.Y))
        {
            int budget=256;
            element=FindAtPoint(AutomationElement.FromHandle(window),point,0,ref budget);
            if(element==null)return Reject("no bounded browser element");
            info=element.Current;
        }
        if (info.ProcessId != pid || info.IsOffscreen || !info.IsEnabled
            || !info.BoundingRectangle.Contains(point.X, point.Y)) return Reject("element not visible/enabled/in bounds");
        bool sawDocument = false, verifiedReadOnlyText = false;
        var pageHandlers = new List<Rect>();
        var documents = new List<Rect>();

        // A generic pane nested inside an editor/button still belongs to that control.
        // Reach the actual target HWND before accepting; incomplete ancestry is unknown.
        for (int depth = 0; element != null && depth < 96; depth++)
        {
            info = element.Current;
            bool document = browser && info.ControlType == ControlType.Document;
            if (!FocusedClickPolicy.IsBackgroundContainer(info.ControlType.Id) && !document) return Reject("control: " + info.ControlType.ProgrammaticName);
            if (document)
            {
                sawDocument = true;
                documents.Add(info.BoundingRectangle);
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                    && !((ValuePattern)value).Current.IsReadOnly) return Reject("editable document");
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text))
                {
                    var range = ((TextPattern)text).RangeFromPoint(new Point(point.X, point.Y));
                    range.ExpandToEnclosingUnit(TextUnit.Character);
                    // Chrome exposes Text/Value on the whole read-only webpage, including
                    // its whitespace. RangeFromPoint chooses the NEAREST character, so
                    // only its actual screen rectangle indicates a click on text.
                    if (range.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not true) return Reject("editable/unknown text");
                    var bounds = range.GetBoundingRectangles().SelectMany(r => new[] { r.X, r.Y, r.Width, r.Height }).ToArray();
                    if (!FocusedClickPolicy.IsOutsideText(point.X, point.Y, bounds)) return Reject("text under pointer");
                    verifiedReadOnlyText = true;
                }
            }
            foreach (var pattern in element.GetSupportedPatterns())
            {
                // Sites often put a delegated click handler on their entire page. Chrome
                // exposes that as Invoke on an unnamed group, even over empty whitespace.
                // Defer only these groups, then require a matching document-sized box.
                if (browser && pattern.Id == 10000 && info.ControlType == ControlType.Group
                    && !info.IsKeyboardFocusable && string.IsNullOrWhiteSpace(info.Name))
                { pageHandlers.Add(info.BoundingRectangle); continue; }
                if (FocusedClickPolicy.IsInteractivePattern(pattern.Id)
                    && !(document && pattern.Id is 10002 or 10014 or 10024)) return Reject("interactive pattern " + pattern.Id);
            }
            if ((nint)info.NativeWindowHandle == window)
            {
                LastDecision="reached target window";
                return (!sawDocument || verifiedReadOnlyText)
                    && pageHandlers.All(h => verifiedReadOnlyText && documents.Any(d => FocusedClickPolicy.IsPageSizedHandler(h, d)));
            }
            element = TreeWalker.RawViewWalker.GetParent(element);
        }
        return Reject("incomplete ancestry");
    }

    static AutomationElement? FindAtPoint(AutomationElement parent, Native.POINT point, int depth, ref int budget)
    {
        if(depth>=96 || --budget<0)throw new InvalidOperationException("Browser point search budget exhausted");
        var info=parent.Current;
        if(info.IsOffscreen || !info.BoundingRectangle.Contains(point.X,point.Y))return null;
        // Chromium sometimes returns a hit with stale scroll coordinates. Walk only
        // branches whose actual rectangles contain the point, within a fixed budget.
        var best=parent;
        var walker=TreeWalker.RawViewWalker;
        for(var child=walker.GetFirstChild(parent);child!=null;child=walker.GetNextSibling(child))
        {
            var candidate=FindAtPoint(child,point,depth+1,ref budget);
            if(candidate!=null)best=candidate;
        }
        return best;
    }
}
