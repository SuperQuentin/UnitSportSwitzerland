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
/// With <see cref="Mission.Moon"/> (#1498) the orbit is followed by the trip to the Moon (the target hold and the cruise
/// drive, as a player flies it), a belly-jet landing on a colony's pad, a while there, the climb back into Space and the
/// trip home to an Earth orbit, then the drive home and the landing as before.
/// It steps the body it is given wherever that body is simulated: a check's or the film's (it sets their
/// <see cref="FootPlayer.FlyControls"/>), or a bot's, offline or on a server (<c>Bots/ShuttleBrain</c>, #1498: the bot's
/// controls read <see cref="Input"/>, <see cref="OwnsControls"/> false). The host reads <see cref="Now"/>, may apply
/// <see cref="WantScale"/> (a check, the film; never a bot) and judges the result.
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
            // the runway is on the LV95 grid, so is its heading: measure on the grid, not in true east and north
            var l = p.Lv95;
            return Of(l.E, l.N);
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
    /// <param name="Turns">Round the Earth this many times in the drive (#1498).</param>
    /// <param name="Moon">After the orbit, to the Moon and down on a pad, then home (#1498).</param>
    /// <param name="Pad">The Moon pad to land on (<see cref="Terrain.Bodies.MoonPads"/>'s id); null: the nearest free one.</param>
    /// <param name="MoonStay">Seconds on the Moon before lifting off.</param>
    /// <param name="LandHome">Back from the Moon, the drive home and the landing; false: it stops in the Earth orbit.</param>
    public sealed record Mission(Runway From, Runway To, (double E, double N, float Heading)? Start = null,
        double OrbitKm = 400, bool RoundTheEarth = true, double CoastSeconds = 120, double PerigeeKm = 45,
        double AirSeconds = 60, double EntryOut = 80_000, double TurnOutKm = 0, int Turns = 1, bool Moon = false,
        string? Pad = null, double MoonStay = 30, bool LandHome = true);

    public enum Stage
    {
        Taxi, LineUp, Roll, Climb, DriveUp, Round, TurnHome, Orbit, Retro, Entry, Air, BackUp, DriveHome, Fall, Glide, Rollout,
        // between bodies and on the Moon (#1498)
        Clear, AimFor, Cruise, LunarOrbit, Approach, Jets, OnMoon, LiftOff, MoonUp, HomeOrbit,
        Stopped, Failed,
    }

    /// <summary>The controls this step, for a host that reads them itself (a bot's <c>FlyControls</c>, #1498).</summary>
    public FlightInput Input { get; private set; }

    /// <summary>The pilot sets the body's <see cref="FootPlayer.FlyControls"/> itself (the checks, the film); false for a bot, which reads <see cref="Input"/>.</summary>
    public bool OwnsControls { get; init; } = true;

    /// <summary>The Moon pad it lands on (#1498), once chosen, and how far from its centre it came down, m.</summary>
    public Terrain.Bodies.MoonPad? LandedPad { get; private set; }
    public double PadMiss { get; private set; } = double.NaN;

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

    /// <summary>Starts in the orbit it was put in (<c>/orbit</c>, a bot's <c>orbit</c> mission, #1498): the coast, then on.</summary>
    public void StartInOrbit() => Next(Stage.Orbit, Fast);

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
        var input = Input = new FlightInput(stick, 0f, 0f, up, down, false, false, 0f, Brake: brake);
        if (OwnsControls) _p.FlyControls = () => input;
    }

    /// <summary>
    /// A heading clockwise from grid north against a wanted one, rad: the jet's world yaw read from grid north at its
    /// place. In the flat ground frame that is the yaw itself; the earth world's turning frame (#1630) has carried its
    /// axes round the Earth with the drive, so there world −Z is north nowhere in particular.
    /// </summary>
    private float HeadingError(float want) =>
        Mathf.Wrap(want + (_frame.IsAnchor ? TrueNorth.WorldYawToGrid(_frame, _p.GlobalPosition, _jet.State.Yaw) : _jet.State.Yaw), -Mathf.Pi, Mathf.Pi);

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
                    // to the Moon (#1498): off into the orbit first, the trip from there
                    if (Plan.Moon) DriveOff();
                    else Next(Plan.RoundTheEarth ? Stage.Round : Stage.TurnHome, Fast);
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
                if (_travel >= 2 * Math.PI * Math.Max(1, Plan.Turns))
                {
                    Say(FormattableString.Invariant($"round the Earth {Math.Max(1, Plan.Turns)} time(s) in the drive in {StageTime:F0} s at {s.Tas / 1000:F1} km/s; now at {alt / 1000:F1} km"));
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
                var (peri, apo, _) = Orbit.Apsides(CelestialBody.Earth, o);
                if (OrbitApsides == null && StageTime >= 0.5)
                {
                    OrbitApsides = (peri, apo);
                    double r0 = CelestialBody.Earth.Shape.A;
                    Say(FormattableString.Invariant($"in orbit: perigee {(peri - r0) / 1000:F1} km, apogee {(apo - r0) / 1000:F1} km, {o.V.Length:F0} m/s"));
                }
                if (StageTime > Plan.CoastSeconds)
                {
                    // to the Moon first (#1498), once
                    if (Plan.Moon && !_moonDone) Travel(BodyId.Moon, Stage.LunarOrbit);
                    else Next(Stage.Retro, Fast);
                }
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
                double perigee = o is { } orbit ? Orbit.Apsides(CelestialBody.Earth, orbit).Periapsis - CelestialBody.Earth.Shape.A : double.NaN;
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
            case Stage.Clear: Clear(); return;
            case Stage.AimFor: AimFor(); return;
            case Stage.Cruise: Cruise(); return;
            case Stage.LunarOrbit: LunarOrbit(); return;
            case Stage.Approach: Approach(alt); return;
            case Stage.Jets: Jets(); return;
            case Stage.OnMoon:
                Fly(Vector2.Zero, 0f, brake: 1f);
                if (StageTime > Plan.MoonStay) { Say("lifting off the Moon"); Next(Stage.LiftOff, 1.0); }
                return;
            case Stage.LiftOff: LiftOff(); return;
            case Stage.MoonUp: MoonUp(alt); return;
            case Stage.HomeOrbit: HomeOrbit(alt); return;
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

    // ---- between bodies and on the Moon (#1498) ---------------------------------------------------

    private BodyId _target;
    private Stage _arrive;
    private double _closest = double.MaxValue;
    private bool _moonDone, _upDone;

    /// <summary>How high over its pad the descent in the drive aims (under the Moon's 8 km Space exit, so it drops out on the way), m.</summary>
    public const float ApproachHeightM = 5_000f;

    /// <summary>The descent turns from level to straight at its aim point once that lies this far under the horizon, rad (35°).</summary>
    private const float DiveAngle = 0.61f;

    /// <summary>The trip to <paramref name="target"/>'s arrival orbit, as a player flies it (the target hold, the drive), then <paramref name="arrive"/>.</summary>
    private void Travel(BodyId target, Stage arrive)
    {
        _target = target;
        _arrive = arrive;
        _closest = double.MaxValue;
        Say($"off to {CelestialBody.Of(target).Name}");
        Next(Stage.Clear, Fast);
    }

    /// <summary>
    /// The direction to a body's centre (world) and the distance to its surface, m, from the craft, in the frame of the body
    /// the world is on; null without one (the flat LV95 frame has no other bodies).
    /// </summary>
    private (Vector3 Direction, double Distance)? Toward(BodyId target)
    {
        if (_frame.Axes is not { } axes) return null;
        var centre = Frames.Convert(target, DVec3.Zero, axes.Body, SkyClock.Now);
        var c = _p.GlobalPosition;
        var e = axes.ToEcef(c.X, c.Y, c.Z);
        var to = centre - new DVec3(e.X, e.Y, e.Z);
        double length = to.Length;
        var dir = length > 0 ? to / length : new DVec3(0, 0, -1);
        var world = new Vector3((float)FrameAxes.Dot(dir.AsEcef, axes.X), (float)FrameAxes.Dot(dir.AsEcef, axes.Y), (float)FrameAxes.Dot(dir.AsEcef, axes.Z));
        return (world.Normalized(), Math.Max(0, length - CelestialBody.Of(target).Shape.A));
    }

    /// <summary>What the target hold aims at: the tangent of the target's arrival orbit (<see cref="ShuttleFlight.TangentAim"/>), or its centre.</summary>
    private Vector3? TangentFor(BodyId target)
    {
        if (Toward(target) is not { } toward) return null;
        var body = CelestialBody.Of(target);
        return ShuttleFlight.TangentAim(toward.Direction, toward.Distance + body.Shape.A, body.Shape.A + ShuttleFlight.ArrivalM(body), _jet.Nose)
            ?? toward.Direction;
    }

    /// <summary>
    /// The line along <paramref name="aim"/> from the craft passes within <paramref name="margin"/> (200 km) of the surface of
    /// the body the world is on. The hold gives up a turn only past a smaller margin (50 km): at the limit, the same one
    /// both ways flipped the two stages every frame.
    /// </summary>
    private bool Blocked(Vector3 aim, double margin = 200_000)
    {
        if (_frame.Axes is not { } axes) return false;
        var p = _p.GlobalPosition;
        var e = axes.ToEcef(p.X, p.Y, p.Z);
        var at = new DVec3(e.X, e.Y, e.Z);
        var dir = new DVec3(axes.X.X * aim.X + axes.Y.X * aim.Y + axes.Z.X * aim.Z,
            axes.X.Y * aim.X + axes.Y.Y * aim.Y + axes.Z.Y * aim.Z,
            axes.X.Z * aim.X + axes.Y.Z * aim.Y + axes.Z.Z * aim.Z).Normalized();
        double t = -(at.X * dir.X + at.Y * dir.Y + at.Z * dir.Z);
        if (t <= 0) return false;
        return (at + dir * t).Length < CelestialBody.Of(axes.Body).Shape.A + margin;
    }

    /// <summary>
    /// The way to the target clear of the body it is round: on to the hold. Else straight up in the drive until it is (a
    /// player coasts round until the target rises; the drive up is quicker, and a bot keeps real time).
    /// </summary>
    private void Clear()
    {
        if (TangentFor(_target) is not { } aim) { Fail("no frame between bodies here (an earth world is needed)"); return; }
        var up = _frame.UpAt(_p.GlobalPosition);
        if (!Blocked(aim))
        {
            // the drive off out here: a circular orbit to turn in
            if (_jet.DriveOn && !_p.ShuttleDrive(_jet)) { Fail("the drive would not switch off"); return; }
            _jet.Aim = null;
            Say(FormattableString.Invariant($"{CelestialBody.Of(_target).Name} clear of {CelestialBody.Of(_frame.Axes!.Body).Name} at {_frame.AltitudeOf(_p.GlobalPosition) / 1000:F0} km"));
            Next(Stage.AimFor, Fast);
            return;
        }
        _jet.Aim = up;
        if (!_jet.DriveOn)
        {
            Fly(Vector2.Zero, 0f);
            if (_jet.Nose.AngleTo(up) < 0.035f && !_p.ShuttleDrive(_jet)) Fail("the drive would not switch on to climb clear");
        }
        else Fly(Vector2.Zero, 1f);
        if (StageTime > 900) Fail("not clear of the body after 15 min");
    }

    /// <summary>The target hold turns the nose onto the arrival orbit's tangent; within 1° the drive goes on.</summary>
    private void AimFor()
    {
        if (TangentFor(_target) is not { } aim) { Fail("lost the frame between bodies"); return; }
        _jet.Aim = aim;
        Fly(Vector2.Zero, 0f);
        if (Blocked(aim, 50_000)) { Next(Stage.Clear, Fast); return; }
        if (_jet.Nose.AngleTo(aim) < Mathf.DegToRad(1f))
        {
            if (!_p.ShuttleDrive(_jet)) { Fail("the drive would not switch on for the trip"); return; }
            Say($"aligned on {CelestialBody.Of(_target).Name}: the drive on, full lever");
            Next(Stage.Cruise, Fast);
        }
        if (StageTime > 180) Fail("could not turn onto the trip in 3 min");
    }

    /// <summary>The drive at full lever along the held tangent; off where it passes the arrival height: an orbit round the target.</summary>
    private void Cruise()
    {
        if (!_jet.DriveOn) { if (StageTime > 0.5) Fail("the drive went off on the trip"); return; }
        _jet.Aim = TangentFor(_target);
        Fly(Vector2.Zero, 1f);
        double d = _jet.DriveDistance, arrival = ShuttleFlight.ArrivalM(CelestialBody.Of(_target));
        bool inSphere = _frame.Axes?.Body == _target;
        if (inSphere) _closest = Math.Min(_closest, d);
        bool there = inSphere && (d <= arrival * 1.02 || d < 2 * arrival && d > _closest + 50);
        if (!there)
        {
            if (StageTime > 900) Fail(FormattableString.Invariant($"still {d / 1000:F0} km out after 15 min in the drive"));
            return;
        }
        _jet.Aim = null;
        if (!_p.ShuttleDrive(_jet)) { Fail("the drive would not switch off at the arrival"); return; }
        Say(FormattableString.Invariant($"at {CelestialBody.Of(_target).Name}: the drive off {d / 1000:F0} km over it after {StageTime:F0} s"));
        Next(_arrive, Fast);
    }

    /// <summary>The craft's unit normal on the body the world is on (body-fixed).</summary>
    private EcefPos NormalHere()
    {
        var e = _frame.ToEcef(_p.GlobalPosition);
        double l = Math.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z);
        return new EcefPos(e.X / l, e.Y / l, e.Z / l);
    }

    /// <summary>The Moon's heights here (the client's ground: a server has none before C3b), or null.</summary>
    private static Terrain.Bodies.BodyHeights? MoonHeights => World.Bodies.BodyGround.Active is { Body.Id: BodyId.Moon } g ? g.Heights : null;

    /// <summary>The pad's ground point, world (the Moon's heights there when this peer has them, else its sphere).</summary>
    private Vector3 PadWorld()
    {
        var n = LandedPad!.Value.Centre;
        if (MoonHeights is { } h) return _frame.FromEcef(h.Ground(n));
        double a = CelestialBody.Moon.Shape.A;
        return _frame.FromEcef(new EcefPos(n.X * a, n.Y * a, n.Z * a));
    }

    /// <summary>The craft's height over the Moon's ground under it, m (its sphere's when there are no heights here).</summary>
    private double OverGround()
    {
        double ground = MoonHeights?.At(NormalHere()) ?? 0;
        return _frame.AltitudeOf(_p.GlobalPosition) - ground;
    }

    private void LunarOrbit()
    {
        _moonDone = true;
        Fly(Vector2.Zero, 0f);
        if (StageTime < 3) return;
        if (_jet.Space?.Orbit is null || _frame.Axes?.Body != BodyId.Moon) { Fail("no lunar orbit after the trip"); return; }
        var pad = Plan.Pad != null ? Terrain.Bodies.MoonPads.Find(Plan.Pad) : Terrain.Bodies.MoonPads.NearestFree(NormalHere())?.Pad;
        if (pad == null) { Fail($"no pad '{Plan.Pad ?? "free"}' on the Moon"); return; }
        LandedPad = pad;
        double away = Math.Acos(Math.Clamp(Dot(NormalHere(), pad.Value.Centre), -1, 1)) * CelestialBody.Moon.Shape.A;
        Say(FormattableString.Invariant($"in lunar orbit: down to {pad.Value.Id} ({pad.Value.Colony}), {away / 1000:F0} km round the Moon"));
        Next(Stage.Approach, Fast);
    }

    private static double Dot(EcefPos a, EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>
    /// Down toward the pad in the drive: level over the Moon toward it, then, once its aim point (5 km over it) lies 35°
    /// under the horizon, straight at that point, the speed by the distance; the drive drops out at the Space exit (8 km)
    /// on the way, and the belly jets take it.
    /// </summary>
    private void Approach(double alt)
    {
        var pos = _p.GlobalPosition;
        var up = _frame.UpAt(pos);
        var pad = PadWorld();
        var point = pad + _frame.UpAt(pad) * ApproachHeightM;
        var to = point - pos;
        float dist = to.Length();
        var flat = to - up * to.Dot(up);
        float dive = Mathf.Asin(Mathf.Clamp(-to.Dot(up) / Mathf.Max(dist, 1f), -1f, 1f));
        bool diving = dive > DiveAngle || flat.Length() < 1f;
        var aim = diving ? to / Mathf.Max(dist, 1f) : flat.Normalized();
        _jet.Aim = aim;
        if (_jet.Lander)
        {
            _jet.Aim = null;
            Say(FormattableString.Invariant($"on the belly jets at {OverGround() / 1000:F1} km, {_jet.State.Velocity.Length():F0} m/s, {(pad - pos).Length() / 1000:F1} km from the pad"));
            Next(Stage.Jets, Near);
            return;
        }
        if (!_jet.DriveOn)
        {
            Fly(Vector2.Zero, 0f);
            if (_jet.Nose.AngleTo(aim) < 0.03f && !_p.ShuttleDrive(_jet)) Fail("the drive would not switch on for the descent");
            if (StageTime > 120 && !_jet.DriveOn) Fail("could not turn toward the pad in 2 min");
            return;
        }
        // slow enough at the drop-out (~100-150 m/s some kilometres short) for the jets' slide to stop it over the pad
        float speed = diving ? Mathf.Clamp(dist / 40f, 80f, 1_500f) : Mathf.Clamp(flat.Length() / 25f, 300f, 100_000f);
        Fly(Vector2.Zero, ShuttleFlight.DriveLever(speed, alt));
        if (StageTime > 1800) Fail("not down to the pad after 30 min in the drive");
    }

    /// <summary>
    /// The belly jets to the pad: the slide toward it (at most 40 m/s, slowing within 500 m), the height held to 0.4 of the
    /// distance left (and at least 150 m over the ground under it), then straight down at a tenth of the height.
    /// </summary>
    private void Jets()
    {
        if (!_jet.Lander) { if (StageTime > 1) Fail("off the belly jets on the way down"); return; }
        var pos = _p.GlobalPosition;
        var up = _frame.UpAt(pos);
        var pad = PadWorld();
        var r = pad - pos;
        float h = -r.Dot(up);
        var rh = r + up * h;
        float dist = rh.Length();
        var v = _jet.State.Velocity;
        float vs = v.Dot(up);
        var vh = v - up * vs;
        var want = dist > 0.5f ? rh / dist * Mathf.Min(dist / 12f, 40f) : Vector3.Zero;
        var dv = want - vh;
        var nose = _jet.Nose;
        var ahead = (nose - up * nose.Dot(up)).Normalized();
        var right = ahead.Cross(up).Normalized();
        var stick = new Vector2(Mathf.Clamp(dv.Dot(right) / 3f, -1f, 1f), Mathf.Clamp(-dv.Dot(ahead) / 3f, -1f, 1f));
        var moon = CelestialBody.Moon;
        double rr = moon.Shape.A + _frame.AltitudeOf(pos);
        float g = (float)(moon.GM / (rr * rr));
        float vsWant = dist > 40f
            ? Mathf.Clamp((Mathf.Clamp(dist * 0.4f, 40f, 6_000f) - h) / 8f, -40f, 15f)
            : -Mathf.Clamp(h / 10f, 1.2f, 40f);
        if (dist > 40f && OverGround() < 150) vsWant = Mathf.Max(vsWant, 5f);
        float lever = Mathf.Clamp((g + (vsWant - vs) * 1.2f) / ShuttleFlight.BellyAccel, 0f, 1f);
        if (h < 1_500 && !_jet.State.GearDown) _jet.Command(AirlinerCommand.Gear);
        Fly(stick, lever);
        if (_jet.State.OnGround)
        {
            PadMiss = dist;
            Say(FormattableString.Invariant($"down on the Moon at {LandedPad!.Value.Id}: {_jet.TouchSink:F2} m/s, {dist:F1} m from its centre (radius {LandedPad.Value.RadiusM:F0} m) after {StageTime:F0} s on the jets"));
            Next(Stage.OnMoon, 1.0);
        }
        if (StageTime > 900) Fail(FormattableString.Invariant($"not down after 15 min on the jets: {h:F0} m up, {dist:F0} m from the pad"));
    }

    /// <summary>The belly jets up into Space (the Moon's 10 km), the gear up past 200 m.</summary>
    private void LiftOff()
    {
        Fly(Vector2.Zero, 0.8f);
        if (OverGround() > 200 && _jet.State.GearDown) _jet.Command(AirlinerCommand.Gear);
        if (!_jet.Lander && _jet.InSpace)
        {
            _upDone = false;
            Next(Stage.MoonUp, Fast);
        }
        if (StageTime > 300) Fail("not in Space 5 min after lifting off the Moon");
    }

    /// <summary>The nose up, the drive to 100 km, then home.</summary>
    private void MoonUp(double alt)
    {
        var up = _frame.UpAt(_p.GlobalPosition);
        _jet.Aim = up;
        if (!_jet.DriveOn && !_upDone)
        {
            Fly(Vector2.Zero, 0f);
            if (_jet.Nose.AngleTo(up) < 0.035f)
            {
                if (!_p.ShuttleDrive(_jet)) { Fail("the drive would not switch on over the Moon"); return; }
                _upDone = true;
            }
            if (StageTime > 120) Fail("could not turn the nose up over the Moon");
            return;
        }
        Fly(Vector2.Zero, 0.3f);
        if (alt >= 100_000) Travel(BodyId.Earth, Stage.HomeOrbit);
        if (StageTime > 600) Fail("not at 100 km over the Moon after 10 min");
    }

    /// <summary>Home in an Earth orbit: the drive home and the landing, or stopped there.</summary>
    private void HomeOrbit(double alt)
    {
        Fly(Vector2.Zero, 0f);
        if (StageTime < 2) return;
        if (_jet.Space?.Orbit is not { } o || _frame.Axes?.Body != BodyId.Earth) { Fail("no Earth orbit after the trip home"); return; }
        var earth = CelestialBody.Earth;
        var (peri, apo, _) = Orbit.Apsides(earth, o);
        OrbitApsides = (peri, apo);
        Say(FormattableString.Invariant($"home in an Earth orbit: {(peri - earth.Shape.A) / 1000:F1} x {(apo - earth.Shape.A) / 1000:F1} km at {alt / 1000:F0} km"));
        if (!Plan.LandHome) { Next(Stage.Stopped, 1.0); return; }
        if (!_p.ShuttleDrive(_jet)) { Fail("the drive would not switch on to come home"); return; }
        Next(Stage.DriveHome, Fast);
    }
}
