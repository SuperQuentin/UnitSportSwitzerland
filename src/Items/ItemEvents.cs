using Godot;
using UnitSport.Audio;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>What a held item just did, once. Append new kinds at the end: the value goes over the wire.</summary>
public enum ItemEventKind
{
    None = 0,
    /// <summary>A shotgun blast. Position = muzzle, direction = where it points.</summary>
    Shot = 1,
    /// <summary>A camera flash. Position = the camera, direction = where it looks.</summary>
    PhotoFlash = 2,
}

/// <summary>
/// One item event as a handler sees it. <see cref="Peer"/> is the owner (the RPC sender online,
/// 1 offline); <see cref="Local"/> is true on the owner's own machine.
/// </summary>
public readonly record struct ItemEvent(long Peer, ItemEventKind Kind, Vector3 Position, Vector3 Direction,
    string Extra, bool Local);

/// <summary>
/// One-shot, client-authoritative events of held items — a shot, a flash — at <c>World/ItemEvents</c>
/// on the server and every client (RPCs route by node path).
///
/// <para>
/// The owner calls <see cref="Send"/>: it runs the kind's handler here at once (the owner sees and
/// hears the 3D effect too), then goes to the server, which relays it to the peers that can see the
/// owner (<c>Net/InterestService</c>) — exactly as combat tracers are relayed. The server runs no
/// handler and draws nothing. Offline only the local handler runs.
/// </para>
///
/// <para>
/// A remote event is drawn at the owner's <i>body</i> as this peer shows it (hand + barrel along
/// the direction), not at the sent position, so the flash sits on the interpolated gun rather than
/// a few centimetres ahead of it. Handlers: <see cref="Register"/>; the docs are
/// <c>docs/notes/items/item-net-events.md</c>.
/// </para>
/// </summary>
public partial class ItemEvents : Node
{
    public const string NodeName = "ItemEvents";

    /// <summary>A sender this far from its own body is not believed (server side).</summary>
    private const float MaxOffset = 30f;
    private const int MaxExtra = 256;

    public static ItemEvents? Instance { get; private set; }

    /// <summary>Raised on every peer that runs an event (owner included), after its handler. For probes and logs.</summary>
    public static event Action<ItemEvent>? Received;

    private static readonly Dictionary<ItemEventKind, Action<ItemEvents, ItemEvent>> Handlers = new()
    {
        [ItemEventKind.Shot] = (n, e) => n.ShotEffect(e),
        [ItemEventKind.PhotoFlash] = (n, e) => n.FlashEffect(e),
    };

    /// <summary>
    /// Sets (or replaces) what happens when an event of <paramref name="kind"/> arrives — on the
    /// owner and on every peer it is relayed to. The handler gets this node to parent effects to.
    /// </summary>
    public static void Register(ItemEventKind kind, Action<ItemEvents, ItemEvent> handler) => Handlers[kind] = handler;

    private bool _server;
    private readonly Random _rng = new();

