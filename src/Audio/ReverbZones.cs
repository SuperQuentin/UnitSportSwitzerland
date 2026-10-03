using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Audio;

/// <summary>
/// Chooses a reverb for where the ears are (<see cref="Ears"/>) and eases the buses' reverbs
/// toward it (<see cref="SfxBus"/>).
///
/// <para>
/// Reverb is what tells the ear whether it is in a tunnel, a room, a wood or a valley, and it is
/// nearly free. The environment is read from the world, not authored: a handful of physics rays
/// from the ears (up and to both sides for a tunnel or a street, four long ones for a valley
/// echo), the cover raster for woods, and indoors the room the ears stand in. Collision only
/// exists near the player, so a ray that hits nothing is a valid answer meaning open space.
/// </para>
///
/// <para>
/// Open air is not a dead studio (#375): the ground right under the ears sends a short, faint,
/// dark reflection back, which is most of what "outside" sounds like. A valley gives an echo
/// (the round trip to the rock as the pre-delay), not a hall. A church is a stone hall; a room's
/// tail follows its volume; a car cabin is a small dead box.
/// </para>
///
/// <para>
/// Sampling runs in the physics tick at ~4 Hz; easing runs every frame over about a second, so a
/// change of environment is a crossfade and never a switch you can hear. <c>_Process</c> also
/// closes the world's cabin filter as the ears get into a car.
/// </para>
/// </summary>
public partial class ReverbZones : Node
{
    private const float PollSeconds = 0.25f;
    private const float EaseSeconds = 0.4f;   // time constant: ~1 s to settle
    private const float CoverRange = 12f;
    /// <summary>Facades on both sides within this: a street canyon.</summary>
    private const float StreetRange = 25f;
    private const float SlapRange = 300f;
    /// <summary>Upward tilt of the slap-back rays (~11°): enough to clear the slope underfoot.</summary>
    private const float SlapRise = 0.2f;

    private readonly Func<Node3D?> _listener;
    private readonly Func<bool> _indoors;
    private readonly ChunkManager? _chunks;

