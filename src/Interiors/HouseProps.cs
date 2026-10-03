using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// The house furniture that does something (#433), at <c>World/HouseProps</c> on the server and on
/// every client (RPCs route by node path):
/// <list type="bullet">
/// <item><b>Taps</b> (sink, bathtub, the kitchen counter's sink): E turns the water on or off. Which
/// taps run is a table on the server, plan key to furniture indices, broadcast like an open door
/// (<c>InteriorManager.SetDoor</c>) and sent whole to a joining client. Every peer draws the stream
/// and plays the loop at its tap; a house nobody is in has its taps turned off.</item>
/// <item><b>Instruments</b> (piano, keyboard, drum kit): E sits you at it (stands you at the
/// keyboard) and <see cref="InstrumentUi"/> turns the letter keys into notes. A note sounds here at
/// once and goes to the server unreliably, which passes it to everyone in the same building.</item>
/// </list>
/// Notes: docs/notes/terrain/house-props.md.
/// </summary>
public partial class HouseProps : Node
{
    public const string NodeName = "HouseProps";

    /// <summary>How close to a tap or an instrument to use it, m.</summary>
    public const float Reach = 1.3f;

    /// <summary>Notes a peer may send per second before the server drops the rest.</summary>
    private const int NotesPerSecond = 40;

    public static HouseProps? Instance { get; private set; }

    /// <summary>Server and client: the taps running, per plan key.</summary>
    private readonly Dictionary<string, HashSet<int>> _taps = new();
    private int _tapsVersion;

    /// <summary>Server: notes per peer in the current second.</summary>
    private readonly Dictionary<long, (double Second, int Count)> _noteRate = new();
    private double _housekeeping;

    /// <summary>Client: what the current interior shows, rebuilt when the node or the table changes.</summary>
    private ulong _shownNode;
    private int _shownVersion = -1;
    private readonly Dictionary<int, PropSpeaker> _instruments = new();
    private InstrumentUi? _ui;
    private StandardMaterial3D? _waterMaterial;

    /// <summary>Client: notes heard from other players, for the probes.</summary>
    public int NotesHeard { get; private set; }

    public static HouseProps Create(Node world, bool client)
    {
        var node = new HouseProps { Name = NodeName };
        world.AddChild(node);
        if (client)
        {
            node._ui = new InstrumentUi { Name = "InstrumentUi" };
            node.AddChild(node._ui);
        }
        Instance = node;
        return node;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);

    private long MyId => Multiplayer.MultiplayerPeer is { } p and not OfflineMultiplayerPeer ? Multiplayer.GetUniqueId() : 1;

    public InstrumentUi? Ui => _ui;

    // ---- what each piece is -----------------------------------------------------------------------

    public static bool IsTap(FurnitureType t) => t is FurnitureType.Sink or FurnitureType.Bathtub or FurnitureType.Counter;

    public static InstrumentKind? InstrumentOf(FurnitureType t) => t switch
    {
        FurnitureType.Piano => InstrumentKind.Piano,
        FurnitureType.Keyboard => InstrumentKind.Keyboard,
        FurnitureType.DrumKit => InstrumentKind.Drums,
        _ => null,
    };

    /// <summary>Across a kitchen counter, where its sink is (piece frame, from the middle): toward its right end.</summary>
    public static float CounterSinkX(FurniturePlan p) => Mathf.Max(p.W / 2 - 0.45f, -p.W / 2 + 0.3f);

    /// <summary>
    /// A tap's stream in the piece's own frame (back to -Z, unturned, floor at 0): where the water
    /// leaves the spout, and the height it lands at. Matches the taps <c>InteriorMeshBuilder</c> draws.
    /// </summary>
    public static (Vector3 Spout, float Lands) TapOf(FurniturePlan p) => p.Type switch
    {
        FurnitureType.Sink => (new Vector3(0, p.H + 0.16f, -p.D / 2 + 0.12f), p.H + 0.005f),
        FurnitureType.Bathtub => (new Vector3(0, p.H + 0.14f, -p.D / 2 + 0.15f), p.H - 0.3f),
        _ => (new Vector3(CounterSinkX(p), 1.11f, -p.D / 2 + 0.18f), 0.905f),
    };

