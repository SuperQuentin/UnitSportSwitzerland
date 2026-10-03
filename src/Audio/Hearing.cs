using Godot;
using UnitSport.Interiors;
using UnitSport.Player;

namespace UnitSport.Audio;

/// <summary>
/// Where a long-playing source (a radio, a car stereo) is heard from, and how muffled, for the
/// ears on this machine (<see cref="Ears"/>, #261, #375). A speaker that uses it is
/// <c>TopLevel</c> and is placed here every frame rather than riding its parent.
///
/// <list type="bullet">
/// <item><b>In the cabin</b>: the stereo of the car the ears sit in plays from the dash, clear and
/// wide, whoever drives. A radio one carries plays where it hangs, panned softly.</item>
/// <item><b>Same space, something between</b>: five rays from the ears to the source and around
/// it, a few times a second; the share that is blocked sets how much is lost. A pillar or the
/// radio's own table takes a little, a building takes the highs and ~10 dB. Inside one building
/// the room's reflections fill in behind an obstacle, so it never goes past a mild dulling.
/// Bodies do not count.</item>
/// <item><b>Through a car's shell</b>: a stereo in a closed car (driven or parked) one is not in
/// is the bass thump through the glass; the ears in a closed car hear every other radio through it too.</item>
/// <item><b>The other space</b>: an interior is 3 km under its building, so a radio in a room is
/// silent in the street, and the street's radio silent in the room. Through a door this client
/// has linked (<see cref="DoorLink"/>), the source is moved through the doorway's own map and is
/// as clear as the leaf is open; with no link, it is put at the same spot on the other side
/// (<see cref="InteriorManager.SurfacePoint"/>) and heard through the walls: low and dull.</item>
/// </list>
///
/// <para>Level and cutoff ease over ~0.15 s, so a door swinging or a corner turned is a sweep, never a click.</para>
/// </summary>
public sealed class Hearing
{
    private const double PollSeconds = 0.2;
    /// <summary>Fully behind a wall outdoors, and fully behind something inside one building.</summary>
    private const float WallDb = -10f, WallCutoff = 700f, RoomDb = -4f, RoomCutoff = 2400f;
    /// <summary>A closed car's shell between the source and the ears.</summary>
    private const float ShellDb = -10f, ShellCutoff = 900f;
    /// <summary>From the other space, door shut (or no door near) and wide open.</summary>
    private const float ShutDb = -15f, ShutCutoff = 420f, OpenDb = -3f, OpenCutoff = 4500f;
    /// <summary>The dash, from the ears: a little down and ahead, so the cabin stereo sits centred.</summary>
    private static readonly Vector3 Dash = new(0f, -0.3f, -0.75f);

    /// <summary>The terrain height at a point, when known (set by the client world).</summary>
    public static Func<Vector3, float?>? Ground { get; set; }

    private readonly float _clearCutoff;
    private Vector3 _offset;
    private bool _ready;
    private float _blocked;
    private double _poll;
    private float _db, _cut, _wantDb, _wantCut;

    /// <summary>The extra level this frame, dB (0 = in the open), for the speaker to add to its own.</summary>
    public float Db => _db;

    /// <summary>
    /// What the last frame decided, for probes: "open", "own", "cabin", "shell", "wall", "room",
    /// "door", "walls".
    /// </summary>
    public string Path { get; private set; } = "open";

    /// <summary>Where the source sits on its parent, taken at <see cref="Attach"/>. For the probes.</summary>
    public Vector3 Offset => _offset;

    /// <summary>The share of the occlusion rays blocked at the last poll, 0..1. For the probes.</summary>
    public float Blocked => _blocked;

    public Hearing(float clearCutoff)
    {
        _clearCutoff = clearCutoff;
        _cut = _wantCut = clearCutoff;
    }

    /// <summary>
    /// Call from the speaker's <c>_Ready</c>: takes its offset from the parent, goes top level, and
    /// processes after <see cref="Ears"/> so it is placed from this frame's ear, not the last one.
    /// </summary>
    public void Attach(AudioStreamPlayer3D speaker)
    {
        _offset = speaker.Position;
        speaker.TopLevel = true;
        speaker.ProcessPriority = Ears.Priority + 1;
        _ready = true;
    }

