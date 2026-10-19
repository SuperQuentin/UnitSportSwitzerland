using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Concert.Protocols;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>What an operator's cue does to the desk on every peer.</summary>
public enum CueKind : byte { Go = 0, Section = 1, Bpm = 2, Blackout = 3, Master = 4 }

/// <summary>
/// Hosting a set and running it live (#885), at <c>World/Concerts</c> on the server and every client.
///
/// <para>
/// <b>Hosting</b>: a player who may (an admin; offline, the player) takes a stadium's show with
/// <c>/concert host [seed]</c> or the OSC <c>/concert/host</c>: the server records the seed and the
/// operator and tells every peer (and every late joiner), so everyone builds that stage and rig in
/// place of tonight's. <c>/concert release</c>, or the operator leaving, gives tonight's back.
/// </para>
///
/// <para>
/// <b>Running it</b>: the operator's game listens to Chataigne, TouchDesigner or a console
/// (<see cref="ShowInput"/>: Art-Net, sACN, OSC, turned on with <c>/concert input …</c>). Universes it
/// receives play on its own rig at once and go to the server, at most 30 times a second each,
/// unreliable-ordered on their own channel; the server takes them only from the operator and relays
/// them to the players within <see cref="RelayRange"/> of the stadium, whose rigs play them in place of
/// the desk (<see cref="ConcertRig.TakeUniverse"/>; the desk comes back two seconds after the last
/// frame). OSC cues (go, section, bpm, blackout, master) drive the desk instead when no DMX streams:
/// reliable, kept by the server for late joiners, stamped with the shared clock so the beat is the
/// same everywhere. <c>/concert patch</c> writes the rig's patch for the controller.
/// </para>
/// </summary>
public partial class ConcertService : Node
{
    public const string NodeName = "Concerts";
    /// <summary>The DMX stream's own ENet channel: 2 is the tiles', 3 the web radio's.</summary>
    public const int Channel = 4;
    /// <summary>Players this far from a stadium get its DMX, m.</summary>
    public const double RelayRange = 1500;
    /// <summary>A universe goes to the server at most this often, Hz.</summary>
    public const float SendRate = 30f;

    public static ConcertService? Instance { get; private set; }

    private bool _server;

    /// <summary>Server: the chat, to answer a request with a line (wired by ServerWorld).</summary>
    public ChatManager? Chat { get; set; }
    /// <summary>Client: this screen's player, whose stadium the operator's commands are about.</summary>
    public Func<FootPlayer?>? Local { get; set; }
    /// <summary>The seed each stadium's rig was last built for here (null: tonight's).</summary>
    private readonly Dictionary<int, int?> _built = new();

    /// <summary>A stadium's hosted set: its seed, its operator; and the cues in force.</summary>
    private sealed class Hosted
    {
        public int Seed = -1;
        public long Operator;
        public int Held = -1;
        public bool Blackout;
        public float Master = 1f, Bpm;
        public double BpmAt;
    }

    private readonly Dictionary<int, Hosted> _hosted = new();

    /// <summary>Universes received from the server so far (a check reads it).</summary>
    public long FramesReceived { get; private set; }
    public byte[]? LastFrame { get; private set; }

    /// <summary>The operator's input, once turned on.</summary>
    public ShowInput? Input { get; private set; }
    private readonly Dictionary<int, double> _sentAt = new();

    public static ConcertService Create(Node world, bool server)
    {
        var s = new ConcertService { Name = NodeName, _server = server };
        world.AddChild(s);
        Instance = s;
        return s;
    }

    public override void _ExitTree()
    {
        Input?.Dispose();
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);

    // ---- what the director asks -------------------------------------------------------------------

    /// <summary>The seed hosted at a stadium, or null for tonight's.</summary>
    public int? SeedFor(int stadium) => _hosted.TryGetValue(stadium, out var h) && h.Seed >= 0 ? h.Seed : null;

    /// <summary>The cues in force at a stadium, if a set is hosted there.</summary>
    public (int Held, bool Blackout, float Master)? CuesOf(int stadium) =>
        _hosted.TryGetValue(stadium, out var h) ? (h.Held, h.Blackout, h.Master) : null;

    public long OperatorOf(int stadium) => _hosted.TryGetValue(stadium, out var h) ? h.Operator : 0;

