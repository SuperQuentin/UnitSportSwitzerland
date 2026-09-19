using Godot;

namespace UnitSport.Gpx.Cinema;

// ---------------------------------------------------------------------------------------------
// Rig shots — mounted on the athlete. All of them read the gait's own mount points, so they
// inherit the real stride bob rather than an invented one.
// ---------------------------------------------------------------------------------------------

/// <summary>Camera on the head, unfiltered. Every footfall is in the frame.</summary>
public sealed class HelmetPov : Shot
{
    public override string Name => "Helmet POV";
    public override ShotScale Scale => ShotScale.Close;
    public override ShotFamily Family => ShotFamily.Rig;
    public override float MinSeconds => 3f;
    public override float MaxSeconds => 6f;

    public override float Fit(CinemaEventKind kind) => kind switch
    {
        CinemaEventKind.SpeedSurge or CinemaEventKind.Wall or CinemaEventKind.ClimbOnset => 1.6f,
        CinemaEventKind.Summit or CinemaEventKind.Valley => 0.4f,
        _ => 1f,
    };

    public override bool Begin(ShotContext ctx) => true;

    public override void Step(ShotContext ctx)
    {
        var eye = ctx.Eye;
        // a touch of roll into the turn, which is what a head does and a tripod does not
        ctx.Place(eye, eye + ctx.Heading * 12f + Vector3.Up * 0.6f, 92f);
    }
}

/// <summary>
/// The same mount, gimbal-stabilised: the position still rides the stride, the aim does not.
///
/// <para>
/// The contrast with <see cref="HelmetPov"/> is the whole point — one is a camera strapped to a
/// person, the other is a camera a person is carrying. Only the rotation filter differs.
/// </para>
/// </summary>
public sealed class StabilisedHead : Shot
{
    private Vector3 _aim;
    private bool _primed;

    public override string Name => "Stabilised head";
    public override ShotScale Scale => ShotScale.Close;
    public override ShotFamily Family => ShotFamily.Rig;
    public override float MinSeconds => 4f;
    public override float MaxSeconds => 8f;
    public override float Weight => 1.3f;

    public override bool Begin(ShotContext ctx)
    {
        _primed = false;
        return true;
    }

    public override void Step(ShotContext ctx)
    {
        var eye = ctx.Eye;
        var want = eye + ctx.Heading * 14f;

        if (!_primed) { _aim = want; _primed = true; }
        // critically damped: fast enough to follow a corner, slow enough to eat the bob
        else _aim = _aim.Lerp(want, 1f - Mathf.Exp(-ctx.Follow(3.2f) * ctx.Dt));

        ctx.Place(eye, _aim, 74f);
    }
}

/// <summary>Behind and outboard of one shoulder, framing past the head down the road.</summary>
public sealed class OverTheShoulder : Shot
{
    private bool _left;

    public override string Name => "Over the shoulder";
    public override ShotScale Scale => ShotScale.Close;
    public override ShotFamily Family => ShotFamily.Rig;
    public override float Weight => 1.2f;

    public override bool Begin(ShotContext ctx)
    {
        _left = ctx.Rng.Randf() > 0.5f;
        return true;
    }

    public override void Step(ShotContext ctx)
    {
        // Well back and outboard. Sitting on the shoulder itself put the lens inside the runner's
        // head - a real over-the-shoulder frames the subject in the near third with the road
        // beyond, which needs roughly a metre of standoff, not a hand's width.
        var shoulder = _left ? ctx.ShoulderLeft : ctx.ShoulderRight;
        var out_ = ctx.Right * (_left ? -0.85f : 0.85f);
        var eye = shoulder + out_ - ctx.Heading * 1.6f + Vector3.Up * 0.35f;

        ctx.Place(eye, ctx.Head + ctx.Heading * 11f, 58f);
    }
}

/// <summary>Low and behind, tracking the feet. The gait is the subject.</summary>
public sealed class AnkleCam : Shot
{
    public override string Name => "Ankle cam";
    public override ShotScale Scale => ShotScale.Close;
    public override ShotFamily Family => ShotFamily.Rig;
    public override float MinSeconds => 3f;
    public override float MaxSeconds => 5f;

