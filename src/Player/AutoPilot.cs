using Godot;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// A scripted racer for one player on a <see cref="RaceRoute"/>, working from its own mount's
/// numbers — no speed limit of its own: it goes as fast as the road ahead allows (corners, crests,
/// braking distance) and as its machine can go.
///
/// <para>
/// A car driver (the original): the racing line, its own speed profile
/// (<see cref="RaceLine.SpeedProfile(CarSpec, bool, float)"/>), drifts only where a forward
/// simulation of its own car (<see cref="Car.Clone"/>) through a handbrake entry stays on the
/// tarmac — grip cars never drift — soft hands for a slide nobody planned, a gentle return from
/// the verge, backing out when stuck, and racecraft: pass where a car's width fits beside the
/// rival (tarmac plus safe verge), queue otherwise, go round wrecks. Under- and oversteer are
/// the tyre model's, never scripted.
/// </para>
///
/// <para>
/// A rider on anything lean-steered (road bike, skis, a motorbike: any <see cref="Rideable"/> that
/// steers through <c>SteerByLean</c>): corners at <c>√(g·R·tan φmax)</c> with a margin, brakes
/// with the mount's own deceleration — both measured by stepping a fresh copy of the mount, never
/// typed — and steers by commanding the bank a pure-pursuit arc needs. On foot: a runner that
/// follows the line through <see cref="FootPlayer.WalkControls"/>.
/// </para>
///
/// <para>
/// Plug <see cref="Drive"/> into <see cref="FootPlayer.RideControls"/> (a runner on foot plugs
/// itself in; set <see cref="Go"/>). Get one with <see cref="For"/>. Used by <c>--drivecheck</c>
/// and by <c>--raceauto</c> players in a multiplayer race.
/// </para>
/// </summary>
public sealed class AutoPilot
{
    /// <summary>Only corners tighter than this radius, m, are considered for a drift.</summary>
    private const float DriftRadius = 70f;
    private const float PlanSeconds = 3.5f, SimDt = 1f / 60f;
    /// <summary>
    /// The drift angle held through a corner, rad (~30°). Tried 22° to sweep less of a 6 m road:
    /// no more corners came out feasible and the cars left the road more often, so it stays.
    /// </summary>
    private const float HoldAngle = 0.52f;

    /// <summary>What is being driven, which decides the policy.</summary>
    public enum Mount { Car, Lean, Foot }

    /// <summary>Everything the driver decides from, so the same policy drives the car and its simulations.</summary>
    public struct State
    {
        public int Near;
        public bool Drifting, Planned, Recovering;
        public float Handbrake, Bend, Cap, Lateral;
        public float PeakSlip, DriftTime;
        /// <summary>Seconds stopped off the road, and the reverse manoeuvre that gets out of it.</summary>
        public float Stuck, Reversing;
        /// <summary>Seconds spent stuck off the road in total since the last time the tarmac was reached.</summary>
        public float Lost;
        /// <summary>A rival within a second ahead (attacking) or behind (defending): where mistakes happen.</summary>
        public bool Pressure;
        /// <summary>Beside a rival or diving up the inside: off the line, into a tighter radius.</summary>
        public bool Passing;
        /// <summary>A rival within 30 m ahead or alongside: no planned drift (three cars drifting one corner together is a pile-up).</summary>
        public bool Crowded;
    }

    /// <summary>
    /// Another vehicle on the road, as this driver sees it: where it is and how it moves (a remote
    /// player's replicated <c>WorldVelocity</c>) — the direction matters: a car coming the other way
    /// and one going away at the same speed are opposite problems. <see cref="Yielding"/>: a traffic car
    /// making way (pulled over, slowing), which may be passed anywhere a car fits beside it.
    /// </summary>
    public readonly record struct Other(Vector3 Position, Vector3 Velocity, bool Wreck, bool Yielding = false, bool Civil = false)
    {
        public float Speed => new Vector2(Velocity.X, Velocity.Z).Length();
    }

    public readonly RaceRoute Route;
    public readonly FootPlayer Player;
    public readonly Mount Kind;
    /// <summary>The car, for a car driver; null for every other mount.</summary>
    public readonly CarSpec? Spec;
    public Car? Car => Player.Vehicle as Car;
    public State D = new() { Bend = 1f, Cap = float.MaxValue };
    public float[] Profile;
    /// <summary>The line <see cref="Profile"/> was computed on: the route's line is swapped for a wider one once the verge is surveyed.</summary>
    private RaceLine _profiled;
    private System.Threading.Tasks.Task<RaceLine>? _widen;

    /// <summary>Lean-steered mounts: the lean it can hold, rad, and its braking, m/s², as measured.</summary>
    private readonly float _maxLean, _brake;

    /// <summary>Anyone who wants the driver's commentary (plans, drifts); null for silence.</summary>
    public System.Action<string>? Log;

    public int Drifts, Plans, Feasible;
    public float BestDrift;
    /// <summary>
    /// Metres driven with a wheel past the tarmac edge where the verge was surveyed safe, and where
    /// it was not (by what blocked it) — the second should stay 0 short of a real excursion.
    /// </summary>
    public float VergeMetres;
    public readonly Dictionary<RaceLine.Block, float> VergeUnsafe = new();
    /// <summary>Where (100 m bins along the line) and how far (m of the body past the edge) that happened.</summary>
    public readonly SortedSet<int> UnsafeAt = new();
    public float UnsafeDepth;
    private float _planTimer, _cooldown, _gripUntil;

    /// <summary>
    /// The driver: <see cref="Skill"/> 0.8..1.1 (how close to the car's limit it brakes and corners, and
    /// how rarely it gets a braking point wrong under pressure), <see cref="Aggression"/> 0..1 (how
    /// late it brakes, how close it follows, whether it dives up the inside). Set with
    /// <see cref="Temperament"/>; the defaults are a calm, perfect driver.
    /// </summary>
    public float Skill { get; private set; } = 1f;
    public float Aggression { get; private set; } = 0.3f;
    /// <summary>Braking points got wrong (late, then too hard), logged each time.</summary>
    public int Mistakes;
    private System.Random _rng = new(1);
    private float _late, _hard, _sinceMistake = 20f;
    private bool _inZone;
    private float _backwards;

    /// <summary>
    /// A grid's skills: one drawn in each of <paramref name="count"/> equal bands of 0.8..1.1, shuffled —
    /// levels that really differ, never a grid of 0.8s — and the best raised to an ace (≥ 1.05) if its
    /// band did not already make one.
    /// </summary>
    public static float[] GridSkills(int count, System.Random rng)
    {
        var skills = new float[count];
        for (int i = 0; i < count; i++) skills[i] = 0.8f + 0.3f * (i + (float)rng.NextDouble()) / count;
        int best = count - 1;
        skills[best] = Mathf.Max(skills[best], 1.05f + 0.05f * (float)rng.NextDouble());
        for (int i = count - 1; i > 0; i--) { int j = rng.Next(i + 1); (skills[i], skills[j]) = (skills[j], skills[i]); }
        return skills;
    }

    /// <summary>Sets the driver (clamped) and recomputes its speed profile; <paramref name="seed"/> drives its mistakes.</summary>
    public void Temperament(float skill, float aggression, int seed)
    {
        Skill = Mathf.Clamp(skill, 0.8f, 1.1f);
        Aggression = Mathf.Clamp(aggression, 0f, 1f);
        _rng = new System.Random(seed);
        Profile = ComputeProfile();
    }

    /// <summary>
    /// Chance of a braking point got wrong, per braking zone entered under pressure: 0 at skill 1,
    /// ~10% at 0.8 and full aggression. At three times that, in a pack (always under pressure) the
    /// worst drivers blundered every other corner and half the field went into the trees.
    /// </summary>
    private float MistakeChance => Mathf.Max(0f, 0.35f * (1f - Skill) * (0.5f + Aggression));   // none from skill 1 up

    /// <summary>A runner on foot waits for this (a vehicle gets it through <see cref="Drive"/>).</summary>
    public bool Go = true;

    public float Arc => Route.Line.Arc[D.Near];
    public string Label => Spec?.Label ?? Player.Vehicle?.Label ?? "Runner";
    private CarSpec S => Spec!;

    /// <summary>
    /// A pilot for whatever <paramref name="player"/> is on: a car, anything lean-steered (bike,
    /// skis, motorbike), or its own legs. Null for a flyer or a mount it cannot drive.
    /// </summary>
    public static AutoPilot? For(RaceRoute route, FootPlayer player) => player.Vehicle switch
    {
        Car car => new AutoPilot(route, player, car.Spec),
        null => new AutoPilot(route, player, Mount.Foot, 0f, 0f),
        Flyer => null,
        { } ride when MeasureLean(ride.Kind) is { } m => new AutoPilot(route, player, Mount.Lean, m.Lean, m.Brake),
        _ => null,
    };

    public AutoPilot(RaceRoute route, FootPlayer player, CarSpec spec) : this(route, player, Mount.Car, 0f, 0f, spec) { }

    private AutoPilot(RaceRoute route, FootPlayer player, Mount kind, float maxLean, float brake, CarSpec? spec = null)
    {
        Route = route;
        Player = player;
        Kind = kind;
        Spec = spec;
        _maxLean = maxLean;
        _brake = brake;
        _profiled = route.Line;
        Profile = ComputeProfile();
        D.Near = route.Line.IndexAt(0);
        // trunks and parked vehicles are only solid around collision anchors: a pilot's body must
        // be one, or it drives through the forest (FootPlayer registers itself; this is the guard)
        if (player.Terrain is { } terrain && !terrain.HasAnchor(player)) terrain.AddAnchor(player, collision: true);
        // the verge: surveyed off the main thread, swapped in when it lands (same point count)
        if (!route.Line.Surveyed && player.Terrain is { Source: { } source, Origin: { } origin })
            _widen = RaceLine.Widen(route, source, origin);
        if (kind == Mount.Foot) player.WalkControls = Walk;
    }

