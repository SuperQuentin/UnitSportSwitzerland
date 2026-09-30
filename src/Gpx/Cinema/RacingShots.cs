using Godot;

namespace UnitSport.Gpx.Cinema;

/// <summary>
/// Absolute Racing's shots: how Initial D films a battle, rebuilt for cars. Short and low, the
/// horizon tilted with the slide, the camera on the outside of the bend, and the chaser in frame
/// whenever there is one. The last one is the director's fallback.
/// </summary>
public static class RacingShots
{
    public static Shot[] All() => new Shot[]
    {
        new BumperCam(), new SideTracking(), new FrontReverse(), new HairpinCrane(),
        new TracksideSweep(), new DuelChase(), new WheelCam(), new RacingChase(),
    };
}

/// <summary>Low behind the bumper, the horizon leaning with the slide: the road rushing at the lens.</summary>
public sealed class BumperCam : Shot
{
    private Vector3 _pos;
    public override string Name => "Bumper cam";
    public override ShotScale Scale => ShotScale.Close;
    public override ShotFamily Family => ShotFamily.Rig;
    public override float MinSeconds => 2f;
    public override float MaxSeconds => 4f;
    public override float Fit(CinemaEventKind kind) => kind is CinemaEventKind.LongStraight or CinemaEventKind.SpeedSurge ? 2f : 1f;

    public override bool Begin(ShotContext ctx) { _pos = Want(ctx); return true; }

    private static Vector3 Want(ShotContext ctx)
    {
        var p = ctx.Subject - ctx.Nose * 2.9f;
        p.Y = ctx.GroundNear(p) + 0.55f;
        return p;
    }

    public override void Step(ShotContext ctx)
    {
        _pos = _pos.Lerp(Want(ctx), 1f - Mathf.Exp(-ctx.Follow(12f) * ctx.Dt));
        ctx.Place(_pos, ctx.Subject + ctx.Heading * 25f + Vector3.Up * 0.6f, 78f, ctx.Slip * 0.35f);
    }
}

/// <summary>Alongside at wheel height on the outside of the bend, a long lens, tracking with the car.</summary>
public sealed class SideTracking : Shot
{
    private Vector3 _pos;
    private float _side;
    public override string Name => "Side tracking";
    public override ShotScale Scale => ShotScale.Medium;
    public override ShotFamily Family => ShotFamily.Rig;
    public override float MinSeconds => 2.2f;
    public override float MaxSeconds => 4.5f;
    public override float Fit(CinemaEventKind kind) => kind is CinemaEventKind.Bend ? 2f : kind is CinemaEventKind.Hairpin ? 1.5f : 1f;

    public override bool Begin(ShotContext ctx)
    {
        // the outside of the slide: a car drifting left (nose left of travel) swings its tail right
        _side = ctx.Slip > 0.05f ? 1f : ctx.Slip < -0.05f ? -1f : (ctx.Rng.Randf() > 0.5f ? 1f : -1f);
        _pos = Want(ctx);
        return ctx.CanSee(_pos);
    }

    private Vector3 Want(ShotContext ctx)
    {
        var p = ctx.Subject + ctx.Right * _side * 5.5f + ctx.Heading * 1.5f;
        p.Y = ctx.GroundNear(p) + 0.9f;
        return p;
    }

    public override void Step(ShotContext ctx)
    {
        _pos = _pos.Lerp(Want(ctx), 1f - Mathf.Exp(-ctx.Follow(8f) * ctx.Dt));
        ctx.Place(_pos, ctx.Subject + Vector3.Up * 0.6f, 48f, -_side * 0.06f);
    }
}

/// <summary>Ahead of the car looking back at it — and at whoever is on its bumper.</summary>
public sealed class FrontReverse : Shot
{
    private Vector3 _pos;
    public override string Name => "Front reverse";
    public override ShotScale Scale => ShotScale.Medium;
    public override ShotFamily Family => ShotFamily.Rig;
    public override float MinSeconds => 2f;
    public override float MaxSeconds => 4f;
    public override float Weight => 1.2f;
    public override float Fit(CinemaEventKind kind) => kind == CinemaEventKind.Overtake ? 2.5f : 1f;

