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
        for(int bits=0;bits<16;bits++)
        {
            bool dock=(bits&1)!=0,minimized=(bits&2)!=0,pending=(bits&4)!=0,
                explicitRestore=(bits&8)!=0;
            bool expected=explicitRestore||!(dock||minimized||pending);
            Check(WindowActivationPolicy.CanActivate(dock,minimized,pending,explicitRestore)==expected,
                "activation-origin matrix "+bits);
        }
        Check(!WindowActivationPolicy.IsNewTaskbarTarget(12,12),"unchanged taskbar foreground cannot undock");
        Check(!WindowActivationPolicy.IsNewTaskbarTarget(12,0),"null taskbar foreground is not a target");
        Check(WindowActivationPolicy.IsNewTaskbarTarget(12,23),"real taskbar selection allowed");

        Check(DesktopBuildSupport.IsSupported(26300),
            "Windows build 26300 must not disable desktop enumeration and creation");
        Check(DesktopBuildSupport.IsSupported(26100)&&DesktopBuildSupport.IsSupported(26200),
            "previously verified Windows builds retain desktop controls");
        Check(!DesktopBuildSupport.IsSupported(26099)&&!DesktopBuildSupport.IsSupported(26201)
            &&!DesktopBuildSupport.IsSupported(26301)&&!DesktopBuildSupport.IsSupported(int.MaxValue),
            "unverified shell builds never invoke the private desktop ABI");
        var twoDesktops=Tiler.DesktopStripLayout(2,DesktopBuildSupport.IsSupported(26300),1920,28,70,6);
        Check(twoDesktops.DesktopLefts.Count==2&&twoDesktops.AddWidth>0
            &&twoDesktops.DesktopLefts[0]<twoDesktops.AddLeft
            &&twoDesktops.DesktopLefts[1]>twoDesktops.AddLeft,
            "two Windows desktops retain both cards and the centered add-desktop control");
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

        // A dock remains global when Windows cloaks its HWND on the inactive desktop.
        // The next valid enumeration refreshes metadata, whereas explicit undock/close
        // removes membership and its cached thumbnail source.
        var dockA=new AppWindow((nint)101,"A",(nint)4,false,false);
        var dockB=new AppWindow((nint)202,"B",(nint)4,false,false);
        var cache=new DockWindowCache();
        cache.Remember(dockA);cache.Remember(dockB);
        var both=new nint[]{(nint)101,(nint)202};
        Check(cache.Resolve(new[]{dockA,dockB},both,_=>true).Count==2,
            "both global dock sources are initially visible");
        var cloaked=cache.Resolve(new[]{dockA},both,_=>true);
        Check(cloaked.Select(w=>w.Handle).SequenceEqual(both),
            "temporarily cloaked foreign desktop dock source stays docked");
        Check(cache.Resolve(Array.Empty<AppWindow>(),both,_=>true).Count==2,
            "an empty shell enumeration does not undock live windows");
        var renamed=dockB with {Title="Updated title"};
        Check(cache.Resolve(new[]{renamed},both,_=>true).Single(w=>w.Handle==dockB.Handle).Title==renamed.Title,
            "new shell enumeration updates cached dock metadata");
        Check(cache.Resolve(Array.Empty<AppWindow>(),new nint[]{dockA.Handle},_=>true).Single().Handle==dockA.Handle,
            "only an explicit undock removes its saved source");
        Check(cache.Resolve(Array.Empty<AppWindow>(),new nint[]{dockA.Handle},_=>false).Count==0,
            "a destroyed HWND is never kept in the dock");

        var desk1=Guid.NewGuid();var desk2=Guid.NewGuid();
        var savedDesktops=new Dictionary<Guid,Dictionary<nint,DesktopWindowState>>{
            [desk1]=new(){[dockA.Handle]=saved,[dockB.Handle]=saved},
            [desk2]=new(){[dockA.Handle]=saved}};
        DesktopArrangement.ForgetStalePlacement(savedDesktops,dockA.Handle);
        Check(!savedDesktops.Values.Any(s=>s.ContainsKey(dockA.Handle))
            &&savedDesktops[desk1].ContainsKey(dockB.Handle),
            "user move invalidates stale desktop placement without erasing other windows");

        var desktop=Guid.NewGuid();var action=new FocusRequest(8,100,desktop);
        Check(action.IsCurrent(8,100,desktop,true),"current deferred action accepted");
        Check(!action.IsCurrent(9,100,desktop,true),"A-B-A transition invalidates older action by revision");
        Check(!action.IsCurrent(8,101,desktop,true),"later input invalidates action");
        Check(!action.IsCurrent(8,100,Guid.NewGuid(),true),"foreign desktop invalidates action");
        Check(!action.IsCurrent(8,100,desktop,false),"closed session invalidates action");
        Console.WriteLine($"PASS: REVIEW_POLICY_ASSERTIONS={count} (dock intent, taskbar selection, coordinates, snapshots, stale actions)");
    }
}