    private float[] ComputeProfile() => ComputeProfile(Skill);

    /// <summary>Seconds a driver of skill 1 would take for the first <paramref name="distance"/> m on this line (the yardstick of <c>--drivecheck</c>'s pace index).</summary>
    public float ReferenceSeconds(float distance)
    {
        var line = Route.Line;
        var v = ComputeProfile(1f);
        float t = 0f;
        for (int i = 1; i < line.Arc.Count && line.Arc[i] <= distance; i++)
            t += (line.Arc[i] - line.Arc[i - 1]) / Mathf.Max(0.5f * (v[i] + v[i - 1]), 1f);
        return t;
    }

    /// <summary>
    /// What a driver of <paramref name="skill"/> can hold: a share of the tyre limit in corners, and of the braking
    /// limit, that grows with skill. The default driver (1.0) is the reference (×1, ×1); a novice (0.8)
    /// corners at ×0.90 and brakes at ×0.85 of it (was ×0.94 / ×0.92: the grid's skills hardly showed in
    /// the pace, #159); an ace (1.1) at ×1.03 and ×1.04, the braking share capped at 0.9 of the rear-lockup
    /// limit whatever the temper.
    /// </summary>
    private float[] ComputeProfile(float skill)
    {
        var line = Route.Line;
        float g = Rideable.Gravity;
        float lo = Mathf.Clamp((skill - 0.8f) / 0.2f, 0f, 1f), hi = Mathf.Clamp((skill - 1f) / 0.1f, 0f, 1f);
        float cornerShare = Mathf.Lerp(0.90f, 1f, lo) * Mathf.Lerp(1f, 1.03f, hi), brakeShare = Mathf.Lerp(0.85f, 1f, lo) * Mathf.Lerp(1f, 1.04f, hi);
        return Kind switch
        {
            // a share of the tyre limit: a narrow road with camber and bumps is not a flat skidpad
            Mount.Car => line.SpeedProfile(S, Rideable.Arcade, (S.Style == DriveStyle.Grip ? 0.64f : 0.6f) * cornerShare,
                Mathf.Min(0.9f, 0.85f * brakeShare + 0.04f * Aggression)),
            // v = √(g·R·tan φ): 65% of the lean it can hold (measured upright on the flat: braking
            // into a bend takes grip off the lean, and at 80% two R1s ran wide off a R 50 m bend at
            // 93 km/h on full lock), 85% of its brakes
            Mount.Lean => line.SpeedProfile(0.65f * g * Mathf.Tan(_maxLean), _ => 0.85f * _brake),
            // a runner corners on its feet at any speed it can run
            _ => line.SpeedProfile(0.6f * g, _ => 4f),
        };
    }

    /// <summary>
    /// How far a lean-steered mount banks and how hard it brakes, from a fresh copy of it stepped on
    /// flat ground: full steer for 2 s at 10 m/s (the bank settles on its maximum), and 0.5 s of
    /// full brake against 0.5 s of coasting. Null when it does not steer by leaning.
    /// </summary>
    private static (float Lean, float Brake)? MeasureLean(RideKind kind)
    {
        if (Rideable.Create(kind) is not { } probe || probe is UnitSport.Player.Car or Flyer) return null;
        var flat = new RideGround(true, 0f);
        var turn = new RideMotion { Speed = 10f };
        for (int i = 0; i < 120; i++) probe.Step(new RideInput(0.3f, 0f, 1f, false), flat, SimDt, ref turn);
        float lean = Mathf.Abs(turn.Bank);
        if (lean < 0.1f || turn.Slip != 0f) return null;
        var braked = new RideMotion { Speed = 10f };
        var coast = new RideMotion { Speed = 10f };
        var a = Rideable.Create(kind)!;
        var b = Rideable.Create(kind)!;
        for (int i = 0; i < 30; i++)
        {
            a.Step(new RideInput(0f, 1f, 0f, false), flat, SimDt, ref braked);
            b.Step(new RideInput(0f, 0f, 0f, false), flat, SimDt, ref coast);
        }
        float brake = Mathf.Max((coast.Speed - braked.Speed) / 0.5f, 1.5f);
        return (lean, brake);
    }

    /// <summary>Set once past the finish: from then on it only brakes to a stop.</summary>
    public bool Finished;

    /// <summary>One step of driving. <paramref name="go"/> false holds the vehicle on the grid.</summary>
    public RideInput Drive(float dt, bool go, IEnumerable<Other> others)
    {
        // the route in the frame the car is in (#185): whoever holds it may not follow the shifts
        // (a probe), and a route already followed is left alone
        if (Player.Origin is { } origin) Route.Follow(origin.Frame);
        TakeWiderLine();
        if (Kind == Mount.Lean) return Ride(dt, go, others);
        // On the grid: the handbrake, not the brake — at a standstill the brake pedal selects
        // reverse and then drives it, and the whole grid reversed off the line during a countdown.
        if (!go || Car is not { } car) return new RideInput(0f, 0f, 0f, false, Handbrake: true);
        var motion = Player.Motion;
        bool wasDrifting = D.Drifting, wasPlanned = D.Planned;
        Traffic(dt, others);
        CountVerge(dt);
        // Wedged between trunks for 8 s with backing out not working (an AWD car can dig itself in
        // spinning against a root): put it back on the line, the way a game resets a car to the
        // track. Measured before this: three AWD grip cars out of 26 never reached the bottom.
        if (D.Lost > 8f && !Finished) ResetToLine();   // a car stopped past the line is not lost
        // Standing still for 10 s with the race on, whatever holds it (a jam of racers and traffic
        // each waiting for the other — measured: all six cars parked at 170 m for ten minutes):
        // put back on the line past it, the way a game unsticks a car
        _held = motion.Speed < 1f && !Finished ? _held + dt : 0f;
        if (_held > 3f && _held - dt <= 3f || _held > 6f && _held - dt <= 6f) Log?.Invoke($"{Label}: held {_held:F0} s ({Seen})");
        if (_held > 10f) { _held = 0f; ResetToLine($"held up ({(Seen.Length > 0 ? Seen : "nothing seen")})"); }
        var input = Policy(ref D, Player.GlobalPosition, motion, dt, live: true);
        FacingBack(dt, motion);
        // Past the line the route runs out a few tens of metres later: stay on the line and brake to
        // a stop there, instead of racing into the trees at 150 km/h (every demo ended in a pile-up).
        if (Finished)
            // (not the full pedal: past the rear's saturation that spun a GT-R at 100 km/h)
            return motion.Speed > 1.5f ? input with { Throttle = 0f, Brake = Mathf.Abs(Wrap(motion.Slip)) > 0.05f ? 0.2f : 0.7f, Effort = false }
                                       : new RideInput(0f, 0f, 0f, false, Handbrake: true);
        if (wasDrifting && !D.Drifting) EndDrift(wasPlanned);
        CountSpin(car, motion);
        Plan(car, Player.GlobalPosition, motion, dt);
        return input;
    }

    /// <summary>
    /// A spin that ended facing back up the road: backing out does not help (the nose points the
    /// wrong way), so after 3 s of it the car is put back on the line like a game resets a car.
    /// </summary>
    private void FacingBack(float dt, in RideMotion m)
    {
        var line = Route.Line;
        var tan = RaceRoute.Flat(line.PointAt(Arc + 2f) - line.PointAt(Arc - 2f));
        var nose = new Basis(Vector3.Up, m.Yaw) * Vector3.Forward;
        bool back = !Finished && m.Speed < 5f && tan.LengthSquared() > 0.1f && Mathf.Abs(RaceRoute.SignedAngle(RaceRoute.Flat(nose), tan)) > 2.1f;
        _backwards = back ? _backwards + dt : 0f;
        if (_backwards > 3f) { _backwards = 0f; ResetToLine(); }
    }

    /// <summary>The surveyed line, once it is ready: the route takes it, and this driver's profile follows.</summary>
    private void TakeWiderLine()
    {
        if (_widen is { IsCompletedSuccessfully: true } done)
        {
            if (Route.Line == _profiled && done.Result.Points.Count == Route.Line.Points.Count)
            {
                // surveyed in the frame the route was in then: the origin may have moved since (#185)
                if (Route.Frame is { } frame) done.Result.Follow(frame);
                Route.Line = done.Result;
            }
            _widen = null;
        }
        if (Route.Line == _profiled) return;
        _profiled = Route.Line;
        Profile = ComputeProfile();
    }

    /// <summary>
    /// Signed distance of a point from the centreline, m, + to the left of travel, and the
    /// centreline index it was measured at.
    /// </summary>
    private (float Lateral, int Index) Side(Vector3 pos)
    {
        int i = Route.NearestCentre(pos);
        var n = RaceLine.Normal(Route.Centre, i);
        var rel = pos - Route.Centre[i];
        return (rel.X * n.X + rel.Z * n.Y, i);
    }