    /// <summary>Places <paramref name="speaker"/> for this frame and sets its filter.</summary>
    public void Step(AudioStreamPlayer3D speaker, float dt)
    {
        if (!_ready || speaker.GetParent() is not Node3D parent || !parent.IsInsideTree()) return;
        var source = parent.GlobalTransform * _offset;
        var heard = source;
        float panning = 1f;
        if (Ears.FrameOf(speaker) is { } ear)
        {
            var listener = ear.Origin;
            if (Ears.Cabin is { } cabin && parent == cabin)
            {
                // the stereo of the car the ears sit in: the dash, in front, all of it
                Path = "cabin";
                heard = ear * Dash;
                panning = 0.35f;
                _wantDb = 0f;
                _wantCut = _clearCutoff;
            }
            else if (Ears.Body is { } body && parent == body)
            {
                // a radio in one's own hand or on one's back: right there, never behind anything
                Path = "own";
                panning = 0.45f;
                _wantDb = 0f;
                _wantCut = _clearCutoff;
            }
            else
            {
                bool srcIn = InteriorManager.InInteriorSpace(source), earIn = InteriorManager.InInteriorSpace(listener);
                if (srcIn == earIn)
                {
                    _poll -= dt;
                    if (_poll <= 0)
                    {
                        _poll = PollSeconds;
                        _blocked = Blocking(parent, listener, source);
                    }
                    // inside one building the room fills in behind a pillar: never more than a dulling
                    bool room = srcIn && earIn;
                    // one ray of five clipping a corner is a dulling, not a wall
                    Path = _blocked >= 0.4f ? room ? "room" : "wall" : "open";
                    _wantDb = (room ? RoomDb : WallDb) * _blocked;
                    _wantCut = LogLerp(_clearCutoff, room ? RoomCutoff : WallCutoff, _blocked);
                }
                else
                {
                    heard = Across(source, listener, srcIn, out float open);
                    Path = open >= 0 ? "door" : "walls";
                    open = Mathf.Max(open, 0f);
                    _wantDb = Mathf.Lerp(ShutDb, OpenDb, open);
                    _wantCut = Mathf.Lerp(ShutCutoff, OpenCutoff, open * open);
                }
                if (InClosedCar(parent))
                {
                    // the stereo of a closed car one is not in: the thump through the glass
                    Path = "shell";
                    _wantDb += ShellDb;
                    _wantCut = Mathf.Min(_wantCut, ShellCutoff);
                }
                if (Ears.Shut > 0f)
                {
                    // sat in a closed car oneself: every other radio comes through the shell
                    _wantDb += ShellDb * 0.8f * Ears.Shut;
                    _wantCut = LogLerp(_wantCut, Mathf.Min(_wantCut, 1400f), Ears.Shut);
                }
            }
        }
        else
        {
            Path = "open";
            _wantDb = 0f;
            _wantCut = _clearCutoff;
        }

        float k = 1f - Mathf.Exp(-dt / 0.15f);
        _db += (_wantDb - _db) * k;
        // the cutoff eases in octaves, not hertz: a linear sweep spends all its time up high
        _cut = Mathf.Exp(Mathf.Lerp(Mathf.Log(_cut), Mathf.Log(_wantCut), k));
        speaker.GlobalPosition = heard;
        speaker.AttenuationFilterCutoffHz = _cut;
        speaker.PanningStrength = panning;
    }

    private static float LogLerp(float a, float b, float t) => Mathf.Exp(Mathf.Lerp(Mathf.Log(a), Mathf.Log(b), t));

    /// <summary>Whether the source is a stereo in a closed car: a driver's body at the wheel, or a parked one.</summary>
    private static bool InClosedCar(Node3D parent) => parent switch
    {
        FootPlayer p => p.Ride != RideKind.OnFoot && FootPlayer.ClosedCabin(p.Ride),
        Vehicles.VehicleBody v => FootPlayer.ClosedCabin(v.Kind),
        _ => false,
    };

