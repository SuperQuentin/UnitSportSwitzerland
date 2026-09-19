using Godot;
using UnitSport.Core;
using UnitSport.Player;
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
    private MeshInstance3D? _body;
    private UnitSport.Avatar.Cyclist? _cyclist;
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

    /// <summary>
    /// Longest chord the facing may be taken across, in metres.
    ///
    /// <para>
    /// The look-ahead exists to outrun GPS jitter, so it is naturally phrased in seconds - but
    /// seconds are the wrong unit once the course is a road. A road-matched track has real
    /// corners, sharper than anything the smoothed fixes contained, and 2.5 s either side is
    /// ~17 m on foot and 60-100 m on a bike. Through a hairpin the two samples chord straight
    /// across the bend, so the body faces the exit while it is still entering; on a switchback
    /// they land on opposite legs and the difference collapses toward the degenerate guard
    /// below, which FREEZES the heading outright. Bounding the chord by distance fixes both.
    /// </para>
    /// </summary>
    private const double MaxHeadingChordM = 12.0;

    /// <summary>
    /// Floor for the same window. Shrink it further and the per-fix jitter the look-ahead was
    /// introduced to defeat comes straight back.
    /// </summary>
    private const double MinHeadingLookahead = 0.8;

    public Node3D Avatar { get; private set; } = null!;
    public Vector3 Heading { get; private set; } = Vector3.Forward;
    public double Distance { get; private set; }
    public double Speed { get; private set; }
    public double ElevationDrift { get; private set; }

    /// <summary>True once this runner has reached the end of its own track.</summary>
    public bool Finished { get; private set; }

    /// <summary>Gait cycle position, 0..1. Held across an edit skip rather than advanced.</summary>
    public float StridePhase => _stridePhase;

    /// <summary>
    /// Camera mounts on this runner, in world space, refreshed every <see cref="UpdateTo"/>.
    ///
    /// <para>
    /// Taken from the same gait the mesh is built from, so a head-mounted camera rides the real
    /// stride bob. Cached rather than computed on demand because several shots may ask in one
    /// frame and the gait solve is not free.
    /// </para>
    /// </summary>
    public Vector3 EyeWorld { get; private set; }
    public Vector3 HeadWorld { get; private set; }
    public Vector3 ChestWorld { get; private set; }
    public Vector3 HipWorld { get; private set; }
    public Vector3 ShoulderLeftWorld { get; private set; }
    public Vector3 ShoulderRightWorld { get; private set; }
    public Vector3 FootWorld { get; private set; }

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

        if (Track.Kind == RideKind.RoadBike)
        {
            // The bike already exists — this is the same rig the player mounts with E, not a
            // second one built for GPX. A ghost recorded on a ride should look ridden, the same
            // way one recorded on foot looks run.
            _cyclist = UnitSport.Avatar.Cyclist.CreateWithTint(Tint);
            Avatar.AddChild(_cyclist);
        }
        else
        {
            // A running figure, jerseyed in the runner's own tint so the leaderboard colour and
            // the avatar agree. One mesh, one material, one draw call — a race can have a dozen
            // of these on screen and each is a few hundred triangles. The mesh is rebuilt each
            // frame from the gait, which is the same cost again and buys legs that actually run.
            _palette = UnitSport.Avatar.HumanPalette.Default with { Jersey = Tint, Helmet = Tint };
            _body = new MeshInstance3D
            {
                Name = "Body",
                Mesh = UnitSport.Avatar.HumanMeshBuilder.BuildStride(_palette, 0f, 0f),
                MaterialOverride = UnitSport.Avatar.HumanMeshBuilder.Material(),
            };
            Avatar.AddChild(_body);
        }

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

        // A road-matched course already sits on the road surface, bridge decks and tunnel bores
        // included, so re-draping it onto the terrain would undo exactly that. Only a raw
        // recording gets draped, because its own elevation is GPS noise.
        if (_chunks.TryGetHeight(pos, out float ground))
        {
            ElevationDrift = recordedEle - ground;
            if (!course.ElevationIsSurface) pos.Y = ground;
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
        //
        // The window is capped by DISTANCE as well as time (see MaxHeadingChordM): the faster
        // the travel the shorter it has to be, or a real road corner is chorded across instead
        // of turned through. Slow sections keep the full 2.5 s and are unaffected.
        double window = Math.Clamp(
            speed > 0.01 ? MaxHeadingChordM / (2.0 * speed) : HeadingLookahead,
            MinHeadingLookahead, HeadingLookahead);

        double ahead = Math.Min(raceTime + window, course.Duration);
        double behind = Math.Max(raceTime - window, 0);
        var (fe, fn, _, _, _) = course.Sample(ahead);
        var (be, bn, _, _, _) = course.Sample(behind);
        var dir = _origin.ToWorld(fe, fn, recordedEle) - _origin.ToWorld(be, bn, recordedEle);
        dir.Y = 0;

        if (dir.LengthSquared() > 1e-4f)
        {
            var target = dir.Normalized();
            // Ease into the new facing so corners turn rather than snap - at the clock's rate,
            // not the wall clock's. The position follow above is already scaled this way; leaving
            // the facing on a fixed rate meant that at 8x the body kept up with the course while
            // its heading lagged eight times as far behind every corner.
            float turn = 5f * Mathf.Max(1f, (float)clockSpeed);
            Heading = delta > 0
                ? Heading.Slerp(target, 1f - Mathf.Exp(-turn * (float)delta)).Normalized()
                : target;
        }

        // basis built by hand: LookAt raises a Godot error on degenerate input, and an
        // error from a C# callback can bring the runtime down
        Avatar.GlobalTransform = new Transform3D(SafeBasis(Heading), pos);

        if (_cyclist != null)
        {
            // Same formula Bicycle.cs drives the player's own cadence from: watts don't exist for
            // a recording, but speed does, and cadence is what makes the legs agree with it.
            // Cyclist already freezes the cranks below ~0.01 rpm, so a finished or paused ghost
            // simply stops pedalling rather than needing a floor here too.
            _cyclist.CadenceRpm = Speed > 0.1
                ? Mathf.Clamp((float)Speed * 60f / 6.2f, 40f, 112f) : 0f;
        }
        else
        {
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

            _body!.Mesh = UnitSport.Avatar.HumanMeshBuilder.BuildStride(
                _palette, (float)Speed, _stridePhase);
        }

        RefreshMounts();
    }

    /// <summary>
    /// Lifts the gait's mount points into world space.
    ///
    /// <para>
    /// The mounts arrive already flipped to the mesh's own -Z facing, so the avatar transform is
    /// all that is needed. Getting that wrong puts a shoulder camera on the wrong shoulder and
    /// aims it out of the back of the runner's head.
    /// </para>
    /// </summary>
    private void RefreshMounts()
    {
        // A cyclist does not have a gait phase to mount from - the pose is fixed, the legs just
        // turn a crank around it - so this reads the same joint table the mesh itself is built
        // from (HumanPose.Cycling) rather than a running gait sampled with a meaningless speed.
        var mounts = _cyclist != null
            ? UnitSport.Avatar.HumanMeshBuilder.MountsForPose(UnitSport.Avatar.HumanPose.Cycling)
            : UnitSport.Avatar.HumanMeshBuilder.MountsFor((float)Speed, _stridePhase);
        var frame = Avatar.GlobalTransform;

        EyeWorld = frame * mounts.Eye;
        HeadWorld = frame * mounts.Head;
        ChestWorld = frame * mounts.Chest;
        HipWorld = frame * mounts.Hip;
        ShoulderLeftWorld = frame * mounts.ShoulderL;
        ShoulderRightWorld = frame * mounts.ShoulderR;

        // whichever foot is lower is the one on the ground, which is the one worth filming
        var left = frame * mounts.FootL;
        var right = frame * mounts.FootR;
        FootWorld = left.Y <= right.Y ? left : right;
    }

    public static Basis SafeBasis(Vector3 forward)
    {
        var fwd = forward with { Y = 0 };
        if (fwd.LengthSquared() < 1e-8f) fwd = Vector3.Forward;
        fwd = fwd.Normalized();
        // forward x up, not up x forward: the other order gives a left-handed basis, which
        // mirrors the avatar. Facing still looks right, because the -Z column is unaffected, so
        // it hides well - but the arms swing on the wrong sides, and any camera mounted on a
        // shoulder ends up on the opposite one.
        var right = fwd.Cross(Vector3.Up).Normalized();
        return new Basis(right, Vector3.Up, -fwd);   // Godot columns: right, up, back
    }
}