    public static ItemEvents Create(Node world, bool server)
    {
        var e = new ItemEvents { Name = NodeName, _server = server };
        world.AddChild(e);
        if (!server) Instance = e;
        return e;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>
    /// Owner side: plays the event here and has the server relay it to everyone who can see this
    /// player. <paramref name="extra"/> is free text for the kind (≤ 256 chars).
    /// </summary>
    public void Send(ItemEventKind kind, Vector3 position, Vector3 direction, string extra = "")
    {
        if (extra.Length > MaxExtra) extra = extra[..MaxExtra];
        long me = Online ? Multiplayer.GetUniqueId() : 1;
        Run(new ItemEvent(me, kind, position, direction, extra, Local: true));
        if (Online) RpcId(1, MethodName.Relay, (int)kind, position, direction, extra);
    }

    /// <summary>Where a held item's business end is: the hand plus <paramref name="reach"/> along the aim, else in front of the camera.</summary>
    public static Vector3 MuzzleOf(FootPlayer player, Vector3 direction, float reach = 0.55f)
    {
        if (player.HandLocal is { } hand)
            return (player.GlobalTransform * hand).Origin + direction * reach;
        var cam = player.Camera;
        return cam.GlobalPosition + direction * (reach + 0.2f) - cam.GlobalTransform.Basis.Y * 0.15f;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Relay(int kind, Vector3 position, Vector3 direction, string extra)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (extra.Length > MaxExtra) return;
        // a sound somewhere the sender is not is not an item it is holding
        if (GetNodeOrNull<Node3D>("../Players/" + sender) is { } body && body.GlobalPosition.DistanceTo(position) > MaxOffset)
            return;
        var interest = GetNodeOrNull<Net.InterestService>("../" + Net.InterestService.NodeName);
        foreach (int peer in Multiplayer.GetPeers())
            if (peer != sender && interest?.ServerSees(peer, sender) != false)
                RpcId(peer, MethodName.Deliver, sender, kind, position, direction, extra);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Deliver(long sender, int kind, Vector3 position, Vector3 direction, string extra)
    {
        // on the owner's body as this peer shows it, when it is here
        if (GetNodeOrNull<FootPlayer>("../Players/" + sender) is { } body && direction.LengthSquared() > 1e-6f)
            position = MuzzleOf(body, direction.Normalized(), kind == (int)ItemEventKind.Shot ? 0.55f : 0.1f);
        GD.Print(FormattableString.Invariant($"[items] event {(ItemEventKind)kind} from peer {sender} at {position.X:F1},{position.Y:F1},{position.Z:F1}"));
        Run(new ItemEvent(sender, (ItemEventKind)kind, position, direction, extra, Local: false));
    }

    private void Run(ItemEvent e)
    {
        if (_server) return;
        if (Handlers.TryGetValue(e.Kind, out var handler))
        {
            try { handler(this, e); }
            catch (Exception ex) { GD.PushError($"[items] {e.Kind} handler: {ex.Message}"); }
        }
        Received?.Invoke(e);
    }

    // ---- built-in effects ----------------------------------------------------------------------

    private void ShotEffect(ItemEvent e)
    {
        var (stream, pitch, db) = SfxSynth.Shotgun.Pick(_rng);
        Sound3D(e.Position, stream, pitch, db - 1f, unitSize: 18f, maxDistance: 1500f);
        LightPulse(e.Position, new Color(1f, 0.78f, 0.45f), energy: 6f, range: 7f, time: 0.07f);
        Glow(e.Position, new Color(1f, 0.85f, 0.5f), size: 0.35f, time: 0.05f);
    }

    private void FlashEffect(ItemEvent e)
    {
        // the owner has its own full-screen flash; a light pulse right in front of its eyes would
        // only wash out the photo that is being taken
        LightPulse(e.Position + e.Direction * 0.15f, Colors.White, energy: 10f, range: 9f, time: 0.1f);
        if (!e.Local) Glow(e.Position, Colors.White, size: 0.2f, time: 0.08f);
        if (!e.Local) Sound3D(e.Position, SfxSynth.Tick, 0.6f, -6f, unitSize: 4f, maxDistance: 60f);
    }

    /// <summary>A positional one-shot on the Sfx bus, freed when it ends.</summary>
    public void Sound3D(Vector3 at, AudioStream stream, float pitch, float db, float unitSize = 10f, float maxDistance = 500f)
    {
        var s = new AudioStreamPlayer3D
        {
            Stream = stream, PitchScale = pitch, VolumeDb = db, UnitSize = unitSize, MaxDistance = maxDistance,
            Bus = SfxBus.Name, TopLevel = true,
        };
        AddChild(s);
        s.GlobalPosition = at;
        s.Finished += s.QueueFree;
        s.Play();
    }

    /// <summary>An omni light that fades to nothing over <paramref name="time"/> seconds, then goes.</summary>
    public void LightPulse(Vector3 at, Color color, float energy, float range, float time)
    {
        var light = new OmniLight3D
        {
            LightColor = color, LightEnergy = energy, OmniRange = range, ShadowEnabled = false, TopLevel = true,
        };
        AddChild(light);
        light.GlobalPosition = at;
        var t = light.CreateTween();
        t.TweenProperty(light, "light_energy", 0f, time).SetEase(Tween.EaseType.In);
        t.TweenCallback(Callable.From(light.QueueFree));
    }

    /// <summary>A small unshaded additive billboard, gone after <paramref name="time"/> seconds.</summary>
    public void Glow(Vector3 at, Color color, float size, float time)
    {
        var quad = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(size, size) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = color,
                NoDepthTest = false,
            },
            TopLevel = true,
        };
        AddChild(quad);
        quad.GlobalPosition = at;
        var t = quad.CreateTween();
        t.TweenInterval(time);
        t.TweenCallback(Callable.From(quad.QueueFree));
    }
}
