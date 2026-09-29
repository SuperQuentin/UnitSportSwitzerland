using Godot;
using UnitSport.Terrain;

namespace UnitSport.Gpx.Cinema;

/// <summary>How much of the frame the subject fills. The director never cuts between two the same.</summary>
public enum ShotScale { Close, Medium, Wide }

/// <summary>Where the camera lives. Used to avoid two rig shots back to back.</summary>
public enum ShotFamily { Rig, Free, Route }

/// <summary>
/// Everything a shot may read, and the only two ways it may write.
///
/// <para>
/// Shots never touch the camera's transform directly — they call <see cref="Place"/>, which
/// builds a right-handed basis. That is deliberate: the hand-built basis in this file's neighbour
/// had its cross product the wrong way round and rendered the whole world mirrored for weeks, so
/// there is now exactly one place that maths lives.
/// </para>
/// </summary>
public sealed class ShotContext
{
    public required Runner Runner { get; init; }
    public required Camera3D Camera { get; init; }
    public required ChunkManager Chunks { get; init; }
    public required RandomNumberGenerator Rng { get; init; }

    /// <summary>Track time, which runs at <see cref="ClockSpeed"/> times screen time.</summary>
    public double Time { get; set; }

    /// <summary>Screen seconds since the last frame. Shot pacing is always in these.</summary>
    public float Dt { get; set; }

    /// <summary>
    /// Playback multiplier. Anything phrased as "time until" has to account for it.
    ///
    /// <para>
    /// At 8x the subject covers eight times the ground per second of screen time, so a camera
    /// placed "four seconds ahead" is passed in half a second, and an easing rate tuned for 1x
    /// lags eight times as far behind. <see cref="Runner"/> already scales its position follow by
    /// this for exactly the same reason.
    /// </para>
    /// </summary>
    public float ClockSpeed { get; set; } = 1f;

    /// <summary>Follow rate corrected for the clock, so easing looks the same at any speed.</summary>
    public float Follow(float rate) => rate * Mathf.Max(1f, ClockSpeed);

    public Vector3 Subject => Runner.Avatar.GlobalPosition;
    public Vector3 Head => Runner.HeadWorld;
    public Vector3 Eye => Runner.EyeWorld;
    public Vector3 Chest => Runner.ChestWorld;
    public Vector3 Foot => Runner.FootWorld;
    public Vector3 ShoulderLeft => Runner.ShoulderLeftWorld;
    public Vector3 ShoulderRight => Runner.ShoulderRightWorld;

    public Vector3 Heading => Runner.Heading;
    public float Speed => (float)Runner.Speed;

    /// <summary>The subject's right, in world space.</summary>
    public Vector3 Right
    {
        get
        {
            var h = Heading;
            return new Vector3(-h.Z, 0, h.X).Normalized();
        }
    }

    /// <summary>Minimum air beneath any camera.</summary>
    private const float MinClearance = 0.5f;

    /// <summary>How far below the surface still counts as "legitimately under something".</summary>
    private const float TunnelDepth = 4f;

    private SphereShape3D? _probe;

    /// <summary>
    /// Moves a camera out of anything solid.
    ///
    /// <para>
    /// Applied to every placement rather than left to each shot, because "do not film from inside
    /// a wall" is a property of cameras, not of any particular shot — and a shot added later would
    /// otherwise have to remember.
    /// </para>
    ///
    /// <para>
    /// The depth guard matters more than it looks. Terrain height is the <i>surface</i>, so inside
    /// a tunnel it reports the mountain overhead; clamping to it unconditionally would fire the
    /// camera up through the rock the moment the run entered a bore. A camera well below the
    /// surface is under a bridge deck or in a tunnel, which are both places a camera may be, so
    /// only a small correction is applied.
    /// </para>
    /// </summary>
    public Vector3 MakeSafe(Vector3 p)
    {
        if (Ground(p) is { } g && p.Y < g + MinClearance && p.Y > g - TunnelDepth)
            p.Y = g + MinClearance;

        // Buildings and walls carry real collision, so a small sphere finds them. Rising is the
        // right escape: over a wall you get a vantage, sideways you get another wall.
        _probe ??= new SphereShape3D { Radius = 0.35f };
        var space = Camera.GetWorld3D().DirectSpaceState;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            var query = new PhysicsShapeQueryParameters3D
            {
                Shape = _probe,
                Transform = new Transform3D(Basis.Identity, p),
            };
            if (space.IntersectShape(query, 1).Count == 0) break;
            p.Y += 1.2f;
        }

