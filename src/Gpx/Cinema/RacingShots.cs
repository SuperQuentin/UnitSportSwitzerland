using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Gpx.Cinema;

/// <summary>
/// Absolute Racing's shots: the race filmed by camera drones, the way MF Ghost covers a battle.
///
/// <para>
/// Every shot is a <see cref="DroneShot"/>: a body with a velocity, capped at 200 km/h and a
/// racing drone's acceleration, that flies at where the shot wants it. It never teleports along
/// with the car, so the camera has weight; and because it flies straight at its target it takes
/// shortcuts on its own, the way a real one does. <see cref="ApexCut"/> is built on exactly that:
/// it flies the chord of the corner ahead and hovers on its inside while the car slides round.
/// </para>
///
/// <para>
/// Nothing sits at ground level. The ground-rig shots this replaced (bumper, wheel, trackside)
/// spent most of a descent filming grass, and a drone that keeps its height sees the road over
/// the verge. The last shot is the director's fallback.
/// </para>
/// </summary>
public static class RacingShots
{
    public static Shot[] All() => new Shot[]
    {
        new ApexCut(), new ParallelTrack(), new DroneTopDown(), new LeadReverse(),
        new SwoopOver(), new DuelWide(), new DroneChase(),
    };
}

/// <summary>A camera drone: the flight model every racing shot shares.</summary>
public abstract class DroneShot : Shot
{
    /// <summary>200 km/h, what a racing camera drone does flat out.</summary>
    private const float MaxSpeed = 56f;

    /// <summary>About 2.5 g: quick, but it still has to lean into a change of direction.</summary>
    private const float MaxAccel = 25f;

    /// <summary>Gain from distance-to-target to speed. Higher is a stiffer, twitchier drone.</summary>
    private const float Gain = 1.6f;

    /// <summary>Never lower than this over the ground, and the same over the ground it is heading for.</summary>
    private const float MinAgl = 3.5f;

    protected Vector3 Pos;
    private Vector3 _vel, _lastTarget, _targetVel, _aim;
    private float _climb;
    private bool _flying;

    public override ShotFamily Family => ShotFamily.Free;
    public override float MinSeconds => 3f;
    public override float MaxSeconds => 7f;

    /// <summary>Where the drone wants to be this frame.</summary>
    protected abstract Vector3 Target(ShotContext ctx);

    /// <summary>What it points the camera at.</summary>
    protected virtual Vector3 Aim(ShotContext ctx) => ctx.Subject + Vector3.Up * 0.6f;

    protected virtual float Fov => 55f;

    /// <summary>Where the drone is when the director cuts to it. Its target, unless the move is the point.</summary>
    protected virtual Vector3 Start(ShotContext ctx) => Target(ctx);

    /// <summary>
    /// Puts the drone on station, as if it were already flying there when the director cut to it,
    /// with the car's own velocity so it does not start from a hover and fall behind.
    /// </summary>
    public override bool Begin(ShotContext ctx)
    {
        _climb = 0f;
        Pos = Floor(ctx, Start(ctx));
        _lastTarget = Floor(ctx, Target(ctx));
        _targetVel = _vel = ctx.Heading * ctx.Speed;
        _aim = Aim(ctx);
        _flying = true;
        return ctx.CanSee(Pos);
    }