    private float _room = 0.08f, _damp = 0.9f, _wet, _pre = 6f, _fb;
    private float _tRoom = 0.08f, _tDamp = 0.9f, _tWet, _tPre = 6f, _tFb;
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
        SfxBus.SetCabin(Ears.Shut);
        if (SfxBus.Reverb is not { } r) return;
        float k = 1f - Mathf.Exp(-(float)delta / EaseSeconds);
        _room += (_tRoom - _room) * k;
        _damp += (_tDamp - _damp) * k;
        _wet += (_tWet - _wet) * k;
        _pre += (_tPre - _pre) * k;
        _fb += (_tFb - _fb) * k;
        Enclosure += (_tEnclosure - Enclosure) * k;
        Apply(r, 1f);
        // one's own steps ring in the same room, a little drier: they are right under the ears
        if (SfxBus.PlayerReverb is { } p) Apply(p, 0.8f);
        // music is mixed drier: a song drowned in reverb is mud, but the room still tells
        if (SfxBus.MusicReverb is { } m) Apply(m, 0.7f);
    }

    private void Apply(AudioEffectReverb r, float wetScale)
    {
        r.RoomSize = _room;
        r.Damping = _damp;
        r.Wet = _wet * wetScale;
        r.PredelayMsec = _pre;
        r.PredelayFeedback = _fb;
    }

    private void Poll()
    {
        // sat in a car: a small dead box of glass and cloth, whatever is outside
        if (Ears.Cabin != null)
        {
            Set("cabin", 0.05f, 0.9f, 0.05f, 2f);
            return;
        }
        if (_indoors())
        {
            Indoors();
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
        float left = Cast(space, head, -right, StreetRange);
        float rgt = Cast(space, head, right, StreetRange);

        if (up < CoverRange && left < CoverRange && rgt < CoverRange)
        {
            Set("tunnel", 0.85f, 0.3f, 0.4f, 12f);
            return;
        }

        bool wooded = _chunks != null && _chunks.TryGetCover(head, out var c) && CoverFormat.IsWooded(c);
        if (wooded)
        {
            // trunks and leaves scatter: a soft, damped, diffuse tail with no echo in it
            Set("forest", 0.45f, 0.82f, 0.07f, 20f);
            return;
        }
        if (up < 6f)
        {
            // eaves, a bridge, an overhang: covered but not enclosed
            Set("covered", 0.28f, 0.6f, 0.09f, 8f);
            return;
        }
        if (left < StreetRange && rgt < StreetRange)
        {
            // facades on both sides: a street canyon, the flutter between them
            float wide = (left + rgt) / (2f * StreetRange);
            Set("street", 0.38f - 0.12f * wide, 0.55f, 0.09f - 0.04f * wide, 6f + 20f * wide);
            return;
        }

        // open air: how much rock is standing around to throw the sound back? The rays climb a
        // little: level rays hit the slope the player stands on (in Switzerland that is nearly
        // everywhere), which made every hillside a "valley" and put a hall on every footstep.
        // A real wall has to stand up and face the listener on at least three sides.
        float nearest = SlapRange;
        int hits = 0;
        for (int i = 0; i < 4; i++)
        {
            var dir = fwd.Rotated(Vector3.Up, Mathf.Pi * 0.25f + i * Mathf.Pi * 0.5f);
            dir = (dir + Vector3.Up * SlapRise).Normalized();
            float d = Cast(space, head, dir, SlapRange);
            if (d < SlapRange) { hits++; nearest = Mathf.Min(nearest, d); }
        }
        if (hits >= 3 && nearest < SlapRange)
        {
            // a gorge has no hall in it, it has an echo: the round trip to the nearest rock face as
            // the pre-delay, a little of it fed back for the second bounce, faint and dark
            float close = 1f - nearest / SlapRange;
            float echoMs = Mathf.Clamp(2f * nearest / 343f * 1000f, 20f, 500f);
            Set("valley", 0.12f, 0.85f, 0.03f + 0.05f * close, echoMs, 0.22f);
        }
        else if (head.Y > 2200f)
            // thin, still air high up: almost nothing comes back
            Set("high", 0.05f, 0.95f, 0.015f, 4f);
        else
            // the field: only the ground right under the ears answers, short, faint and dark
            Set("open", 0.08f, 0.9f, 0.03f, 6f);
    }

    /// <summary>Inside a building: the size of the room the ears are in picks the tail; a church is a stone hall.</summary>
    private void Indoors()
    {
        if (InteriorManager.Instance is { Current: { } layout, CurrentNode: { } node } && Ears.Of(node) is { } ear)
        {
            if (layout.Type == BuildingType.Church)
            {
                Set("church", 0.9f, 0.3f, 0.32f, 35f);
                return;
            }
            float volume = RoomVolume(layout, node.GlobalTransform.AffineInverse() * ear);
            if (volume > 250f) { Set("hall", 0.62f, 0.45f, 0.24f, 15f); return; }
            if (volume > 40f) { Set("indoors", 0.42f, 0.5f, 0.19f, 8f); return; }
            if (volume > 0f) { Set("room", 0.25f, 0.62f, 0.13f, 4f); return; }
        }
        Set("indoors", 0.38f, 0.55f, 0.18f, 8f);
    }

    /// <summary>The volume of the room around an interior-local point, m³, or 0 when it is in none.</summary>
    private static float RoomVolume(InteriorLayout layout, Vector3 local)
    {
        int best = -1;
        float bestDy = float.MaxValue;
        for (int f = 0; f < layout.Floors.Count; f++)
        {
            float dy = local.Y - layout.FloorY(f);
            if (dy < -0.5f || dy >= bestDy) continue;
            bestDy = dy;
            best = f;
        }
        if (best < 0) return 0f;
        foreach (var r in layout.Floors[best].Rooms)
            if (local.X >= r.X0 && local.X <= r.X1 && local.Z >= r.Z0 && local.Z <= r.Z1)
                return r.Area * r.Span * layout.StoreyHeight;
        return 0f;
    }

    /// <summary>Distance to the first hit along <paramref name="dir"/>, or <paramref name="range"/> for none.</summary>
    private static float Cast(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 dir, float range)
    {
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * range);
        var hit = space.IntersectRay(q);
        return hit.Count > 0 ? from.DistanceTo((Vector3)hit["position"]) : range;
    }

    /// <summary>
    /// How enclosed the listener is, 0 (open air) to 1 (indoors), eased like the bus reverb. Voices
    /// with a built-in tail (<see cref="PsxSpuVoice"/>) scale it by this, or engines echo outdoors.
    /// </summary>
    public static float Enclosure { get; private set; }
    private float _tEnclosure;

    private void Set(string env, float room, float damp, float wet, float predelayMs, float feedback = 0f)
    {
        Environment = env;
        _tEnclosure = env switch
        {
            "indoors" or "room" or "hall" or "church" or "tunnel" => 1f,
            "cabin" => 0.6f,
            "covered" or "street" => 0.5f,
            _ => 0f,
        };
        if (GameSettings.Current.EngineVoice == EngineVoice.Ps1)
        {
            // the PlayStation's SPU reverb is a fixed hall: a touch wetter and darker than a
            // real space would be, and it is part of that console's sound
            // (outdoors stays near-dry: a constant floor put that hall on every sound in the open)
            wet = Mathf.Min(1f, wet * 1.25f);
            damp = Mathf.Min(1f, damp + 0.1f);
            room = Mathf.Min(1f, room + 0.05f);
        }
        _tRoom = room; _tDamp = damp; _tWet = wet; _tPre = predelayMs; _tFb = feedback;
    }
}