    /// <summary>Metres with a wheel on the verge: where the survey allowed it, and where it did not.</summary>
    private void CountVerge(float dt)
    {
        if (Finished) return;
        var (lat, i) = Side(Player.GlobalPosition);
        // half the body: a car is its capsule (0.85 m), a rider far narrower
        float beyond = Mathf.Abs(lat) + (Player.Vehicle?.BodyRadius ?? 0.32f) - Route.Width[i] * 0.5f;
        // a wheel or two over the edge; further out is an excursion, counted as off road elsewhere
        if (beyond < 0.1f || beyond > 1.6f) return;
        var line = Route.Line;
        float margin = lat > 0 ? line.MarginLeft[i] : line.MarginRight[i];
        float ds = Player.Motion.Speed * dt;
        if (margin > 0f) VergeMetres += ds;
        else
        {
            var why = lat > 0 ? line.WhyLeft[i] : line.WhyRight[i];
            VergeUnsafe[why] = VergeUnsafe.GetValueOrDefault(why) + ds;
            if (UnsafeAt.Add((int)(Arc / 100f) * 100)) Log?.Invoke($"{Label}: wheel over a blocked edge ({why}) at {Arc:F0} m: lat {lat:F2} of half {Route.Width[i] * 0.5f:F2}, line {line.Offset[i]:F2} + {D.Lateral:F2}, room L {RoomL(i):F2} R {RoomR(i):F2}, {Player.Motion.Speed * 3.6f:F0} km/h, slip {Mathf.RadToDeg(Wrap(Player.Motion.Slip)):F0}, k {line.Curvature[i] * 1000f:F1}/km ahead {line.Curvature[line.IndexAt(Arc + 15f)] * 1000f:F1}, edge saves {EdgeSaves}, cap {(D.Cap < 1e9f ? D.Cap * 3.6f : 0f):F0}, recovering {D.Recovering}, passing {D.Passing}");
            UnsafeDepth = Mathf.Max(UnsafeDepth, beyond);
        }
    }

    /// <summary>
    /// How far the car's centre may go from the centreline at line point i, m, to the left and to
    /// the right: the line's room (tarmac, safe verge, a drop's extra clearance), plus the 0.3 m the
    /// line keeps off the edge given up to a pass or an escape only where the verge beyond it was
    /// surveyed safe — never where it was not. A blocked edge (drop, wall, trunk, no data) is a hard
    /// limit: nothing, not a pass, not an oncoming car, not a wreck, puts a wheel over it.
    /// </summary>
    /// <summary>Moving things ahead in this car's way this step: where across the road, and the speed that follows them.</summary>
    private readonly List<(float Lateral, float Follow, float Beside)> _ahead = new();

    private float RoomL(int i) => Route.Line.RoomLeft[i] + (Route.Line.MarginLeft[i] > 0f ? 0.3f : 0f);
    private float RoomR(int i) => Route.Line.RoomRight[i] + (Route.Line.MarginRight[i] > 0f ? 0.3f : 0f);