    public override float Fit(CinemaEventKind kind) => kind switch
    {
        CinemaEventKind.GaitChange => 2.5f,       // the changeover is visible down there
        CinemaEventKind.Wall or CinemaEventKind.ClimbOnset => 1.4f,
        _ => 0.8f,
    };

    public override bool Begin(ShotContext ctx) => true;

    public override void Step(ShotContext ctx)
    {
        var behind = ctx.Subject - ctx.Heading * 1.7f;
        behind.Y = ctx.GroundNear(behind) + 0.55f;

        ctx.Place(behind, ctx.Foot + ctx.Heading * 0.4f, 62f);
    }
}

/// <summary>An operator running alongside: lag, overshoot, breathing, and a little noise.</summary>
public sealed class Handheld : Shot
{
    private Vector3 _pos;
    private float _side, _phase;
    private bool _primed;

    public override string Name => "Handheld";
    public override ShotScale Scale => ShotScale.Medium;
    public override ShotFamily Family => ShotFamily.Rig;
    public override float Weight => 1.1f;

    public override bool Begin(ShotContext ctx)
    {
        _side = ctx.Rng.Randf() > 0.5f ? 1f : -1f;
        _primed = false;
        _phase = ctx.Rng.Randf() * 10f;
        return true;
    }

    public override void Step(ShotContext ctx)
    {
        _phase += ctx.Dt;

        var want = ctx.Subject
            + ctx.Right * _side * (2.6f + Mathf.Sin(_phase * 0.7f) * 0.5f)
            - ctx.Heading * 1.2f
            + Vector3.Up * 1.5f;

        if (!_primed) { _pos = want; _primed = true; }
        else _pos = _pos.Lerp(want, 1f - Mathf.Exp(-ctx.Follow(4.5f) * ctx.Dt));

        // breathing plus a slow wander; amplitude grows with pace, as an operator's would
        float shake = 0.02f + ctx.Speed * 0.006f;
        var jitter = new Vector3(
            Mathf.Sin(_phase * 5.3f) + Mathf.Sin(_phase * 2.1f),
            Mathf.Sin(_phase * 4.1f) * 1.4f,
            Mathf.Cos(_phase * 3.7f)) * shake;

        ctx.Place(ctx.Lift(_pos + jitter, 0.4f), ctx.Chest + jitter * 0.3f, 58f);
    }
}

// ---------------------------------------------------------------------------------------------
// Free-space shots
// ---------------------------------------------------------------------------------------------

/// <summary>Circles the runner while matching pace.</summary>
public sealed class DroneOrbit : Shot
{
    private float _angle, _radius, _height, _rate;

    public override string Name => "Drone orbit";
    public override ShotScale Scale => ShotScale.Medium;
    public override ShotFamily Family => ShotFamily.Free;
    public override float MinSeconds => 5f;
    public override float MaxSeconds => 9f;

    public override float Fit(CinemaEventKind kind) => kind switch
    {
        CinemaEventKind.Hairpin or CinemaEventKind.Switchbacks => 2.0f,
        CinemaEventKind.Summit => 1.5f,
        _ => 1f,
    };

    public override bool Begin(ShotContext ctx)
    {
        // Radius scales with the clock for the same reason LowHeroPass's standoff does: the orbit
        // has to still contain the runner when the shot ends, and at 32x a 16 m circle is crossed
        // before the cut has settled. CanSee then fails and the director cuts again immediately.
        float reach = Mathf.Max(1f, ctx.ClockSpeed);
        _radius = (16f + ctx.Rng.Randf() * 16f) * Mathf.Sqrt(reach);
        _height = (7f + ctx.Rng.Randf() * 12f) * Mathf.Sqrt(reach);
        _rate = (ctx.Rng.Randf() > 0.5f ? 1f : -1f) * (0.15f + ctx.Rng.Randf() * 0.15f);
        _angle = ctx.Rng.Randf() * Mathf.Tau;

        return ctx.CanSee(Position(ctx));
    }

