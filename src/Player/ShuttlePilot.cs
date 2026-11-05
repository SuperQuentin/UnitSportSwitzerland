using System;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// A scripted pilot for the space shuttle (#1497, from #1492's <c>--shuttlecheck</c>): it flies a
/// <see cref="Mission"/> on the shuttle's own controls, as a player would (<see cref="FootPlayer.FlyControls"/>,
/// <see cref="Airliner.Command"/>, the drive through <see cref="FootPlayer.ShuttleDrive"/>): a taxi onto the
/// runway, the take-off, a rocket climb into Space, the drive to orbit height, round the Earth or back over home,
/// the drive off onto a circular orbit, a retro burn, re-entry, back up into Space and the drive home, out of it
/// up the landing runway's line, and the orbiter's straight-in approach to a stop on that runway.
/// Client-side: it steps the body it is given (a server-run bot pilot is #1498, after #1443 G2). The host
/// (the check, the film) reads <see cref="Now"/>, applies <see cref="WantScale"/> and judges the result.
/// </summary>
public sealed class ShuttlePilot
{
    /// <summary>A runway: its threshold (LV95), heading (clockwise from grid north, rad), length, half width and height.</summary>
    public sealed record Runway(double E, double N, float Heading, float Length, float HalfWidth, double Elevation)
    {
        /// <summary>The grid direction along it (east, north).</summary>
        public (double E, double N) Dir => (Math.Sin(Heading), Math.Cos(Heading));

        /// <summary>A place on its line, <paramref name="along"/> metres past the threshold (negative: before it), LV95.</summary>
        public (double E, double N) At(double along, double across = 0)
        {
            var (de, dn) = Dir;
            return (E + de * along + dn * across, N + dn * along - de * across);
        }

        /// <summary>Where a place is: metres along past the threshold, and to the right of the centreline.</summary>
        public (double Along, double Across) Of(GlobalPos p)
        {
            var (de, dn) = Dir;
            var (x, y, _) = GlobalPos.FromLv95(E, N, 0).OffsetTo(p);
            return (x * de + y * dn, x * dn - y * de);
        }

        /// <summary>Where an LV95 place is: metres along past the threshold, and to the right of the centreline.</summary>
        public (double Along, double Across) Of(double e, double n)
        {
            var (de, dn) = Dir;
            double x = e - E, y = n - N;
            return (x * de + y * dn, x * dn - y * de);
        }
    }

    /// <summary>
    /// What to fly. <paramref name="Start"/>: where it stands to begin (LV95 and a heading), a taxi from there onto
    /// <paramref name="From"/> first; null: it stands on <paramref name="From"/>'s threshold, lined up.
    /// <paramref name="RoundTheEarth"/>: the drive round the Earth at <paramref name="OrbitKm"/>; else back toward home
    /// (the reach of #1497's film, over Europe). <paramref name="PerigeeKm"/>: the retro burn's target (above the
    /// equator's radius; deep below the surface, a steep re-entry soon after). <paramref name="TurnOutKm"/>, back
    /// toward home: the drive first goes that far out along <paramref name="From"/>'s heading, so the orbit, the
    /// burn and re-entry pass over home and stay within about that reach of it.
    /// </summary>
    public sealed record Mission(Runway From, Runway To, (double E, double N, float Heading)? Start = null,
        double OrbitKm = 400, bool RoundTheEarth = true, double CoastSeconds = 120, double PerigeeKm = 45,
        double AirSeconds = 60, double EntryOut = 80_000, double TurnOutKm = 0);

    public enum Stage { Taxi, LineUp, Roll, Climb, DriveUp, Round, TurnHome, Orbit, Retro, Entry, Air, BackUp, DriveHome, Fall, Glide, Rollout, Stopped, Failed }

    public readonly Mission Plan;
    public Stage Now { get; private set; }
    public double StageTime { get; private set; }

    /// <summary>Why it failed (<see cref="Stage.Failed"/>).</summary>
    public string? Failure { get; private set; }

    /// <summary>The time scale the stage wants: real time on the ground, a quarter of SimClock's fastest near it, its fastest away.</summary>
    public double WantScale { get; private set; } = 1.0;