    public override void Step(ShotContext ctx)
    {
        float dt = ctx.Dt * Mathf.Max(1f, ctx.ClockSpeed);   // track seconds: at 8x the drone is 8x quick too
        if (dt <= 0f || !_flying) { ctx.Place(Pos, _aim, Fov); return; }

        // A ridge or a house between lens and car: climb until the car is back in view, then
        // settle back down. Trees do not count, the sightline cut dissolves those.
        _climb = ctx.CanSee(Pos) ? Mathf.Max(0f, _climb - 3f * dt) : Mathf.Min(30f, _climb + 9f * dt);

        var target = Floor(ctx, Target(ctx) + Vector3.Up * _climb);
        // Feed-forward the target's own motion so a drone holding station alongside a 150 km/h car
        // does not trail it by the distance the spring needs to generate that speed.
        _targetVel = _targetVel.Lerp((target - _lastTarget) / dt, 1f - Mathf.Exp(-4f * dt));
        _lastTarget = target;

        var want = _targetVel + (target - Pos) * Gain;
        if (want.Length() > MaxSpeed) want = want.Normalized() * MaxSpeed;
        var dv = want - _vel;
        float step = MaxAccel * dt;
        _vel += dv.Length() > step ? dv.Normalized() * step : dv;
        Pos += _vel * dt;

        // terrain following: over the ground here and the ground 0.8 s ahead, never through it
        float floor = FloorAt(ctx, Pos);
        float ahead = FloorAt(ctx, Pos + new Vector3(_vel.X, 0, _vel.Z) * 0.8f);
        floor = Mathf.Max(floor, ahead - 1f);
        if (Pos.Y < floor) { Pos.Y = floor; if (_vel.Y < 0f) _vel.Y = 0f; }

        // a gimbal, not a tripod: the camera eases onto its subject rather than snapping to it
        _aim = _aim.Lerp(Aim(ctx), 1f - Mathf.Exp(-7f * dt));
        ctx.Place(Pos, _aim, Fov);
    }

    private static float FloorAt(ShotContext ctx, Vector3 p) => (ctx.Ground(p) ?? ctx.Subject.Y) + MinAgl;

    /// <summary>
    /// Over a forest a drone flies above the canopy: at 6 m it is inside the crowns and films black.
    /// The road through a wood is inside the forest polygon too, so a drone over the carriageway
    /// climbs as well, and looks down the cut through the trees.
    /// </summary>
    private const float Canopy = 24f;

    /// <summary>Wood under the drone or within 10 m of it: a narrow road through a forest is mapped
    /// as open ground, and at 6 m over it the frame is two walls of trees.</summary>
    private static bool Wooded(ShotContext ctx, Vector3 p)
    {
        foreach (var o in Around)
            if (ctx.Chunks.TryGetCover(p + o, out var c) && CoverFormat.IsWooded(c)) return true;
        return false;
    }

    private static readonly Vector3[] Around =
        { Vector3.Zero, new(10, 0, 0), new(-10, 0, 0), new(0, 0, 10), new(0, 0, -10) };

    /// <summary>The height a target is lifted to: over the ground, and over the trees where there are any.</summary>
    private Vector3 Floor(ShotContext ctx, Vector3 p)
    {
        float above = Wooded(ctx, p) || Wooded(ctx, Pos) ? Canopy : MinAgl;
        p.Y = Mathf.Max(p.Y, (ctx.Ground(p) ?? ctx.Subject.Y) + above);
        return p;
    }

    /// <summary>The course a few track seconds from now, for planning ahead of the car.</summary>
    protected static Vector3 Course(ShotContext ctx, double ahead) => ctx.Runner.CourseAt(ctx.Time + ahead);

    /// <summary>
    /// The sharpest corner the car reaches between <paramref name="from"/> and <paramref name="to"/>
    /// track seconds from now: its apex, the unit vector toward the inside, and how far it turns.
    /// </summary>
    protected static (Vector3 Apex, Vector3 Inside, float Turn) NextCorner(ShotContext ctx, double from, double to)
    {
        (Vector3, Vector3, float) best = (Vector3.Zero, Vector3.Zero, 0f);
        for (double t = from; t <= to; t += 0.25)
        {
            var p = Course(ctx, t);
            var before = Flat(p - Course(ctx, t - 1.0));
            var after = Flat(Course(ctx, t + 1.0) - p);
            if (before.LengthSquared() < 4f || after.LengthSquared() < 4f) continue;   // stopped
            before = before.Normalized();
            after = after.Normalized();
            float turn = before.AngleTo(after);
            // the change of direction points at the centre of the turn
            var inside = after - before;
            if (turn > best.Item3 && inside.LengthSquared() > 1e-4f) best = (p, inside.Normalized(), turn);
        }
        return best;
    }

    protected static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>The rival when it is close enough to share the frame.</summary>
    protected static Vector3? RivalNear(ShotContext ctx, float within) =>
        ctx.Rival is { } r && r.Avatar.GlobalPosition.DistanceTo(ctx.Subject) < within ? r.Avatar.GlobalPosition : null;
}

