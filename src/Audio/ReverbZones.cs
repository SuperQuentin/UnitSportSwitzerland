using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Audio;

/// <summary>
/// Chooses a reverb for where the listener is and eases <see cref="SfxBus.Reverb"/> toward it.
///
/// <para>
/// Reverb is what tells the ear whether it is in a tunnel, a room, a wood or a valley, and it is
/// nearly free. The environment is read from the world, not authored: a handful of physics rays
/// from the listener (up and to both sides for a tunnel, four long horizontal ones for a valley
/// slap-back) plus the cover raster for woods. Collision only exists near the player, so a ray
/// that hits nothing is a valid answer meaning open space, never an error.
/// </para>
///
/// <para>
/// Sampling runs in the physics tick at ~4 Hz; easing runs every frame over about a second, so a
/// change of environment is a crossfade and never a switch you can hear.
/// </para>
/// </summary>
public partial class ReverbZones : Node
{
    private const float PollSeconds = 0.25f;
    private const float EaseSeconds = 0.4f;   // time constant: ~1 s to settle
    private const float CoverRange = 12f;
    private const float SlapRange = 300f;

    private readonly Func<Node3D?> _listener;
    private readonly Func<bool> _indoors;
    private readonly ChunkManager? _chunks;

    private float _room = 0.3f, _damp = 0.5f, _wet;
    private float _tRoom = 0.3f, _tDamp = 0.5f, _tWet;
    private double _timer;

    /// <summary>What the last poll decided, for probes and the perf overlay.</summary>
    public string Environment { get; private set; } = "open";

    public ReverbZones(Func<Node3D?> listener, Func<bool> indoors, ChunkManager? chunks = null)
    {
        _listener = listener;
        _indoors = indoors;
        _chunks = chunks;
    }

    public ReverbZones() : this(() => null, () => false) { }

    public override void _Ready() => Name = "ReverbZones";

    public override void _PhysicsProcess(double delta)
    {
        _timer -= delta;
        if (_timer > 0) return;
        _timer = PollSeconds;
        Poll();
    }

    public override void _Process(double delta)
    {
        if (SfxBus.Reverb is not { } r) return;
        float k = 1f - Mathf.Exp(-(float)delta / EaseSeconds);
        _room += (_tRoom - _room) * k;
        _damp += (_tDamp - _damp) * k;
        _wet += (_tWet - _wet) * k;
        r.RoomSize = _room;
        r.Damping = _damp;
        r.Wet = _wet;
    }

    private void Poll()
    {
        if (_indoors())
        {
            Set("indoors", 0.3f, 0.6f, 0.25f);
            return;
        }
        if (_listener() is not { } l || !l.IsInsideTree()) return;
        var space = l.GetWorld3D()?.DirectSpaceState;
        if (space == null) return;

        var head = l.GlobalPosition;
        // sideways in the horizontal plane of the listener's heading; a world axis would make the
        // verdict change as the player turns in a tunnel
        var fwd = -l.GlobalTransform.Basis.Z; fwd.Y = 0;
        fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
        var right = fwd.Cross(Vector3.Up);

        float up = Cast(space, head, Vector3.Up, CoverRange);
        float left = Cast(space, head, -right, CoverRange);
        float rgt = Cast(space, head, right, CoverRange);

        if (up < CoverRange && left < CoverRange && rgt < CoverRange)
        {
            Set("tunnel", 0.85f, 0.3f, 0.45f);
            return;
        }

        bool wooded = _chunks != null && _chunks.TryGetCover(head, out var c) && CoverFormat.IsWooded(c);
        if (wooded)
        {
            Set("forest", 0.4f, 0.55f, 0.12f);
            return;
        }
        if (up < 6f)
        {
            // eaves, a bridge, an overhang: covered but not enclosed
            Set("covered", 0.45f, 0.45f, 0.16f);
            return;
        }

        // open air: how much rock is standing around to throw the sound back?
        float nearest = SlapRange;
        int hits = 0;
        for (int i = 0; i < 4; i++)
        {
            var dir = fwd.Rotated(Vector3.Up, Mathf.Pi * 0.25f + i * Mathf.Pi * 0.5f);
            float d = Cast(space, head, dir, SlapRange);
            if (d < SlapRange) { hits++; nearest = Mathf.Min(nearest, d); }
        }
        if (hits >= 2 && nearest < SlapRange)
        {
            // walls close by raise the wet a little; a canyon is louder than a far valley side
            float close = 1f - nearest / SlapRange;
            Set("valley", 0.6f + 0.2f * close, 0.4f, 0.05f + 0.14f * close);
        }
        else if (head.Y > 2200f)
            Set("high", 0.5f, 0.5f, 0.02f);
        else
            Set("open", 0.5f, 0.5f, 0.04f);
    }

    /// <summary>Distance to the first hit along <paramref name="dir"/>, or <paramref name="range"/> for none.</summary>
    private static float Cast(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 dir, float range)
    {
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * range);
        var hit = space.IntersectRay(q);
        return hit.Count > 0 ? from.DistanceTo((Vector3)hit["position"]) : range;
    }

    private void Set(string env, float room, float damp, float wet)
    {
        Environment = env;
        if (GameSettings.Current.EngineVoice == EngineVoice.Ps1)
        {
            // the PlayStation's SPU reverb is a fixed hall: a touch wetter and darker than a
            // real space would be, and it is part of that console's sound
            wet = Mathf.Min(1f, wet * 1.25f + 0.03f);
            damp = Mathf.Min(1f, damp + 0.15f);
            room = Mathf.Min(1f, room + 0.05f);
        }
        _tRoom = room; _tDamp = damp; _tWet = wet;
    }
}
