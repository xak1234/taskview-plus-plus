using StayView.Core;

static class ReviewPolicyChecks
{
    public static void Run()
    {
        int count=0;
        void Check(bool condition,string name)
        {
            if(!condition)throw new Exception("REVIEW FAILED: "+name);
            count++;
        }
        for(int bits=0;bits<32;bits++)
        {
            bool dock=(bits&1)!=0,minimized=(bits&2)!=0,pending=(bits&4)!=0,
                request=(bits&8)!=0,explicitRestore=(bits&16)!=0;
            bool expected=request||explicitRestore||!(dock||minimized||pending);
            Check(WindowActivationPolicy.CanActivate(dock,minimized,pending,request,explicitRestore)==expected,
                "activation-origin matrix "+bits);
        }
        Check(!WindowActivationPolicy.IsNewTaskbarTarget(12,12),"unchanged taskbar foreground cannot undock");
        Check(!WindowActivationPolicy.IsNewTaskbarTarget(12,0),"null taskbar foreground is not a target");
        Check(WindowActivationPolicy.IsNewTaskbarTarget(12,23),"real taskbar selection allowed");

        var workspace=new Native.RECT(-1450,90,900,650);
        var screen=new Native.RECT(-1450,138,900,650); // e.g. 48px top taskbar
        var current=new Native.WINDOWPLACEMENT { ShowCmd=3,Flags=2,
            NormalPosition=new Native.RECT(40,40,700,500) };
        var saved=new DesktopWindowState(screen,1,false,false,true,1,default,false,0,workspace,true);
        var recalled=DesktopArrangement.RecallPlacement(saved,current,4);
        Check(recalled.NormalPosition.Equals(workspace),"restore retains workspace coordinates");
        Check(!recalled.NormalPosition.Equals(screen),"screen coordinates never overwrite workspace rectangle");
        Check(recalled.ShowCmd==4,"ordinary restore is non-activating");
        Check(recalled.Flags==current.Flags,"placement flags preserved");
        Check(DesktopArrangement.RecallPlacement(saved with {NormalPosition=default},current,4)
            .NormalPosition.Equals(current.NormalPosition),"unknown saved normal position never invents coordinates");
        Check(DesktopArrangement.RecallPlacement(saved,current,3).ShowCmd==3,"maximized state retained");
        var previous=new Dictionary<nint,DesktopWindowState>{[1]=saved,[2]=saved with {Docked=true},[3]=saved,[4]=saved};
        var observed=new Dictionary<nint,DesktopWindowState>{[1]=saved with {ShowCmd=3},[5]=saved};
        var merged=DesktopArrangement.MergeSnapshots(previous,observed,h=>h is 1 or 2);
        Check(merged.Count==3,"snapshot merge retains only known live windows and new samples");
        Check(merged[1].ShowCmd==3,"fresh snapshot overrides previous value");
        Check(merged[2].Docked,"temporarily absent docked source retains its saved state");
        Check(!merged.ContainsKey(3)&&!merged.ContainsKey(4),"closed/moved windows removed");
        Check(previous[1].ShowCmd==1,"merge does not mutate an earlier desktop snapshot");
        var desktop=Guid.NewGuid();var action=new FocusRequest(8,100,desktop);
        Check(action.IsCurrent(8,100,desktop,true),"current deferred action accepted");
        Check(!action.IsCurrent(9,100,desktop,true),"A-B-A transition invalidates older action by revision");
        Check(!action.IsCurrent(8,101,desktop,true),"later input invalidates action");
        Check(!action.IsCurrent(8,100,Guid.NewGuid(),true),"foreign desktop invalidates action");
        Check(!action.IsCurrent(8,100,desktop,false),"closed session invalidates action");
        Console.WriteLine($"PASS: REVIEW_POLICY_ASSERTIONS={count} (dock intent, taskbar selection, coordinates, snapshots, stale actions)");
    }
}