    private Vector3 Position(ShotContext ctx) => ctx.Lift(
        ctx.Subject + new Vector3(Mathf.Cos(_angle), 0, Mathf.Sin(_angle)) * _radius
        + Vector3.Up * _height, 3f);

    public override void Step(ShotContext ctx)
    {
        _angle += _rate * ctx.Dt;
        ctx.Place(Position(ctx), ctx.Chest, 60f);
    }

    // No CanSee here: the sightline cut dissolves whatever drifts between the orbit and the
    // runner, so a passing tree is no longer a reason to abandon the shot mid-orbit - it was,
    // before that existed, which is why this used to re-test visibility every frame.
    public override bool StillGood(ShotContext ctx) => true;
}

/// <summary>Starts tight behind and climbs away to reveal the landscape.</summary>
public sealed class DroneReveal : Shot
{
    private float _t;

    public override string Name => "Drone reveal";
    public override ShotScale Scale => ShotScale.Wide;
    public override ShotFamily Family => ShotFamily.Free;
    public override float MinSeconds => 6f;
    public override float MaxSeconds => 10f;
    public override float Weight => 0.9f;

    public override float Fit(CinemaEventKind kind) => kind switch
    {
        CinemaEventKind.Summit => 3f,
        CinemaEventKind.DescentOnset or CinemaEventKind.Valley => 1.8f,
        CinemaEventKind.Start or CinemaEventKind.Finish => 2.5f,
        _ => 0.7f,
    };

    public override bool Begin(ShotContext ctx)
    {
        _t = 0;
        return true;
    }

    public override void Step(ShotContext ctx)
    {
        _t = Mathf.Min(1f, _t + ctx.Dt / 9f);
        float e = _t * _t * (3f - 2f * _t);            // smoothstep, so it eases out of the climb

        var pos = ctx.Subject
            - ctx.Heading * Mathf.Lerp(7f, 70f, e)
            + Vector3.Up * Mathf.Lerp(2.5f, 55f, e);

        ctx.Place(ctx.Lift(pos, 4f), ctx.Chest, Mathf.Lerp(70f, 52f, e));
    }
}

/// <summary>A tripod beside the route: it pans and tilts, it never moves.</summary>
public sealed class LockedOff : Shot
{
    private Vector3 _anchor;
    private float _range = 160f;

    public override string Name => "Locked off";
    public override ShotScale Scale => ShotScale.Medium;
    public override ShotFamily Family => ShotFamily.Free;
    public override float MinSeconds => 4f;
    public override float MaxSeconds => 7f;

    public override bool Begin(ShotContext ctx)
    {
        // Ahead and to one side, so the runner comes into frame rather than leaving it - and both
        // that standoff and the range at which the shot gives up are distances the subject eats at
        // a rate set by the CLOCK, not by their pace. At 32x a runner covers the fixed 160 m in
        // about a second of screen time, so the tripod was being torn down almost as fast as it
        // was set up. Same correction LowHeroPass already carries.
        float reach = Mathf.Max(1f, ctx.ClockSpeed);
        float side = ctx.Rng.Randf() > 0.5f ? 1f : -1f;
        _anchor = ctx.Lift(ctx.Subject
            + ctx.Heading * (25f + ctx.Rng.Randf() * 35f) * reach
            + ctx.Right * side * (12f + ctx.Rng.Randf() * 18f) * Mathf.Sqrt(reach)
            + Vector3.Up * (3f + ctx.Rng.Randf() * 9f) * Mathf.Sqrt(reach), 2.5f);
        _range = 160f * reach;

        return ctx.CanSee(_anchor);
    }

    public override void Step(ShotContext ctx) => ctx.Place(_anchor, ctx.Chest, 52f);

    // Distance only, not CanSee: a tripod is exactly the kind of shot the dissolve exists for -
    // fixed, held for several seconds, with the runner free to pass behind whatever is between
    // them and the lens. Cutting away the instant that happens defeated the dissolve outright.
    public override bool StillGood(ShotContext ctx) => _anchor.DistanceTo(ctx.Subject) < _range;
}

