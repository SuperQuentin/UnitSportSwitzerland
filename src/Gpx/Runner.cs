using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Gpx;

/// <summary>
/// One competitor: a track plus the avatar following it. Several runners share the race
/// clock, so they all start together and you can see who is ahead at any moment — which
/// is the point of ghost racing.
/// </summary>
public partial class Runner : Node3D
{
    public GpxTrack Track { get; private set; } = null!;

    /// <summary>The same track snapped to the road network, once matching has finished.</summary>
    public GpxTrack? Snapped { get; set; }

    /// <summary>Whether playback follows <see cref="Snapped"/>. Ignored until it exists.</summary>
    public bool UseSnapped { get; set; }

    /// <summary>
    /// The variant actually being played. Everything that reads the course — the runner, the
    /// ribbon, the leaderboard — goes through this, so the toggle can never move one and not
    /// the other.
    /// </summary>
    public GpxTrack Active => UseSnapped && Snapped != null ? Snapped : Track;

    public Color Tint { get; private set; }

    private WorldOrigin _origin = null!;
    private ChunkManager _chunks = null!;
    private MeshInstance3D _body = null!;
    private UnitSport.Avatar.HumanPalette _palette = null!;
    private float _stridePhase;
    private Vector3 _smoothPos;
    private bool _placed;

    /// <summary>
    /// Follow rate for the rendered position, per second. Even after the track itself is
    /// smoothed, real per-second pace variation makes the avatar surge and stall; easing
    /// the rendered position removes that without altering the recorded pacing. The lag
    /// this introduces is roughly 0.4 s — under a metre at running speed, and far less
    /// noticeable than the surging it removes. Raise it for a tighter, twitchier follow.
    /// </summary>
    private const float PositionFollow = 2.5f;

    /// <summary>Look-ahead used to derive facing, in seconds of travel.</summary>
    private const double HeadingLookahead = 2.5;

    public Node3D Avatar { get; private set; } = null!;
    public Vector3 Heading { get; private set; } = Vector3.Forward;
    public double Distance { get; private set; }
    public double Speed { get; private set; }
    public double ElevationDrift { get; private set; }

    /// <summary>True once this runner has reached the end of its own track.</summary>
    public bool Finished { get; private set; }

    public const float EyeHeight = 1.68f;

    /// <summary>Distinct, readable colours; extra runners wrap around.</summary>
    public static readonly Color[] Palette =
    {
        new(0.90f, 0.28f, 0.18f),   // red
        new(0.22f, 0.55f, 0.92f),   // blue
        new(0.96f, 0.76f, 0.15f),   // amber
        new(0.32f, 0.78f, 0.38f),   // green
        new(0.76f, 0.36f, 0.85f),   // violet
        new(0.20f, 0.80f, 0.80f),   // teal
    };

    public static Runner Create(GpxTrack track, ChunkManager chunks, WorldOrigin origin, int index) =>
        new()
        {
            Name = $"Runner{index}",
            Track = track,
            Tint = Palette[index % Palette.Length],
            _chunks = chunks,
            _origin = origin,
        };

    public override void _Ready()
    {
        Avatar = new Node3D { Name = "Avatar" };
        AddChild(Avatar);

        // A running figure, jerseyed in the runner's own tint so the leaderboard colour and the
        // avatar agree. One mesh, one material, one draw call — a race can have a dozen of these
        // on screen and each is a few hundred triangles. The mesh is rebuilt each frame from the
        // gait, which is the same cost again and buys legs that actually run.
        _palette = UnitSport.Avatar.HumanPalette.Default with { Jersey = Tint, Helmet = Tint };
        _body = new MeshInstance3D
        {
            Name = "Body",
            Mesh = UnitSport.Avatar.HumanMeshBuilder.BuildStride(_palette, 0f, 0f),
            MaterialOverride = UnitSport.Avatar.HumanMeshBuilder.Material(),
        };
        Avatar.AddChild(_body);

        _chunks.AddAnchor(Avatar);
    }

    public override void _ExitTree() => _chunks.RemoveAnchor(Avatar);

    /// <summary>Places the avatar for the shared race time.</summary>
    public void UpdateTo(double raceTime, double clockSpeed, double delta)
    {
        var course = Active;
        Finished = raceTime >= course.Duration;

        var (e, n, recordedEle, speed, distance) = course.Sample(raceTime);
        Speed = Finished ? 0 : speed;
        Distance = distance;

        var pos = _origin.ToWorld(e, n, recordedEle);
        if (_chunks.TryGetHeight(pos, out float ground))
        {
            ElevationDrift = recordedEle - ground;
            pos.Y = ground;
        }

        // Ease the rendered position toward the sampled one. This damps both the surge
        // left by per-second pace variation and the hop from the 2 m heightfield stepping
        // under a moving runner. Seeking (delta == 0) snaps, so scrubbing stays responsive.
        if (!_placed || delta <= 0)
        {
            _smoothPos = pos;
            _placed = true;
        }
        else
        {
            // scale with clock speed, or fast playback would lag badly behind
            float rate = PositionFollow * Mathf.Max(1f, (float)clockSpeed);
            _smoothPos = _smoothPos.Lerp(pos, 1f - Mathf.Exp(-rate * (float)delta));
        }
        pos = _smoothPos;

        // Facing comes from a look-ahead rather than the adjacent point: over a single
        // 1 Hz step the residual jitter still dominates the direction, which is what makes
        // the runner yaw from side to side like a boat.
        double ahead = Math.Min(raceTime + HeadingLookahead, course.Duration);
        double behind = Math.Max(raceTime - HeadingLookahead, 0);
        var (fe, fn, _, _, _) = course.Sample(ahead);
        var (be, bn, _, _, _) = course.Sample(behind);
        var dir = _origin.ToWorld(fe, fn, recordedEle) - _origin.ToWorld(be, bn, recordedEle);
        dir.Y = 0;

        if (dir.LengthSquared() > 1e-4f)
        {
            var target = dir.Normalized();
            // ease into the new facing so corners turn rather than snap
            Heading = delta > 0
                ? Heading.Slerp(target, 1f - Mathf.Exp(-5f * (float)delta)).Normalized()
                : target;
        }

        // basis built by hand: LookAt raises a Godot error on degenerate input, and an
        // error from a C# callback can bring the runtime down
        Avatar.GlobalTransform = new Transform3D(SafeBasis(Heading), pos);

        // The stride runs on the *replay* clock, not the wall clock: at 4x playback the legs
        // have to turn over four times as fast or the runner skates. Head bob is no longer
        // applied here — the gait raises and drops the hips itself, which is the real thing
        // the old sine wave was standing in for.
        if (delta > 0)
        {
            float scaled = (float)(delta * clockSpeed);
            _stridePhase = UnitSport.Avatar.HumanMeshBuilder.AdvancePhase(
                _stridePhase, (float)Speed, scaled);
        }

        _body.Mesh = UnitSport.Avatar.HumanMeshBuilder.BuildStride(
            _palette, (float)Speed, _stridePhase);
    }

    public static Basis SafeBasis(Vector3 forward)
    {
        var fwd = forward with { Y = 0 };
        if (fwd.LengthSquared() < 1e-8f) fwd = Vector3.Forward;
        fwd = fwd.Normalized();
        var right = Vector3.Up.Cross(fwd).Normalized();
        return new Basis(right, Vector3.Up, -fwd);   // Godot columns: right, up, back
    }
}