    public override bool Begin(ShotContext ctx) { _pos = Want(ctx); return ctx.CanSee(_pos); }

    private static Vector3 Want(ShotContext ctx) => ctx.Lift(ctx.Subject + ctx.Heading * 11f + Vector3.Up * 1.4f, 1f);

    public override void Step(ShotContext ctx)
    {
        _pos = _pos.Lerp(Want(ctx), 1f - Mathf.Exp(-ctx.Follow(9f) * ctx.Dt));
        var aim = ctx.Rival is { } r && r.Avatar.GlobalPosition.DistanceTo(ctx.Subject) < 35f
            ? (ctx.Subject + r.Avatar.GlobalPosition) * 0.5f + Vector3.Up * 0.6f
            : ctx.Subject + Vector3.Up * 0.6f;
        ctx.Place(_pos, aim, 42f, 0.05f);
    }
}

/// <summary>High over the apex looking straight down on the slide: the drift from above.</summary>
public sealed class HairpinCrane : Shot
{
    private Vector3 _anchor;
    public override string Name => "Hairpin crane";
    public override ShotScale Scale => ShotScale.Wide;
    public override ShotFamily Family => ShotFamily.Free;
    public override float MinSeconds => 2.5f;
    public override float MaxSeconds => 5f;
    public override float Weight => 0.8f;
    public override float Fit(CinemaEventKind kind) => kind switch
    {
        CinemaEventKind.Hairpin => 3.5f,
        CinemaEventKind.Switchbacks => 3f,
        CinemaEventKind.Bend => 1.4f,
        _ => 0.5f,
    };

    public override bool Begin(ShotContext ctx)
    {
        float ahead = Mathf.Clamp(ctx.Speed * 1.2f * Mathf.Max(1f, ctx.ClockSpeed), 12f, 40f);
        _anchor = ctx.Lift(ctx.Subject + ctx.Heading * ahead + ctx.Right * (ctx.Slip >= 0 ? 8f : -8f) + Vector3.Up * 16f, 12f);
        return ctx.CanSee(_anchor);
    }

    public override void Step(ShotContext ctx) => ctx.Place(_anchor, ctx.Subject, 55f);

    public override bool StillGood(ShotContext ctx) => _anchor.DistanceTo(ctx.Subject) < 70f;
}

/// <summary>A camera by the verge a little ahead; the car sweeps past it close.</summary>
public sealed class TracksideSweep : Shot
{
    private Vector3 _anchor;
    public override string Name => "Trackside sweep";
    public override ShotScale Scale => ShotScale.Medium;
    public override ShotFamily Family => ShotFamily.Route;
    public override float MinSeconds => 2f;
    public override float MaxSeconds => 4.5f;
    public override float Fit(CinemaEventKind kind) => kind is CinemaEventKind.Bend or CinemaEventKind.Hairpin ? 1.6f : 1f;

    public override bool Begin(ShotContext ctx)
    {
        float ahead = Mathf.Clamp(ctx.Speed * 2.2f * Mathf.Max(1f, ctx.ClockSpeed), 25f, 70f);
        var p = ctx.Subject + ctx.Heading * ahead + ctx.Right * (ctx.Rng.Randf() > 0.5f ? 7f : -7f);
        p.Y = (ctx.Ground(p) ?? ctx.Subject.Y) + 1.2f;
        _anchor = p;
        return ctx.CanSee(_anchor);
    }

    public override void Step(ShotContext ctx) => ctx.Place(_anchor, ctx.Subject + Vector3.Up * 0.6f, 58f);

    // once the car is well past, the shot has done its job
    public override bool StillGood(ShotContext ctx) => (ctx.Subject - _anchor).Dot(ctx.Heading) < 25f;
}