    /// <summary>
    /// Racecraft, every step: where on the road to be (<see cref="State.Lateral"/>, an offset from the
    /// line) and how fast at most (<see cref="State.Cap"/>), from everyone about — the race's cars and
    /// whatever solid thing is sensed on the road ahead (traffic, a parked machine), each with where
    /// it is and where it is going (its velocity, carried forward to the moment the two meet).
    /// <list type="bullet">
    /// <item>Coming the other way, anywhere on the road within 5 s: keep a car's width and a gap from
    /// where it will be when we meet (its lane, plus its own sideways drift); no room, and it is met
    /// crawling. No pass is started with one in sight.</item>
    /// <item>Stopped or wrecked: go round on the side with room, slowly; else stop short of it.</item>
    /// <item>Alongside, or less than a car length clear: keep a car's width from it — the chop back
    /// onto the line comes once the rival is more than a car length behind.</item>
    /// <item>Slower ahead in the way: pass beside it where the straight allows, or up the inside
    /// into the next bend (a late-braking dive: close behind, aggressive enough, room on the inside
    /// there and at the apex); otherwise follow at a gap that shrinks with aggression.</item>
    /// </list>
    /// All of it inside <see cref="RoomL"/>/<see cref="RoomR"/>: a blocked edge is never given up.
    /// </summary>
    private void Traffic(float dt, IEnumerable<Other> others)
    {
        var line = Route.Line;
        var me = Player.GlobalPosition;
        float v = Player.Motion.Speed;
        var fwd = RaceRoute.Flat(new Basis(Vector3.Up, Player.Motion.Yaw + Player.Motion.Slip) * Vector3.Forward);
        // at a crawl the direction of travel means nothing (a car on the grid turned across the
        // road, one just out of a spin): "ahead" is along the road
        if (v < 3f)
        {
            var tan = RaceRoute.Flat(line.PointAt(Arc + 2f) - line.PointAt(Arc - 2f));
            if (tan.LengthSquared() > 0.1f) fwd = tan.Normalized();
        }
        var (myLat, _) = Side(me);
        int ai = line.IndexAt(Arc + Mathf.Clamp(v * 0.7f, 7f, 70f));
        float lo = -RoomR(ai), hi = RoomL(ai);
        float target = line.Offset[ai];
        float urgency = 1.2f, passTarget = float.NaN, passCap = float.MaxValue;
        bool oncoming = false, stopped = false, pressure = false, dive = false, crowded = false;
        float nearest = float.MaxValue;
        _ahead.Clear();
        _around.Clear();
        D.Cap = float.MaxValue;
        bool boxed = false, tailed = false;
        // traffic about to come out onto the road ahead, not on it yet (the boxes below cannot see it)
        D.Cap = Crossing(me, v);
        _capRule = D.Cap < float.MaxValue ? -1 : 0;
        // a car's width (1.7 m) and 0.3 m beside a rival; 2.3 from one coming the other way
        const float Beside = 2.0f, Clear = 2.3f, CarLength = 4.5f;

        // where the tightest bounds on each side come from (metres ahead): see the squeeze below
        float loAt = float.MaxValue, hiAt = float.MaxValue, lastLo = lo, lastHi = hi, lastAhead = 0f;
        void Note()
        {
            if (lo != lastLo) { loAt = lastAhead; lastLo = lo; }
            if (hi != lastHi) { hiAt = lastAhead; lastHi = hi; }
        }
        foreach (var q in others.Concat(Sensed(dt, fwd)))
        {
            Note();
            // the traffic (a 1.8 m car, a 2 m van, boxed at its mesh bounds) gets more room than a racer:
            // at a racer's 2.0 / 2.3 m centre to centre the pack clipped traffic at 40-80 km/h (#85)
            float clear = q.Civil ? Clear + 0.3f : Clear, beside = q.Civil ? Beside + 0.4f : Beside;
            var rel = RaceRoute.Flat(q.Position - me);
            _around.Add(q.Position);
            float ahead = rel.Dot(fwd);
            lastAhead = ahead;
            tailed |= ahead < 0f && ahead > -2.5f * CarLength && Mathf.Abs(rel.Dot(new Vector3(fwd.Z, 0, -fwd.X))) < 2.5f;
            float along = q.Velocity.X * fwd.X + q.Velocity.Z * fwd.Z;
            if (ahead > -3f && ahead < 40f && rel.Length() < nearest)
            {
                nearest = rel.Length();
                Seen = $"nearest {ahead:F0} m ahead, {rel.Dot(new Vector3(fwd.Z, 0, -fwd.X)):F1} m left, along {along * 3.6f:F0} km/h, lat {Side(q.Position).Lateral:F1} vs mine {myLat:F1}";
            }
            float closing = v - along;
            float reach = 30f + Mathf.Max(closing, 0f) * (1f + Mathf.Max(closing, 0f) / (2f * EasyBrake));
            if (ahead < -2f * CarLength || ahead > reach) continue;
            var (theirs, qi) = Side(q.Position);
            // the bounds below are where this car must be THERE; the plan is an offset from the line at
            // the aim point, and where the line swings across the road between the two, a bound kept at
            // the aim point was a metre off at the car — racers passed standing traffic 0.5 m too close
            // and hit it at 35-77 km/h (#85)
            float shift = line.Offset[ai] - line.Offset[qi];
            var n = RaceLine.Normal(Route.Centre, qi);
            float mine = line.Offset[qi] + D.Lateral;   // where this car is headed as it reaches them

            if (along < -1f && !(q.Civil && q.Speed < 6f))
            {
                // coming the other way
                if (ahead < 1f) continue;   // gone by
                float meet = ahead / Mathf.Max(closing, 1f);
                if (meet > 5f) continue;
                oncoming = true;
                urgency = 4f;
                // where it will be when we meet: its sideways speed carried forward (a car cutting a
                // bend, a rival spinning across), no further than the road
                float sideways = q.Velocity.X * n.X + q.Velocity.Z * n.Y;
                float half = Route.Width[qi] * 0.5f;
                float at = Mathf.Clamp(theirs + sideways * Mathf.Min(meet, 2f), -half, half);
                float spaceL = RoomL(qi) - (at + clear), spaceR = (at - clear) + RoomR(qi);
                if (spaceL >= 0f || spaceR >= 0f)
                {
                    float bound = spaceR >= spaceL ? at - clear : at + clear;
                    if (spaceR >= spaceL) hi = Mathf.Min(hi, bound + shift); else lo = Mathf.Max(lo, bound + shift);
                    // not over yet, and not the time to get over (1.5 m/s sideways — what a car at speed
                    // really makes — and a beat and a half): slow, so that there is. At 80 km/h into a blind
                    // bend it met the car nose first; with 2 m/s and a beat, racers at 60-90 km/h still hit
                    // traffic that stopped short in front of them (#85)
                    float wrong = spaceR >= spaceL ? myLat - bound : bound - myLat;
                    if (wrong > 0f && meet < wrong / 1.5f + 1.5f)
                        CapBy(StopWithin(meet * v - 8f) + 2f);
                }
                else
                {
                    // no room to meet it at speed: to the wider side, and slow enough to meet it crawling
                    if (spaceR >= spaceL) hi = Mathf.Min(hi, -RoomR(qi) + shift); else lo = Mathf.Max(lo, RoomL(qi) + shift);
                    CapBy(StopWithin(meet * v - 8f) + 2f);
                }
                continue;
            }

            // (a traffic car creeping along the edge for the race is gone round like a standing one: followed
            // as a car ahead at 2 km/h, it held racers up until they were reset, #85)
            // and one coming the other way at a walk, stopping at the edge for the race: met as a standing car
            // (slowed for in time), not as an oncoming one — at 90 km/h a racer met one that had just stopped (#159)
            if (q.Wreck || q.Speed < 0.5f || (q.Civil && (q.Speed < 3f || (along < 0f && q.Speed < 6f))))
            {
                // stopped: just clear of it — on the side this car is on if that is a way through, else the roomier
                if (ahead < 1f) continue;
                float gap = Mathf.Abs(theirs - myLat);
                // in the way where this car is headed OR where it still is: a car at -0.5 m ran into one
                // standing at -1.7 m at 66 km/h because its target lateral was clear of it (#85)
                float spaceL = RoomL(qi) - (theirs + clear), spaceR = (theirs - clear) + RoomR(qi);
                // a squeeze of up to 0.4 m past it is still a way through (the room keeps 0.3 off the edge)
                bool wayL = spaceL >= -0.4f, wayR = spaceR >= -0.4f;
                // already clear of it where it is, with room on that side: the bound below keeps it there (WIP #159)
                bool inWay = gap <= clear + 0.5f || (!(myLat > theirs ? wayL : wayR) && Mathf.Abs(theirs - mine) <= clear + 0.5f);
                if (!wayL && !wayR)
                {
                    if (inWay) { stopped = true; CapBy(StopWithin(ahead - 6f)); }
                    continue;
                }
                // the bound holds even while it is out of the way: without it the line swung into a car
                // standing 30 m on, too late to get round it (racers hit parked traffic at 50-85 km/h, #85)
                if (myLat > theirs ? wayL : !wayR) lo = Mathf.Max(lo, theirs + clear + Mathf.Min(spaceL, 0f) + shift);
                else hi = Mathf.Min(hi, theirs - clear - Mathf.Min(spaceR, 0f) + shift);
                if (!inWay) continue;
                stopped = true;
                urgency = 4f;
                // across by how much: past a traffic car at a crawl 2.0 m centre to centre (0.2 m of air between
                // the boxes) is enough and is exact — with the full margin a car stopped 5 m short of one it
                // would have cleared; at speed the full margin — 2.3 m off at 77 km/h, a racer clipped it and was thrown
                float need = (q.Civil ? Mathf.Lerp(2.0f, clear, Mathf.Clamp((v - 5f) / 10f, 0f, 1f)) : clear) - gap;
                // clear of it: a wreck or a stopped racer is passed at a walk (it may move off, a driver may
                // get out); a traffic car waiting tucked in for the race is passed at speed — at a walk past
                // every one of them the pack ran at 8% of its pace (#85)
                // A traffic car standing in the road is passed at 50 km/h at most: at 90 the car's own line
                // (the steering aims 20 m on, past the car) swung it 2 m back towards the traffic in the
                // last 15 m, too late to stop — five racers thrown off that way in one run (#85)
                if (need <= 0f) CapBy(q.Civil ? Mathf.Max(14f, StopWithin(ahead - 12f)) : Mathf.Max(8f, StopWithin(ahead - 12f)));
                else if (q.Civil)
                {
                    // not across yet: slow to a crawl by where the metres left are just enough to get across
                    // at one (~0.15 m across per metre on: the steering aims 7 m ahead), and creep round it;
                    // short of them, creep on to 5 m short, and if still not across, back off to try again (see
                    // _boxed) — nose to nose with a car tucked in for the race, both waited for a reset
                    float runway = CarLength + need / 0.15f;
                    CapBy(ahead >= runway ? Mathf.Max(2.5f, StopWithin(ahead - runway)) : StopWithin(ahead - 5f));
                    boxed |= ahead < runway;
                }
                // a rival standing, not yet cleared: stop short and wait. Backing off to get round it (an
                // earlier try) had whole queues of racers reversing into each other on the grid (#85)
                else CapBy(StopWithin(ahead - 5f));
                continue;
            }

            // a rival going our way: within a second of it, ahead or behind, is racing under pressure
            if (Mathf.Abs(ahead) < Mathf.Max(v, 5f)) pressure = true;
            if (ahead > -CarLength && ahead < 30f) crowded = true;
            // alongside, or not yet a car length clear of it: keep a car's width from it — no chop
            // across its nose until it is more than a car length behind
            bool close = ahead > -2f * CarLength && ahead < CarLength;
            if (close && Mathf.Abs(theirs - myLat) >= 1.2f)
            {
                if (Mathf.Abs(theirs - myLat) < beside + 1f)
                {
                    if (theirs > myLat) hi = Mathf.Min(hi, theirs - beside + shift); else lo = Mathf.Max(lo, theirs + beside + shift);
                }
                continue;
            }
            // behind (its problem), or not in the way; right on its bumper it is followed below
            if (ahead < 1f) continue;
            // the traffic brakes like traffic, for things a racer does not see: a longer gap behind it
            float followGap = Mathf.Lerp(10f, 5f, Aggression) + (q.Civil ? 4f : 0f);
            // (and one that may stop: traffic stops for things a racer does not see, so behind it the gap
            // is one this car can stop in even if it stops too — racers ran into cars pulling over for them)
            float follow = q.Civil ? Mathf.Sqrt(Mathf.Max(0f, 0.7f * along * Mathf.Abs(along) + 2f * EasyBrake * (ahead - followGap)))
                : along + StopWithin(ahead - followGap);
            _ahead.Add((theirs, follow, beside));   // in the way or not: a pass may move into its lane
            // in the way where this car is headed, or where it still is (the car lags its target)
            // or clear of it where it is now, with room for a car on that side (WIP #159)
            bool clearNow = Mathf.Abs(theirs - myLat) > beside + 0.1f
                && (theirs > myLat ? theirs - beside >= -RoomR(qi) : theirs + beside <= RoomL(qi));
            if (clearNow || Mathf.Min(Mathf.Abs(theirs - mine), Mathf.Abs(theirs - myLat)) > beside + 0.1f)
            {
                // out of the way but closing on it: stay on this side of it until past — the line swinging
                // over at the last moment ran racers into the back of traffic at 65-85 km/h (#85)
                if (clearNow || (closing > 0.5f && ahead / closing < 3f))
                {
                    if (theirs > myLat) hi = Mathf.Min(hi, theirs - beside + shift); else lo = Mathf.Max(lo, theirs + beside + shift);
                }
                continue;
            }

            // not faster than it where it is going: just follow
            if (Profile[Mathf.Min(qi, Profile.Length - 1)] < along + 1f && closing < 1f) { CapBy(follow); continue; }
            float l = theirs + beside, r = theirs - beside;
            bool fitsL = l <= RoomL(qi), fitsR = r >= -RoomR(qi);
            bool straight = MaxCurvature(Arc, 0f, 60f + v) < 1f / 150f;
            // a traffic car making way for the race: past it wherever a car fits beside it here and 25 m on
            int later = line.IndexAt(Arc + ahead + 25f);
            bool easyL = q.Yielding && fitsL && l <= RoomL(later), easyR = q.Yielding && fitsR && r >= -RoomR(later);
            // a dive up the inside of the next bend: close behind, braking later than it, where a car
            // fits beside it on the inside both here and at the apex
            float k = NextBend(Arc + ahead, 50f, out float apexAt);
            int apex = line.IndexAt(apexAt);
            bool inside = k > 0f ? fitsL && theirs + beside <= RoomL(apex) : fitsR && theirs - beside >= -RoomR(apex);
            bool diveHere = !straight && k != 0f && Aggression > 0.3f && ahead < 15f && inside;
            // no racer-vs-racer pass above 90 km/h (#159): one at ~120 km/h on a fast descent ended in the trees.
            // Behind the car ahead at its pace, the pass waits for a straight where it is slower
            bool tooFast = !q.Civil && v > 25f;
            // and a traffic car is passed from a speed this car could still drop back behind it from: a pass
            // started at 105 km/h on one doing 25 had to be given up for a car coming the other way 25 m short
            // of it, and nothing was left but to hit it at 80 (#159). Not yet: close up, then pull out
            if (q.Civil && !D.Passing && v > follow + 3f) tooFast = true;
            if ((((straight && (fitsL || fitsR)) || diveHere) || easyL || easyR) && !tooFast)
            {
                float side = diveHere ? (k > 0f ? l : r)
                    : !straight ? (easyL ? l : r)
                    : fitsL && (!fitsR || Mathf.Abs(l - line.Offset[qi]) <= Mathf.Abs(r - line.Offset[qi])) ? l : r;
                if (float.IsNaN(passTarget) || Mathf.Abs(side - myLat) < Mathf.Abs(passTarget - myLat)) passTarget = side + shift;
                passCap = Mathf.Min(passCap, follow);
                dive |= diveHere;
            }
            else CapBy(follow);
        }
        // a pass only with nothing coming the other way and nothing stopped in the road: pulling out
        // into the other lane with a car in it is how the traffic runs ended, head-on at 130 km/h
        Note();
        // no way through: bounds from both sides that cross, set by things close together ahead (a car
        // coming, one standing on the other side): stop short of the nearer — at 90-100 km/h racers
        // drove into such a gap because nothing but the corridor said it was shut (#85)
        if (lo > hi + 0.1f && Mathf.Min(loAt, hiAt) > 3f && Mathf.Abs(loAt - hiAt) < 20f)
            CapBy(StopWithin(Mathf.Min(loAt, hiAt) - 8f));
        // one bound from a rival alongside or just behind (no chop), the other from something ahead: the
        // car cannot get over to its side of it, so it stops short of that — not, as before, keep the line
        // into it: three racers in one run hit traffic standing in its lane at 44-59 km/h that way (#159)
        else if (lo > hi + 0.1f && Mathf.Max(loAt, hiAt) > 3f && Mathf.Min(loAt, hiAt) <= 3f)
            CapBy(StopWithin(Mathf.Max(loAt, hiAt) - 8f));
        // boxed in short of a traffic car, too close to steer round it: back off a little and try again —
        // not with a car right behind (a queue of racers backing into each other on the grid, #85)
        _boxed = boxed && !tailed && v < 0.5f ? _boxed + dt : 0f;
        if (_boxed > 1.5f) { _boxed = 0f; D.Reversing = 2f; }
        D.Passing = !float.IsNaN(passTarget) && !oncoming && !stopped;
        // the lane a pass moves into must be empty too: passing one car into the back of another
        // (a slow van beyond it) threw a car off at 107 km/h
        if (D.Passing)
            foreach (var (theirs, follow, beside) in _ahead)
                // and until this car is out beside the one it passes, it still follows it
                if (Mathf.Abs(theirs - passTarget) < beside - 0.1f || Mathf.Abs(theirs - myLat) < beside - 0.1f) CapBy(follow);
        if (D.Passing)
        {
            target = passTarget;
            if (dive) urgency = 2f;
        }
        else if (!float.IsNaN(passTarget)) CapBy(passCap);   // stay behind it for now
        // past the line: over to the right, out of the way of whoever is still racing
        if (Finished) target = -RoomR(ai);
        // everyone's room, then the edges: a blocked edge outranks all of it
        // bounds that cross (no way through between them): keep the one set by the nearer thing. Clamped
        // between the two, the target drifted anywhere in that gap, into a traffic car standing 10 m on (#159)
        target = lo <= hi ? Mathf.Clamp(target, lo, hi) : hiAt <= loAt ? Mathf.Min(target, hi) : Mathf.Max(target, lo);
        target = Mathf.Clamp(target, -RoomR(ai), RoomL(ai));
        D.Pressure = pressure;
        D.Crowded = crowded;
        if (nearest == float.MaxValue) Seen = "";
        else Seen += $" | onc {oncoming} stop {stopped} pass {D.Passing} cap {(D.Cap < 1e9f ? D.Cap * 3.6f : 0f):F0} corridor {lo:F1}..{hi:F1} (at {(loAt < 1e9f ? loAt : 0f):F0}/{(hiAt < 1e9f ? hiAt : 0f):F0} m)";
        // over quickly for a car coming the other way or a wreck: 1.2 m/s is a lane change in two
        // seconds, and closing at 30 m/s from 60 m there is one
        D.Lateral = Mathf.MoveToward(D.Lateral, target - line.Offset[ai], urgency * dt);
        if (Seen.Length > 0) Seen += $" aim {line.Offset[ai] + D.Lateral:F1} rule {_capRule} v {v * 3.6f:F0} rev {D.Reversing:F1}";
        // the last 2 s of it, every 0.25 s: what led up to a crash (DriveProbe prints it with one)
        if ((_traceT += dt) > 0.25f)
        {
            _traceT = 0f;
            Trail.Enqueue($"arc {Arc:F0} myLat {myLat:F2} | {Seen}");
            while (Trail.Count > 8) Trail.Dequeue();
        }
    }

