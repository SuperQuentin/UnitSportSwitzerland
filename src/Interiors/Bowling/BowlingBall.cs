using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A bowling ball's run down the lane (#1323), as a pure function of its release: every peer computes
/// the same path from the same numbers, so the roller's ball rolls the moment it leaves the hand and
/// nobody's is ever corrected on the lane. Only the pin deck is the server's (<see cref="PinDeck"/>).
///
/// <para>
/// A sphere on a plane with sliding friction: while the ball's contact point slips, friction pushes
/// against the slip, slowing the slide and turning the spin into roll. The bowler's side rotation is
/// the hook: on the oiled front of the lane friction is low and the ball skids nearly straight; on the
/// dry back end it bites, and the sideways slip of a spinning ball curves it toward the pocket until
/// it rolls, after which it runs straight. A ball whose centre passes a lane's edge drops into the
/// gutter and runs down it to the pit.
/// </para>
/// <para>
/// Lane frame: x across the lane from its centre (+x the bowler's left), z from the foul line toward the pins, y up from the
/// lane's surface (the ball's centre at <see cref="BowlingSizes.BallRadius"/>).
/// </para>
/// </summary>
public static class BowlingBall
{
    /// <summary>
    /// What a bowler gives the ball: where it crosses the foul line, its line, its speed, its revs and
    /// the tilt of its axis toward the hook side (−1..1: + is a right-hander's hook, toward +x, the
    /// bowler's left as they face the pins; − a left-hander's).
    /// </summary>
    public readonly record struct Release(float X, float Angle, float Speed, float Revs, float Axis)
    {
        /// <summary>Within a real bowler's range: on the lane (its centre on the edge boards at most), a few degrees, 3-11 m/s, up to ~450 rpm.</summary>
        public Release Clamped() => new(
            Mathf.Clamp(X, -BowlingSizes.HalfLane + 0.02f, BowlingSizes.HalfLane - 0.02f),
            Mathf.Clamp(Angle, -0.12f, 0.12f), Mathf.Clamp(Speed, 3f, 11f), Mathf.Clamp(Revs, 0f, 48f), Mathf.Clamp(Axis, -1f, 1f));

        public int[] Pack() => new[] { Q(X, 1e4f), Q(Angle, 1e5f), Q(Speed, 1e3f), Q(Revs, 1e2f), Q(Axis, 1e4f) };

        public static Release Unpack(int[] v) => v.Length < 5 ? default
            : new Release(v[0] / 1e4f, v[1] / 1e5f, v[2] / 1e3f, v[3] / 1e2f, v[4] / 1e4f).Clamped();

        // on the wire as integers: the server and the roller compute the path from the very same numbers
        static int Q(float x, float scale) => Mathf.RoundToInt(x * scale);
    }

    /// <summary>The ball's state at one moment of its run.</summary>
    public readonly record struct State(Vector3 Pos, Vector3 Vel, Vector3 Spin);

    /// <summary>
    /// A computed run: the ball's centre and turn after every <see cref="Step"/>, where it reaches the
    /// deck (or that it went into the gutter), and its state there for the deck's simulation.
    /// </summary>
    public sealed class Run
    {
        public readonly List<Vector3> Pos = new();
        public readonly List<Quaternion> Turn = new();
        /// <summary>Seconds from the release to the hand-over to the deck (or to the pit, for a gutter ball).</summary>
        public float Time => (Pos.Count - 1) * Step;
        public bool Gutter;
        /// <summary>The time it left the lane for the gutter, NaN if it never did.</summary>
        public float GutterAt = float.NaN;
        /// <summary>The ball as the deck takes it over (meaningless for a gutter ball).</summary>
        public State AtDeck;

        /// <summary>The ball at <paramref name="t"/> seconds after its release, between two steps.</summary>
        public (Vector3 Pos, Quaternion Turn) At(float t)
        {
            if (Pos.Count == 0) return (Vector3.Zero, Quaternion.Identity);
            float f = Mathf.Clamp(t / Step, 0, Pos.Count - 1);
            int i = Mathf.Min((int)f, Pos.Count - 2);
            if (i < 0) return (Pos[0], Turn[0]);
            float k = f - i;
            return (Pos[i].Lerp(Pos[i + 1], k), Turn[i].Slerp(Turn[i + 1], k));
        }
    }

    /// <summary>The run's step: 240 Hz, what keeps a 10 m/s ball's slip settling cleanly.</summary>
    public const float Step = 1f / 240f;