    /// <summary>The cues in force, put on a rig as it is built (a tile loaded after they came).</summary>
    public void ApplyCues(int stadium, ConcertRig rig)
    {
        if (!_hosted.TryGetValue(stadium, out var h)) return;
        var d = rig.Desk;
        d.HeldSection = h.Held >= 0 ? (ShowSection)h.Held : null;
        d.Blackout = h.Blackout;
        d.Master = h.Master;
        if (h.Bpm > 0) d.SetTempo(h.Bpm, h.BpmAt % 86400.0);
    }

    private static ConcertRig? RigOf(int stadium) =>
        stadium >= 0 && stadium < StadiumCatalog.All.Length && ConcertRig.Live.TryGetValue(StadiumCatalog.All[stadium].Name, out var r) ? r : null;

    /// <summary>The stadium nearest a player, the one its commands are about.</summary>
    public static int StadiumNear(GlobalPos at)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < StadiumCatalog.All.Length; i++)
        {
            var s = StadiumCatalog.All[i];
            double d = at.HorizontalDistanceTo(new GlobalPos(s.E, s.N, 0));
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private long Me => Online ? Multiplayer.GetUniqueId() : 1;
    public bool IsOperator(int stadium) => !Online || OperatorOf(stadium) == Me;

    // ---- the server -------------------------------------------------------------------------------

    /// <summary>Server: <c>/concert host|release|status</c> from a player; returns the reply.</summary>
    public string Command(long sender, string rest, bool admin, GlobalPos? at)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int stadium = at is { } p ? StadiumNear(p) : 0;
        string name = StadiumCatalog.All[stadium].Name;
        switch (parts.Length == 0 ? "status" : parts[0])
        {
            case "host":
                int seed = parts.Length > 1 && int.TryParse(parts[1], out int s) ? s : Random.Shared.Next(1, 100000);
                return ServeHost(sender, stadium, seed, admin);
            case "release":
                return ServeRelease(sender, stadium, admin);
            case "status":
                return _hosted.TryGetValue(stadium, out var h) && h.Seed >= 0
                    ? $"{name}: set {h.Seed} ({ConcertPlanner.Plan(StadiumCatalog.All[stadium], h.Seed).Artist}), operator {h.Operator}"
                    : $"{name}: tonight's show; '/concert host [seed]' takes it";
            default:
                return "/concert host [seed] | release | status; on your own game: /concert input artnet|sacn|osc on|off, /concert patch";
        }
    }

    private string ServeHost(long sender, int stadium, int seed, bool admin)
    {
        if (!admin) return "Hosting a concert is for admins ('/login').";
        if (_hosted.TryGetValue(stadium, out var h) && h.Operator != 0 && h.Operator != sender)
            return $"Someone else ({h.Operator}) runs that show; '/concert release' first.";
        _hosted[stadium] = new Hosted { Seed = seed, Operator = sender };
        Announce(stadium);
        var e = ConcertPlanner.Plan(StadiumCatalog.All[stadium], seed);
        return $"You host set {seed} at {e.Stadium.Name}: {e}";
    }

    private string ServeRelease(long sender, int stadium, bool admin)
    {
        if (!_hosted.TryGetValue(stadium, out var h) || h.Seed < 0) return "No set is hosted there.";
        if (h.Operator != sender && !admin) return "Only its operator or an admin can release it.";
        _hosted.Remove(stadium);
        Announce(stadium);
        return "Released: tonight's show is back.";
    }

    /// <summary>Server: a stadium's set and cues to every client.</summary>
    private void Announce(int stadium)
    {
        foreach (int peer in Peers()) SendState(peer, stadium);
        if (!Online) ApplyState(stadium);
    }

    private void SendState(long peer, int stadium)
    {
        var h = _hosted.GetValueOrDefault(stadium) ?? new Hosted();
        RpcId(peer, MethodName.State, stadium, h.Seed, h.Operator, h.Held, h.Blackout, h.Master, h.Bpm, h.BpmAt);
    }

    /// <summary>Server: what a joining player must know (<c>ServerWorld.OnPeerAccepted</c>).</summary>
    public void SendTo(long peer)
    {
        foreach (int stadium in _hosted.Keys) SendState(peer, stadium);
    }

    /// <summary>Server: an operator who leaves gives the show back.</summary>
    public void PeerLeft(long peer)
    {
        foreach (var stadium in _hosted.Where(kv => kv.Value.Operator == peer).Select(kv => kv.Key).ToList())
        {
            _hosted.Remove(stadium);
            Announce(stadium);
        }
    }

    private IEnumerable<int> Peers() => Online && Multiplayer.IsServer() ? Multiplayer.GetPeers() : Array.Empty<int>();

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskHost(int stadium, int seed)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        string reply = ServeHost(sender, stadium, seed, Chat?.IsAdminPeer(sender) == true);
        Chat?.Tell(sender, reply, ChatKind.Private);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskCue(int stadium, int kind, float value, double at)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (OperatorOf(stadium) != sender) return;
        ServeCue(stadium, kind, value, at);
    }

    private void ServeCue(int stadium, int kind, float value, double at)
    {
        if (!_hosted.TryGetValue(stadium, out var h)) return;
        switch ((CueKind)kind)
        {
            case CueKind.Go: h.Held = h.Held < 0 ? (int)ShowSection.Calm : (h.Held % 4) + 1; break;   // calm, groove, chorus, peak, calm...
            case CueKind.Section: h.Held = value < 0 ? -1 : Math.Clamp((int)value, 0, (int)ShowSection.Blackout); break;
            case CueKind.Bpm: h.Bpm = value; h.BpmAt = at; break;
            case CueKind.Blackout: h.Blackout = value > 0.5f; break;
            case CueKind.Master: h.Master = Math.Clamp(value, 0f, 1f); break;
        }
        Announce(stadium);
    }

    /// <summary>Operator to server: one universe, only taken from the stadium's operator.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered, TransferChannel = Channel)]
    private void Frame(int stadium, int universe, byte[] data)
    {
        if (!_server || stadium < 0 || stadium >= StadiumCatalog.All.Length || data.Length > DmxPatch.Channels) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (OperatorOf(stadium) != sender) return;
        var s = StadiumCatalog.All[stadium];
        var here = new GlobalPos(s.E, s.N, 0);
        // only to those near the stadium: a show is half a megabit a second
        foreach (int peer in Multiplayer.GetPeers())
            if (peer != sender && GetNodeOrNull<FootPlayer>("../Players/" + peer) is { } body
                && body.Global.HorizontalDistanceTo(here) < RelayRange)
                RpcId(peer, MethodName.Framed, stadium, universe, data);
    }

    // ---- the clients ------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void State(int stadium, int seed, long op, int held, bool blackout, float master, float bpm, double bpmAt)
    {
        if (seed < 0) _hosted.Remove(stadium);
        else _hosted[stadium] = new Hosted { Seed = seed, Operator = op, Held = held, Blackout = blackout, Master = master, Bpm = bpm, BpmAt = bpmAt };
        ApplyState(stadium);
    }

    /// <summary>What this peer shows for a stadium: its rig built again when the set changed, the cues put on it.</summary>
    private void ApplyState(int stadium)
    {
        var seed = SeedFor(stadium);
        if (_built.GetValueOrDefault(stadium) != seed)
        {
            _built[stadium] = seed;
            GetParent()?.GetNodeOrNull<ConcertDirector>(nameof(ConcertDirector))?.Rebuild(stadium);
        }
        if (RigOf(stadium) is { } rig) ApplyCues(stadium, rig);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered, TransferChannel = Channel)]
    private void Framed(int stadium, int universe, byte[] data)
    {
        FramesReceived++;
        LastFrame = data;
        RigOf(stadium)?.TakeUniverse(universe, data);
    }

    // ---- the operator's input ----------------------------------------------------------------------

    /// <summary>Client-local <c>/concert input|patch</c>: the operator's own ports and files; null when the line is not ours.</summary>
    public static string? RunLocal(string text)
    {
        if (!text.StartsWith("/concert ", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || Instance is not { } svc) return null;
        return parts[1] switch
        {
            "input" => svc.InputCommand(parts.Skip(2).ToArray()),
            "patch" => svc.ExportPatch(),
            _ => null,   // host, release, status: the server's
        };
    }

    private string InputCommand(string[] args)
    {
        Input ??= new ShowInput { Universes = 8 };
        if (args.Length == 0 || args[0] == "status")
            return $"Show input: Art-Net {(Input.ArtNetOn ? $"on ({Input.ArtNetPackets} packets)" : "off")}, "
                + $"sACN {(Input.SacnOn ? $"on ({Input.SacnPackets})" : "off")}, OSC {(Input.OscOn ? $"on ({Input.OscPackets})" : "off")}";
        bool on = args.Length < 2 || args[1] != "off";
        if (!on)
        {
            Input.StopAll();
            return "Show input off.";
        }
        return args[0] switch
        {
            "artnet" => Input.StartArtNet(),
            "sacn" => Input.StartSacn(),
            "osc" => Input.StartOsc(),
            "all" => string.Join("; ", Input.StartArtNet(), Input.StartSacn(), Input.StartOsc()),
            _ => "/concert input artnet|sacn|osc|all [on|off] | status",
        };
    }

    /// <summary>Writes the rig's patch for the controller: a CSV, one fixture a line, both protocols' numbering.</summary>
    private string ExportPatch()
    {
        int stadium = LocalStadium();
        if (RigOf(stadium) is not { } rig) return "No concert rig is loaded near you.";
        var e = rig.Event;
        var sb = new StringBuilder("id,group,type,channels,universe,artnet_port_address,sacn_universe,address,layout\n");
        foreach (var f in rig.Patch.Fixtures)
            sb.Append(CultureInfo.InvariantCulture, $"{f.Id},{f.Group},{f.Type.Name},{f.Type.Footprint},{f.Universe},{(Input?.ArtNetBase ?? 0) + f.Universe},"
                + $"{(Input?.SacnBase ?? 1) + f.Universe},{f.Address + 1},{string.Join('|', f.Type.Channels)}\n");
        string dir = ProjectSettings.GlobalizePath("user://concert");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"patch_{e.Seed}.csv");
        System.IO.File.WriteAllText(path, sb.ToString());
        return $"Patch of set {e.Seed} ({e.Artist}): {rig.Patch.Fixtures.Count} fixtures on {rig.Patch.Universes.Count} universes -> {path}";
    }

    private int LocalStadium() => Origin() is { } at ? StadiumNear(at) : 0;

    private GlobalPos? Origin() => Local?.Invoke() is { } p ? p.Global : null;

    public override void _Process(double delta)
    {
        if (_server || Input == null) return;
        int stadium = LocalStadium();
        double now = RealClock.Now;
        Input.Drain((universe, data) =>
        {
            // here at once, and to the server for the others, throttled per universe
            RigOf(stadium)?.TakeUniverse(universe, data);
            if (!Online || !IsOperator(stadium)) return;
            if (now - _sentAt.GetValueOrDefault(universe, double.NegativeInfinity) < 1.0 / SendRate) return;
            _sentAt[universe] = now;
            RpcId(1, MethodName.Frame, stadium, universe, data);
        });
        while (Input.Cues.TryDequeue(out var m)) Cue(stadium, m);
    }

    /// <summary>An OSC message from the controller, as a cue on every peer.</summary>
    private void Cue(int stadium, OscMessage m)
    {
        double at = ClockSync.ServerNow % 86400.0;
        switch (m.Address.ToLowerInvariant())
        {
            case "/concert/host":
                if (Online) RpcId(1, MethodName.AskHost, stadium, m.Int(0, Random.Shared.Next(1, 100000)));
                else GD.Print($"[concert] {ServeHost(1, stadium, m.Int(0, 1), admin: true)}");
                return;
            case "/concert/go": SendCue(stadium, CueKind.Go, 0, at); return;
            case "/concert/section":
                float section = m.Args.Length > 0 && m.Args[0] is string name
                    ? Enum.TryParse<ShowSection>(name, true, out var s) ? (int)s : -1
                    : m.Float(0, -1);
                SendCue(stadium, CueKind.Section, section, at);
                return;
            case "/concert/bpm": SendCue(stadium, CueKind.Bpm, m.Float(0), at); return;
            case "/concert/blackout": SendCue(stadium, CueKind.Blackout, m.Float(0, 1), at); return;
            case "/concert/master": SendCue(stadium, CueKind.Master, m.Float(0, 1), at); return;
        }
    }

    private void SendCue(int stadium, CueKind kind, float value, double at)
    {
        if (Online) RpcId(1, MethodName.AskCue, stadium, (int)kind, value, at);
        else
        {
            // offline this game is the server
            _hosted.TryAdd(stadium, new Hosted { Seed = RigOf(stadium)?.Event.Seed ?? -1, Operator = 1 });
            ServeCue(stadium, (int)kind, value, at);
        }
    }

    /// <summary>Offline <c>/concert host|release|status</c>: this game is the server.</summary>
    public string LocalCommand(string rest) => Command(1, rest, admin: true, Origin());
}