/// <summary>On the ground ahead. The runner approaches, fills the lens and passes.</summary>
public sealed class LowHeroPass : Shot
{
    private Vector3 _anchor;

    public override string Name => "Low hero pass";
    public override ShotScale Scale => ShotScale.Close;
    public override ShotFamily Family => ShotFamily.Free;
    public override float MinSeconds => 3.5f;
    public override float MaxSeconds => 6f;

    public override float Fit(CinemaEventKind kind) => kind switch
    {
        CinemaEventKind.PeakSpeed or CinemaEventKind.SpeedSurge => 2.2f,
        CinemaEventKind.ClimbOnset or CinemaEventKind.Wall => 1.6f,
        _ => 1f,
    };

    public override bool Begin(ShotContext ctx)
    {
        // Far enough ahead that the approach reads, close enough that it arrives within the
        // shot — and that is screen time, so it scales with the clock. At 8x the runner covers
        // eight times the ground per second and was passing the lens before the cut had settled.
        float ahead = Mathf.Max(14f, ctx.Speed * 4.5f * Mathf.Max(1f, ctx.ClockSpeed));
        _anchor = ctx.Subject + ctx.Heading * ahead + ctx.Right * 1.4f;
        _anchor.Y = ctx.GroundNear(_anchor) + 0.45f;

        return ctx.CanSee(_anchor);
    }

    public override void Step(ShotContext ctx) => ctx.Place(_anchor, ctx.Chest, 64f);

    // Done once the runner is well past the lens. Used to also re-test CanSee and cut away the
    // moment anything came between camera and runner - which pre-empted the sightline dissolve
    // before it could ever be seen doing its job: the runner would ALREADY be off-shot, cut to
    // something else, by the time the dissolve had a chance to open. CanSee still gates Begin,
    // so the shot never STARTS blind; once running, the dissolve is trusted to keep it clear.
    public override bool StillGood(ShotContext ctx)
    {
        var toward = ctx.Subject - _anchor;
        return toward.Dot(ctx.Heading) < 6f * Mathf.Max(1f, ctx.ClockSpeed);
    }
}

/// <summary>Straight down. Shows the route as a line on the map, which nothing else does.</summary>
public sealed class TopDown : Shot
{
    private float _height;

    public override string Name => "Top down";
    public override ShotScale Scale => ShotScale.Wide;
    public override ShotFamily Family => ShotFamily.Free;
    public override float MinSeconds => 4f;
    public override float MaxSeconds => 8f;
    public override float Weight => 0.7f;

    public override float Fit(CinemaEventKind kind) => kind switch
    {
        CinemaEventKind.Switchbacks => 3.5f,
        CinemaEventKind.Hairpin => 1.8f,
        _ => 0.6f,
    };

    public override bool Begin(ShotContext ctx)
    {
        _height = 45f + ctx.Rng.Randf() * 45f;
        return true;
    }

    public override void Step(ShotContext ctx)
    {
        var above = ctx.Subject + Vector3.Up * _height - ctx.Heading * 6f;
        ctx.Place(above, ctx.Subject, 58f);
    }
}

/// <summary>Trails behind and above — the reliable one, and the fallback when nothing else fits.</summary>
public sealed class ChaseShot : Shot
{
    private Vector3 _pos;
    private bool _primed;

    public override string Name => "Chase";
    public override ShotScale Scale => ShotScale.Medium;
    public override ShotFamily Family => ShotFamily.Free;
    public override float Weight => 0.8f;

    public override bool Begin(ShotContext ctx)
    {
        _primed = false;
        return true;
    }

    public override void Step(ShotContext ctx)
    {
        var want = ctx.Subject - ctx.Heading * 7.5f + Vector3.Up * 3f;
        if (!_primed) { _pos = want; _primed = true; }
        else _pos = _pos.Lerp(want, 1f - Mathf.Exp(-ctx.Follow(6f) * ctx.Dt));

        ctx.Place(ctx.Lift(_pos, 1.5f), ctx.Chest + Vector3.Up * 0.3f, 68f);
    }
}
