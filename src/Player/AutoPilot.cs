using Godot;

namespace UnitSport.Player;

/// <summary>
/// A scripted racing driver for one car on a <see cref="RaceRoute"/>, working from that car's own
/// specs: the racing line, its own speed profile (<see cref="RaceLine.SpeedProfile"/>), drifts only
/// where a forward simulation of its own car (<see cref="Car.Clone"/>) through a handbrake entry
/// stays on the tarmac — grip cars never drift — soft hands for a slide nobody planned, a gentle
/// return from the verge, backing out when stuck, and racecraft: pass on straights, queue in
/// bends, go round wrecks.
///
/// <para>
/// Plug <see cref="Drive"/> into <see cref="FootPlayer.RideControls"/>. Used by <c>--drivecheck</c>
/// and by <c>--raceauto</c> players in a multiplayer race.
/// </para>
/// </summary>
public sealed class AutoPilot
{
    /// <summary>A narrow Jura pass is not a motorway: no scripted driver goes faster than this.</summary>
    public const float MaxSpeed = 42f;
    /// <summary>Only corners tighter than this radius, m, are considered for a drift.</summary>
    private const float DriftRadius = 70f;
    private const float PlanSeconds = 3.5f, SimDt = 1f / 60f;
    /// <summary>
    /// The drift angle held through a corner, rad (~30°). Tried 22° to sweep less of a 6 m road:
    /// no more corners came out feasible and the cars left the road more often, so it stays.
    /// </summary>
    private const float HoldAngle = 0.52f;

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

    /// <summary>Another car on the road, as this driver sees it.</summary>
    public readonly record struct Other(Vector3 Position, float Speed, bool Wreck);

    public readonly RaceRoute Route;
    public readonly FootPlayer Player;
    public readonly CarSpec Spec;
    public Car? Car => Player.Vehicle as Car;
    public State D = new() { Bend = 1f, Cap = MaxSpeed };
    public float[] Profile;

    /// <summary>Anyone who wants the driver's commentary (plans, drifts); null for silence.</summary>
    public System.Action<string>? Log;

    public int Drifts, Plans, Feasible;
    public float BestDrift;
    private float _planTimer, _cooldown, _gripUntil;

    public float Arc => Route.Line.Arc[D.Near];

    public AutoPilot(RaceRoute route, FootPlayer player, CarSpec spec)
    {
        Route = route;
        Player = player;
        Spec = spec;
        // a share of the tyre limit: a narrow road with camber and bumps is not a flat skidpad
        Profile = route.Line.SpeedProfile(spec, Rideable.Arcade, spec.Style == DriveStyle.Grip ? 0.64f : 0.6f, MaxSpeed);
        D.Near = route.Line.IndexAt(0);
    }

    /// <summary>One step of driving. <paramref name="go"/> false holds the car on the grid.</summary>
    /// <summary>Set once the car has crossed the finish: from then on it only brakes to a stop.</summary>
    public bool Finished;

    public RideInput Drive(float dt, bool go, IEnumerable<Other> others)
    {
        // On the grid: the handbrake, not the brake — at a standstill the brake pedal selects
        // reverse and then drives it, and the whole grid reversed off the line during a countdown.
        if (!go || Car is not { } car) return new RideInput(0f, 0f, 0f, false, Handbrake: true);
        var motion = Player.Motion;
        bool wasDrifting = D.Drifting, wasPlanned = D.Planned;
        Traffic(dt, others);
        // Wedged between trunks for 8 s with backing out not working (an AWD car can dig itself in
        // spinning against a root): put it back on the line, the way a game resets a car to the
        // track. Measured before this: three AWD grip cars out of 26 never reached the bottom.
        if (D.Lost > 8f && !Finished) ResetToLine();   // a car stopped past the line is not lost
        var input = Policy(ref D, Player.GlobalPosition, motion, dt);
        // Past the line the route runs out a few tens of metres later: stay on the line and brake to
        // a stop there, instead of racing into the trees at 150 km/h (every demo ended in a pile-up).
        if (Finished)
            return motion.Speed > 1.5f ? input with { Throttle = 0f, Brake = 1f, Effort = false }
                                       : new RideInput(0f, 0f, 0f, false, Handbrake: true);
        if (wasDrifting && !D.Drifting) EndDrift(wasPlanned);
        Plan(car, Player.GlobalPosition, motion, dt);
        return input;
    }

