using Godot;

namespace UnitSport.Player;

/// <summary>
/// Something a UFO's tractor beam can lift (#861): the seam other features plug into (a cow, #863).
/// Implement it on the node, <see cref="TractorBeams.Register"/> it when it enters the tree and
/// <see cref="TractorBeams.Unregister"/> it when it leaves. The beam service calls the three methods
/// only on the peer that simulates the thing (<see cref="BeamMovable"/>): the server for what it owns,
/// a client for what it owns, everyone offline. The thing moves itself and publishes its position
/// the way it always does, so every other peer sees it rise through its usual replication.
/// </summary>
public interface IBeamable
{
    /// <summary>Where it is, world space: what the cone is tested against.</summary>
    Vector3 BeamPosition { get; }

    /// <summary>This peer simulates it, and it may be lifted now (not carried, not too big, not being driven).</summary>
    bool BeamMovable { get; }

    /// <summary>Back on the ground: letting it down ends here.</summary>
    bool BeamGrounded { get; }

    /// <summary>
    /// One physics step held by a beam (lifted, or let down): move by <paramref name="velocity"/>
    /// over <paramref name="dt"/>, ignoring your own physics until <see cref="BeamRelease"/>.
    /// </summary>
    void BeamMove(Vector3 velocity, float dt);

    /// <summary>The beam has let go of it on the ground: back to its own physics (or rest).</summary>
    void BeamRelease();
}

/// <summary>
/// The tractor beams (#861, docs/notes/player/ufo.md): which saucers have theirs on, read from what
/// every peer already has (each pilot's replicated ride pose, <see cref="Ufo.BeamOn"/>), and the
/// service that moves the registered <see cref="IBeamable"/> things in them on whichever peer
/// simulates each (the server for its own, so the beam is server-authoritative like every moving
/// body: a client never moves what it does not own). A player on foot is lifted by their own body
/// (<c>FootPlayer.BeamPhysics</c>), as a blast pushes them, from the same list.
/// </summary>
public partial class TractorBeams : Node
{
    public const string NodeName = "TractorBeams";

    /// <summary>One beam on this tick: the saucer's pilot, the hatch (the cone's apex), the craft's velocity.</summary>
    public readonly record struct Beam(FootPlayer Pilot, Vector3 Apex, Vector3 Velocity);

    private static readonly List<IBeamable> Things = new();
    private static readonly List<Beam> Beams = new();
    private static ulong _tick = ulong.MaxValue;

    /// <summary>What each held thing is doing: lifted (true) or being let down (false).</summary>
    private readonly Dictionary<IBeamable, bool> _held = new();
    private readonly List<IBeamable> _drop = new();
    private float _time, _wakeIn;

    /// <summary>Things lifted or being let down right now (probes read it).</summary>
    public int Held => _held.Count;

    public static TractorBeams? Instance { get; private set; }

    /// <summary>Adds the service under <paramref name="world"/> (client, server and test worlds alike).</summary>
    public static TractorBeams Create(Node world)
    {
        var beams = new TractorBeams { Name = NodeName };
        world.AddChild(beams);
        return beams;
    }

    public override void _EnterTree() => Instance = this;
    public override void _ExitTree() { if (Instance == this) Instance = null; }

    /// <summary>A beamable thing entered the world.</summary>
    public static void Register(IBeamable thing) { if (!Things.Contains(thing)) Things.Add(thing); }

    /// <summary>It left the world (freed, claimed, filed).</summary>
    public static void Unregister(IBeamable thing)
    {
        Things.Remove(thing);
        Instance?._held.Remove(thing);
    }

    /// <summary>The beams on this physics tick, built by the first caller of the tick (from <see cref="PlayerSnapshot"/>).</summary>
    public static List<Beam> Active(SceneTree tree)
    {
        ulong tick = Engine.GetPhysicsFrames();
        if (tick == _tick) return Beams;
        _tick = tick;
        Beams.Clear();
        foreach (var s in PlayerSnapshot.Of(tree))
            if (s.Ride == RideKind.Ufo && Ufo.BeamOn(s.Player.Anim))
                Beams.Add(new Beam(s.Player, s.Pos + Ufo.HatchOffset, s.Vel));
        return Beams;
    }