    private static readonly Core.RayQuery OccluderRay = new();
    private static readonly Godot.Collections.Array<Rid> OccluderExclude = new();

    /// <summary>
    /// The share (0..1) of five rays, from the ears to the source and to points 0.4-0.8 m around
    /// it (four around it and one over the top), that something solid stops: one slim pillar blocks one or two, a wall all five. A hit
    /// near the source (the table a radio stands on, the car its stereo is in) or near the ears
    /// does not count, nor does a body or the source's own collider.
    /// </summary>
    private static float Blocking(Node3D source3D, Vector3 listener, Vector3 source)
    {
        var space = source3D.GetWorld3D()?.DirectSpaceState;
        if (space == null || listener.DistanceSquaredTo(source) < 1f) return 0f;
        var dir = (source - listener).Normalized();
        var side = dir.Cross(Vector3.Up);
        side = side.LengthSquared() > 1e-4f ? side.Normalized() : Vector3.Right;
        var up = side.Cross(dir).Normalized();
        int blocked = 0;
        for (int i = 0; i < 5; i++)
        {
            var target = i switch
            {
                0 => source,
                1 => source + side * 0.4f,
                2 => source - side * 0.4f,
                3 => source + up * 0.4f,
                // over the top rather than under: under is the ground the source stands on
                _ => source + up * 0.8f,
            };
            if (Stopped(space, source3D, listener, target)) blocked++;
        }
        return blocked / 5f;
    }

    private static bool Stopped(PhysicsDirectSpaceState3D space, Node3D source3D, Vector3 listener, Vector3 target)
    {
        // one query and one exclude array for every source, refilled per call (#221)
        var exclude = OccluderExclude;
        exclude.Clear();
        if (source3D is CollisionObject3D own) exclude.Add(own.GetRid());
        for (int i = 0; i < 4; i++)
        {
            OccluderRay.Forget();   // the array changed in place since the last cast
            var hit = OccluderRay.Cast(space, listener, target, uint.MaxValue, exclude);
            if (hit.Count == 0) return false;
            var at = hit["position"].AsVector3();
            // touching the source (whatever it stands on) or the ears (one's own collar): no wall
            if (at.DistanceTo(target) < 0.35f || at.DistanceTo(listener) < 0.3f) return false;
            // a person in the way is no wall
            if (hit["collider"].AsGodotObject() is FootPlayer body) { exclude.Add(body.GetRid()); continue; }
            return true;
        }
        return false;
    }

    /// <summary>
    /// The source moved into the listener's space: through a linked doorway when one leads from the
    /// source's building (or into the listener's), with how open it stands (0..1); else straight
    /// across, with <paramref name="open"/> = -1.
    /// </summary>
    private static Vector3 Across(Vector3 source, Vector3 listener, bool srcIn, out float open)
    {
        open = -1f;
        var interiors = InteriorManager.Instance;
        var inside = srcIn ? source : listener;
        string? plan = interiors?.LayoutAt(inside)?.Key;
        if (interiors != null && plan != null)
        {
            DoorLink? best = null;
            float bestD = float.MaxValue;
            foreach (var link in interiors.Links.Values)
            {
                if (link.Plan != plan) continue;
                // the doorway nearer the source and the listener both: the one the sound comes through
                float d = (srcIn ? link.Inside : link.Outside).Origin.DistanceTo(source)
                          + (srcIn ? link.Outside : link.Inside).Origin.DistanceTo(listener);
                if (d < bestD) { bestD = d; best = link; }
            }
            if (best != null)
            {
                open = best.Swing;
                return (srcIn ? best.ToOutside : best.ToInside) * source;
            }
        }
        if (srcIn) return InteriorManager.SurfacePoint(source, Ground);
        float floor = Ground?.Invoke(source) ?? source.Y;
        return source with { Y = InteriorManager.InteriorBaseY + (source.Y - floor) };
    }
}
