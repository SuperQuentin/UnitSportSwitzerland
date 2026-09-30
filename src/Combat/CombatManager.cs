using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Vehicles;
using Plane = UnitSport.Player.Plane;

namespace UnitSport.Combat;

/// <summary>
/// Guns on the plane and the helicopter, at <c>World/Combat</c> on the server and every client —
/// the path has to match, because Godot routes RPCs by node path.
///
/// <para>
/// Bullets are <b>ray-marched tracers</b>, not rigid bodies: each physics step casts one ray from
/// where the round was to where it is now, so a 700 m/s round never tunnels through a wing and a
/// burst costs a handful of ray queries, not a pile of physics objects.
/// </para>
///
/// <para>
/// Client-authoritative, like everything else here. The shooter sends each round (origin and
/// velocity) to every peer, every peer flies every round, and each peer applies damage only to
/// what it has authority over — its own player, the vehicles it parked, its own target drones —
/// exactly as <see cref="Explosion.Blast"/> does. The shooter id comes from the RPC's sender,
/// which a client cannot forge. The server relays and otherwise ignores the whole thing.
/// </para>
/// </summary>
public partial class CombatManager : Node3D
{
    public const string NodeName = "Combat";

    public const float BulletSpeed = 700f;
    /// <summary>Per round. A plane (100 HP) goes down after ~15 hits, a drone after ~9, a pedestrian after ~15.</summary>
    public const float Damage = 7f;
    public const int Magazine = 500;
    private const float Life = 2.5f;              // ~1.7 km of flight
    private const float FireInterval = 1f / 14f;  // rounds per second, both barrels together
    private const float TracerLength = 9f;
    private const int MaxTracers = 400;
    /// <summary>Where the helicopter's turret converges on the camera's line of sight, m.</summary>
    private const float TurretRange = 600f;
    /// <summary>Where the plane's crosshair is drawn along the boresight, m.</summary>
    private const float Boresight = 400f;

    public static CombatManager? Instance { get; private set; }

    /// <summary>The player this client fires as. Resolved per frame, never captured.</summary>
    public Func<FootPlayer?>? LocalPlayer { get; set; }
    public ChunkManager? Terrain { get; set; }

    // --- for the HUD and the probe ---
    public int Ammo { get; private set; } = Magazine;
    /// <summary>The local player is in an armed craft right now.</summary>
    public bool Armed { get; private set; }
    /// <summary>Where the guns are pointing (world), for the crosshair.</summary>
    public Vector3 AimPoint { get; private set; }
    /// <summary>Where to aim to hit the target nearest the crosshair, if any.</summary>
    public Vector3? LeadPoint { get; private set; }
    /// <summary>Seconds of hit marker left.</summary>
    public float HitFlash { get; private set; }
    public int Shots { get; private set; }
    public int Hits { get; private set; }
    public int Kills { get; private set; }

    /// <summary>Offline target drones appear once the player first opens fire.</summary>
    public bool DronesEnabled { get; set; } = true;
    private const int DroneCount = 3;
    private float _droneRespawn;
    private bool _dronesWanted;

    private bool _server;
    private struct Tracer { public Vector3 Pos, Vel; public float Age; public long Shooter; }
    private readonly List<Tracer> _tracers = new();
    private MultiMeshInstance3D _draw = null!;
    private readonly AudioStreamPlayer3D[] _voices = new AudioStreamPlayer3D[6];
    private int _voice, _barrel;
    private float _cooldown;
    private readonly Random _rng = new();
    private readonly Dictionary<long, Godot.Collections.Array<Rid>> _exclude = new();

