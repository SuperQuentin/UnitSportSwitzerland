using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.World;

/// <summary>
/// Car races between connected players. One class on both sides at <c>World/Race</c> — Godot routes
/// RPCs by node path, like <c>World/Chat</c>.
///
/// <para>
/// <b>The server decides everything that matters</b>: the route (<see cref="RaceRoute"/> — the main
/// road from where the host stands), who is in, the grid, the start instant and every finish time,
/// which it measures itself from its own start, so a client cannot report a time. A client only
/// reports which checkpoint it passed (every 200 m, in order — a shortcut across a hairpin misses
/// one) and that it crossed the line. That is the same client-authoritative trust the rest of the
/// game gives positions; the order check makes the obvious cheat not work.
/// </para>
///
/// <para>
/// Flow: <c>/race start [metres]</c> opens a 15 s entry window (the host is in), <c>/race join</c>
/// enters, then everyone gets <see cref="Setup"/>: the route's centreline and widths up to just past
/// the finish, their slot on a single-file grid, and a countdown in SECONDS (not a clock time —
/// the two machines' clocks need not agree). The client puts its player on the grid in a car
/// (the AE86 if it was on foot), holds the handbrake until GO, then hands the car back to the
/// player — or, with <c>--raceauto</c>, to an <see cref="AutoPilot"/>.
/// </para>
/// </summary>
public partial class RaceManager : Node
{
    public const string NodeName = "Race";
    private const double EntryWindow = 15.0, Countdown = 5.0;
    private const float CheckpointEvery = 200f, CheckpointReach = 30f;

    // ---- server ----
    private bool _server;
    private ChatManager? _chat;
    private Node3D? _players;
    private IChunkSource? _source;
    private WorldOrigin? _origin;
    private enum Phase { Idle, Building, Entry, Running }
    private Phase _phase;
    private long _host;
    private float _finish;
    private RaceRoute? _route;
    private double _clock, _entryEnds, _startAt, _deadline;
    private readonly List<long> _entrants = new();
    private readonly Dictionary<long, int> _checkpoint = new();
    private readonly Dictionary<long, double> _finished = new();

    // ---- client ----
    /// <summary>The local player, resolved when needed (never captured: it is respawned).</summary>
    public System.Func<FootPlayer?>? LocalPlayer { get; set; }
    private RaceRoute? _myRoute;
    private float _myFinish;
    private int _mySlot, _myCount, _myNext;
    private double _goIn = -1, _raceClock;
    private bool _going, _done;
    private AutoPilot? _pilot;
    private Label? _hud;
    private readonly bool _auto = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--raceauto") >= 0;

    /// <summary>Raised on the client when this player finishes (for checks): position, seconds.</summary>
    public event System.Action<int, double>? Finished;

    public static RaceManager CreateServer(ChatManager chat, Node3D players, IChunkSource source, WorldOrigin origin) => new()
    {
        Name = NodeName, _server = true, _chat = chat, _players = players, _source = source, _origin = origin,
    };

    public static RaceManager CreateClient() => new() { Name = NodeName };

    // ------------------------------------------------------------------------------------
    // server: commands
    // ------------------------------------------------------------------------------------