/// <summary>Behind the rear car of a battle, both in frame.</summary>
public sealed class DuelChase : Shot
{
    private Vector3 _pos;
    public override string Name => "Duel chase";
    public override ShotScale Scale => ShotScale.Medium;
    public override ShotFamily Family => ShotFamily.Rig;
    public override float MinSeconds => 2.5f;
    public override float MaxSeconds => 5f;
    public override float Weight => 1.6f;
    public override float Fit(CinemaEventKind kind) => kind == CinemaEventKind.Overtake ? 3f : 1.2f;

    public override bool Begin(ShotContext ctx)
    {
        if (ctx.Rival == null || ctx.Rival.Avatar.GlobalPosition.DistanceTo(ctx.Subject) > 40f) return false;
        _pos = Want(ctx);
        return true;
    }

    private static Vector3 Want(ShotContext ctx)
    {
        var rival = ctx.Rival!.Avatar.GlobalPosition;
        // behind whichever of the two is at the back
        var rear = (rival - ctx.Subject).Dot(ctx.Heading) < 0 ? rival : ctx.Subject;
        return ctx.Lift(rear - ctx.Heading * 8f + Vector3.Up * 2.4f, 1.5f);
    }

    public override void Step(ShotContext ctx)
    {
        if (ctx.Rival == null) { ctx.Place(ctx.Subject - ctx.Heading * 8f + Vector3.Up * 2.4f, ctx.Subject, 55f); return; }
        _pos = _pos.Lerp(Want(ctx), 1f - Mathf.Exp(-ctx.Follow(6f) * ctx.Dt));
        ctx.Place(_pos, (ctx.Subject + ctx.Rival.Avatar.GlobalPosition) * 0.5f + Vector3.Up * 0.8f, 55f);
    }

    public override bool StillGood(ShotContext ctx) =>
        ctx.Rival != null && ctx.Rival.Avatar.GlobalPosition.DistanceTo(ctx.Subject) < 55f;
}

/// <summary>Down by the front wheel looking back along the flank: tyre, road, and the slide.</summary>
public sealed class WheelCam : Shot
{
    private float _side;
    public override string Name => "Wheel cam";
    public override ShotScale Scale => ShotScale.Close;
    public override ShotFamily Family => ShotFamily.Rig;
    public override float MinSeconds => 1.8f;
    public override float MaxSeconds => 3f;
    public override float Weight => 0.9f;

    public override bool Begin(ShotContext ctx)
    {
        _side = ctx.Rng.Randf() > 0.5f ? 1f : -1f;
        return true;
    }

    public override void Step(ShotContext ctx)
    {
        var nose = ctx.Nose;
        var flank = new Vector3(-nose.Z, 0, nose.X) * _side;
        var p = ctx.Subject + flank * 1.15f + nose * 1.2f;
        p.Y = ctx.GroundNear(p) + 0.35f;
        ctx.Place(p, ctx.Subject - nose * 3f + Vector3.Up * 0.3f, 70f, _side * 0.1f);
    }
}

/// <summary>The fallback: a chase behind the direction of travel, looking down the road.</summary>
public sealed class RacingChase : Shot
{
    private Vector3 _pos;
    public override string Name => "Racing chase";
    public override ShotScale Scale => ShotScale.Medium;
    public override ShotFamily Family => ShotFamily.Rig;
    public override float MinSeconds => 2.5f;
    public override float MaxSeconds => 5f;
    public override float Weight => 0.7f;

    public override bool Begin(ShotContext ctx) { _pos = Want(ctx); return true; }

    private static Vector3 Want(ShotContext ctx) => ctx.Lift(ctx.Subject - ctx.Heading * 7.5f + Vector3.Up * 2.6f, 1.5f);

    public override void Step(ShotContext ctx)
    {
        _pos = _pos.Lerp(Want(ctx), 1f - Mathf.Exp(-ctx.Follow(6f) * ctx.Dt));
        ctx.Place(_pos, ctx.Subject + ctx.Heading * 12f + Vector3.Up * 0.8f, 62f, ctx.Slip * 0.15f);
    }
}
