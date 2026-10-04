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
    /// <summary>
    /// A weapon hit a player (#178). Position = the hit point, direction = the shot, extra =
    /// <see cref="PlayerHits.Hit"/>. Delivered to the victim only, and only while PvP is on.
    /// </summary>
    Hit = 3,
    /// <summary>A flare fired into the sky (#198). Position = the muzzle; it climbs and burns red.</summary>
    Flare = 4,
    /// <summary>
    /// A thrown item hit a player (#261). Position = where, direction = the item's velocity, extra =
    /// <see cref="ThrowHits.Bonk"/>. Relayed to everyone near: all play the reaction, the victim takes it.
    /// </summary>
    Bonk = 5,
    /// <summary>An alphorn blown (#478). Position = the bell; heard far (<see cref="SwissItems.OnHorn"/>).</summary>
    Horn = 6,
    /// <summary>A fondue set out (#478). Position = the pot; everyone near eats (<see cref="SwissItems.OnFondue"/>).</summary>
    Fondue = 7,
    /// <summary>A smoke canister landed (#478). Position = where; a cloud on every peer (<see cref="SwissItems.OnSmoke"/>).</summary>
    Smoke = 8,
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
    /// <summary>Drops the subscribers a world left behind when it was freed (<see cref="Core.WorldStatics"/>).</summary>
    internal static void ResetEvents() => Received = null;

    private static readonly Dictionary<ItemEventKind, Action<ItemEvents, ItemEvent>> Handlers = new()
    {
        [ItemEventKind.Shot] = (n, e) => n.ShotEffect(e),
        [ItemEventKind.PhotoFlash] = (n, e) => n.FlashEffect(e),
        [ItemEventKind.Hit] = PlayerHits.OnHit,
        [ItemEventKind.Flare] = (n, e) => n.FlareEffect(e),
        [ItemEventKind.Bonk] = ThrowHits.OnBonk,
        [ItemEventKind.Horn] = SwissItems.OnHorn,
        [ItemEventKind.Fondue] = SwissItems.OnFondue,
        [ItemEventKind.Smoke] = SwissItems.OnSmoke,
    };

    /// <summary>
    /// Sets (or replaces) what happens when an event of <paramref name="kind"/> arrives — on the
    /// owner and on every peer it is relayed to. The handler gets this node to parent effects to.
    /// </summary>
    public static void Register(ItemEventKind kind, Action<ItemEvents, ItemEvent> handler) => Handlers[kind] = handler;

    private bool _server;
    private readonly Random _rng = new();

    /// <summary>This peer's origin: events go to the server and back in LV95 (#185).</summary>
    private Core.WorldOrigin _origin = null!;

    /// <summary>This peer's origin, for handlers that keep a place beyond the moment (a smoke cloud, a horn's marker).</summary>
    public Core.WorldOrigin? Origin => _origin;

    public static ItemEvents Create(Node world, Core.WorldOrigin origin, bool server)
    {
        var e = new ItemEvents { Name = NodeName, _origin = origin, _server = server };
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
        if (!Online) return;
        var at = _origin.ToGlobal(position);
        RpcId(1, MethodName.Relay, (int)kind, at.E, at.N, at.Alt, direction, extra);
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
    private void Relay(int kind, double e, double n, double alt, Vector3 direction, string extra)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (extra.Length > MaxExtra) return;
        var at = new Core.GlobalPos(e, n, alt);
        if (kind == (int)ItemEventKind.Hit)
        {
            RelayHit(sender, at, direction, extra);
            return;
        }
        if (kind == (int)ItemEventKind.Bonk)
        {
            RelayBonk(sender, at, direction, extra);
            return;
        }
        // a sound somewhere the sender is not is not an item it is holding
        if (GetNodeOrNull<FootPlayer>("../Players/" + sender) is { } body && !(body.Global.DistanceTo(at) <= MaxOffset))
            return;
        var interest = GetNodeOrNull<Net.InterestService>("../" + Net.InterestService.NodeName);
        foreach (int peer in Multiplayer.GetPeers())
            if (peer != sender && interest?.ServerSees(peer, sender) != false)
                RpcId(peer, MethodName.Deliver, sender, kind, e, n, alt, direction, extra);
    }

    /// <summary>
    /// Server: a player says it hit another. Passed on to the victim alone when PvP is on, the
    /// weapon exists and could do that much, the shooter stands by its body and the victim is
    /// within the weapon's reach of it, where the shot says.
    /// </summary>
    private void RelayHit(long sender, Core.GlobalPos position, Vector3 direction, string extra)
    {
        if (PlayerHits.Hit.Parse(extra) is not { } hit || hit.Victim == sender) return;
        if (!Combat.PvpRules.Allows(sender, hit.Victim)) return;
        if (Weapons.Get(hit.Weapon) is not { } weapon || hit.Damage > weapon.MaxHit + 0.5f) return;
        var shooter = GetNodeOrNull<FootPlayer>("../Players/" + sender);
        var victim = GetNodeOrNull<FootPlayer>("../Players/" + hit.Victim);
        if (shooter == null || victim == null || victim.Down != 0 || shooter.Down != 0) return;
        // the bodies are where their owners last said (in LV95, #185): allow for a quarter second of
        // running at both ends
        const float Slack = 8f;
        if (!(shooter.Global.DistanceTo(victim.Global) <= weapon.Range + Slack)) return;
        if (!(victim.Global.DistanceTo(position) <= Slack)) return;
        if (!Multiplayer.GetPeers().Contains((int)hit.Victim)) return;
        GD.Print(FormattableString.Invariant($"[pvp] peer {sender} hit peer {hit.Victim} for {hit.Damage:F1} ({hit.Weapon})"));
        RpcId(hit.Victim, MethodName.Deliver, sender, (int)ItemEventKind.Hit, position.E, position.N, position.Alt, direction, extra);
        Combat.PvpRules.RaiseHit(sender, hit.Victim, hit.Damage);
    }

    /// <summary>
    /// Server: a player says something it threw hit another (#261). Passed on to everyone who can
    /// see the thrower or the victim when it could be true: a real throw's speed, the victim where
    /// the hit says, the thrower within a long throw of it, no more damage than a throw can do.
    /// Not a weapon and never lethal, so it is not a PvP matter.
    /// </summary>
    private void RelayBonk(long sender, Core.GlobalPos position, Vector3 direction, string extra)
    {
        if (ThrowHits.Bonk.Parse(extra) is not { } bonk || bonk.Victim == sender || bonk.Damage > ThrowHits.MaxDamage + 0.5f) return;
        if (!direction.IsFinite() || direction.Length() > 45f) return;
        var thrower = GetNodeOrNull<FootPlayer>("../Players/" + sender);
        var victim = GetNodeOrNull<FootPlayer>("../Players/" + bonk.Victim);
        if (thrower == null || victim == null || victim.Down != 0) return;
        const float Slack = 8f;
        // in LV95, from what the players published (#185)
        if (!(victim.Global.DistanceTo(position) <= Slack) || !(thrower.Global.DistanceTo(position) <= 60f)) return;
        GD.Print(FormattableString.Invariant($"[bonk] peer {sender} hit peer {bonk.Victim} with {bonk.Item} for {bonk.Damage:F1}"));
        var interest = GetNodeOrNull<Net.InterestService>("../" + Net.InterestService.NodeName);
        foreach (int peer in Multiplayer.GetPeers())
            if (peer != sender && (peer == bonk.Victim || interest?.ServerSees(peer, sender) != false || interest?.ServerSees(peer, bonk.Victim) != false))
                RpcId(peer, MethodName.Deliver, sender, (int)ItemEventKind.Bonk, position.E, position.N, position.Alt, direction, extra);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Deliver(long sender, int kind, double e, double n, double alt, Vector3 direction, string extra)
    {
        var position = _origin.ToWorld(e, n, alt);
        // on the owner's body as this peer shows it, when it is here
        if (kind is not ((int)ItemEventKind.Hit or (int)ItemEventKind.Bonk) && GetNodeOrNull<FootPlayer>("../Players/" + sender) is { } body && direction.LengthSquared() > 1e-6f)
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
        // which gun: extra is its item id; empty is the shotgun (the only gun before #178)
        var weapon = int.TryParse(e.Extra, out int id) ? Weapons.Get((ItemId)id) : null;
        bool pump = weapon == null || weapon.Id == ItemId.Shotgun;
        var (stream, pitch, db) = SfxSynth.Shotgun.Pick(_rng);
        Sound3D(e.Position, stream, pitch * (weapon?.Pitch ?? 1f), db - (pump ? 1f : 3f), unitSize: 18f, maxDistance: 1500f);
        LightPulse(e.Position, new Color(1f, 0.78f, 0.45f), energy: 2.5f, range: 3f, time: 0.05f);
        if (!e.Local) Glow(e.Position, new Color(1f, 0.85f, 0.5f), size: 0.25f, time: 0.04f);   // in the owner's own view it is a hard-edged square on the lens

        // the shooter racks the next shell: its body rocks back now, the slide handle and its clack
        // follow one cycle later. The owner's own viewmodel was already driven by ItemController.
        var shooter = e.Local
            ? GetViewport().GetCamera3D()?.GetParent() as Player.FootPlayer
            : GetNodeOrNull<Player.FootPlayer>("../Players/" + e.Peer);
        if (shooter != null)
        {
            shooter.BodyJolt();
            if (!e.Local && pump) shooter.GetNodeOrNull<HeldItemVisual>("HeldItem")?.Pump();
        }
        if (!pump) return;
        var at = e.Position;
        GetTree().CreateTimer(HeldItemVisual.PumpDelay + 0.10f).Timeout += () =>
        {
            if (!IsInsideTree()) return;
            var (stream, pitch, db) = SfxSynth.Pump.Pick(_rng);
            Sound3D(shooter != null && IsInstanceValid(shooter) ? shooter.GlobalPosition + Vector3.Up * 1.3f : at,
                stream, pitch, db - 4f, unitSize: 6f, maxDistance: 250f);
        };
    }

    /// <summary>A red flare climbing about 140 m over four seconds, a light on it, a pop and a hiss.</summary>
    private void FlareEffect(ItemEvent e)
    {
        Sound3D(e.Position, SfxSynth.Shotgun.Pick(_rng).Stream, 1.9f, -6f, unitSize: 12f, maxDistance: 1500f);
        var flare = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f, RadialSegments = 8, Rings = 4 },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.25f, 0.15f),
            },
            TopLevel = true,
        };
        flare.AddChild(new OmniLight3D { LightColor = new Color(1f, 0.3f, 0.2f), LightEnergy = 8f, OmniRange = 60f, ShadowEnabled = false });
        AddChild(flare);
        flare.GlobalPosition = e.Position;
        var t = flare.CreateTween();
        t.TweenProperty(flare, "global_position", e.Position + Vector3.Up * 140f, 4.0).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        t.TweenInterval(2.0);
        t.TweenCallback(Callable.From(flare.QueueFree));
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