    /// <summary>Called on each stage change (from, to), and with a line worth logging.</summary>
    public Action<Stage, Stage>? Changed;
    public Action<string>? Log;

    /// <summary>The touchdown's sink, m/s, and where it stopped on the landing runway (along, across).</summary>
    public float TouchSink { get; private set; } = float.NaN;
    public (double Along, double Across) StoppedAt { get; private set; }

    /// <summary>The drive went round the Earth (or back home), a circular orbit was flown, and its apsides then (from the centre), m.</summary>
    public bool DroveRound { get; private set; }
    public (double Peri, double Apo)? OrbitApsides { get; private set; }

    /// <summary>SimClock's fastest, for the long stages; take-off and landing at half of it.</summary>
    public const double Fast = SimClock.MaxScale, Near = 4.0;

    private readonly EcefPos _home;
    private double _travel;
    private EcefPos _last;
    private bool _passedFar, _circling, _flare, _turnedOut;
    private float _flareAt;

    public ShuttlePilot(Mission plan)
    {
        Plan = plan;
        _home = SwissGeodesy.ToEcef(plan.To.E, plan.To.N, plan.To.Elevation);
        Now = plan.Start == null ? Stage.LineUp : Stage.Taxi;
        WantScale = 1.0;
    }

    /// <summary>Starts at the Fall stage (the landing alone), from where the drive leaves it.</summary>
    public void SkipToLanding() => Next(Stage.Fall, Fast);

    private void Next(Stage stage, double scale)
    {
        var was = Now;
        Now = stage;
        StageTime = 0;
        WantScale = scale;
        Changed?.Invoke(was, stage);
    }

    private void Fail(string why)
    {
        Failure = why;
        Next(Stage.Failed, 1.0);
    }

    private void Say(string line) => Log?.Invoke(line);

    // ---- the geometry, in doubles ------------------------------------------------------------

    private OriginFrame _frame = null!;
    private FootPlayer _p = null!;
    private Airliner _jet = null!;

    private EcefPos Here => _frame.ToEcef(_p.GlobalPosition);