/// <summary>
/// The shortcut: sees a corner coming, flies straight across its inside while the car goes the
/// long way round, and hovers there a little high to watch it slide past — then falls in behind.
/// </summary>
public sealed class ApexCut : DroneShot
{
    private Vector3 _hover;
    private double _apexTime;
    public override string Name => "Drone apex cut";
    public override ShotScale Scale => ShotScale.Wide;
    public override ShotFamily Family => ShotFamily.Route;
    public override float MinSeconds => 3.5f;
    public override float MaxSeconds => 9f;
    public override float Weight => 1.6f;
    public override float Fit(CinemaEventKind kind) => kind switch
    {
        CinemaEventKind.Hairpin or CinemaEventKind.Switchbacks => 3.5f,
        CinemaEventKind.Bend => 2.5f,
        _ => 0.8f,
    };

    public override bool Begin(ShotContext ctx)
    {
        var (apex, inside, turn) = NextCorner(ctx, 1.5, 6.0);
        if (turn < 0.45f) return false;   // under ~25 degrees there is no inside worth cutting across
        // tighter corners get the drone closer in: a hairpin is watched from almost on top of it
        float into = Mathf.Lerp(20f, 11f, Mathf.Clamp((turn - 0.45f) / 1.2f, 0f, 1f));
        _hover = apex + inside * into + Vector3.Up * 10f;
        _apexTime = ctx.Time;
        for (double t = 1.5; t <= 6.0; t += 0.25)
            if (Course(ctx, t).DistanceSquaredTo(apex) < 1f) { _apexTime = ctx.Time + t; break; }
        return base.Begin(ctx) && ctx.CanSee(_hover);
    }

    // Start from the chase position; the hover point is ahead, across the inside of the bend.
    // Once the car is through the apex the drone drops in behind it and follows it out.
    protected override Vector3 Target(ShotContext ctx) =>
        ctx.Time < _apexTime + 1.2 ? _hover : ctx.Subject - ctx.Heading * 14f + Vector3.Up * 7f;

    protected override Vector3 Aim(ShotContext ctx) => ctx.Subject + ctx.Heading * 3f + Vector3.Up * 0.5f;
    protected override float Fov => 52f;

    // it starts where a chase drone would be, so the shortcut across the bend is on screen
    protected override Vector3 Start(ShotContext ctx) => ctx.Subject - ctx.Heading * 14f + Vector3.Up * 7f;

    public override bool StillGood(ShotContext ctx) => ctx.Time < _apexTime + 4.0;
}

/// <summary>
/// Alongside, a few metres up, matching the car's speed: the long-lens tracking shot. It takes the
/// inside of whatever bend is coming, so it runs the shorter line and stays level with the car.
/// </summary>
public sealed class ParallelTrack : DroneShot
{
    private float _side;
    public override string Name => "Drone parallel";
    public override ShotScale Scale => ShotScale.Medium;
    public override float Weight => 1.3f;
    public override float Fit(CinemaEventKind kind) => kind is CinemaEventKind.LongStraight or CinemaEventKind.SpeedSurge ? 2f : 1.2f;

    public override bool Begin(ShotContext ctx)
    {
        var (_, inside, turn) = NextCorner(ctx, 1.0, 5.0);
        _side = turn > 0.3f ? Mathf.Sign(inside.Dot(ctx.Right)) : (ctx.Rng.Randf() > 0.5f ? 1f : -1f);
        if (_side == 0f) _side = 1f;
        return base.Begin(ctx);
    }

    protected override Vector3 Target(ShotContext ctx) =>
        ctx.Subject + ctx.Right * _side * 15f + ctx.Heading * 2f + Vector3.Up * 6f;

    protected override float Fov => 42f;
}

/// <summary>Straight above and slightly behind, looking down: the whole slide and the line it draws.</summary>
public sealed class DroneTopDown : DroneShot
{
    public override string Name => "Drone top-down";
    public override ShotScale Scale => ShotScale.Wide;
    public override float Weight => 0.9f;
    public override float Fit(CinemaEventKind kind) => kind switch
    {
        CinemaEventKind.Hairpin or CinemaEventKind.Switchbacks => 2.5f,
        CinemaEventKind.Bend => 1.5f,
        _ => 0.7f,
    };