    private float _traceT, _boxed;

    /// <summary>Lowers the cap, noting which rule set it (for the logs).</summary>
    private void CapBy(float cap, [System.Runtime.CompilerServices.CallerLineNumber] int rule = 0)
    {
        if (cap < D.Cap) { D.Cap = cap; _capRule = rule; }
    }
    private int _capRule;
    /// <summary>The source line that set the speed cap this step (checks).</summary>
    public int CapRule => _capRule;

    /// <summary>
    /// Traffic about to come out onto the road ahead — out of a side road, across a junction — that
    /// the road-wide boxes cannot see before it is on the route: each car's lane over the next 6 s
    /// (<see cref="World.Traffic.CarView.Path"/>), and where it first comes within the road's width
    /// ahead of this car. Arriving there no earlier than 1.5 s before it does, this car slows to a
    /// speed from which it stops 10 m short; once it is on the road the boxes take over. A car
    /// waiting at the junction for the race (<c>Holding</c>) is not coming out. Before this, a car
    /// joining at a junction was seen only once on the route and threw a racer off at 118 km/h.
    /// </summary>
    private float Crossing(Vector3 me, float v)
    {
        if (World.Traffic.Current is not { } traffic) return float.MaxValue;
        float cap = float.MaxValue;
        int mine = Side(me).Index;
        foreach (var c in traffic.CarsNear(me, 250f))
        {
            if (c.Holding || c.Path[^1].DistanceSquaredTo(c.Pos) < 1f) continue;
            // on the road already: the boxes see it
            var (latNow, now) = Side(c.Pos);
            if (Mathf.Abs(latNow) < Route.Width[now] * 0.5f + 1.5f && Mathf.Abs(c.Pos.Y - Route.Centre[now].Y) < 4f) continue;
            for (int k = 0; k < c.Path.Length; k++)
            {
                var (lat, i) = Side(c.Path[k]);
                if (Mathf.Abs(lat) > Route.Width[i] * 0.5f + 1.2f || Mathf.Abs(c.Path[k].Y - Route.Centre[i].Y) > 4f) continue;
                float dist = Route.Arc[i] - Route.Arc[mine];
                // it comes out behind this car, or this car is through well before it
                if (dist < 0f || dist / Mathf.Max(v, 1f) < 0.5f * (k + 1) - 1.5f) break;
                float slow = StopWithin(dist - 10f) + 1f;
                if (slow < cap && slow < v + 5f && !_crossingLogged)
                    Log?.Invoke($"{Label}: traffic coming out {dist:F0} m ahead in {0.5f * (k + 1):F1} s, from {Mathf.Abs(latNow):F0} m off the road, slowing from {v * 3.6f:F0} km/h");
                _crossingLogged |= slow < v + 5f;
                cap = Mathf.Min(cap, slow);
                break;
            }
        }
        if (cap == float.MaxValue) _crossingLogged = false;
        return cap;
    }

    private bool _crossingLogged;

    /// <summary>The sharpest curvature (signed, + left) within <paramref name="span"/> m after <paramref name="s"/>, and where; 0 if all of it is straighter than R 150 m.</summary>
    private float NextBend(float s, float span, out float at)
    {
        var line = Route.Line;
        float best = 0f;
        at = s;
        for (float d = 0f; d <= span; d += 4f)
        {
            float k = line.Curvature[line.IndexAt(s + d)];
            if (Mathf.Abs(k) > Mathf.Abs(best)) { best = k; at = s + d; }
        }
        return Mathf.Abs(best) < 1f / 150f ? 0f : best;
    }

    /// <summary>
    /// Braking for traffic, m/s²: firm but well inside the tyres, so a car crossing at a junction or
    /// a queue in a bend is met slowing down, not locked up. The speed from which that stops the car
    /// within <paramref name="gap"/> m: the old linear "their speed + half the gap" asked 11 m/s² of a
    /// car closing at 80 km/h.
    /// </summary>
    private const float EasyBrake = 5f;
    private static float StopWithin(float gap) => Mathf.Sqrt(2f * EasyBrake * Mathf.Max(0f, gap));

    private readonly Dictionary<ulong, Vector3> _sensedAt = new();
    private readonly Dictionary<ulong, Vector3> _sensedNow = new();
    private BoxShape3D? _probe;
    private readonly Godot.Collections.Array<Rid> _ignore = new();

    /// <summary>
    /// Solid bodies on the road ahead that the race does not list: boxes the width of the road swept
    /// along it, from 4 m to a gentle stopping distance ahead, keeping anything that is not static (terrain and
    /// trunks are) and not a player (the race lists those). Velocity from where each was last frame.
    /// </summary>
    private IEnumerable<Other> Sensed(float dt, Vector3 fwd)
    {
        if (!Player.IsInsideTree() || dt <= 0f) yield break;
        var space = Player.GetWorld3D().DirectSpaceState;
        // the whole road, not the line: oncoming traffic in the other lane is exactly what a pass,
        // a cut bend or a spun rival has to see
        _probe ??= new BoxShape3D();
        // the terrain and anything else static is excluded once met: a box the width of the road
        // touches the ground's faces, and a terrain body returns a hit PER FACE — eight of them
        // filled the result and the car behind them was never seen
        if (_ignore.Count == 0) _ignore.Add(Player.GetRid());
        var query = new PhysicsShapeQueryParameters3D { Shape = _probe, CollisionMask = 1, Exclude = _ignore };
        var line = Route.Line;
        float v = Player.Motion.Speed;
        float reach = Mathf.Clamp(25f + v + v * v / (2f * EasyBrake), 25f, 200f);
        _sensedNow.Clear();
        for (float d = 4f; d <= reach; d += 5f)
        {
            var a = line.PointAt(Arc + d - 2.5f);
            var b = line.PointAt(Arc + d + 2.5f);
            var along = RaceRoute.Flat(b - a);
            if (along.LengthSquared() < 1f) continue;
            float yaw = Mathf.Atan2(-along.X, -along.Z);
            int ci = line.IndexAt(Arc + d), cj = Mathf.Min(ci + 1, line.Arc.Count - 1);
            // the centre THERE, between two points: across a bridged junction (points up to 18 m apart) every
            // box sat on the point before it and left a hole in the middle — racers hit traffic standing in
            // the junction without ever sensing it (#159: four of eleven traffic retirements)
            float span = line.Arc[cj] - line.Arc[ci];
            var centre = span > 0.01f ? Route.Centre[ci].Lerp(Route.Centre[cj], Mathf.Clamp((Arc + d - line.Arc[ci]) / span, 0f, 1f)) : Route.Centre[ci];
            _probe.Size = new Vector3(Route.Width[ci] + 1f, 1.2f, 5f);
            query.Transform = new Transform3D(new Basis(Vector3.Up, yaw), centre with { Y = (a.Y + b.Y) * 0.5f } + Vector3.Up * 1.1f);
            bool more = false;
            foreach (var hit in space.IntersectShape(query, 16))
            {
                if (hit["collider"].AsGodotObject() is not Node3D body || body is FootPlayer) continue;
                // traffic is an AnimatableBody3D, a StaticBody3D subclass
                if (body is StaticBody3D and not AnimatableBody3D)
                {
                    var rid = ((CollisionObject3D)body).GetRid();
                    if (!_ignore.Contains(rid)) { _ignore.Add(rid); more = true; }
                    continue;
                }
                _sensedNow[body.GetInstanceId()] = body.GlobalPosition;
            }
            if (more) { query.Exclude = _ignore; d -= 5f; }   // the same box again, without them
        }
        foreach (var (id, at) in _sensedNow)
        {
            // first seen this step: its motion is not known yet, and taken for stopped it had the
            // car swerve for a body driving away from it — one step later it is known
            if (!_sensedAt.TryGetValue(id, out var was)) continue;
            yield return new Other(at, RaceRoute.Flat(at - was) / dt, false, World.Traffic.Current?.Yielding(id) ?? false, Civil: true);
        }
        _sensedAt.Clear();
        foreach (var kv in _sensedNow) _sensedAt[kv.Key] = kv.Value;
    }