    /// <summary>The great-circle angle between two places, rad.</summary>
    public static double Angle(EcefPos a, EcefPos b)
    {
        double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        double cx = a.Y * b.Z - a.Z * b.Y, cy = a.Z * b.X - a.X * b.Z, cz = a.X * b.Y - a.Y * b.X;
        return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), dot);
    }

    /// <summary>Over the ground to the landing runway's threshold, m.</summary>
    public double DistanceHome(OriginFrame frame, FootPlayer p) => Angle(frame.ToEcef(p.GlobalPosition), _home) * 6_371_000;

    private EcefPos OnLine(double along)
    {
        var (e, n) = Plan.To.At(along);
        return SwissGeodesy.ToEcef(e, n, 0);
    }

    /// <summary>The nose's elevation over the local horizon, rad.</summary>
    private float Elevation()
    {
        var up = _frame.UpAt(_p.GlobalPosition);
        return Mathf.Asin(Mathf.Clamp((-_jet.State.Attitude.Z).Dot(up), -1f, 1f));
    }

    /// <summary>The heading error to a place, rad: + it is to the right of the nose.</summary>
    private float Bearing(EcefPos to)
    {
        var at = _p.GlobalPosition;
        var here = Here;
        var d = _frame.WorldDirection(at, new EcefPos(to.X - here.X, to.Y - here.Y, to.Z - here.Z));
        var up = _frame.UpAt(at);
        var flat = (d - up * d.Dot(up)).Normalized();
        var nose = -_jet.State.Attitude.Z;
        nose = (nose - up * nose.Dot(up)).Normalized();
        return Mathf.Atan2(nose.Cross(flat).Dot(-up), nose.Dot(flat));
    }

    /// <summary>
    /// The most the drive banks to turn, rad: 20°. At 57° the turns out, back and home rolled the craft over on film
    /// for no reason (the user, #1528): a wider turn costs only drive time.
    /// </summary>
    public const float DriveBank = 0.35f;

    /// <summary>The stick for a nose elevation and a bank (the reaction jets', or the law's in the air).</summary>
    private static Vector2 Attitude(float elevation, float wantElevation, float bank, float wantBank) =>
        new(Mathf.Clamp((wantBank - bank) * 2.5f, -1f, 1f), Mathf.Clamp((wantElevation - elevation) * 3f, -1f, 1f));

    /// <summary>The intent for this step: the stick, the levers servoed to a setting, the wheel brakes.</summary>
    private void Fly(Vector2 stick, float lever, float brake = 0f)
    {
        float now = _jet.State.Lever;
        float up = Mathf.Clamp((lever - now) * 6f, 0f, 1f), down = Mathf.Clamp((now - lever) * 6f, 0f, 1f);
        _p.FlyControls = () => new FlightInput(stick, 0f, 0f, up, down, false, false, 0f, Brake: brake);
    }

    /// <summary>A heading clockwise from grid north (minus the yaw in a ground frame) against a wanted one, rad.</summary>
    private float HeadingError(float want) => Mathf.Wrap(want + _jet.State.Yaw, -Mathf.Pi, Mathf.Pi);

    // ---- the stages ---------------------------------------------------------------------------

    /// <summary>One physics step: the body's place and the shuttle it flies; <paramref name="agl"/> its height over the ground there.</summary>
    public void Step(FootPlayer p, Airliner jet, OriginFrame frame, float agl, float dt)
    {
        if (Now is Stage.Stopped or Stage.Failed) return;
        _p = p;
        _jet = jet;
        _frame = frame;
        StageTime += dt;
        var s = jet.State;
        double alt = frame.AltitudeOf(p.GlobalPosition);
        float elev = Elevation(), bank = AirlinerFlight.BankOf(s.Attitude);
        switch (Now)
        {
            case Stage.Taxi: Taxi(); return;
            case Stage.LineUp:
                // lined up: brakes on, a breath, then the roll
                Fly(Vector2.Zero, 0f, brake: 1f);
                if (StageTime > 3 && s.Velocity.Length() < 0.5f) Next(Stage.Roll, 1.0);
                return;
            case Stage.Roll:
                // full thrust, along the runway, rotate at 1.15 Vs, to a 50° climb
                Fly(new Vector2(Mathf.Clamp(HeadingError(Plan.From.Heading) * 2f, -1f, 1f) - bank, s.Ias > jet.Spec.StallSpeed(s.Mass, 0) * 1.15f ? 1f : 0f), 1f);
                if (!s.OnGround && agl > 30) { jet.Command(AirlinerCommand.Gear); Next(Stage.Climb, 1.0); }
                if (StageTime > 120) Fail("no lift-off");
                return;
            case Stage.Climb:
                Fly(Attitude(elev, Mathf.DegToRad(50f), bank, 0f), 1f);
                WantScale = alt > 2000 ? Fast : 1.0;
                if (jet.InSpace)
                {
                    Say(FormattableString.Invariant($"in Space at {alt / 1000:F1} km, {s.Tas:F0} m/s after {StageTime:F0} s of climb"));
                    if (!p.ShuttleDrive(jet)) { Fail("the drive would not switch on"); return; }
                    Next(Stage.DriveUp, Fast);
                }
                if (StageTime > 1200) Fail("not in Space after 20 min of climb");
                return;
            case Stage.DriveUp:
            {
                // the drive up to orbit height, then level
                double top = Plan.OrbitKm * 1000;
                float want = Mathf.Clamp((float)((top - alt) / 60_000), -1f, 1f) * Mathf.DegToRad(35f);
                // over home's reach (#1497), the climb also turns back toward home
                float wantBank = Plan.RoundTheEarth || alt < 150_000 ? 0f : Mathf.Clamp(Bearing(_home) * 2f, -DriveBank, DriveBank);
                Fly(Attitude(elev, want, bank, wantBank), 1f);
                if (!jet.DriveOn && StageTime > 1) { Fail("the drive dropped out on the way up"); return; }
                if (Math.Abs(alt - top) < 3000 && Mathf.Abs(elev) < 0.02f)
                {
                    Say(FormattableString.Invariant($"{Plan.OrbitKm:F0} km reached in the drive, {s.Tas / 1000:F1} km/s, after {StageTime:F0} s"));
                    _last = Here;
                    Next(Plan.RoundTheEarth ? Stage.Round : Stage.TurnHome, Fast);
                }
                if (StageTime > 600) Fail(FormattableString.Invariant($"not at {Plan.OrbitKm:F0} km after 10 min of drive: {alt / 1000:F0} km"));
                return;
            }
            case Stage.Round:
            {
                double top = Plan.OrbitKm * 1000;
                float want = Mathf.Clamp((float)((top - alt) / 20_000), -1f, 1f) * Mathf.DegToRad(10f);
                Fly(Attitude(elev, want, bank, 0f), 1f);
                var here = Here;
                _travel += Angle(_last, here);
                _last = here;
                if (Math.Abs(alt - top) > 20_000) { Fail(FormattableString.Invariant($"the drive strayed to {alt / 1000:F0} km")); return; }
                if (_travel >= 2 * Math.PI)
                {
                    Say(FormattableString.Invariant($"round the Earth in the drive in {StageTime:F0} s at {s.Tas / 1000:F1} km/s; now at {alt / 1000:F1} km"));
                    DriveOff();
                }
                if (StageTime > 3600) Fail("not round the Earth after an hour");
                return;
            }
            case Stage.TurnHome:
            {
                // out first (TurnOutKm along the take-off's heading), then back toward home, slowing to the orbit's
                // speed: off once home is ahead (and close, without a way out)
                double top = Plan.OrbitKm * 1000;
                float want = Mathf.Clamp((float)((top - alt) / 20_000), -1f, 1f) * Mathf.DegToRad(10f);
                var (oe, on) = Plan.From.At(Plan.TurnOutKm * 1000);
                var outPoint = SwissGeodesy.ToEcef(oe, on, 0);
                _turnedOut |= Plan.TurnOutKm <= 0 || Angle(Here, outPoint) * 6_371_000 < 100_000;
                var target = _turnedOut ? _home : outPoint;
                float err = Bearing(target);
                double dist = Angle(Here, target) * 6_371_000;
                float lever = ShuttleFlight.DriveLever((float)Math.Clamp(dist / 30, 7_700, 1e6), alt);
                Fly(Attitude(elev, want, bank, Mathf.Clamp(err * 2f, -DriveBank, DriveBank)), lever);
                double home = DistanceHome(frame, p);
                if (_turnedOut && Mathf.Abs(err) < 0.05f && (Plan.TurnOutKm > 0 || home < 200_000))
                {
                    Say(FormattableString.Invariant($"heading home in the drive after {StageTime:F0} s, {home / 1000:F0} km out"));
                    DriveOff();
                }
                if (StageTime > 1800) Fail("not back over home after 30 min of drive");
                return;
            }
            case Stage.Orbit:
            {
                Fly(Vector2.Zero, 0f);
                if (jet.DriveOn || jet.Space!.Orbit is not { } o) { if (StageTime > 2) Fail("no orbit after the drive went off"); return; }
                var (peri, apo, _) = Orbit.Apsides(Planet.Earth, o);
                if (OrbitApsides == null && StageTime >= 0.5)
                {
                    OrbitApsides = (peri, apo);
                    double r0 = Planet.Earth.Shape.A;
                    Say(FormattableString.Invariant($"in orbit: perigee {(peri - r0) / 1000:F1} km, apogee {(apo - r0) / 1000:F1} km, {o.V.Length:F0} m/s"));
                }
                if (StageTime > Plan.CoastSeconds) Next(Stage.Retro, Fast);
                return;
            }
            case Stage.Retro:
            {
                // nose to retrograde, then the rockets till the perigee is down to the plan's. The retrograde direction
                // in the craft's own axes (nose -Z, up +Y, right +X): a roll brings it into the pitch plane, then a
                // pitch turns the nose onto it, both slowing as they close. Pitching alone missed it whenever it lay
                // off that plane and looped the craft over and over on film (the user, #1528)
                var v = s.Velocity.Normalized();
                var nose = -s.Attitude.Z;
                float off = Mathf.Acos(Mathf.Clamp(nose.Dot(-v), -1f, 1f));
                var t = s.Attitude.Inverse() * -v;
                float pitchErr = Mathf.Atan2(t.Y, -t.Z);
                float rollErr = off > 0.2f ? Mathf.Atan2(t.X, Mathf.Abs(t.Y) + 1e-3f) * Mathf.Sign(t.Y == 0 ? 1f : t.Y) : 0f;
                var o = jet.Space!.Orbit;
                double target = Plan.PerigeeKm * 1000;
                double perigee = o is { } orbit ? Orbit.Apsides(Planet.Earth, orbit).Periapsis - Planet.Earth.Shape.A : double.NaN;
                bool burn = off < 0.05f && perigee > target;
                Fly(off > 0.03f ? new Vector2(Mathf.Clamp(rollErr, -0.4f, 0.4f), Mathf.Clamp(pitchErr * 0.8f, -0.4f, 0.4f)) : Vector2.Zero, burn ? 1f : 0f);
                if (o != null && perigee <= target && s.Spool <= jet.Spec.IdleSpool + 0.02f)
                {
                    Say(FormattableString.Invariant($"retro burn done: perigee {perigee / 1000:F1} km after {StageTime:F0} s"));
                    Next(Stage.Entry, Fast);
                }
                if (StageTime > 600) Fail("the retro burn took over 10 min");
                return;
            }
            case Stage.Entry:
            {
                // back to prograde, then belly first: the nose 25° over the horizon, down through the heat
                var v = s.Velocity.Normalized();
                float pitchStick = (-s.Attitude.Z).Dot(v) < 0.5f ? 1f : Mathf.Clamp((Mathf.DegToRad(25f) - elev) * 3f, -1f, 1f);
                Fly(Attitude(elev, Mathf.DegToRad(25f), bank, 0f) with { Y = pitchStick }, 0f);
                if (!jet.InSpace)
                {
                    Say(FormattableString.Invariant($"re-entered: out of Space at {alt / 1000:F1} km, {s.Tas:F0} m/s, heat peak {jet.HeatPeak:F2}, {DistanceHome(frame, p) / 1000:F0} km from home"));
                    Next(Stage.Air, Fast);
                }
                if (StageTime > 4000) Fail("not down out of Space within 4000 s of the burn");
                return;
            }
            case Stage.Air:
                // a while in the air at hypersonic speed, the law flying level-ish
                Fly(Attitude(elev, Mathf.DegToRad(5f), bank, 0f), 0f);
                if (alt < 15_000) { Fail(FormattableString.Invariant($"fell to {alt / 1000:F1} km in the air")); return; }
                if (StageTime > Plan.AirSeconds) Next(Stage.BackUp, Fast);
                return;
            case Stage.BackUp:
                Fly(Attitude(elev, Mathf.DegToRad(45f), bank, 0f), 1f);
                if (jet.InSpace && alt > ShuttleFlight.DriveFloorM + 2000)
                {
                    if (!p.ShuttleDrive(jet)) { Fail("the drive would not switch on again"); return; }
                    Next(Stage.DriveHome, Fast);
                }
                if (StageTime > 900) Fail("not back in Space");
                return;
            case Stage.DriveHome: DriveHome(alt, elev, bank); return;
            case Stage.Fall: Fall(alt, elev, bank); return;
            case Stage.Glide: Glide(alt, agl, bank, dt); return;
            case Stage.Rollout: Rollout(); return;
        }
    }

    private void DriveOff()
    {
        DroveRound = true;
        if (!_p.ShuttleDrive(_jet)) { Fail("the drive would not switch off"); return; }
        Next(Stage.Orbit, Fast);
    }

    /// <summary>Taxiing from the start onto the runway: at walking pace to the centreline, then round onto its heading.</summary>
    private void Taxi()
    {
        var s = _jet.State;
        var g = _frame.ToGlobal(_p.GlobalPosition);
        var (along, across) = Plan.From.Of(g);
        // square to the centreline, then round onto it from 40 m out (across + is right of it)
        float want = Math.Abs(across) >= 40
            ? Plan.From.Heading + Mathf.Sign((float)-across) * Mathf.Pi / 2
            : Plan.From.Heading + (float)Math.Atan2(-across, 15);
        float err = HeadingError(want);
        float speed = s.Velocity.Length();
        float wantSpeed = Mathf.Abs(err) > 0.3f ? 5f : 9f;
        float lever = speed < wantSpeed ? 0.06f : 0f;
        float brake = speed > wantSpeed + 2f ? 0.5f : 0f;
        Fly(new Vector2(Mathf.Clamp(err * 2.5f, -1f, 1f), 0f), lever, brake);
        if (Math.Abs(across) < 3 && Mathf.Abs(HeadingError(Plan.From.Heading)) < 0.03f)
        {
            Say(FormattableString.Invariant($"taxied onto the runway {along:F0} m past its threshold after {StageTime:F0} s"));
            Next(Stage.LineUp, 1.0);
        }
        if (StageTime > 300) Fail("no line-up after 5 min of taxi");
    }

    private void DriveHome(double alt, float elev, float bank)
    {
        // first a point 400 km out on the line, then the entry point: so it comes in along the runway
        var far = OnLine(-400_000);
        var entry = OnLine(-Plan.EntryOut);
        var here = Here;
        double toFar = Angle(here, far) * 6_371_000, toEntry = Angle(here, entry) * 6_371_000;
        _passedFar |= toFar < 30_000;
        var target = _passedFar ? entry : far;
        double dist = _passedFar ? toEntry : toFar + 250_000;
        float err = Bearing(target);
        float wantBank = Mathf.Clamp(err * 2f, -DriveBank, DriveBank);
        // the altitude falls with the distance, the speed with it (the lever sets it: v = lever · d / τ)
        double wantAlt = Math.Clamp(EntryAlt + Math.Max(dist - 100_000, 0) * 0.2, EntryAlt, 300_000);
        float wantElev = Mathf.Clamp((float)((wantAlt - alt) / 40_000), -1f, 1f) * Mathf.DegToRad(25f);
        double speed = Math.Clamp(dist / 40, 300, 1e6);
        Fly(Attitude(elev, wantElev, bank, wantBank), ShuttleFlight.DriveLever((float)speed, alt));
        if (_passedFar && toEntry < 8_000)
        {
            Say(FormattableString.Invariant($"over the entry point {DistanceHome(_frame, _p) / 1000:F0} km out at {alt / 1000:F1} km, {_jet.State.Tas:F0} m/s, after {StageTime:F0} s of drive"));
            Next(Stage.Fall, Fast);
        }
        if (StageTime > 3600) Fail("not home after an hour of drive");
    }

    /// <summary>The height the drive brings it down to at the entry point, m.</summary>
    private const double EntryAlt = 56_000;

    private void Fall(double alt, float elev, float bank)
    {
        float err = Bearing(_home);
        // out of the drive, 25° down at 300 m/s: it drops out below 50 km some 20 km on (45° looked like a plunge)
        if (_jet.DriveOn) { Fly(Attitude(elev, Mathf.DegToRad(-25f), bank, Mathf.Clamp(err * 2f, -DriveBank, DriveBank)), ShuttleFlight.DriveLever(300f, alt)); return; }
        // then the air takes it: pull out to the glide
        float q = 0.5f * AirlinerFlight.Density((float)alt) * _jet.State.Tas * _jet.State.Tas;
        Fly(Attitude(elev, q < 3000 ? Mathf.DegToRad(-15f) : Mathf.DegToRad(-8f), bank, Mathf.Clamp(err * 2f, -0.5f, 0.5f)), 0f);
        if (q > 4000 && alt < 30_000)
        {
            Say(FormattableString.Invariant($"flying again at {alt / 1000:F1} km, {_jet.State.Ias:F0} m/s IAS, {DistanceHome(_frame, _p) / 1000:F0} km out"));
            Next(Stage.Glide, Fast);
        }
        if (alt < 8_000 + Plan.To.Elevation) Fail(FormattableString.Invariant($"no pull-out: {alt / 1000:F1} km"));
    }

    /// <summary>
    /// The orbiter's way, shortened for a scripted pilot: a steep outer glideslope aimed 1 km short of the
    /// threshold, the speed held by the speedbrake (a heading-alignment circle when too high); a pre-flare from
    /// 400 m round to a 2.5° path by 80 m, the speed held by the rockets (the real one has none on approach; a
    /// player has them, unlimited: a 4:1 brick bleeds its speed fast); the flare flown on the attitude.
    /// </summary>
    private void Glide(double alt, float agl, float bank, float dt)
    {
        var s = _jet.State;
        var g = _frame.ToGlobal(_p.GlobalPosition);
        var (along, across) = Plan.To.Of(g);
        double height = alt - Plan.To.Elevation;
        if (agl < 3_000) WantScale = Near;
        var v = s.Velocity;
        float speed = Mathf.Max(v.Length(), 50f);
        float gamma = Mathf.Asin(Mathf.Clamp(v.Y / speed, -1f, 1f));
        float outer = Mathf.Clamp(-(float)Math.Atan2(height, Math.Max(-1_000 - along, 300)), -0.36f, -0.05f);
        float k = Mathf.SmoothStep(0f, 1f, (400f - agl) / 320f);
        float gammaWant = Mathf.Lerp(outer, Mathf.Max(outer, -0.044f), k);
        float stickY = Mathf.Clamp((gammaWant - gamma) * 3f, -1f, 1f);
        // the flare as a pilot flies it: the nose to an attitude and held there (a loop on the sink itself lags
        // the airframe, balloons and drops): 8° at once, then the attitude wanted creeps toward the sink wanted
        float pitch = AirlinerFlight.PitchOf(s.Attitude);
        if (!_flare && agl < 30f) { _flare = true; _flareAt = Mathf.Max(pitch, 0.14f); }
        if (_flare)
        {
            _flareAt = Mathf.Clamp(_flareAt + (-Mathf.Max(1.5f, agl * 0.25f) - v.Y) * 0.008f * dt, -0.05f, 0.22f);
            stickY = Mathf.Clamp((_flareAt - pitch) * 6f, -0.8f, 0.8f);
        }
        // lateral: onto the line, then along it
        float track = Plan.To.Heading + (float)Math.Atan2(-across, Math.Max(Math.Abs(along) * 0.25, 1_500));
        float wantBank = Mathf.Clamp(HeadingError(track) * 2.5f, agl < 200 ? -0.1f : -0.6f, agl < 200 ? 0.1f : 0.6f);
        // too high for a 15° glide to the aim point: a turn burns the height off without closing in
        double high = height - Math.Max(-1_000 - along, 0) * Math.Tan(0.26);
        _circling = along < -8_000 && (high > (_circling ? 0 : 800));
        if (_circling) wantBank = 0.7f;
        float vLow = agl > 400 ? 130f : 125f;
        if (_flare) vLow = 115f;
        float lever = agl > 2f && s.Ias < vLow ? Mathf.Clamp((vLow + 5f - s.Ias) * 0.08f, 0f, 0.8f) : 0f;
        // the speedbrake holds the speed on the outer slope, and closes for the flare
        bool brakes = agl > 400 && s.Ias > 140f;
        if (brakes != (s.SpeedBrake == 2)) _jet.Command(AirlinerCommand.Speedbrake);
        if (agl < 500 && !s.GearDown) _jet.Command(AirlinerCommand.Gear);
        Fly(new Vector2(Mathf.Clamp((wantBank - bank) * 2.5f, -1f, 1f), stickY), lever);
        if (s.OnGround)
        {
            TouchSink = s.LastSink;
            Say(FormattableString.Invariant($"touchdown {along:F0} m past the threshold, {across:F1} m off the centreline, sink {s.LastSink:F2} m/s, {s.Ias / 0.514444f:F0} kt"));
            Next(Stage.Rollout, 1.0);
        }
        if (StageTime > 2400) Fail("no touchdown within 40 min of the glide");
    }

    private void Rollout()
    {
        var s = _jet.State;
        if (s.SpeedBrake != 2) _jet.Command(AirlinerCommand.Speedbrake);
        var g = _frame.ToGlobal(_p.GlobalPosition);
        Fly(new Vector2(Mathf.Clamp(HeadingError(Plan.To.Heading) * 2f, -1f, 1f), 0f), 0f, brake: 1f);
        if (s.Velocity.Length() > 0.5f) { if (StageTime > 300) Fail("not stopped"); return; }
        StoppedAt = Plan.To.Of(g);
        Say(FormattableString.Invariant($"stopped {StoppedAt.Along:F0} m past the threshold, {StoppedAt.Across:F1} m off the centreline"));
        Next(Stage.Stopped, 1.0);
    }
}