        return p;
    }

    /// <summary>Points the camera, always with a proper (determinant +1) basis.</summary>
    public void Place(Vector3 position, Vector3 target, float fov = 70f)
    {
        position = MakeSafe(Runner.KeepOutside(position));
        Camera.GlobalPosition = position;

        // The lens widens the angle; the post-process bends it. Applied here because Place is the
        // single chokepoint every one of the eleven shots already routes through - each of them
        // writes its own FOV every frame, so anywhere else would simply be overwritten.
        Camera.Fov = fov * LensProfile.Current.FovBias;

        var dir = target - position;
        if (dir.LengthSquared() < 1e-6f) return;
        dir = dir.Normalized();

        var up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.999f ? Vector3.Forward : Vector3.Up;
        var right = dir.Cross(up).Normalized();          // forward x up, never up x forward
        Camera.GlobalBasis = new Basis(right, right.Cross(dir).Normalized(), -dir);
    }

    /// <summary>Terrain height, or null where the tile has not streamed in.</summary>
    public float? Ground(Vector3 at) => Chunks.TryGetHeight(at, out float h) ? h : null;

    /// <summary>
    /// Ground height for a point close beside the runner - AnkleCam, LowHeroPass - which trusts
    /// the runner's OWN elevation as a floor, not just raw terrain.
    ///
    /// <para>
    /// <see cref="Ground"/> samples the bare terrain grid, but a runner on a road is not always
    /// AT terrain height: a graded cut, a low embankment, a bridge deck all sit somewhere else,
    /// and <c>Runner.Avatar</c> already carries the right answer for wherever it actually is
    /// (road-matched or draped, whichever applies). For a camera placed within a couple of
    /// metres of the runner, that is a far better local reference than the bare grid — using
    /// terrain alone put low shots' cameras UNDER the rendered road surface on exactly the
    /// stretches where the two disagree, which is also where it mattered most: a low angle is
    /// the one that shows the ground clipping through the lens.
    /// </para>
    ///
    /// <para>
    /// Capped by <paramref name="maxDrop"/> rather than just taking the runner's height outright,
    /// so a shot legitimately lower than the runner - AnkleCam sits below Subject by design - is
    /// not dragged back up to it.
    /// </para>
    /// </summary>
    public float GroundNear(Vector3 at, float maxDrop = 1.2f) =>
        Mathf.Max(Ground(at) ?? Subject.Y, Subject.Y - maxDrop);

    /// <summary>Raises a point so it clears the ground. A camera inside a hill films nothing.</summary>
    public Vector3 Lift(Vector3 p, float above)
    {
        if (Ground(p) is { } g && p.Y < g + above) p.Y = g + above;
        return p;
    }

    /// <summary>
    /// Whether the subject can actually be seen from here.
    ///
    /// <para>
    /// The single most important test in the whole mode. Without it the director happily cuts to
    /// a camera behind a ridge and films a hillside for six seconds.
    /// </para>
    /// </summary>
    public bool CanSee(Vector3 from)
    {
        var space = Camera.GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(from, Head);
        return space.IntersectRay(query).Count == 0;
    }
}

/// <summary>One way of filming the runner.</summary>
public abstract class Shot
{
    public abstract string Name { get; }
    public abstract ShotScale Scale { get; }
    public abstract ShotFamily Family { get; }

    public virtual float MinSeconds => 3.5f;
    public virtual float MaxSeconds => 7f;

    /// <summary>Relative likelihood before context is considered.</summary>
    public virtual float Weight => 1f;

    /// <summary>Extra weight for the moment about to happen. 0 means never pick it for this.</summary>
    public virtual float Fit(CinemaEventKind kind) => 1f;

    /// <summary>Chooses a placement. False means "not from here" and the director tries another.</summary>
    public abstract bool Begin(ShotContext ctx);

    /// <summary>Drives the camera. Called once per frame with the film's own step, never real time.</summary>
    public abstract void Step(ShotContext ctx);

    /// <summary>False once the shot stops working — subject behind terrain, or too far away.</summary>
    public virtual bool StillGood(ShotContext ctx) => true;
}
