using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// <c>godot --headless --path . -- --worldpickcheck [host &lt;port&gt;] [connect &lt;host:port&gt;] [nosignals] --world fixture</c>
/// (#999, <c>docs/notes/core/test-worlds.md</c>): the world picker, session by session from the title.
/// <list type="bullet">
/// <item>Play solo's picker set to the junction course and its Start pressed: that course, no generated
/// fill, saves under <c>user://worlds/junction/</c>, started at the course's start; gone after the leave.</item>
/// <item>The traffic lights region: built by the game into its own folder and played from there.</item>
/// <item><c>host</c>: the junction course hosted from the menu; the server's status reply names it.</item>
/// <item><c>connect</c>: a server started with <c>--test-world junction</c>, joined as if from a typed
/// address (the open world): the server's rules say otherwise and the shell joins again on the course.</item>
/// </list>
/// </summary>
public partial class WorldPickCheck : Node
{
    private readonly GameShell _shell;
    private readonly List<(string Name, Action Start, Func<string?> Check)> _steps = new();
    private int _index = -1;
    private ulong _since;
    private bool _entered, _leaving, _failed;
    private readonly string _soloBefore = GameSettings.Current.SoloWorld;
    private readonly WorldChoice _junction = WorldChoice.Find("junction")!, _signals = WorldChoice.Find(WorldChoice.SignalsId)!;

    public static bool Requested() => CmdArgs.Has("--worldpickcheck");

    public WorldPickCheck(GameShell shell)
    {
        _shell = shell;
        Name = "WorldPickCheck";
        _steps.Add(("solo picker, junction course", PickOnSolo, CheckJunction));
        if (!CmdArgs.Has("nosignals"))   // the net check: built and played in the quick one
            _steps.Add(("traffic lights region", () => _shell.Launch(new WorldLaunch { Mode = GameMode.Explore, World = _signals, Landing = _signals.Start }), CheckSignals));
        if (CmdArgs.Int("host") is int port)
            _steps.Add(("hosted junction course", () => _shell.Host("WorldPick host", port, false, null, true, _junction), () => CheckHosted(port)));
        if (CmdArgs.Value("connect") is { } endpoint)
            _steps.Add(("joined from the open world", () => _shell.Join(endpoint, "WorldPick server"), CheckRejoined));
    }

    private static double Seconds(ulong since) => (Time.GetTicksMsec() - since) / 1000.0;

    public override void _Process(double delta)
    {
        if (_failed) return;
        // wall clock throughout: a world loads and a server starts in real time, whatever --fixed-fps does
        if (_index < 0)
        {
            if (_since == 0) _since = Time.GetTicksMsec();
            if (Seconds(_since) < 2) return;
            Next();
            return;
        }
        if (_leaving)
        {
            if (_shell.Top is not TitleScreen || _shell.World != null) { if (Seconds(_since) > 30) Fail("never got back to the title"); return; }
            if (WorldChoice.Active != null) { Fail($"world {WorldChoice.Active.Id} still active at the title"); return; }
            if (Seconds(_since) < 1) return;
            GD.Print($"[worldpick] ok   left {_steps[_index].Name}: the open world again at the title");
            Next();
            return;
        }
        if (!_entered)
        {
            if (!_shell.InWorld)
            {
                if (Seconds(_since) > 150) Fail($"{_steps[_index].Name}: never in the world ({_shell.World?.Failure ?? "no failure"})");
                return;
            }
            _entered = true;
            _since = Time.GetTicksMsec();
            return;
        }
        if (Seconds(_since) < 2) return;
        if (_steps[_index].Check() is { } why) { Fail($"{_steps[_index].Name}: {why}"); return; }
        GD.Print($"[worldpick] ok   {_steps[_index].Name}");
        _leaving = true;
        _since = Time.GetTicksMsec();
        _shell.LeaveWorld(null);
    }

    private void Next()
    {
        _index++;
        _entered = _leaving = false;
        _since = Time.GetTicksMsec();
        if (_index >= _steps.Count)
        {
            GameSettings.Current.SoloWorld = _soloBefore;
            GameSettings.Current.Save();
            GD.Print("[worldpick] RESULT: ok");
            GetTree().Quit(0);
            return;
        }
        GD.Print($"[worldpick] {_steps[_index].Name}");
        _steps[_index].Start();
    }

