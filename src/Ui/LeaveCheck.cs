using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// <c>godot --headless --path . -- --leavecheck [connect &lt;host:port&gt;]</c>: enters a world and
/// leaves it to the title, twice (and twice more online when a server is named), and after each
/// leave checks that the world is really gone — no World node, every world singleton released,
/// the node count back near what the title screen alone had (<c>docs/notes/ui/teardown.md</c>).
/// </summary>
public partial class LeaveCheck : Node
{
    private readonly GameShell _shell;
    private readonly List<WorldLaunch> _sessions = new();
    private int _index;
    private double _wait;
    private bool _entering = true, _left;
    private double _baselineNodes = -1;
    private bool _failed;

    public LeaveCheck(GameShell shell)
    {
        _shell = shell;
        Name = "LeaveCheck";
        _sessions.Add(new WorldLaunch { Mode = GameMode.Explore });
        _sessions.Add(new WorldLaunch { Mode = GameMode.Explore });
        var a = CmdArgs.All;
        int c = Array.IndexOf(a, "connect");
        if (c >= 0 && c + 1 < a.Length)
            for (int i = 0; i < 2; i++)
                _sessions.Add(new WorldLaunch { Mode = GameMode.Multiplayer, Endpoint = a[c + 1], PlayerName = $"Leaver{i}" });
        // "host <port>": host from the menu, join it, leave, and check the server process went too
        int h = Array.IndexOf(a, "host");
        if (h >= 0 && h + 1 < a.Length && int.TryParse(a[h + 1], out int port))
            _sessions.Add(new WorldLaunch { Mode = GameMode.Multiplayer, Hosted = true, Endpoint = $"127.0.0.1:{port}", ServerName = "LeaveCheck host" });
    }

    private int _hostedPid = -1;

    public static bool Requested() => CmdArgs.Has("--leavecheck");

    public override void _Process(double delta)
    {
        if (_failed || _index >= _sessions.Count) return;
        _wait += delta;
        if (_baselineNodes < 0)
        {
            if (_wait < 2) return;
            _baselineNodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
            GD.Print($"[leavecheck] title alone: {_baselineNodes} nodes");
            _wait = 0;
        }

        if (_entering)
        {
            if (_wait < 0.5) return;
            if (!_left)
            {
                var s = _sessions[_index];
                if (s.Hosted) _shell.Host(s.ServerName!, Net.NetworkManager.ParseEndpoint(s.Endpoint).Port, true);
                else _shell.Launch(s);
                _left = true;
                _wait = 0;
                return;
            }
            if (_shell.InWorld && _wait > (_sessions[_index].Hosted ? 40 : 6))
            {
                _hostedPid = _shell.HostedPid;
                if (_sessions[_index].Hosted) GD.Print($"[leavecheck] hosting, server pid {_hostedPid}");
                GD.Print($"[leavecheck] in world {_index} ({_sessions[_index].Mode}), {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)} nodes");
                _shell.LeaveWorld(null);
                _entering = false;
                _wait = 0;
            }
            else if (_wait > 200) Fail($"world {_index} never became ready");
            return;
        }

        // leaving: wait for the title, then a little for the frees to land
        if (_shell.Top is not TitleScreen && _shell.Top is not MultiplayerScreen && _shell.Top is not SoloScreen) { if (_wait > 30) Fail("never got back to the title"); return; }
        if (_wait < 2) return;
        Check();
        _index++;
        _entering = true;
        _left = false;
        _wait = 0;
        if (_index == _sessions.Count && !_failed)
        {
            GD.Print("[leavecheck] RESULT: ok");
            GetTree().Quit(0);
        }
    }

    private void Check()
    {
        var leftovers = new List<string>();
        if (GetTree().Root.GetNodeOrNull("Main/World") != null) leftovers.Add("Main/World");
        if (Net.HostedServer.Alive(_hostedPid)) leftovers.Add($"the hosted server (pid {_hostedPid}) still running");
        _hostedPid = -1;
        void Gone(string name, object? instance) { if (instance != null) leftovers.Add(name + ".Instance"); }
        Gone("CdLibrary", Audio.Cd.CdLibrary.Instance);
        Gone("WebRadio", Audio.Live.WebRadio.Instance);
        Gone("BirdLife", Birds.BirdLife.Instance);
        Gone("CombatManager", Combat.CombatManager.Instance);
        Gone("InteriorManager", Interiors.InteriorManager.Instance);
        Gone("Bank", Items.Bank.Instance);
        Gone("ItemEvents", Items.ItemEvents.Instance);
        Gone("PhotoTransfer", Items.PhotoTransfer.Instance);
        Gone("PlacedObjects", Items.PlacedObjects.Instance);
        Gone("RadioManager", Items.RadioManager.Instance);
        Gone("RadioUi", Items.RadioUi.Instance);
        Gone("LootService", Loot.LootService.Instance);
        Gone("OccasionDecor", Occasions.OccasionDecor.Instance);
        Gone("OccasionHunt", Occasions.OccasionHunt.Instance);
        Gone("OccasionManager", Occasions.OccasionManager.Instance);
        Gone("PassengerService", Vehicles.PassengerService.Instance);
        Gone("VehicleManager", Vehicles.VehicleManager.Instance);
        Gone("DayNight", World.DayNight.Instance);
        Gone("TreeColliders", World.TreeColliders.Instance);
        Gone("Traffic", World.Traffic.Current);

        double nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
        double orphans = Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount);
        GD.Print($"[leavecheck] after leave {_index}: {nodes} nodes ({orphans} orphan), title alone had {_baselineNodes}");
        // the title the check lands on can be a different page than the first one: allow its own controls
        if (nodes > _baselineNodes * 1.5 + 200) leftovers.Add($"{nodes - _baselineNodes} more nodes than the title alone");
        if (leftovers.Count > 0) Fail("left behind: " + string.Join(", ", leftovers));
        else GD.Print($"[leavecheck] ok   leave {_index}: nothing left behind");
    }

    private void Fail(string why)
    {
        _failed = true;
        GD.Print($"[leavecheck] FAIL {why}");
        GD.Print("[leavecheck] RESULT: FAILED");
        GetTree().Quit(1);
    }
}