    protected override Vector3 Target(ShotContext ctx) => ctx.Subject - ctx.Heading * 9f + Vector3.Up * 30f;
    protected override Vector3 Aim(ShotContext ctx) => ctx.Subject + ctx.Heading * 4f;
    protected override float Fov => 50f;
}

/// <summary>Ahead of the car, flying backwards and looking at it — and at whoever is on its bumper.</summary>
public sealed class LeadReverse : DroneShot
{
    public override string Name => "Drone lead reverse";
    public override ShotScale Scale => ShotScale.Close;
    public override float Weight => 1.1f;
    public override float Fit(CinemaEventKind kind) => kind == CinemaEventKind.Overtake ? 2.5f : 1f;

    protected override Vector3 Target(ShotContext ctx) => ctx.Subject + ctx.Heading * 16f + Vector3.Up * 4.5f;

    protected override Vector3 Aim(ShotContext ctx) =>
        (RivalNear(ctx, 35f) is { } r ? (ctx.Subject + r) * 0.5f : ctx.Subject) + Vector3.Up * 0.6f;

    protected override float Fov => 48f;
}

/// <summary>Starts high up the road ahead and dives over the car as it passes, ending behind it.</summary>
public sealed class SwoopOver : DroneShot
{
    private double _start;
    public override string Name => "Drone swoop";
    public override ShotScale Scale => ShotScale.Wide;
    public override float MinSeconds => 4f;
    public override float MaxSeconds => 6f;
    public override float Weight => 0.9f;
    public override float Fit(CinemaEventKind kind) => kind is CinemaEventKind.LongStraight or CinemaEventKind.Summit ? 2f : 1f;

    public override bool Begin(ShotContext ctx) { _start = ctx.Time; return base.Begin(ctx); }

    protected override Vector3 Target(ShotContext ctx)
    {
        // over five track seconds: from 45 m ahead and 25 m up to 12 m behind and 6 m up
        float k = Mathf.Clamp((float)(ctx.Time - _start) / 5f, 0f, 1f);
        k = k * k * (3f - 2f * k);
        return ctx.Subject + ctx.Heading * Mathf.Lerp(45f, -12f, k) + Vector3.Up * Mathf.Lerp(25f, 6f, k);
    }

    protected override Vector3 Aim(ShotContext ctx) => ctx.Subject + ctx.Heading * 5f;
}

/// <summary>Wide and high beside a battle, both cars in frame, pulling back as the gap opens.</summary>
public sealed class DuelWide : DroneShot
{
    private float _side;
    public override string Name => "Drone duel";
    public override ShotScale Scale => ShotScale.Wide;
    public override float Weight => 1.8f;
    public override float Fit(CinemaEventKind kind) => kind == CinemaEventKind.Overtake ? 3f : 1.2f;

    public override bool Begin(ShotContext ctx)
    {
        if (RivalNear(ctx, 50f) == null) return false;
        _side = ctx.Rng.Randf() > 0.5f ? 1f : -1f;
        return base.Begin(ctx);
    }

    private static Vector3 Mid(ShotContext ctx) => RivalNear(ctx, 80f) is { } r ? (ctx.Subject + r) * 0.5f : ctx.Subject;

    protected override Vector3 Target(ShotContext ctx)
    {
        float gap = RivalNear(ctx, 80f) is { } r ? r.DistanceTo(ctx.Subject) : 10f;
        return Mid(ctx) + ctx.Right * _side * (12f + gap * 0.5f) - ctx.Heading * 6f + Vector3.Up * (9f + gap * 0.3f);
    }

    protected override Vector3 Aim(ShotContext ctx) => Mid(ctx) + Vector3.Up * 0.5f;

    public override bool StillGood(ShotContext ctx) => RivalNear(ctx, 80f) != null;
}

/// <summary>The fallback: behind and above, looking down the road the car is about to take.</summary>
public sealed class DroneChase : DroneShot
{
    public override string Name => "Drone chase";
    public override ShotScale Scale => ShotScale.Medium;
    public override float Weight => 0.8f;

    protected override Vector3 Target(ShotContext ctx) => ctx.Subject - ctx.Heading * 13f + Vector3.Up * 6.5f;
    protected override Vector3 Aim(ShotContext ctx) => ctx.Subject + ctx.Heading * 12f + Vector3.Up * 0.5f;
    protected override float Fov => 62f;
}
