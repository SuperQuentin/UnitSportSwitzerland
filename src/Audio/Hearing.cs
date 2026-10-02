using Godot;
using UnitSport.Interiors;

namespace UnitSport.Audio;

/// <summary>
/// Where a long-playing source (a radio, a car stereo) is heard from, and how muffled, for the
/// listener on this machine (#261). A speaker that uses it is <c>TopLevel</c> and is placed here
/// every frame rather than riding its parent.
///
/// <list type="bullet">
/// <item><b>Same space, a wall between</b>: one ray from the ear to the source, a few times a
/// second. A building, the terrain or a rock in the way take the highs and some level off.
/// Bodies (players, the holder's own capsule) do not count.</item>
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
    /// <summary>Through a wall in the same space.</summary>
    private const float WallDb = -9f, WallCutoff = 900f;
    /// <summary>From the other space, door shut (or no door near) and wide open.</summary>
    private const float ShutDb = -15f, ShutCutoff = 420f, OpenDb = -3f, OpenCutoff = 4500f;

    /// <summary>The terrain height at a point, when known (set by the client world).</summary>
    public static Func<Vector3, float?>? Ground { get; set; }

    private readonly float _clearCutoff;
    private Vector3 _offset;
    private bool _ready, _occluded;
    private double _poll;
    private float _db, _cut, _wantDb, _wantCut;

    /// <summary>The extra level this frame, dB (0 = in the open), for the speaker to add to its own.</summary>
    public float Db => _db;

    /// <summary>What the last frame decided, for probes: "open", "wall", "door", "walls".</summary>
    public string Path { get; private set; } = "open";

    public Hearing(float clearCutoff)
    {
        _clearCutoff = clearCutoff;
        _cut = _wantCut = clearCutoff;
    }

    /// <summary>Call from the speaker's <c>_Ready</c>: takes its offset from the parent and goes top level.</summary>
    public void Attach(AudioStreamPlayer3D speaker)
    {
        _offset = speaker.Position;
        speaker.TopLevel = true;
        _ready = true;
    }

    /// <summary>Places <paramref name="speaker"/> for this frame and sets its filter.</summary>
    public void Step(AudioStreamPlayer3D speaker, float dt)
    {
        if (!_ready || speaker.GetParent() is not Node3D parent || !parent.IsInsideTree()) return;
        var source = parent.GlobalTransform * _offset;
        var heard = source;
        var ear = speaker.GetViewport()?.GetCamera3D();
        if (ear != null)
        {
            var listener = ear.GlobalPosition;
            bool srcIn = InteriorManager.InInteriorSpace(source), earIn = InteriorManager.InInteriorSpace(listener);
            if (srcIn == earIn)
            {
                _poll -= dt;
                if (_poll <= 0)
                {
                    _poll = PollSeconds;
                    _occluded = Occluded(speaker, listener, source);
                }
                Path = _occluded ? "wall" : "open";
                _wantDb = _occluded ? WallDb : 0f;
                _wantCut = _occluded ? WallCutoff : _clearCutoff;
            }
            else
            {
                heard = Across(source, listener, srcIn, out float open);
                Path = open >= 0 ? "door" : "walls";
                open = Mathf.Max(open, 0f);
                _wantDb = Mathf.Lerp(ShutDb, OpenDb, open);
                _wantCut = Mathf.Lerp(ShutCutoff, OpenCutoff, open * open);
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
    }

    /// <summary>Anything solid on the line, other than a body or the source itself.</summary>
    private static bool Occluded(Node3D from, Vector3 listener, Vector3 source)
    {
        var space = from.GetWorld3D()?.DirectSpaceState;
        if (space == null || listener.DistanceSquaredTo(source) < 1f) return false;
        var exclude = new Godot.Collections.Array<Rid>();
        for (int i = 0; i < 3; i++)
        {
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(listener, source, uint.MaxValue, exclude));
            if (hit.Count == 0) return false;
            var at = hit["position"].AsVector3();
            if (at.DistanceTo(source) < 0.7f) return false;   // the radio's own box, the car it is in
            // a person in the way (the holder's own capsule under a third-person camera) is no wall
            if (hit["collider"].AsGodotObject() is CharacterBody3D body) { exclude.Add(body.GetRid()); continue; }
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