    /// <summary>Through the page itself: the dropdown set, its signal sent, Start pressed.</summary>
    private void PickOnSolo()
    {
        _shell.Push(SoloScreen.Create());
        Callable.From(() =>
        {
            var page = _shell.Top;
            var picker = page?.FindChildren("*", nameof(OptionButton), owned: false).OfType<OptionButton>().FirstOrDefault();
            var start = page?.FindChildren("*", nameof(Button), owned: false).OfType<Button>().FirstOrDefault(b => b.Text == "Start exploring");
            if (picker == null || start == null) { Fail("no world picker or Start button on Play solo"); return; }
            int index = picker.GetItemIndex(Array.IndexOf(WorldChoice.All, _junction));
            picker.Select(index);
            picker.EmitSignal(OptionButton.SignalName.ItemSelected, index);
            start.EmitSignal(BaseButton.SignalName.Pressed);
        }).CallDeferred();
    }

    private string? CheckJunction()
    {
        if (WorldChoice.Active?.Id != "junction") return $"active world {WorldChoice.ActiveId}";
        if (Systems.FixtureCourse != "junction") return $"fixture course {Systems.FixtureCourse ?? "none"}";
        if (Systems.On(Systems.Generated)) return "the generated fill is on";
        if (WorldChoice.SavePath("user://placed/offline.json") != "user://worlds/junction/placed/offline.json")
            return $"saves at {WorldChoice.SavePath("user://placed/offline.json")}";
        if (GameSettings.Current.SoloWorld != "junction") return $"the pick was not remembered ({GameSettings.Current.SoloWorld})";
        if (_shell.World?.Launch.Landing != _junction.Start) return "not started at the course's start";
        return null;
    }

    private string? CheckSignals()
    {
        if (WorldChoice.Active?.Id != WorldChoice.SignalsId) return $"active world {WorldChoice.ActiveId}";
        string dir = TerrainPaths.FindChunkDir();
        if (dir != WorldChoice.SignalsDir) return $"chunks from {dir}, not {WorldChoice.SignalsDir}";
        int tiles = System.IO.Directory.GetFiles(dir, "*.terr").Length;
        GD.Print($"[worldpick] the region: {tiles} tiles in {dir}");
        if (tiles < 24) return $"{tiles} tiles in the region";
        if (Systems.FixtureCourse != null) return $"a fixture course ({Systems.FixtureCourse}) instead of the region";
        if (Systems.On(Systems.Generated)) return "the generated fill is on";
        return null;
    }

    private Net.ServerQuery? _query;

    private string? CheckHosted(int port)
    {
        if (!_shell.Hosting) return "no hosted server";
        if (WorldChoice.Active?.Id != "junction") return $"active world {WorldChoice.ActiveId}";
        // the status reply the server list reads: a few tries, it answers on the next frames
        string endpoint = $"127.0.0.1:{port}";
        _query ??= new Net.ServerQuery();
        _query.Start();
        for (int i = 0; i < 40 && _query.Probed(endpoint) == null; i++)
        {
            _query.Probe(endpoint);
            System.Threading.Thread.Sleep(50);
            _query.Poll();
        }
        var status = _query.Probed(endpoint);
        _query.Dispose();
        _query = null;
        if (status == null) return "the hosted server never answered a status query";
        GD.Print($"[worldpick] the hosted server's status: world {status.Status.World}");
        return status.Status.World == "junction" ? null : $"status world {status.Status.World}";
    }

    private string? CheckRejoined()
    {
        if (WorldChoice.Active?.Id != "junction") return $"active world {WorldChoice.ActiveId}: the shell did not join again";
        if (_shell.World?.Launch.World?.Id != "junction") return "the launch does not name the server's world";
        return null;
    }

    private void Fail(string why)
    {
        _failed = true;
        GameSettings.Current.SoloWorld = _soloBefore;
        GameSettings.Current.Save();
        GD.Print($"[worldpick] FAIL {why}");
        GD.Print("[worldpick] RESULT: FAILED");
        GetTree().Quit(1);
    }
}