    /// <summary>
    /// Racecraft: closing on a car ahead, move to the side of the line it is not on where the road
    /// is straight and has room, otherwise lift to its speed; a wreck is gone round whatever it takes.
    /// </summary>
    private void Traffic(float dt, IEnumerable<Other> others)
    {
        float want = 0f;
        D.Cap = MaxSpeed;
        var me = Player.GlobalPosition;
        var fwd = RaceRoute.Flat(new Basis(Vector3.Up, Player.Motion.Yaw + Player.Motion.Slip) * Vector3.Forward);
        var left = new Vector3(fwd.Z, 0, -fwd.X);
        foreach (var q in others)
        {
            var rel = RaceRoute.Flat(q.Position - me);
            float ahead = rel.Dot(fwd);
            if (ahead < 2f || ahead > 22f) continue;
            float lat = rel.Dot(left);
            if (Mathf.Abs(lat) > 2.6f) continue;
            float room = Route.Line.Room[D.Near];
            bool straight = MaxCurvature(Arc, 0f, 60f) < 1f / 300f;
            if (q.Wreck)
            {
                // just clear of the wreck, on the side it leaves most road, a metre onto the verge at most
                float half = Route.HalfWidthAt(me) + 1f;
                float passLeft = Mathf.Min(lat + 2.2f, half), passRight = Mathf.Max(lat - 2.2f, -half);
                want = Mathf.Abs(passLeft - lat) >= Mathf.Abs(passRight - lat) ? passLeft : passRight;
                D.Cap = Mathf.Min(D.Cap, 8f);
            }
            else if (room > 1.4f && straight) want = lat > 0 ? -room : room;
            else D.Cap = Mathf.Min(D.Cap, q.Speed + (ahead - 8f) * 0.5f);
        }
        D.Lateral = Mathf.MoveToward(D.Lateral, want, 1.2f * dt);
    }

    private RideInput Policy(ref State d, Vector3 pos, in RideMotion m, float dt)
    {
        var line = Route.Line;
        float v = m.Speed;
        float slip = Wrap(m.Slip);
        while (d.Near < line.Points.Count - 2
               && RaceRoute.Flat(line.Points[d.Near + 1] - pos).Length() < RaceRoute.Flat(line.Points[d.Near] - pos).Length()) d.Near++;
        float s0 = line.Arc[d.Near];

        // steer the TRAVEL toward a point ahead on the line (plus any overtaking offset)
        float look = Mathf.Clamp(v * 0.7f, 7f, 30f);
        var ahead = line.PointAt(s0 + look);
        if (d.Lateral != 0f)
        {
            var t = RaceRoute.Flat(line.PointAt(s0 + look + 2f) - line.PointAt(s0 + look - 2f)).Normalized();
            ahead += new Vector3(t.Z, 0, -t.X) * d.Lateral;
        }
        var travel = new Basis(Vector3.Up, m.Yaw + m.Slip) * Vector3.Forward;
        float angle = RaceRoute.SignedAngle(RaceRoute.Flat(travel), RaceRoute.Flat(ahead - pos));
        // gentler hands at speed: full lock at 90 km/h to fix a metre of line is what starts a slide
        float steer = Mathf.Clamp(-angle * 2.6f * Mathf.Clamp(15f / Mathf.Max(v, 1f), 0.4f, 1f), -1f, 1f);

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
                steer = Mathf.Clamp(-wheel / Spec.MaxSteer, -1f, 1f);
            }
            else
            {
                // catching a slide nobody planned: soft hands — full opposite lock on top of the Game
                // assist threw the car the other way, a fishtail that ended in the forest
                float wheel = (Rideable.Arcade ? 0.3f : 0.75f) * slip - 0.05f * m.YawRate;
                steer = Mathf.Clamp(-wheel / Spec.MaxSteer, -0.6f, 0.6f);
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
            throttle = Mathf.Clamp((want - v) * 0.35f, 0f, 1f);
            // trail off the brake as the wheel turns in: braking hard in a bend unloads the rear
            brake = Mathf.Clamp((v - want) * 0.3f, 0f, 1f) * (1f - 0.7f * Mathf.Abs(steer));
        }
        return new RideInput(throttle, brake, steer, false, handbrake);
    }

    /// <summary>
    /// Decides whether the corner ahead gets drifted, by trying it: the car's own model on a copy of
    /// its state, driven by the same policy through a handbrake entry and a held drift. Fastest
    /// feasible entry wins; if none stays on the road, the corner is taken on grip.
    /// </summary>
    private void Plan(Car car, Vector3 pos, in RideMotion m, float dt)
    {
        if (Spec.Style == DriveStyle.Grip) return;
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
                Log?.Invoke($"{Spec.Label}: DRIFT at {s0:F0} m, {m.Speed * 3.6f:F0} km/h (sim peak {Mathf.RadToDeg(peak):F0}°)");
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
        Log?.Invoke($"{Spec.Label}: stuck off the road, reset to the line at {s:F0} m");
    }

    private void EndDrift(bool planned)
    {
        float deg = Mathf.RadToDeg(D.PeakSlip);
        if (planned && deg > 20f && D.DriftTime > 0.5f) Drifts++;
        if (planned) Log?.Invoke($"{Spec.Label}: drift done, peak {deg:F0}°, {D.DriftTime:F1} s past 15°");
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