    /// <summary>
    /// Where a player goes to play an instrument, in the piece's frame: the spot on the floor, the
    /// way they face (quarter turns on top of the piece's, as a yaw), and whether they sit.
    /// The piano's bench and the keyboard are on its front (+Z), the drummer's stool behind the kit.
    /// </summary>
    public static (Vector3 Spot, float Yaw, bool Sit) SeatOf(FurniturePlan p) => p.Type switch
    {
        FurnitureType.DrumKit => (new Vector3(0, 0, -0.44f), Mathf.Pi, true),
        FurnitureType.Piano => (new Vector3(0, 0, p.D / 2 + 0.45f), 0f, true),
        _ => (new Vector3(0, 0, p.D / 2 + 0.4f), 0f, false),
    };

    /// <summary>Where a piece's sound comes from in the piece's frame.</summary>
    private static Vector3 SoundOf(FurniturePlan p) => p.Type switch
    {
        FurnitureType.DrumKit => new Vector3(0, 0.7f, 0.05f),
        FurnitureType.Piano => new Vector3(0, 1.0f, 0),
        _ => new Vector3(0, p.H, 0),
    };

    /// <summary>The piece's frame in its interior (back to -Z, turned, on its floor).</summary>
    public static Transform3D FrameOf(InteriorLayout l, FurniturePlan p) =>
        new(new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2), new Vector3(p.X, l.FloorY(p.Floor) + p.Lift, p.Z));

    /// <summary>The spot a player uses a piece from, piece frame: before the tap, the keys, the stool.</summary>
    private static Vector3 UseSpot(FurniturePlan p) => p.Type switch
    {
        FurnitureType.Sink => new Vector3(0, 0, p.D / 2 + 0.3f),
        FurnitureType.Counter => new Vector3(CounterSinkX(p), 0, p.D / 2 + 0.35f),
        FurnitureType.Bathtub => new Vector3(0, 0, -p.D / 2 + 0.3f),
        _ => SeatOf(p).Spot,
    };

    // ---- the player at one ------------------------------------------------------------------------

    /// <summary>
    /// The tap or instrument <paramref name="p"/> is at and faces, as an index into the current
    /// layout, or -1. A kitchen counter is a tap only at its sink and a piano an instrument only
    /// from its keys: elsewhere E searches them as before (<c>LootService</c>).
    /// </summary>
    public static int At(FootPlayer p)
    {
        if (!p.Indoors || InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return -1;
        var local = node.ToLocal(p.GlobalPosition);
        var look = node.GlobalTransform.Basis.Inverse() * -p.Camera.GlobalTransform.Basis.Z;
        var lookFlat = new Vector2(look.X, look.Z);
        if (lookFlat.LengthSquared() > 1e-4f) lookFlat = lookFlat.Normalized();
        int best = -1;
        float bestScore = float.MaxValue;
        for (int i = 0; i < layout.Furniture.Count; i++)
        {
            var f = layout.Furniture[i];
            if (!IsTap(f.Type) && InstrumentOf(f.Type) == null) continue;
            float floorY = layout.FloorY(f.Floor);
            if (local.Y < floorY - 0.5f || local.Y > floorY + layout.StoreyHeight - 0.5f) continue;
            var frame = FrameOf(layout, f);
            var spot = frame * UseSpot(f);
            var piece = frame.Origin;
            float dist = new Vector2(local.X - spot.X, local.Z - spot.Z).Length();
            float reach = f.Type == FurnitureType.Counter ? 0.9f : Reach;
            if (dist > reach) continue;
            // facing the piece, not walking away from it
            var to = new Vector2(piece.X - local.X, piece.Z - local.Z);
            float front = to.LengthSquared() > 1e-4f ? lookFlat.Dot(to.Normalized()) : 1f;
            if (front < 0.1f) continue;
            float score = dist - front * 0.5f;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>The prompt at a tap or an instrument, or null.</summary>
    public static string? PromptFor(FootPlayer p)
    {
        if (p.PlayingAt >= 0) return null;
        int i = At(p);
        if (i < 0 || InteriorManager.Instance?.Current is not { } layout) return null;
        var f = layout.Furniture[i];
        string verb = f.Type switch
        {
            FurnitureType.Piano => "Play the piano",
            FurnitureType.Keyboard => "Play the keyboard",
            FurnitureType.DrumKit => "Play the drums",
            _ => Instance?.Running(layout.Key, i) == true ? "Turn the tap off" : "Turn the tap on",
        };
        return InputHints.Prompt(PlayerInput.InteractMount, verb);
    }

    /// <summary>E at a tap (toggles it) or an instrument (sits down to it). False when at neither.</summary>
    public static bool TryUse(FootPlayer p)
    {
        if (Instance is not { } props || p.PlayingAt >= 0) return false;
        int i = At(p);
        if (i < 0 || InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return false;
        var f = layout.Furniture[i];
        if (InstrumentOf(f.Type) is { } kind)
        {
            var (spot, yaw, sit) = SeatOf(f);
            var frame = FrameOf(layout, f);
            p.PlayAt(node, i, frame * spot, f.Turns * Mathf.Pi / 2 + yaw, sit);
            props._ui?.Open(p, layout.Key, i, kind);
            return true;
        }
        props.ToggleTap(layout.Key, i);
        return true;
    }

    // ---- taps -------------------------------------------------------------------------------------

    public bool Running(string plan, int index) => _taps.TryGetValue(plan, out var set) && set.Contains(index);

    public void ToggleTap(string plan, int index)
    {
        if (Online) RpcId(1, MethodName.AskTap, plan, index);
        else ServeTap(MyId, plan, index);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskTap(string plan, int index)
    {
        if (Multiplayer.IsServer()) ServeTap(Multiplayer.GetRemoteSenderId(), plan, index);
    }

    /// <summary>Only someone inside that building works its taps (offline: the building this machine is in, or a probe's with no interiors).</summary>
    private bool Inside(long peer, string plan) =>
        plan.Length > 0 && (InteriorManager.Instance is { } im ? im.SpaceOf(peer) == plan || !Online && im.Current?.Key == plan : !Online);

    private void ServeTap(long peer, string plan, int index)
    {
        if (index < 0 || index > 4096 || !Inside(peer, plan)) return;
        BroadcastTap(plan, index, !Running(plan, index));
    }

    private void BroadcastTap(string plan, int index, bool on)
    {
        SetTap(plan, index, on);
        if (Online) Rpc(MethodName.SetTap, plan, index, on);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetTap(string plan, int index, bool on)
    {
        if (on)
        {
            if (!_taps.TryGetValue(plan, out var set)) _taps[plan] = set = new HashSet<int>();
            set.Add(index);
        }
        else if (_taps.TryGetValue(plan, out var set))
        {
            set.Remove(index);
            if (set.Count == 0) _taps.Remove(plan);
        }
        _tapsVersion++;
    }

    /// <summary>Server: a newly joined client sees every running tap.</summary>
    public void SendTo(long peer)
    {
        foreach (var (plan, set) in _taps)
            foreach (int i in set) RpcId(peer, MethodName.SetTap, plan, i, true);
    }

    private bool Occupied(string plan)
    {
        if (InteriorManager.Instance is not { } im) return !Online;
        if (im.SpaceOf(MyId) == plan) return true;
        if (!Online) return im.Current?.Key == plan;
        foreach (long peer in Multiplayer.GetPeers())
            if (im.SpaceOf(peer) == plan) return true;
        return false;
    }

    // ---- notes ------------------------------------------------------------------------------------

    /// <summary>Plays a note on instrument <paramref name="index"/> of <paramref name="plan"/>: here at once, then for everyone in the building.</summary>
    public void Strike(string plan, int index, int note, float velocity)
    {
        Sound(plan, index, note, velocity);
        if (Online) RpcId(1, MethodName.AskNote, plan, index, note, velocity);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void AskNote(string plan, int index, int note, float velocity)
    {
        if (!Multiplayer.IsServer()) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (index < 0 || !Inside(peer, plan)) return;
        double second = Math.Floor(Time.GetTicksMsec() / 1000.0);
        var (at, count) = _noteRate.GetValueOrDefault(peer);
        count = at == second ? count + 1 : 1;
        _noteRate[peer] = (second, count);
        if (count > NotesPerSecond) return;
        var im = InteriorManager.Instance;
        // the server's own player (a listen host) hears it too
        if (im?.SpaceOf(MyId) == plan && MyId != peer) Heard(plan, index, note, velocity);
        foreach (long other in Multiplayer.GetPeers())
            if (other != peer && im?.SpaceOf(other) == plan)
                RpcId(other, MethodName.Note, plan, index, note, velocity);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void Note(string plan, int index, int note, float velocity) => Heard(plan, index, note, velocity);

    private void Heard(string plan, int index, int note, float velocity)
    {
        NotesHeard++;
        Sound(plan, index, note, velocity);
    }

    /// <summary>Client: the note at its instrument, if this player is in that building.</summary>
    private void Sound(string plan, int index, int note, float velocity)
    {
        if (NetworkManager.DedicatedServer || DisplayServer.GetName() == "headless") return;
        if (InteriorManager.Instance?.CurrentNode is not { } node || node.Layout.Key != plan) return;
        var l = node.Layout;
        if (index < 0 || index >= l.Furniture.Count || InstrumentOf(l.Furniture[index].Type) is not { } kind) return;
        if (!_instruments.TryGetValue(index, out var speaker) || !IsInstanceValid(speaker) || speaker.GetParent() != node)
        {
            var f = l.Furniture[index];
            speaker = new PropSpeaker { Name = $"Instrument{index}", Position = FrameOf(l, f) * SoundOf(f), BaseDb = -4f };
            node.AddChild(speaker);
            _instruments[index] = speaker;
        }
        speaker.Strike(InstrumentSynth.Note(kind, note), velocity);
    }

    // ---- the frame --------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        ShowTaps();
        if (!NetLink.IsServer(this)) return;
        _housekeeping += delta;
        if (_housekeeping < 1) return;
        _housekeeping = 0;
        // nobody left inside: the taps go off (a probe without interiors keeps its own)
        if (InteriorManager.Instance == null) return;
        foreach (var plan in _taps.Keys.ToList())
            if (!Occupied(plan))
                foreach (int i in _taps[plan].ToList()) BroadcastTap(plan, i, false);
    }

    /// <summary>
    /// The running taps of the interior this player is in, as children of its node: a stream and
    /// a loop each. Built again whenever the node (left and come back) or the table changes.
    /// </summary>
    private void ShowTaps()
    {
        var node = InteriorManager.Instance?.CurrentNode;
        ulong id = node?.GetInstanceId() ?? 0;
        if (id == _shownNode && _tapsVersion == _shownVersion) return;
        if (id != _shownNode) _instruments.Clear();
        _shownNode = id;
        _shownVersion = _tapsVersion;
        if (node == null) return;
        var l = node.Layout;
        var on = _taps.GetValueOrDefault(l.Key);
        foreach (var child in node.GetChildren())
            if (child is Node3D n && n.Name.ToString().StartsWith("Tap", StringComparison.Ordinal)
                && (on == null || !int.TryParse(n.Name.ToString().AsSpan(3), out int k) || !on.Contains(k)))
                n.QueueFree();
        if (on == null) return;
        foreach (int i in on)
        {
            if (i >= l.Furniture.Count || !IsTap(l.Furniture[i].Type) || node.HasNode($"Tap{i}")) continue;
            node.AddChild(TapNode(l, i));
        }
    }

    private Node3D TapNode(InteriorLayout l, int i)
    {
        var f = l.Furniture[i];
        var (spout, lands) = TapOf(f);
        var frame = FrameOf(l, f);
        var tap = new Node3D { Name = $"Tap{i}", Transform = frame };
        float len = Mathf.Max(0.05f, spout.Y - lands);
        _waterMaterial ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(0.72f, 0.86f, 1f, 0.55f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        tap.AddChild(new MeshInstance3D
        {
            Name = "Stream",
            Mesh = new CylinderMesh { TopRadius = 0.009f, BottomRadius = 0.014f, Height = len, RadialSegments = 6, Rings = 1, Material = _waterMaterial },
            Position = new Vector3(spout.X, lands + len / 2, spout.Z),
        });
        tap.AddChild(new MeshInstance3D
        {
            Name = "Splash",
            Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.06f, Height = 0.006f, RadialSegments = 8, Rings = 1, Material = _waterMaterial },
            Position = new Vector3(spout.X, lands + 0.004f, spout.Z),
        });
        if (!NetworkManager.DedicatedServer && DisplayServer.GetName() != "headless")
            tap.AddChild(new PropSpeaker { Name = "Water", Loop = InstrumentSynth.Water, Position = new Vector3(spout.X, lands + 0.05f, spout.Z), BaseDb = -10f });
        return tap;
    }
}