    public static CombatManager Create(Node world, ChunkManager? terrain, bool server)
    {
        var c = new CombatManager { Name = NodeName, Terrain = terrain, _server = server };
        world.AddChild(c);
        Instance = c;
        return c;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        if (_server) return;

        var mesh = new BoxMesh
        {
            Size = new Vector3(0.14f, 0.14f, TracerLength),
            Material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(1f, 0.82f, 0.35f),
            },
        };
        _draw = new MultiMeshInstance3D
        {
            Name = "Tracers",
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // bounds are world-sized: the rounds go wherever they go
            CustomAabb = new Aabb(new Vector3(-1e5f, -1e5f, -1e5f), new Vector3(2e5f, 2e5f, 2e5f)),
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = mesh,
                InstanceCount = MaxTracers,
                VisibleInstanceCount = 0,
            },
        };
        AddChild(_draw);

        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new AudioStreamPlayer3D { UnitSize = 12f, MaxDistance = 1500f, Bus = SfxBus.Name, TopLevel = true };
            AddChild(_voices[i]);
        }

        // under the HUD's layer 10, over the speed lines at 4
        var hud = new CanvasLayer { Name = "CombatHud", Layer = 9 };
        hud.AddChild(new CombatHud(this));
        AddChild(hud);
    }

    private bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>This peer's id as a shooter; offline, the local player's default authority (1).</summary>
    private long LocalId => Online ? Multiplayer.GetUniqueId() : 1;

    public override void _PhysicsProcess(double delta)
    {
        if (_server) return;
        float dt = (float)delta;
        HitFlash = Mathf.Max(0f, HitFlash - dt);
        _exclude.Clear();

        TickGuns(dt);
        StepTracers(dt);
        TickDrones(dt);
    }

    public override void _Process(double delta)
    {
        if (_server) return;
        var mm = _draw.Multimesh;
        int n = Math.Min(_tracers.Count, MaxTracers);
        for (int i = 0; i < n; i++)
        {
            var t = _tracers[i];
            var dir = t.Vel.Normalized();
            // the streak trails the round, so it never draws ahead of where it can hit
            mm.SetInstanceTransform(i, new Transform3D(Flyer.Orient(dir, Vector3.Up, Vector3.Right),
                t.Pos - dir * (TracerLength * 0.5f)));
        }
        mm.VisibleInstanceCount = n;
    }

    // ------------------------------------------------------------------------------------
    // firing
    // ------------------------------------------------------------------------------------

    private void TickGuns(float dt)
    {
        var p = LocalPlayer?.Invoke();
        var craft = p?.Vehicle as Flyer;
        bool paraglider = craft is Canopy { Kind: RideKind.Paraglider };
        Armed = p != null && (craft is Plane or Helicopter || paraglider);
        _cooldown -= dt;
        if (!Armed) { Ammo = Magazine; LeadPoint = null; return; }

        // rearmed on the ground
        if (p!.IsOnFloor()) Ammo = Magazine;

        var m = p.Flight;
        var att = m.Attitude == default ? new Basis(Vector3.Up, m.Yaw) : m.Attitude.Orthonormalized();
        Vector3 Muzzle(Vector3 local) => p.GlobalPosition + craft!.Pivot + att * (local - craft.Pivot);

        Vector3 dir, muzzle;
        if (craft is Plane)
        {
            // two wing guns, fixed along the nose, fired alternately
            muzzle = Muzzle(new Vector3(_barrel == 0 ? -1.7f : 1.7f, 1.3f, -1.2f));
            dir = -att.Z;
            AimPoint = Muzzle(new Vector3(0, 1.3f, -1.2f)) + dir * Boresight;
        }
        else if (paraglider)
        {
            // a gun in the pilot's hands, aimed where they look: the wing flies itself hands-off
            // for a moment, and a paraglider cannot point its nose at anything quickly anyway
            muzzle = p.GlobalPosition + p.GlobalBasis * new Vector3(0.25f, 1.15f, -0.5f);
            var cam = p.Camera;
            AimPoint = CrosshairPoint(p, cam);
            dir = (AimPoint - muzzle).Normalized();
        }
        else
        {
            // a chin turret that follows the camera: the helicopter flies where it looks anyway
            muzzle = Muzzle(new Vector3(0, 0.6f, -2.4f));
            var cam = p.Camera;
            AimPoint = CrosshairPoint(p, cam);
            dir = (AimPoint - muzzle).Normalized();
        }
        LeadPoint = FindLead(p, dir, muzzle);

        if (!PlayerInput.Held(PlayerInput.Fire) || _cooldown > 0f || Ammo <= 0) return;
        _cooldown = FireInterval;
        _barrel ^= 1;
        Ammo--;
        Shots++;
        _dronesWanted = true;

        // a hair of spread, so a long burst reads as a stream rather than one line
        var jitter = new Vector3(Rand(), Rand(), Rand()) * 0.004f;
        var vel = (dir + jitter).Normalized() * BulletSpeed + m.Velocity;
        Spawn(muzzle, vel, LocalId);
        if (Online) SendShot(muzzle, vel);
    }

    private float Rand() => (float)_rng.NextDouble() * 2f - 1f;

    /// <summary>
    /// A round goes to the server once; the server relays it to the players who can see the
    /// shooter (Net/InterestService), not to the whole map — someone 60 km away has no tracer to
    /// draw and no bullet that can reach them.
    /// </summary>
    private void SendShot(Vector3 muzzle, Vector3 vel) => RpcId(1, MethodName.Shot, muzzle, vel);

    /// <summary>
    /// What the crosshair is on: the first thing along the camera's line of sight — terrain or a
    /// body, or a target within a few metres of the line — else the turret's range. A gun that
    /// fires from the pilot's hands or the chin at a fixed point 600 m down the CAMERA's line
    /// crosses that line only out there: from a chase camera 7 m above the gun, a wing 60 m away
    /// dead under the crosshair was missed by 3 m every time.
    /// </summary>
    private Vector3 CrosshairPoint(FootPlayer me, Camera3D cam)
    {
        var from = cam.GlobalPosition;
        var fwd = -cam.GlobalBasis.Z;
        float dist = TurretRange;
        var query = PhysicsRayQueryParameters3D.Create(from, from + fwd * TurretRange);
        query.Exclude = new Godot.Collections.Array<Rid> { me.GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0) dist = (hit["position"].AsVector3() - from).Length();
        foreach (var (pos, _) in Targets(me))
        {
            float along = (pos - from).Dot(fwd);
            if (along > 5f && along < dist && (from + fwd * along).DistanceTo(pos) < 6f) dist = along;
        }
        return from + fwd * dist;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void Shot(Vector3 origin, Vector3 velocity)
    {
        if (!_server) return;
        long shooter = Multiplayer.GetRemoteSenderId();
        var interest = GetNodeOrNull<Net.InterestService>("../" + Net.InterestService.NodeName);
        foreach (int peer in Multiplayer.GetPeers())
            if (peer != shooter && interest?.ServerSees(peer, shooter) != false)
                RpcId(peer, MethodName.ShotFrom, shooter, origin, velocity);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ShotFrom(long shooter, Vector3 origin, Vector3 velocity) => Spawn(origin, velocity, shooter);

    private void Spawn(Vector3 origin, Vector3 velocity, long shooter)
    {
        if (_tracers.Count >= MaxTracers) _tracers.RemoveAt(0);
        _tracers.Add(new Tracer { Pos = origin, Vel = velocity, Shooter = shooter });

        var v = _voices[_voice = (_voice + 1) % _voices.Length];
        var (stream, pitch, db) = SfxSynth.GunBank.Pick(_rng);
        v.Stream = stream;
        v.PitchScale = pitch;
        v.VolumeDb = db - 4f;   // the slider is on the Sfx bus
        v.GlobalPosition = origin;
        v.Play();
    }

    // ------------------------------------------------------------------------------------
    // flight and hits
    // ------------------------------------------------------------------------------------

    private void StepTracers(float dt)
    {
        var space = GetWorld3D().DirectSpaceState;
        for (int i = _tracers.Count - 1; i >= 0; i--)
        {
            var t = _tracers[i];
            t.Age += dt;
            var next = t.Pos + t.Vel * dt;
            t.Vel += Vector3.Down * Rideable.Gravity * dt;

            var query = PhysicsRayQueryParameters3D.Create(t.Pos, next);
            query.Exclude = ExcludeFor(t.Shooter);
            var hit = space.IntersectRay(query);
            if (hit.Count > 0)
            {
                Hit(hit["collider"].AsGodotObject(), t.Shooter);
                _tracers.RemoveAt(i);
                continue;
            }
            if (WingHit(t.Pos, next, t.Shooter) is { } glider)
            {
                Hit(glider, t.Shooter);
                _tracers.RemoveAt(i);
                continue;
            }
            // this client's birds: no colliders, so a sphere test against the local list
            if (Birds.BirdLife.Instance?.TracerHit(t.Pos, next, t.Shooter == LocalId) != null)
            {
                if (t.Shooter == LocalId) { Hits++; HitFlash = 0.15f; }
                _tracers.RemoveAt(i);
                continue;
            }
            if (t.Age > Life) { _tracers.RemoveAt(i); continue; }
            t.Pos = next;
            _tracers[i] = t;
        }
    }

    /// <summary>
    /// A paraglider's wing is 10 m across and 8 m over the pilot, and it has no collider — only the
    /// pilot's capsule does — so every round through the fabric would miss. Tested as a box in the
    /// pilot's yaw frame (the wing hangs straight above whatever the pilot is doing; a remote copy
    /// knows no attitude anyway), and a hit on it is a hit on the pilot's rig.
    /// </summary>
    private FootPlayer? WingHit(Vector3 from, Vector3 to, long shooter)
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
        {
            if (node is not FootPlayer fp || fp.Ride != RideKind.Paraglider || IsShooter(fp, shooter)) continue;
            var inv = fp.GlobalTransform.AffineInverse();
            if (SegmentHitsBox(inv * from, inv * to, WingCentre, WingHalf)) return fp;
        }
        return null;
    }

    private static readonly Vector3 WingCentre = new(0, 7.6f, 0), WingHalf = new(5f, 1.1f, 1.3f);

    /// <summary>Slab test: does the segment a→b pass through the axis-aligned box?</summary>
    private static bool SegmentHitsBox(Vector3 a, Vector3 b, Vector3 centre, Vector3 half)
    {
        float t0 = 0f, t1 = 1f;
        var d = b - a;
        for (int k = 0; k < 3; k++)
        {
            float lo = centre[k] - half[k] - a[k], hi = centre[k] + half[k] - a[k];
            if (Mathf.Abs(d[k]) < 1e-6f) { if (lo > 0f || hi < 0f) return false; continue; }
            float ta = lo / d[k], tb = hi / d[k];
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = Mathf.Max(t0, ta);
            t1 = Mathf.Min(t1, tb);
            if (t0 > t1) return false;
        }
        return true;
    }

    /// <summary>The shooter's own body, which the rounds leave from inside.</summary>
    private Godot.Collections.Array<Rid> ExcludeFor(long shooter)
    {
        if (_exclude.TryGetValue(shooter, out var rids)) return rids;
        rids = new Godot.Collections.Array<Rid>();
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer fp && IsShooter(fp, shooter)) rids.Add(fp.GetRid());
        if (LocalPlayer?.Invoke() is { } me && shooter == LocalId) rids.Add(me.GetRid());
        return _exclude[shooter] = rids;
    }

    /// <summary>
    /// Whether this body is the one that fired. Our own rounds come from the local player only:
    /// every other body on this peer shares our authority offline (and a probe's second pilot does
    /// online too), so testing authority alone made every local body immune to our fire.
    /// </summary>
    private bool IsShooter(FootPlayer fp, long shooter) => shooter == LocalId
        ? fp == LocalPlayer?.Invoke()
        : fp.GetMultiplayerAuthority() == shooter;

    /// <summary>Damage only what this peer has authority over; the shooter's own peer shows the hit marker.</summary>
    private void Hit(GodotObject? collider, long shooter)
    {
        bool mine = shooter == LocalId;
        bool target = true, killed = false;
        switch (collider)
        {
            case FootPlayer fp:
                if (fp.IsMultiplayerAuthority()) fp.ShotHit(Damage);
                break;
            case VehicleBody vb when !vb.Wrecked:
                if (vb.IsMultiplayerAuthority())
                {
                    vb.Health -= Damage;
                    if (vb.Health <= 0f) { vb.Explode(); killed = true; }
                }
                break;
            case TargetDrone drone:
                killed = drone.TakeHit(Damage);
                break;
            default:
                target = false;
                break;
        }
        if (!mine || !target) return;
        Hits++;
        HitFlash = 0.15f;
        if (killed)
        {
            Kills++;
            LocalPlayer?.Invoke()?.Announce("KILL!", true);
        }
    }

    // ------------------------------------------------------------------------------------
    // aiming aid
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The target nearest the line of fire (within ~12°, 1.5 km), and where to put the guns to meet
    /// it: its position after the round's flight time, relative to the shooter's own motion,
    /// because the round leaves carrying the craft's velocity.
    /// </summary>
    private Vector3? FindLead(FootPlayer me, Vector3 dir, Vector3 muzzle)
    {
        Vector3? best = null;
        float bestCos = 0.978f;
        var myVel = me.Flight.Velocity;
        foreach (var (pos, vel) in Targets(me))
        {
            var to = pos - muzzle;
            float dist = to.Length();
            if (dist < 1f || dist > 1500f) continue;
            float cos = to.Dot(dir) / dist;
            if (cos < bestCos) continue;
            bestCos = cos;
            best = pos + (vel - myVel) * (dist / BulletSpeed);
        }
        return best;
    }

    private IEnumerable<(Vector3 Pos, Vector3 Vel)> Targets(FootPlayer me)
    {
        foreach (var node in GetTree().GetNodesInGroup(TargetDrone.Group))
            if (node is TargetDrone d) yield return (d.GlobalPosition, d.Velocity);
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer fp && fp != me)
            {
                yield return (fp.GlobalPosition + Vector3.Up, fp.Velocity);
                if (fp.Ride == RideKind.Paraglider) yield return (fp.GlobalTransform * WingCentre, fp.Velocity);
            }
    }

    // ------------------------------------------------------------------------------------
    // offline targets
    // ------------------------------------------------------------------------------------

    /// <summary>Spawns drones to fight, offline only, and only once the player has opened fire.</summary>
    private void TickDrones(float dt)
    {
        if (!DronesEnabled || !_dronesWanted || Online || !Armed) return;
        var p = LocalPlayer?.Invoke();
        if (p == null) return;

        int alive = GetTree().GetNodesInGroup(TargetDrone.Group).Count;
        _droneRespawn -= dt;
        if (alive >= DroneCount || _droneRespawn > 0f) return;
        _droneRespawn = alive == 0 ? 0f : 8f;

        // a circuit ahead of the player, at their altitude, so it is in view straight away
        float yaw = p.Flight.Yaw + Rand() * 0.8f;
        var ahead = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        var centre = p.GlobalPosition + ahead * (600f + 300f * (float)_rng.NextDouble());
        AddChild(TargetDrone.Circuit(Terrain, centre, 250f + 200f * (float)_rng.NextDouble(),
            42f + 12f * (float)_rng.NextDouble(), (float)_rng.NextDouble() * Mathf.Tau, _rng.Next(2) == 0));
    }
}