    private const float Gravity = 9.81f;
    /// <summary>Sliding friction on the oil and on the dry back end [E: 0.01-0.04 and 0.15-0.25].</summary>
    private const float OilFriction = 0.025f, DryFriction = 0.18f;
    /// <summary>A house pattern's oil length on a full lane [E: 38-42 ft], and the metre it fades over.</summary>
    public const float OilLength = 12f, OilFade = 1.2f;
    /// <summary>A ball's moment of inertia, as a share of m R² (a solid sphere 0.4; a house ball is close).</summary>
    private const float Inertia = 0.4f;
    /// <summary>The gutter's depth [S: 0.048 m]: a ball in it drops this much.</summary>
    private const float GutterDepth = 0.048f;
    /// <summary>The largest axis tilt a bowler gives [E]: 70° of side rotation.</summary>
    private const float MaxTilt = 1.22f;

    /// <summary>Where the run hands the ball to the deck: this far before the head pin.</summary>
    public const float HandOver = 0.35f;

    /// <summary>
    /// The ball's run on <paramref name="lane"/> from <paramref name="release"/>, until
    /// it reaches the deck (or, in the gutter, the pit).
    /// </summary>
    public static Run Roll(BowlingSizes.Run lane, Release release)
    {
        release = release.Clamped();
        float R = BowlingSizes.BallRadius;
        float oil = OilLength * lane.Scale;
        float deck = lane.HeadPin - HandOver, pit = lane.LaneEnd + 0.1f;
        var pos = new Vector3(release.X, R, 0);
        var vel = new Vector3(Mathf.Sin(release.Angle), 0, Mathf.Cos(release.Angle)) * release.Speed;
        // the spin: forward roll and side rotation, the axis tilted by the bowler; the side part hooks
        float tilt = release.Axis * MaxTilt;
        var spin = new Vector3(Mathf.Cos(tilt), 0, -Mathf.Sin(tilt)) * release.Revs;
        var turn = Quaternion.Identity;
        var run = new Run();
        run.Pos.Add(pos);
        run.Turn.Add(turn);
        for (int i = 1; ; i++)
        {
            if (!run.Gutter)
            {
                float mu = pos.Z < oil ? OilFriction : pos.Z < oil + OilFade ? Mathf.Lerp(OilFriction, DryFriction, (pos.Z - oil) / OilFade) : DryFriction;
                // the contact point's velocity: v + ω × (0, −R, 0)
                var slip = new Vector3(vel.X + R * spin.Z, 0, vel.Z - R * spin.X);
                float s = slip.Length();
                if (s > 1e-4f)
                {
                    var dir = slip / s;
                    // friction can at most stop the slip in this step: the velocity and the spin both
                    // move to cut it, and a sphere's slip falls (1 + 1/k) times as fast as its speed
                    float dv = Mathf.Min(mu * Gravity * Step, s / (1 + 1 / Inertia));
                    vel -= dir * dv;
                    // torque of −dir·μmg at the contact (0, −R, 0): ω' = r × F / (k m R²)
                    var torque = new Vector3(0, -R, 0).Cross(-dir * dv);
                    spin += torque / (Inertia * R * R);
                }
                pos += vel * Step;
                if (Mathf.Abs(pos.X) > BowlingSizes.HalfLane)
                {
                    // over the edge: into the gutter, running straight down it
                    run.Gutter = true;
                    run.GutterAt = i * Step;
                    pos = new Vector3(Mathf.Sign(pos.X) * (BowlingSizes.HalfLane + BowlingSizes.Gutter / 2), R - GutterDepth, pos.Z);
                    vel = new Vector3(0, 0, vel.Z);
                    spin = new Vector3(vel.Z / R, 0, 0);
                }
            }
            else pos += vel * Step;
            // the ball turns as it spins, for the eye
            if (spin.LengthSquared() > 1e-8f)
                turn = (new Quaternion(spin.Normalized(), spin.Length() * Step) * turn).Normalized();
            bool end = run.Gutter ? pos.Z >= pit : pos.Z >= deck;
            run.Pos.Add(pos);
            run.Turn.Add(turn);
            if (end)
            {
                run.AtDeck = new State(pos, vel, spin);
                return run;
            }
            // a ball that somehow stops (it cannot on a lane, but never loop forever)
            if (i > 240 * 20) { run.AtDeck = new State(pos, vel, spin); return run; }
        }
    }
}