    /// <summary><c>/race ...</c>, from the chat. Returns the reply for the sender.</summary>
    public string Command(long sender, string args)
    {
        var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "help";
        switch (verb)
        {
            case "start":
                if (_phase != Phase.Idle) return "A race is already on. /race join to enter it.";
                float metres = parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float m) ? Mathf.Clamp(m, 300f, 8000f) : 2000f;
                return Open(sender, metres);
            case "npc":
            case "duel":
                if (verb == "duel" && (parts.Length < 2 || parts[1].ToLowerInvariant() != "npc")) return "/race duel npc";
                bool counted = verb == "npc" && parts.Length > 1 && int.TryParse(parts[1], out _);
                int count = counted ? Mathf.Clamp(int.Parse(parts[1]), 1, RaceNpcs.PerOwner) : 1;
                string car = string.Join(' ', parts.Skip(verb == "duel" || counted ? 2 : 1));
                return AddNpcs(sender, count, car);
            case "join":
                if (_phase != Phase.Entry) return _phase == Phase.Idle ? "No race open. /race start to open one." : "Too late to join this one.";
                if (!_entrants.Contains(sender)) _entrants.Add(sender);
                _chat?.Broadcast($"[race] {Who(sender)} joins ({_entrants.Count} in)", ChatKind.System);
                return "You are in. Get in a car.";
            case "leave":
                _entrants.Remove(sender);
                return "You are out of the race.";
            case "cancel":
                if (sender != _host && sender != 0) return "Only the host can cancel.";
                Reset("cancelled");
                return "Race cancelled.";
            default:
                return "/race start [metres]  /race join  /race leave  /race cancel  /race npc [n] [car]  /race duel npc";
        }
    }

    private string Open(long sender, float metres)
    {
        if (_players?.GetNodeOrNull<Node3D>(sender.ToString()) is not { } host) return "You have no position yet.";
        _phase = Phase.Building;
        _host = sender;
        _entrants.Clear(); _checkpoint.Clear(); _finished.Clear();
        _entrants.Add(sender);
        var at = host.GlobalPosition;
        var source = _source!; var origin = _origin!;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var route = await RaceRoute.BuildAsync(source, origin, at);
            Callable.From(() => Opened(route, metres)).CallDeferred();
        });
        return "Finding the road…";
    }

    // ---- NPC entrants (issue #39): spawned by World/Npcs, simulated on the sender's client ----

    private RaceNpcs? Npcs => GetParent()?.GetNodeOrNull<RaceNpcs>(RaceNpcs.NodeName);

    /// <summary><c>/race npc [n] [car]</c>, <c>/race duel npc</c>: NPCs into the open race (opening one if none).</summary>
    private string AddNpcs(long sender, int count, string car)
    {
        if (_phase == Phase.Running) return "A race is on. Wait for it to finish.";
        if (Npcs is not { } npcs || _players?.GetNodeOrNull<Node3D>(sender.ToString()) is not { } me) return "You have no position yet.";
        var spec = car.Length == 0 ? CarCatalog.All[0]
            : CarCatalog.All.FirstOrDefault(c => c.Label.Contains(car, System.StringComparison.OrdinalIgnoreCase));
        if (spec == null) return $"No car called '{car}'.";
        string opened = _phase == Phase.Idle ? Open(sender, 2000f) + " " : "";
        npcs.ForgetOwner(sender, keep: _entrants);   // the last race's NPCs make way
        var ids = npcs.Spawn(sender, count, spec.Kind, me.GlobalPosition, me.Rotation.Y);
        EnterNpcs(sender, ids);
        return opened + (ids.Count == 0 ? "No room for more NPCs." : $"{ids.Count} NPC(s) in.");
    }

    private void EnterNpcs(long sender, IEnumerable<long> ids)
    {
        foreach (long id in ids)
        {
            if (id >= 0 || PlayerReplication.NpcOwner(id) != sender || _entrants.Contains(id)) continue;
            _entrants.Add(id);
            _chat?.Broadcast($"[race] {Who(id)} joins ({_entrants.Count} in)", ChatKind.System);
        }
    }

    /// <summary>Client → server: enter NPCs the sender owns (already spawned) into the open race.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestNpcEntrants(long[] npcIds)
    {
        if (_server && _phase is Phase.Building or Phase.Entry) EnterNpcs(Multiplayer.GetRemoteSenderId(), npcIds);
    }

    /// <summary>The sender speaks for itself and for the NPCs it owns.</summary>
    private bool SpeaksFor(long entrant)
    {
        long peer = Multiplayer.GetRemoteSenderId();
        return entrant == peer || entrant < 0 && PlayerReplication.NpcOwner(entrant) == peer;
    }

    private void Opened(RaceRoute? route, float metres)
    {
        if (_phase != Phase.Building) return;
        if (route == null || route.Length < 400f)
        {
            _chat?.Broadcast("[race] no road long enough here to race on", ChatKind.Error);
            _phase = Phase.Idle;
            return;
        }
        _route = route;
        _finish = Mathf.Min(metres, route.Length - 40f);
        _phase = Phase.Entry;
        _entryEnds = _clock + EntryWindow;
        _chat?.Broadcast($"[race] {Who(_host)} opens a {_finish / 1000f:0.0} km race on this road — /race join within {EntryWindow:0} s",
            ChatKind.System);
    }

    public override void _Process(double delta)
    {
        if (_server) ServerTick(delta);
        else ClientTick(delta);
    }

    private void ServerTick(double delta)
    {
        _clock += delta;
        if (_phase == Phase.Entry && _clock >= _entryEnds)
        {
            // drop entrants who left the server
            _entrants.RemoveAll(p => _players?.GetNodeOrNull(PlayerReplication.NodeName(p)) == null);
            if (_entrants.Count == 0) { Reset("nobody joined"); return; }
            _phase = Phase.Running;
            _startAt = _clock + Countdown;
            // a generous time limit: the whole distance at 10 m/s, plus the countdown
            _deadline = _startAt + _finish / 10f + 30f;
            var route = _route!;
            int last = route.NearestCentreIndexAt(_finish + 60f);
            var centre = route.Centre.Take(last + 1).ToArray();
            var width = route.Width.Take(last + 1).ToArray();
            for (int i = 0; i < _entrants.Count; i++)
            {
                _checkpoint[_entrants[i]] = 0;
                long e = _entrants[i];
                if (e < 0) RpcId(PlayerReplication.NpcOwner(e), MethodName.NpcSetup, e, centre, width, _finish, i, _entrants.Count, Countdown);
                else RpcId(e, MethodName.Setup, centre, width, _finish, i, _entrants.Count, Countdown);
            }
            _chat?.Broadcast($"[race] {_entrants.Count} on the grid: {string.Join(", ", _entrants.Select(Who))} — GO in {Countdown:0} s",
                ChatKind.System);
        }
        if (_phase == Phase.Running && (_clock > _deadline || _entrants.All(p => _finished.ContainsKey(p))))
            Results();
    }

    private void Results()
    {
        var order = _finished.OrderBy(kv => kv.Value).ToList();
        int pos = 0;
        foreach (var (peer, time) in order)
            _chat?.Broadcast($"[race] {++pos}. {Who(peer)}  {Format(time)}", ChatKind.System);
        foreach (var peer in _entrants.Where(p => !_finished.ContainsKey(p)))
            _chat?.Broadcast($"[race] DNF {Who(peer)}", ChatKind.System);
        GD.Print($"[race] results: {string.Join(", ", order.Select((kv, i) => $"{i + 1}. {Who(kv.Key)} {Format(kv.Value)}"))}");
        Reset(null);
    }

    private void Reset(string? why)
    {
        if (why != null) _chat?.Broadcast($"[race] {why}", ChatKind.System);
        _phase = Phase.Idle;
        _route = null;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Checkpoint(long peer, int index)
    {
        if (!_server || _phase != Phase.Running || !SpeaksFor(peer)) return;
        // in order only: a checkpoint skipped is a shortcut taken
        if (_checkpoint.TryGetValue(peer, out int next) && index == next) _checkpoint[peer] = next + 1;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Crossed(long peer)
    {
        if (!_server || _phase != Phase.Running || !SpeaksFor(peer)) return;
        int needed = Mathf.FloorToInt(_finish / CheckpointEvery);
        if (!_checkpoint.TryGetValue(peer, out int passed) || _finished.ContainsKey(peer)) return;
        if (passed < needed)
        {
            _chat?.Broadcast($"[race] {Who(peer)} crossed the line with {needed - passed} checkpoint(s) missed — not counted", ChatKind.Error);
            return;
        }
        // the server's own clock, from its own start: a client cannot send a time
        double time = _clock - _startAt;
        _finished[peer] = time;
        int position = _finished.Count;
        _chat?.Broadcast($"[race] {Who(peer)} finishes P{position} in {Format(time)}", ChatKind.System);
        if (peer > 0) RpcId(peer, MethodName.Result, position, time);
    }

    private string Who(long peer) => peer < 0 ? Npcs?.Label(peer) ?? $"NPC {peer}" : _chat?.NameOfPeer(peer) ?? $"#{peer}";

    private static string Format(double s) => $"{(int)(s / 60)}:{s % 60:00.00}";

    // ------------------------------------------------------------------------------------
    // client
    // ------------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Setup(Vector3[] centre, float[] width, float finish, int slot, int count, double countdown)
    {
        _myRoute = RaceRoute.FromPoints(centre, width);
        _myFinish = finish;
        _mySlot = slot;
        _myCount = count;
        _myNext = 0;
        _goIn = countdown;
        _going = _done = false;
        _raceClock = 0;
        PlaceOnGrid();
    }

    /// <summary>The grid for one of this client's NPCs: handed to its driver, which reports through here.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NpcSetup(long id, Vector3[] centre, float[] width, float finish, int slot, int count, double countdown)
    {
        if (GetParent()?.GetNodeOrNull<RaceNpc>($"Players/{PlayerReplication.NodeName(id)}/{RaceNpc.NodeName}") is not { } npc) return;
        npc.Checkpoint = i => RpcId(1, MethodName.Checkpoint, id, i);
        npc.Crossed = () => RpcId(1, MethodName.Crossed, id);
        npc.OnRaceSetup(RaceRoute.FromPoints(centre, width), finish, slot, count, countdown);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Result(int position, double time)
    {
        _done = true;
        GD.Print($"[race] finished P{position} in {Format(time)}");
        Finished?.Invoke(position, time);
        // an autopiloted car keeps its pilot, now only braking to a stop; a player just drives on
        if (_pilot == null && LocalPlayer?.Invoke() is { } me) me.RideControls = null;
    }

    /// <summary>On the grid, in a car, facing down the road, holding the handbrake.</summary>
    private void PlaceOnGrid()
    {
        if (LocalPlayer?.Invoke() is not { } me || _myRoute == null) return;
        var line = _myRoute.Line;
        float s = 12f + 15f * (_myCount - 1 - _mySlot);
        var at = line.PointAt(s);
        var fwd = RaceRoute.Flat(line.PointAt(s + 2f) - line.PointAt(s - 2f)).Normalized();
        me.GlobalPosition = at + Vector3.Up * 1.2f;
        me.Rotation = new Vector3(0, Mathf.Atan2(-fwd.X, -fwd.Z), 0);
        me.Velocity = Vector3.Zero;
        if (me.Vehicle is not Car)
        {
            me.SetRide(RideKind.OnFoot);
            if (!me.SetRide(CarCatalog.All[0].Kind)) me.DebugLaunch(me.GlobalPosition, Vector3.Zero);
        }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
        GD.Print($"[race] on the grid, slot {_mySlot + 1} of {_myCount}, {_myFinish:F0} m to go");
    }

    // ---- scripted players, for the loopback check: --racestart [m] opens, --racejoin enters ----
    private readonly string? _autoStart = Arg("--racestart");
    private readonly bool _autoJoin = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--racejoin") >= 0;
    private readonly string? _autoNpc = Arg("--racenpc");   // --racenpc N: N NPCs into the race once it opens
    private bool _npcAsked;
    private double _settled;
    private bool _asked;

    private static string? Arg(string flag)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, flag);
        return i < 0 ? null : i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "";
    }

    public override void _Ready()
    {
        if (_server) return;
        // every race line goes to the log too, which is what the loopback check reads
        if (GetParent()?.GetNodeOrNull<ChatManager>(ChatManager.NodeName) is { } chat)
            chat.LineReceived += (line, _) =>
            {
                if (!line.Contains("[race]")) return;
                GD.Print(line);
                if (_autoJoin && !_asked && line.Contains("opens a")) { _asked = true; chat.Send("/race join"); }
                if (_autoNpc != null && !_npcAsked && line.Contains("opens a")) { _npcAsked = true; chat.Send($"/race npc {_autoNpc}".Trim()); }
            };
    }

    private void AutoOpen(double delta)
    {
        if (_autoStart == null || _asked) return;
        if (LocalPlayer?.Invoke() is not { } me || !me.IsOnFloor()) { _settled = 0; return; }
        _settled += delta;
        if (_settled < 4.0) return;
        _asked = true;
        GetParent()?.GetNodeOrNull<ChatManager>(ChatManager.NodeName)?.Send($"/race start {_autoStart}".Trim());
    }

    private void ClientTick(double delta)
    {
        AutoOpen(delta);
        if (_myRoute == null || _done) { ShowHud(null); return; }
        var me = LocalPlayer?.Invoke();
        if (me == null) return;

        if (!_going)
        {
            // a player not yet in a car (the mount waits for the ground) gets one as soon as it can
            if (me.Vehicle is not Car && me.IsOnFloor()) me.SetRide(CarCatalog.All[0].Kind);
            _goIn -= delta;
            ShowHud(_goIn > 0.9 ? $"{Mathf.CeilToInt((float)_goIn)}" : "GO!");
            if (_goIn > 0) return;
            _going = true;
            GD.Print("[race] GO");
            if (_auto && me.Vehicle is Car car)
            {
                _pilot = new AutoPilot(_myRoute, me, car.Spec);
                me.RideControls = () => _pilot.Drive((float)GetPhysicsProcessDeltaTime(), true, Others(me));
            }
            else me.RideControls = null;   // the player drives
        }

        _raceClock += delta;
        // progress along the line, and the checkpoints in order
        int near = _myRoute.Line.IndexAt(0);
        float best = float.MaxValue;
        var line = _myRoute.Line;
        for (int i = 0; i < line.Points.Count; i += 2)
        {
            float d = RaceRoute.Flat(line.Points[i] - me.GlobalPosition).LengthSquared();
            if (d < best) { best = d; near = i; }
        }
        float arc = line.Arc[near];
        bool onRoute = Mathf.Sqrt(best) < CheckpointReach;
        while (onRoute && arc >= (_myNext + 1) * CheckpointEvery && (_myNext + 1) * CheckpointEvery <= _myFinish)
        {
            RpcId(1, MethodName.Checkpoint, (long)Multiplayer.GetUniqueId(), _myNext);
            _myNext++;
        }
        if (onRoute && arc >= _myFinish && !_done)
        {
            RpcId(1, MethodName.Crossed, (long)Multiplayer.GetUniqueId());
            if (_pilot != null) _pilot.Finished = true;   // brake to a stop past the line
            _done = true;   // the server answers with the result
        }
        ShowHud($"{Format(_raceClock)}   CP {_myNext}/{Mathf.FloorToInt(_myFinish / CheckpointEvery)}   {Mathf.Max(0f, _myFinish - arc):F0} m");
    }

    private static IEnumerable<AutoPilot.Other> Others(FootPlayer me)
    {
        foreach (var node in me.GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != me)
                yield return new AutoPilot.Other(p.GlobalPosition, p.Velocity.Length(), false);
    }

    private void ShowHud(string? text)
    {
        if (text == null) { if (_hud != null) _hud.Visible = false; return; }
        if (_hud == null)
        {
            var layer = new CanvasLayer { Layer = 11, Name = "RaceHud" };
            AddChild(layer);
            _hud = new Label
            {
                AnchorLeft = 0.5f, AnchorRight = 0.5f, OffsetLeft = -300, OffsetRight = 300, OffsetTop = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _hud.AddThemeFontSizeOverride("font_size", 30);
            _hud.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.2f));
            _hud.AddThemeColorOverride("font_outline_color", Colors.Black);
            _hud.AddThemeConstantOverride("outline_size", 6);
            layer.AddChild(_hud);
        }
        _hud.Visible = true;
        _hud.Text = text;
    }
}