    /// <summary>The beam <paramref name="point"/> is in (the nearest hatch), not <paramref name="except"/>'s own; null if none.</summary>
    public static Beam? At(SceneTree tree, Vector3 point, FootPlayer? except = null)
    {
        Beam? best = null;
        float bestDepth = float.MaxValue;
        foreach (var b in Active(tree))
        {
            if (b.Pilot == except || !TractorBeamMath.InCone(b.Apex, point)) continue;
            float depth = b.Apex.Y - point.Y;
            if (depth < bestDepth) { bestDepth = depth; best = b; }
        }
        return best;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _time += dt;
        var beams = Active(GetTree());
        if (beams.Count == 0 && _held.Count == 0) return;

        for (int i = Things.Count - 1; i >= 0; i--)
        {
            var thing = Things[i];
            if (thing is GodotObject o && !IsInstanceValid(o)) { Things.RemoveAt(i); _held.Remove(thing); continue; }
            if (!thing.BeamMovable) { _held.Remove(thing); continue; }
            var at = thing.BeamPosition;
            if (At(GetTree(), at) is { } beam)
            {
                _held[thing] = true;
                thing.BeamMove(TractorBeamMath.Pull(beam.Apex, at, beam.Velocity, _time), dt);
            }
            else if (_held.ContainsKey(thing))
            {
                // out of the cone, or the beam went off: let down gently, then let go on the ground
                _held[thing] = false;
                if (thing.BeamGrounded) _drop.Add(thing);
                else thing.BeamMove(TractorBeamMath.Lower, dt);
            }
        }
        foreach (var thing in _drop)
        {
            _held.Remove(thing);
            thing.BeamRelease();
        }
        _drop.Clear();

        // a dormant vehicle under this peer's own beam is asked awake, a few times a second: once it is
        // a real vehicle it is the server's, and the server lifts it like anything else it owns
        _wakeIn -= dt;
        if (_wakeIn <= 0f)
        {
            _wakeIn = 0.25f;
            foreach (var beam in beams)
                if (beam.Pilot.IsMultiplayerAuthority() && !beam.Pilot.NetProxy) WakeDormant(beam);
        }
    }

    private CylinderShape3D? _probe;

    /// <summary>Dormant vehicles in the cone under <paramref name="beam"/>: woken (<c>DormantVehicles.Wake</c>).</summary>
    private void WakeDormant(in Beam beam)
    {
        if (Vehicles.DormantVehicles.Instance is not { } dormant || GetViewport()?.World3D is not { } world) return;
        _probe ??= new CylinderShape3D { Height = TractorBeamMath.Reach, Radius = TractorBeamMath.RadiusAt(TractorBeamMath.Reach) };
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = _probe,
            Transform = new Transform3D(Basis.Identity, beam.Apex + Vector3.Down * (TractorBeamMath.Reach * 0.5f)),
            CollideWithAreas = false,
        };
        foreach (var hit in world.DirectSpaceState.IntersectShape(query, 16))
            if (hit["collider"].AsGodotObject() is Vehicles.DormantBody body && TractorBeamMath.InCone(beam.Apex, body.GlobalPosition + Vector3.Up))
                dormant.Wake(body.Slot);
    }

    private static readonly Core.RayQuery GroundRay = new();
    private static readonly Godot.Collections.Array<Rid> GroundExclude = new();

    /// <summary>
    /// The ground under <paramref name="at"/>: the first static thing below (terrain, a building, a
    /// roof; not another lifted thing, a player or a vehicle), from the physics a client has, else
    /// <paramref name="terrain"/>, the terrain's height there (all the server has), or NaN.
    /// </summary>
    public static float GroundUnder(Node3D node, Vector3 at, float terrain, Rid self)
    {
        if (node.GetViewport()?.World3D is not { } world) return terrain;
        GroundExclude.Clear();
        GroundExclude.Add(self);
        GroundRay.Forget();
        for (int i = 0; i < 6; i++)
        {
            var hit = GroundRay.Cast(world.DirectSpaceState, at + Vector3.Up * 0.3f, at + Vector3.Down * 80f, 1, GroundExclude);
            if (hit.Count == 0) return terrain;
            if (hit["collider"].AsGodotObject() is StaticBody3D) return hit["position"].AsVector3().Y;
            GroundExclude.Add(hit["rid"].AsRid());
            GroundRay.Forget();
        }
        return terrain;
    }
}