    /// <summary>
    /// The point to steer at, <paramref name="look"/> m ahead on the line plus a sideways offset (+
    /// left). Pure pursuit cuts every bend by the chord's sagitta, <c>L²κ/8</c> — ~0.4 m at corner
    /// speed whatever the radius, 0.6 m in a hairpin — which put a wheel over the inside edge at the
    /// apexes; aiming that much wide of the line puts the car back on it.
    /// </summary>
    private Vector3 Aim(float s0, float look, float lateral)
    {
        var line = Route.Line;
        var t = RaceRoute.Flat(line.PointAt(s0 + look + 2f) - line.PointAt(s0 + look - 2f)).Normalized();
        float k = line.Curvature[line.IndexAt(s0 + look * 0.5f)];
        float sagitta = Mathf.Clamp(look * look * k / 8f, -1.5f, 1.5f);
        // an overtaking offset never takes the car past the room where it is aimed (RoomL/RoomR: a
        // blocked edge is hard): chosen against the line beside the rival, it overshot where the line moved
        int ai = line.IndexAt(s0 + look);
        lateral = Mathf.Clamp(lateral, -RoomR(ai) - line.Offset[ai], RoomL(ai) - line.Offset[ai]);
        return line.PointAt(s0 + look) + new Vector3(t.Z, 0, -t.X) * (lateral - sagitta);
    }

    private RideInput Policy(ref State d, Vector3 pos, in RideMotion m, float dt, bool live = false)
    {
        var line = Route.Line;
        float v = m.Speed;
        float slip = Wrap(m.Slip);
        while (d.Near < line.Points.Count - 2
               && RaceRoute.Flat(line.Points[d.Near + 1] - pos).Length() < RaceRoute.Flat(line.Points[d.Near] - pos).Length()) d.Near++;
        float s0 = Along(d.Near, pos);

        // steer the TRAVEL toward a point ahead on the line (plus any overtaking offset); ~0.7 s
        // ahead, and further at motorway speed, where 30 m is a third of a second and the hands saw
        float look = Mathf.Clamp(v * 0.7f, 7f, 70f);
        var ahead = Aim(s0, look, d.Lateral);
        var travel = new Basis(Vector3.Up, m.Yaw + m.Slip) * Vector3.Forward;
        float angle = RaceRoute.SignedAngle(RaceRoute.Flat(travel), RaceRoute.Flat(ahead - pos));
        // gentler hands at speed: full lock at 90 km/h to fix a metre of line is what starts a slide
        float steer = Mathf.Clamp(-angle * 2.6f * Mathf.Clamp(15f / Mathf.Max(v, 1f), 0.25f, 1f), -1f, 1f);

        // this car's own speed along its own profile, a beat ahead
        float want = Mathf.Min(d.Cap, Mathf.Min(Profile[d.Near], Profile[line.IndexAt(s0 + v * 0.3f)]));
        // off the line toward the inside of a bend (beside a rival, up the inside): a tighter radius
        // than the profile's, √(R'/R) of its speed
        float kHere = line.Curvature[line.IndexAt(s0 + v * 0.3f)];
        if (d.Lateral * kHere > 0f && Mathf.Abs(kHere) > 1e-3f)
        {
            float r = 1f / Mathf.Abs(kHere);
            want *= Mathf.Sqrt(Mathf.Max(r - Mathf.Abs(d.Lateral), 5f) / r);
        }

        // off the tarmac: come back to it gently — slow, soft hands, no drifting
        float past = Route.Off(pos) - Route.HalfWidthAt(pos);
        if (past > 1f) d.Recovering = true;
        else if (past < 0.2f) d.Recovering = false;
        if (d.Recovering)
        {
            want = Mathf.Min(want, 14f);
            steer = Mathf.Clamp(steer, -0.5f, 0.5f);
            // nose into a trunk or a bank: back out with the wheel the other way, then try again
            d.Stuck = v < 1.5f ? d.Stuck + dt : 0f;
            if (d.Stuck > 1.5f) { d.Reversing = 1.6f; d.Stuck = 0f; }
            d.Lost += dt;
        }
        else
        {
            // on the road and going nowhere with somewhere to go: the nose against a parked car or a
            // wreck (a race car sat 60 s against the one a rival left): back off the same way, and
            // after 8 s of it the reset takes over
            bool blocked = v < 1.5f && want > 3f;
            d.Stuck = blocked ? d.Stuck + dt : 0f;
            if (d.Stuck > 2.5f) { d.Reversing = 1.6f; d.Stuck = 0f; }
            d.Lost = blocked || d.Reversing > 0f ? d.Lost + dt : 0f;
        }
        if (d.Reversing > 0f)
        {
            d.Reversing -= dt;
            // backing out never goes over a blocked edge (a drop): moving toward one and within 0.8 s of it,
            // stop there (half of the blocked-edge metres up Sainte-Croix were cars reversing, #159)
            if (BackingOver(pos, m)) { d.Reversing = 0f; return new RideInput(0f, 1f, 0f, false); }
            // at a standstill the brake pedal selects reverse and drives it
            return new RideInput(0f, 0.8f, -steer, false);
        }

        // a slide that started on its own is caught (brake in it and the car spins), never held —
        // and backing out of a ditch is 180° of "slip" and not a slide at all
        bool reversing = Mathf.Abs(slip) > 1.6f;
        if (reversing) d.Drifting = false;
        else if (!d.Drifting && Mathf.Abs(slip) > 0.25f) { d.Drifting = true; d.Planned = false; d.Handbrake = 0f; }

        float throttle, brake = 0f;
        bool handbrake = false;
        if (d.Drifting)
        {
            d.Handbrake -= dt;
            handbrake = d.Handbrake > 0;
            float k = line.Curvature[d.Near];
            if (Mathf.Abs(k) > 0.002f) d.Bend = Mathf.Sign(k);
            float soon = MaxCurvature(s0, 0f, 24f);
            float hold = d.Planned && soon > 1f / (DriftRadius * 1.6f) ? -d.Bend * HoldAngle : 0f;
            if (handbrake) steer = -d.Bend * 0.9f;
            else if (d.Planned)
            {
                // the wheel holds the ANGLE (minus what the Game assist already counter-steers)...
                float wheel = slip + 1.5f * (slip - hold) - 0.12f * m.YawRate - (Rideable.Arcade ? 0.45f * slip : 0f);
                steer = Mathf.Clamp(-wheel / S.MaxSteer, -1f, 1f);
            }
            else
            {
                // catching a slide nobody planned: soft hands — full opposite lock on top of the Game
                // assist threw the car the other way, a fishtail that ended in the forest
                float wheel = (Rideable.Arcade ? 0.3f : 0.75f) * slip - 0.05f * m.YawRate;
                steer = Mathf.Clamp(-wheel / S.MaxSteer, -0.6f, 0.6f);
            }
            // ...and the gas holds the LINE: more gas slides wide, less lets the rears bite
            throttle = handbrake ? 0f : Mathf.Clamp(0.75f - 2.5f * angle * d.Bend + (want - v) * 0.03f, 0.15f, 1f);
            if (hold == 0f) throttle = d.Planned ? Mathf.Min(throttle, 0.4f) : 0.25f;
            // a slide nobody planned, too fast for what is coming: off the gas and a light brake (eased as in a
            // straight-line stop when the rear is out). At 0.25 gas and no brake an AE86 slid into a 22 m
            // hairpin at 96 km/h (#159)
            if (!d.Planned && v > want + 2f)
            {
                throttle = 0f;
                brake = Mathf.Clamp((v - want) * 0.3f, 0f, 1f) * Mathf.Clamp(1f - (Mathf.Abs(slip) - 0.05f) * 8f, 0.2f, 1f);
            }
            // the first second of a planned drift keeps the gas in: lift there and the rears bite
            // before the car has rotated, and the "drift" peaks at 19° and counts for nothing
            else if (d.Planned && d.Handbrake > -1f) throttle = Mathf.Max(throttle, 0.7f);
            d.PeakSlip = Mathf.Max(d.PeakSlip, Mathf.Abs(slip));
            if (Mathf.Abs(slip) > 0.26f) d.DriftTime += dt;
            if (d.Handbrake < -0.4f && Mathf.Abs(slip) < 0.08f) { d.Drifting = false; d.Planned = false; }
        }
        else
        {
            // gentle on the gas out of a slow corner (power-over), flat out at speed: the profile's
            // straights are what the car's power allows, so a soft pedal there only ever lags it
            float gain = Mathf.Lerp(0.35f, 1f, Mathf.Clamp((v - 28f) / 20f, 0f, 1f));
            throttle = Mathf.Clamp((want - v) * gain, 0f, 1f);
            // the rear stepping out under power (low gear, uphill, out of a hairpin): feather the gas as a
            // driver feels it go. Floored, an FD fishtailed ±30° up a straight at 60-75 km/h, caught as an
            // unplanned slide at 0.25 gas, back to full gas as it gripped, and on into the trees (#159)
            // (moving forward only: at a standstill or backing out "slip" is 180° and means nothing)
            if (v > 3f && !reversing && Mathf.Abs(slip) > 0.06f) throttle *= Mathf.Clamp(1f - (Mathf.Abs(slip) - 0.06f) * 5f, 0.25f, 1f);
            // trail off the brake as the wheel turns in: braking hard in a bend unloads the rear
            brake = Mathf.Min(Mathf.Clamp((v - want) * 0.3f, 0f, 1f), PedalMax(v)) * (1f - 0.7f * Mathf.Abs(steer));
            // the rear stepping out under braking (load off it, at 170 km/h a line correction is enough):
            // ease off the pedal as a driver feels it, or it is a spin, not a stop
            if (Mathf.Abs(slip) > 0.05f) brake *= Mathf.Clamp(1f - (Mathf.Abs(slip) - 0.05f) * 8f, 0.2f, 1f);
            if (live) Blunder(ref throttle, ref brake, v - want, s0, v, dt);
            if (live && !d.Recovering) steer = EdgeGuard(pos, m, steer, ref throttle);
        }
        return new RideInput(throttle, brake, steer, false, handbrake);
    }

