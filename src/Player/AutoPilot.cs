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
    /// <summary>Half the car's width for "is a wheel off the tarmac", m (the body capsule's radius).</summary>
    private const float CarHalf = 0.85f;

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
    }

    /// <summary>Another vehicle on the road, as this driver sees it.</summary>
    public readonly record struct Other(Vector3 Position, float Speed, bool Wreck);

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

    private float[] ComputeProfile()
    {
        var line = Route.Line;
        float g = Rideable.Gravity;
        return Kind switch
        {
            // a share of the tyre limit: a narrow road with camber and bumps is not a flat skidpad
            Mount.Car => line.SpeedProfile(S, Rideable.Arcade, S.Style == DriveStyle.Grip ? 0.64f : 0.6f),
            // v = √(g·R·tan φ): 80% of the lean it can hold, 85% of its brakes
            Mount.Lean => line.SpeedProfile(0.8f * g * Mathf.Tan(_maxLean), 0.85f * _brake),
            // a runner corners on its feet at any speed it can run
            _ => line.SpeedProfile(0.6f * g, 4f),
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
        var input = Policy(ref D, Player.GlobalPosition, motion, dt);
        // Past the line the route runs out a few tens of metres later: stay on the line and brake to
        // a stop there, instead of racing into the trees at 150 km/h (every demo ended in a pile-up).
        if (Finished)
            // (not the full pedal: past the rear's saturation that spun a GT-R at 100 km/h)
            return motion.Speed > 1.5f ? input with { Throttle = 0f, Brake = Mathf.Abs(Wrap(motion.Slip)) > 0.05f ? 0.2f : 0.7f, Effort = false }
                                       : new RideInput(0f, 0f, 0f, false, Handbrake: true);
        if (wasDrifting && !D.Drifting) EndDrift(wasPlanned);
        Plan(car, Player.GlobalPosition, motion, dt);
        return input;
    }

    /// <summary>The surveyed line, once it is ready: the route takes it, and this driver's profile follows.</summary>
    private void TakeWiderLine()
    {
        if (_widen is { IsCompletedSuccessfully: true } done)
        {
            if (Route.Line == _profiled && done.Result.Points.Count == Route.Line.Points.Count) Route.Line = done.Result;
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
        float beyond = Mathf.Abs(lat) + CarHalf - Route.Width[i] * 0.5f;
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
            UnsafeAt.Add((int)(Arc / 100f) * 100);
            UnsafeDepth = Mathf.Max(UnsafeDepth, beyond);
        }
    }

    /// <summary>
    /// Racecraft: closing on a car ahead, move beside it where the road is straight and a car's
    /// width fits between it and the edge of what is drivable (tarmac plus safe verge), otherwise
    /// lift to its speed; a car coming the other way is let past on whichever side has room; a
    /// wreck or anything stopped is gone round whatever it takes. Besides the race's own cars
    /// (<paramref name="others"/>), whatever solid thing is sensed on the line ahead counts —
    /// traffic, a parked machine, another player.
    /// </summary>
    private void Traffic(float dt, IEnumerable<Other> others)
    {
        float want = 0f;
        bool oncoming = false;
        D.Cap = float.MaxValue;
        var me = Player.GlobalPosition;
        float v = Player.Motion.Speed;
        var fwd = RaceRoute.Flat(new Basis(Vector3.Up, Player.Motion.Yaw + Player.Motion.Slip) * Vector3.Forward);
        var line = Route.Line;
        foreach (var q in others.Concat(Sensed(dt, fwd)))
        {
            var rel = RaceRoute.Flat(q.Position - me);
            float ahead = rel.Dot(fwd);
            // at 250 km/h 22 m is a third of a second: ~2 s of closing ahead, 4 s for oncoming traffic
            if (ahead < 2f || ahead > Mathf.Max(22f, (v - q.Speed) * (q.Speed < -1f ? 4f : 2f))) continue;
            var (theirs, qi) = Side(q.Position);
            float lineOff = line.Offset[qi];
            if (Mathf.Abs(theirs - lineOff) > 2.4f) continue;   // not on this car's line
            float roomL = line.RoomLeft[qi], roomR = line.RoomRight[qi];
            // a car's width (1.8 m) and a gap beside it, all of it inside the room
            const float Beside = 2.4f;
            bool fitsLeft = theirs + Beside <= roomL, fitsRight = theirs - Beside >= -roomR;
            float PickSide()
            {
                float l = theirs + Beside, r = theirs - Beside;
                return (fitsLeft && (!fitsRight || Mathf.Abs(l - lineOff) <= Mathf.Abs(r - lineOff)) ? l : r) - lineOff;
            }
            if (q.Speed < -1f)
            {
                // coming the other way: out of its path now, bend or not, and slow if there is no room
                oncoming = true;
                if (fitsLeft || fitsRight) want = PickSide();
                else D.Cap = Mathf.Min(D.Cap, 6f);
                continue;
            }
            if (q.Wreck || q.Speed < 0.5f)
            {
                // just clear of the wreck, on the side it leaves most room, within the drivable width
                float passLeft = Mathf.Min(theirs + 2.2f, roomL + 0.3f), passRight = Mathf.Max(theirs - 2.2f, -roomR - 0.3f);
                float target = passLeft - theirs >= theirs - passRight ? passLeft : passRight;
                want = target - lineOff;
                D.Cap = Mathf.Min(D.Cap, 8f);
                continue;
            }
            bool straight = MaxCurvature(Arc, 0f, 60f + v) < 1f / 300f;
            if (straight && (fitsLeft || fitsRight)) want = PickSide();
            else D.Cap = Mathf.Min(D.Cap, q.Speed + (ahead - 8f) * 0.5f);
        }
        // over quickly for a car coming the other way: 1.2 m/s is a lane change in two seconds, and
        // closing at 30 m/s from 60 m there is one
        D.Lateral = Mathf.MoveToward(D.Lateral, want, (oncoming ? 4f : 1.2f) * dt);
    }

    private readonly Dictionary<ulong, Vector3> _sensedAt = new();
    private readonly Dictionary<ulong, Vector3> _sensedNow = new();
    private BoxShape3D? _probe;

    /// <summary>
    /// Solid bodies on the line ahead that the race does not list: boxes the width of a car swept
    /// along the line, from 4 m to ~2.5 s ahead, keeping anything that is not static (terrain and
    /// trunks are) and not a player (the race lists those). Speed along this car's travel from
    /// where each was last frame; negative is coming the other way.
    /// </summary>
    private IEnumerable<Other> Sensed(float dt, Vector3 fwd)
    {
        if (!Player.IsInsideTree() || dt <= 0f) yield break;
        var space = Player.GetWorld3D().DirectSpaceState;
        _probe ??= new BoxShape3D { Size = new Vector3(2.6f, 1.2f, 5f) };
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = _probe,
            CollisionMask = 1,
            Exclude = new Godot.Collections.Array<Rid> { Player.GetRid() },
        };
        var line = Route.Line;
        float reach = Mathf.Clamp(Player.Motion.Speed * 2.5f, 25f, 150f);
        _sensedNow.Clear();
        for (float d = 4f; d <= reach; d += 5f)
        {
            var a = line.PointAt(Arc + d - 2.5f);
            var b = line.PointAt(Arc + d + 2.5f);
            var along = RaceRoute.Flat(b - a);
            if (along.LengthSquared() < 1f) continue;
            float yaw = Mathf.Atan2(-along.X, -along.Z);
            query.Transform = new Transform3D(new Basis(Vector3.Up, yaw), (a + b) * 0.5f + Vector3.Up * 1.1f);
            foreach (var hit in space.IntersectShape(query, 8))
            {
                if (hit["collider"].AsGodotObject() is not Node3D body || body is FootPlayer || body is StaticBody3D and not AnimatableBody3D) continue;   // traffic is an AnimatableBody3D, a StaticBody3D subclass
                _sensedNow[body.GetInstanceId()] = body.GlobalPosition;
            }
        }
        foreach (var (id, at) in _sensedNow)
        {
            float speed = _sensedAt.TryGetValue(id, out var was) ? RaceRoute.Flat(at - was).Dot(fwd) / dt : 0f;
            yield return new Other(at, speed, false);
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
        // an overtaking offset never takes the car past the room where it is aimed (tarmac + safe
        // verge): chosen against the line's offset beside the rival, it overshot where the line moved
        int ai = line.IndexAt(s0 + look);
        lateral = Mathf.Clamp(lateral, -line.RoomRight[ai] - line.Offset[ai], line.RoomLeft[ai] - line.Offset[ai]);
        return line.PointAt(s0 + look) + new Vector3(t.Z, 0, -t.X) * (lateral - sagitta);
    }

    private RideInput Policy(ref State d, Vector3 pos, in RideMotion m, float dt)
    {
        var line = Route.Line;
        float v = m.Speed;
        float slip = Wrap(m.Slip);
        while (d.Near < line.Points.Count - 2
               && RaceRoute.Flat(line.Points[d.Near + 1] - pos).Length() < RaceRoute.Flat(line.Points[d.Near] - pos).Length()) d.Near++;
        float s0 = line.Arc[d.Near];

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
        else d.Lost = 0f;
        if (d.Reversing > 0f)
        {
            d.Reversing -= dt;
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
            // trail off the brake as the wheel turns in: braking hard in a bend unloads the rear
            brake = Mathf.Clamp((v - want) * 0.3f, 0f, 1f) * (1f - 0.7f * Mathf.Abs(steer));
            // the rear stepping out under braking (load off it, at 170 km/h a line correction is enough):
            // ease off the pedal as a driver feels it, or it is a spin, not a stop
            if (Mathf.Abs(slip) > 0.05f) brake *= Mathf.Clamp(1f - (Mathf.Abs(slip) - 0.05f) * 8f, 0.2f, 1f);
        }
        return new RideInput(throttle, brake, steer, false, handbrake);
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
        float s0 = Arc;
        CountVerge(dt);
        var line = Route.Line;
        float look = Mathf.Clamp(v * 0.8f, 5f, 60f);
        var ahead = Aim(s0, look, 0f);
        var travel = RaceRoute.Flat(new Basis(Vector3.Up, m.Yaw) * Vector3.Forward);
        var to = RaceRoute.Flat(ahead - pos);
        float alpha = RaceRoute.SignedAngle(travel, to);
        float kappa = 2f * Mathf.Sin(alpha) / Mathf.Max(to.Length(), 1f);
        float bank = Mathf.Atan(Mathf.Max(v, 2f) * Mathf.Max(v, 2f) * kappa / Rideable.Gravity);
        // the bank takes the yaw's sign and the steer the opposite (SteerByLean: target = -steer·φmax)
        float steer = Mathf.Clamp(-bank / Mathf.Max(_maxLean, 0.1f), -1f, 1f);

        float want = Mathf.Min(Profile[D.Near], Profile[line.IndexAt(s0 + v * 0.5f)]);
        // riders queue behind one another: nobody on two wheels is squeezed past on a Jura road
        foreach (var q in others)
        {
            var rel = RaceRoute.Flat(q.Position - pos);
            float a = rel.Dot(travel);
            if (a > 1.5f && a < 15f && Mathf.Abs(rel.Dot(new Vector3(travel.Z, 0, -travel.X))) < 1.2f)
                want = Mathf.Min(want, q.Speed + (a - 5f) * 0.4f);
        }
        if (Finished) want = 0f;
        float throttle = Mathf.Clamp((want - v) * 0.5f, 0f, 1f);
        float brake = Mathf.Clamp((v - want) * 0.4f, 0f, 1f);
        return new RideInput(throttle, brake, steer, want - v > 2f);
    }

    /// <summary>A runner: toward a point a few metres ahead on the line, running, stopping at the finish.</summary>
    private (Vector3 Wish, bool Run) Walk()
    {
        TakeWiderLine();
        if (!Go || Finished) return (Vector3.Zero, false);
        var pos = Player.GlobalPosition;
        Advance(pos);
        var to = RaceRoute.Flat(Route.Line.PointAt(Arc + 4f) - pos);
        return (to.LengthSquared() > 0.01f ? to.Normalized() : Vector3.Zero, true);
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
        if (D.Drifting || D.Recovering || _planTimer > 0 || _cooldown > 0 || s0 < _gripUntil) return;
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

    private void ResetToLine()
    {
        var line = Route.Line;
        float s = Arc + 5f;
        var at = line.PointAt(s);
        var fwd = RaceRoute.Flat(line.PointAt(s + 2f) - line.PointAt(s - 2f)).Normalized();
        Player.GlobalPosition = at + Vector3.Up * 1.2f;
        Player.Rotation = new Vector3(0, Mathf.Atan2(-fwd.X, -fwd.Z), 0);
        Player.Velocity = Vector3.Zero;
        D.Lost = 0; D.Recovering = false; D.Reversing = 0; D.Stuck = 0; D.Drifting = false;
        D.Near = line.IndexAt(s);
        Resets++;
        Log?.Invoke($"{Label}: stuck off the road, reset to the line at {s:F0} m");
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

    private static float Wrap(float a) => Mathf.Wrap(a, -Mathf.Pi, Mathf.Pi);
}