    /// <summary>
    /// The most brake pedal this driver gives: what stops the car at 90% of the rear-lockup limit (the
    /// profile's own, <see cref="RaceLine.SpeedProfile(CarSpec, bool, float, float)"/>). The pedal follows
    /// the speed over the target, and a cap that drops (a traffic car ahead) floored it: at 120-150 km/h the
    /// rear stepped out 8-13° and the car slid off (#159). A driver without ABS does not stamp on it either.
    /// </summary>
    private float PedalMax(float u)
    {
        float mu = S.Grip * (Rideable.Arcade ? 1.12f : 1f), g = Rideable.Gravity;
        float full = Mathf.Min(S.BrakeDecel > 0 ? S.BrakeDecel * (Rideable.Arcade ? 1.1f : 1f) : 99f, 0.95f * mu * g);
        float rearSat = mu * g * S.FrontAxle / S.Wheelbase / (0.35f + mu * S.CgHeight / S.Wheelbase)
            * Mathf.Lerp(1f, 0.8f, Mathf.Clamp((u - 30f) / 30f, 0f, 1f));
        return Mathf.Clamp(0.9f * rearSat / Mathf.Max(full, 0.1f), 0.3f, 1f);
    }

    /// <summary>
    /// The hard edge: where the side the car is heading for is blocked (no surveyed verge — a drop,
    /// a wall, trunks, no data), the car's body 0.8 s from now must stay 0.35 m inside the tarmac edge. A
    /// target inside the room is not enough on its own: tracking error in S-bends, a knock from a
    /// rival or a correction at speed carried wheels 0.2-1.5 m over. Steers back in by how far it
    /// would go over, and lifts.
    /// </summary>
    private float EdgeGuard(Vector3 pos, in RideMotion m, float steer, ref float throttle)
    {
        var (lat, ci) = Side(pos);
        var n = RaceLine.Normal(Route.Centre, ci);
        var travel = new Basis(Vector3.Up, m.Yaw + m.Slip) * Vector3.Forward * m.Speed;
        float pred = lat + (travel.X * n.X + travel.Z * n.Y) * 0.8f;
        // blocked anywhere from here to where the car will be in 0.8 s (a drop begins a few metres on)
        bool blocked = false;
        int until = Mathf.Min(ci + Mathf.CeilToInt(m.Speed * 0.8f / 2f) + 1, Route.Centre.Count - 1);
        for (int k = ci; k <= until && !blocked; k++)
            blocked = pred > 0f ? Route.Line.MarginLeft[k] <= 0f : Route.Line.MarginRight[k] <= 0f;
        // from 0.35 m short of the edge: by the time the car is at it the correction must already be in
        float over = Mathf.Abs(pred) + (Player.Vehicle?.BodyRadius ?? 0.85f) + 0.35f - Route.Width[ci] * 0.5f;
        if (!blocked || over <= 0f) return steer;
        EdgeSaves++;
        throttle *= 0.5f;
        // + steer is to the right: away from a left edge (pred > 0)
        // with the hands' own gain at speed (see Drive): a raw 0.8 of lock on top at 130-150 km/h started the
        // slides that put cars into the trees on the Sainte-Croix descent (#159)
        float gain = Mathf.Clamp(15f / Mathf.Max(m.Speed, 1f), 0.25f, 1f);
        return Mathf.Clamp(steer + Mathf.Sign(pred) * Mathf.Min(over * 2f, 0.8f) * gain, -1f, 1f);
    }

    private bool BackingOver(Vector3 pos, in RideMotion m)
    {
        if (m.Speed < 0.3f) return false;
        var (lat, ci) = Side(pos);
        var n = RaceLine.Normal(Route.Centre, ci);
        var travel = new Basis(Vector3.Up, m.Yaw + m.Slip) * Vector3.Forward * m.Speed;
        float sideways = travel.X * n.X + travel.Z * n.Y, pred = lat + sideways * 0.8f;
        if (sideways * pred <= 0f) return false;   // moving away from that edge
        bool blocked = pred > 0f ? Route.Line.MarginLeft[ci] <= 0f : Route.Line.MarginRight[ci] <= 0f;
        return blocked && Mathf.Abs(pred) + (Player.Vehicle?.BodyRadius ?? 0.85f) + 0.2f > Route.Width[ci] * 0.5f;
    }

    /// <summary>Steps the edge guard had to correct (for checks).</summary>
    public int EdgeSaves;

    /// <summary>
    /// A braking point got wrong: entering a braking zone under pressure (a rival within a second),
    /// a less skilled or more aggressive driver now and then stays on the gas a beat too long, then
    /// stamps on the pedal and holds it — no easing off as the rear steps out. Whether that is a
    /// spin or a lock-up that runs wide is the car's physics, not this.
    /// </summary>
    private void Blunder(ref float throttle, ref float brake, float over, float s0, float v, float dt)
    {
        // a braking zone: 4 m/s over the profile to enter, under 0.5 to leave (it flickered at one threshold)
        bool zone = over > (_inZone ? 0.5f : 4f);
        _sinceMistake += dt;
        if (zone && !_inZone && D.Pressure && _sinceMistake > 20f && _rng.NextDouble() < MistakeChance)
        {
            _sinceMistake = 0f;
            _late = 0.1f + 0.2f * (float)_rng.NextDouble();
            _hard = 1.2f;
            Mistakes++;
            Log?.Invoke($"{Label}: MISTAKE, late on the brakes at {s0:F0} m, {v * 3.6f:F0} km/h");
        }
        _inZone = zone;
        if (_late > 0f) { _late -= dt; brake = 0f; throttle = 0.6f; }
        else if (_hard > 0f) { _hard -= dt; brake = 1f; throttle = 0f; }
    }

    // ------------------------------------------------------------------------------------
    // lean-steered mounts and runners
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// A rider: pure pursuit to a point ~0.8 s ahead on the line, the bank that arc needs
    /// (<c>tan φ = v²κ/g</c>) as the steering command, pedal/throttle to the profile, brakes over it.
    /// </summary>
    private RideInput Ride(float dt, bool go, IEnumerable<Other> others)
    {
        if (!go) return new RideInput(0f, 1f, 0f, false);
        var m = Player.Motion;
        var pos = Player.GlobalPosition;
        float v = m.Speed;
        Advance(pos);
        // off the road and going nowhere (ran wide into a bank, a fence): put back on the line after
        // 3 s — a rider has no reverse, and one sat 200 s in a field 100 m from the finish
        _held = !Finished && v < 3f && Route.Off(pos) - Route.HalfWidthAt(pos) > 1.5f ? _held + dt : 0f;
        if (_held > 3f) { _held = 0f; ResetToLine(); }
        float s0 = Along(D.Near, pos);
        CountVerge(dt);
        var line = Route.Line;
        float look = Mathf.Clamp(v * 0.8f, 5f, 60f);
        var ahead = Aim(s0, look, D.Lateral);
        var travel = RaceRoute.Flat(new Basis(Vector3.Up, m.Yaw) * Vector3.Forward);
        var to = RaceRoute.Flat(ahead - pos);
        float alpha = RaceRoute.SignedAngle(travel, to);
        float kappa = 2f * Mathf.Sin(alpha) / Mathf.Max(to.Length(), 1f);
        float bank = Mathf.Atan(Mathf.Max(v, 2f) * Mathf.Max(v, 2f) * kappa / Rideable.Gravity);
        // the bank takes the yaw's sign and the steer the opposite (SteerByLean: target = -steer·φmax)
        float steer = Mathf.Clamp(-bank / Mathf.Max(_maxLean, 0.1f), -1f, 1f);

        float want = Mathf.Min(Profile[D.Near], Profile[line.IndexAt(s0 + v * 0.5f)]);
        int ai = line.IndexAt(s0 + look);
        // past the line: pull over to the right and stop, out of the way of whoever is still racing
        float side = Finished ? -RoomR(ai) - line.Offset[ai] : 0f;
        // riders queue behind a moving one (nobody on two wheels is squeezed past on a Jura road);
        // one STOPPED in the way — a finisher, a faller — is ridden round, not queued behind for
        // good (a moto race ended "DNF" behind the winner stopped past the line)
        foreach (var q in others)
        {
            var rel = RaceRoute.Flat(q.Position - pos);
            float a = rel.Dot(travel);
            float across = rel.Dot(new Vector3(travel.Z, 0, -travel.X));
            float along = q.Velocity.X * travel.X + q.Velocity.Z * travel.Z;
            if (a < 1.5f || a > 25f || Mathf.Abs(across) > 1.6f) continue;
            if (along < 1f && !Finished)
            {
                var (theirs, qi) = Side(q.Position);
                float l = theirs + 1.6f, r = theirs - 1.6f;
                side = (l <= RoomL(qi) && (RoomL(qi) - l >= -RoomR(qi) - r) ? l : r) - line.Offset[qi];
                want = Mathf.Min(want, 6f + a * 0.3f);
            }
            else if (a < 15f) want = Mathf.Min(want, Mathf.Max(along, 0f) + (a - 5f) * 0.4f);
        }
        D.Lateral = Mathf.MoveToward(D.Lateral, side, 2f * dt);
        if (Finished) want = 0f;
        float throttle = Mathf.Clamp((want - v) * 0.5f, 0f, 1f);
        float brake = Mathf.Clamp((v - want) * 0.4f, 0f, 1f);
        return new RideInput(throttle, brake, steer, want - v > 2f);
    }

    /// <summary>A runner: toward a point a few metres ahead on the line, running, stopping at the finish.</summary>
    private (Vector3 Wish, bool Run) Walk()
    {
        if (Player.Origin is { } origin) Route.Follow(origin.Frame);   // as in Drive (#185)
        TakeWiderLine();
        if (!Go || Finished) return (Vector3.Zero, false);
        var pos = Player.GlobalPosition;
        Advance(pos);
        var to = RaceRoute.Flat(Route.Line.PointAt(Along(D.Near, pos) + 4f) - pos);
        return (to.LengthSquared() > 0.01f ? to.Normalized() : Vector3.Zero, true);
    }

    /// <summary>
    /// Metres along the line of <paramref name="pos"/>: the nearest point's arc plus the projection on
    /// the segment after it. The point's arc alone stalls wherever the points are far apart (RoadGen's
    /// bridged junctions leave gaps up to 18 m): a runner aimed "4 m past the nearest point" reached
    /// that spot, was still nearest the same point, and stood there — both runners, 280 m from the line.
    /// </summary>
    private float Along(int near, Vector3 pos)
    {
        var line = Route.Line;
        if (near >= line.Points.Count - 1) return line.Arc[near];
        var seg = RaceRoute.Flat(line.Points[near + 1] - line.Points[near]);
        float len = seg.Length();
        if (len < 1e-3f) return line.Arc[near];
        return line.Arc[near] + Mathf.Clamp(RaceRoute.Flat(pos - line.Points[near]).Dot(seg) / len, 0f, len);
    }

    private void Advance(Vector3 pos)
    {
        var line = Route.Line;
        while (D.Near < line.Points.Count - 2
               && RaceRoute.Flat(line.Points[D.Near + 1] - pos).Length() < RaceRoute.Flat(line.Points[D.Near] - pos).Length()) D.Near++;
    }

    // ------------------------------------------------------------------------------------
    // drift planning (cars)
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Decides whether the corner ahead gets drifted, by trying it: the car's own model on a copy of
    /// its state, driven by the same policy through a handbrake entry and a held drift. Fastest
    /// feasible entry wins; if none stays on the road, the corner is taken on grip.
    /// </summary>
    private void Plan(Car car, Vector3 pos, in RideMotion m, float dt)
    {
        if (S.Style == DriveStyle.Grip) return;
        _planTimer -= dt;
        _cooldown -= dt;
        float s0 = Arc;
        if (D.Drifting || D.Recovering || D.Crowded || _planTimer > 0 || _cooldown > 0 || s0 < _gripUntil) return;
        _planTimer = 0.2f;
        if (m.Speed < 12f || MaxCurvature(s0, 10f, 40f) < 1f / DriftRadius) return;

        Plans++;
        // a tight corner is drifted slower rather than not at all: a slower entry is often the one
        // that stays on a 6 m road, and it is how the series drives a hairpin
        foreach (float factor in new[] { 1f, 0.85f, 0.7f, 0.6f, 0.5f })
        {
            var (ok, _, peak, _) = Simulate(car, pos, m, m.Speed * factor);
            if (!ok) continue;
            Feasible++;
            if (factor == 1f)
            {
                D.Drifting = true;
                D.Planned = true;
                D.Handbrake = 0.4f;
                Log?.Invoke($"{S.Label}: DRIFT at {s0:F0} m, {m.Speed * 3.6f:F0} km/h (sim peak {Mathf.RadToDeg(peak):F0}°)");
            }
            else D.Cap = m.Speed * factor;   // brake to the speed the simulation drifted at
            return;
        }
        _gripUntil = s0 + 60f;
    }

    private (bool Ok, float Off, float Peak, bool Entered) Simulate(Car live, Vector3 pos, RideMotion m, float cap)
    {
        var car = live.Clone();
        var d = D;
        d.Cap = cap;
        d.PeakSlip = 0;
        d.DriftTime = 0;
        bool entered = false;
        float maxOff = -100f, entryWait = 0;   // metres past the edge: negative is inside it
        for (float t = 0; t < PlanSeconds; t += SimDt)
        {
            if (!entered && m.Speed <= cap + 0.5f) { entered = true; d.Drifting = true; d.Planned = true; d.Handbrake = 0.4f; }
            if (!entered && (entryWait += SimDt) > 2f) break;
            var input = Policy(ref d, pos, m, SimDt);
            float s = Route.Line.Arc[d.Near];
            float grade = (Route.Line.PointAt(s + 3f).Y - Route.Line.PointAt(s - 3f).Y) / 6f;
            car.Step(input, new RideGround(true, grade), SimDt, ref m);
            var travel = new Basis(Vector3.Up, m.Yaw + m.Slip) * Vector3.Forward;
            pos += travel * m.Speed * SimDt;
            if (entered) maxOff = Mathf.Max(maxOff, Route.Off(pos) - Route.HalfWidthAt(pos));
            if (Mathf.Abs(Wrap(m.Slip)) > 1.35f) return (false, maxOff, d.PeakSlip, entered);
            if (entered && !d.Drifting && t > 1f) break;   // caught and straight: the drift is done
        }
        // 1.5 m INSIDE the edge: the plan runs on the line's heights, and the real camber and bumps
        // put the car wider than a plan that allowed itself the whole road
        bool ok = entered && maxOff < -1.5f && d.PeakSlip > 0.35f;
        return (ok, maxOff, d.PeakSlip, entered);
    }

    public int Resets;
    /// <summary>What racecraft saw last step, nearest thing first (for checks' logs).</summary>
    public string Seen = "";
    public readonly Queue<string> Trail = new();
    /// <summary>Spins: the car past 90° to its travel going forward (not backing out); logged each time.</summary>
    public int Spins;
    private bool _spinning;

    private void CountSpin(Car car, in RideMotion m)
    {
        float slip = Mathf.Abs(Wrap(m.Slip));
        if (!_spinning && slip > 1.6f && car.Gear > 0 && m.Speed > 4f && D.Reversing <= 0f)
        {
            _spinning = true;
            Spins++;
            Log?.Invoke($"{Label}: SPIN at {Arc:F0} m, {m.Speed * 3.6f:F0} km/h");
        }
        else if (_spinning && (slip < 0.3f || m.Speed < 1f)) _spinning = false;
    }

    private float _held;
    /// <summary>Everyone about last step (racers, wrecks, traffic): a reset lands clear of all of them.</summary>
    private readonly List<Vector3> _around = new();

    private void ResetToLine(string why = "stuck off the road")
    {
        var line = Route.Line;
        float s = Arc + 5f;
        // the first point ahead with nothing within 8 m (up to 150 m on)
        for (float t = s; t < Arc + 150f; t += 3f)
        {
            var p = line.PointAt(t);
            if (_around.All(o => RaceRoute.Flat(o - p).Length() > 8f)) { s = t; break; }
        }
        var at = line.PointAt(s);
        var fwd = RaceRoute.Flat(line.PointAt(s + 2f) - line.PointAt(s - 2f)).Normalized();
        // PlaceAt, not Rotation: a car's step writes its rotation back from its own heading, so a car reset
        // that way kept the heading it had in the ditch and drove straight back off the road — at 100 km/h
        // into the trees 40 m from the line, or reset after reset at the same spot (#159)
        Player.PlaceAt(at + Vector3.Up * 1.2f, Mathf.Atan2(-fwd.X, -fwd.Z));
        D.Lost = 0; D.Recovering = false; D.Reversing = 0; D.Stuck = 0; D.Drifting = false;
        D.Near = line.IndexAt(s);
        Resets++;
        Log?.Invoke($"{Label}: {why}, reset to the line at {s:F0} m");
    }

    private void EndDrift(bool planned)
    {
        float deg = Mathf.RadToDeg(D.PeakSlip);
        if (planned && deg > 20f && D.DriftTime > 0.5f) Drifts++;
        if (planned) Log?.Invoke($"{S.Label}: drift done, peak {deg:F0}°, {D.DriftTime:F1} s past 15°");
        if (planned) BestDrift = Mathf.Max(BestDrift, deg);
        D.PeakSlip = 0;
        D.DriftTime = 0;
        _cooldown = 1.2f;
    }

    private float MaxCurvature(float s, float from, float to)
    {
        float k = 0;
        for (float d = from; d <= to; d += 4f) k = Mathf.Max(k, Mathf.Abs(Route.Line.Curvature[Route.Line.IndexAt(s + d)]));
        return k;
    }

    private static float Wrap(float a) => Core.MathX.WrapAngle(a);

    /// <summary>Whether <see cref="For"/> has a pilot for this class (the server asks it before spawning an NPC in it).</summary>
    public static bool Drives(RideKind kind) => kind == RideKind.OnFoot || CarCatalog.IsCar(kind)
        || (Rideable.Create(kind) is { } ride && ride is not Flyer && MeasureLean(kind) != null);
}
