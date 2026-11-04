using System;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --headless --fixed-fps 60 --path . -- --shuttlecheck --world flat</c> (#1492, #1430 C2): the whole
/// mission, flown by a scripted pilot on the shuttle's own controls (<see cref="FootPlayer.FlyControls"/>, the
/// drive through <see cref="FootPlayer.ShuttleDrive"/>, the switch Z / D-pad ↑ / the cockpit's work):
/// <list type="number">
/// <item>take-off from the runway at the start, a rocket climb into Space (60 km);</item>
/// <item>the cruise drive up to 400 km, then round the Earth (a whole turn of the ground track);</item>
/// <item>the drive off: a circular orbit at 400 km, coasted;</item>
/// <item>a retro burn to a 45 km perigee, half an orbit down, re-entry (the glow), handed to the air below 50 km;</item>
/// <item>back up on the rockets, the drive home, out of it 150 km up the runway's line, and a straight-in glide
/// to the runway it left from: down on its wheels, stopped, within the runway.</item>
/// </list>
/// Fails on a crash, a hard landing, a contact in free flight, an orbit that is not circular, no glow, a
/// stage that takes too long, or a landing off the runway.
/// </summary>
public partial class ShuttleCheck : Node
{
    public static bool Requested => CmdArgs.Has("--shuttlecheck");

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private Airliner? _shuttle;
    private Stage _stage;
    private double _t, _stageT, _report;
    private bool _done;

    /// <summary>The runway: its threshold (the start, ECEF, sea level), its heading (the take-off's: north), its length and width.</summary>
    private EcefPos _home;
    private double _homeE, _homeN;
    private const float RunwayHeading = 0f, RunwayLength = 3600f, RunwayHalfWidth = 30f;

    /// <summary>The round the Earth: the ground track's turn so far, rad, and the last place.</summary>
    private double _travel;
    private EcefPos _last;
    private bool _drove400;

    private enum Stage { Spawn, Roll, Climb, DriveUp, Round, Orbit, Retro, Entry, Air, BackUp, DriveHome, Fall, Glide, Rollout }

    /// <summary>SimClock's fastest, for the long stages; take-off and landing at half of it.</summary>
    private const double Fast = SimClock.MaxScale, Near = 4.0;

    public ShuttleCheck(WorldOrigin origin) => _origin = origin;

    public override void _Ready()
    {
        AddChild(new OriginShifter(_origin, () => _player?.GlobalPosition, () => true));
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _t += delta;
        _stageT += delta;
        if (_t > 6 * 3600) { Fail("TIMEOUT"); return; }
        if (_stage == Stage.Spawn) { Spawn(); return; }
        var p = _player!;
        var jet = _shuttle!;
        if (p.Vehicle != jet) { Fail($"out of the shuttle at stage {_stage} (crashed?)"); return; }
        if (p.VehicleHealth < 50f) { Fail($"damaged at stage {_stage}: health {p.VehicleHealth:F0}"); return; }
        if (jet.Space!.Contacts > 0) { Fail($"{jet.Space.Contacts} contacts in free flight"); return; }
        if (_t >= _report)
        {
            _report = _t + (_stage == Stage.Glide && _origin.Frame.AltitudeOf(p.GlobalPosition) < 2_000 ? 5 : 60);
            Report(p, jet);
        }
        Pilot(p, jet, (float)delta);
    }

    private void Next(Stage stage, double scale)
    {
        GD.Print(FormattableString.Invariant($"[shuttlecheck] {_stage} -> {stage} at {_t:F0} s"));
        _stage = stage;
        _stageT = 0;
        Engine.TimeScale = scale;
    }

    private void Report(FootPlayer p, Airliner jet)
    {
        double alt = _origin.Frame.AltitudeOf(p.GlobalPosition);
        // the speed it has (in the flat Space frame far from Switzerland the world is stretched by LV95's scale)
        GD.Print(FormattableString.Invariant(
            $"[shuttlecheck] t {_t:F0} s {_stage}: alt {alt / 1000:F2} km, v {jet.State.Tas:F0} m/s, ias {jet.State.Ias:F0}, pitch {Mathf.RadToDeg(AirlinerFlight.PitchOf(jet.State.Attitude)):F0}°, bank {Mathf.RadToDeg(AirlinerFlight.BankOf(jet.State.Attitude)):F0}°, lever {jet.State.Lever:F2}, heat {jet.Heat:F2}, drive {jet.DriveOn}, home {Distance(p) / 1000:F0} km, health {p.VehicleHealth:F0}"));
    }

    // ---- the geometry, in doubles ------------------------------------------------------------

    private EcefPos Here(FootPlayer p) => _origin.Frame.ToEcef(p.GlobalPosition);

    /// <summary>The great-circle angle between two places, rad.</summary>
    private static double Angle(EcefPos a, EcefPos b)
    {
        double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        double cx = a.Y * b.Z - a.Z * b.Y, cy = a.Z * b.X - a.X * b.Z, cz = a.X * b.Y - a.Y * b.X;
        return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), dot);
    }

    /// <summary>Over the ground to the threshold, m.</summary>
    private double Distance(FootPlayer p) => Angle(Here(p), _home) * 6_371_000;

    /// <summary>A place on the runway's line, <paramref name="along"/> metres past the threshold (negative: before it), at sea level.</summary>
    private EcefPos OnLine(double along) => SwissGeodesy.ToEcef(_homeE, _homeN + along, 0);

    /// <summary>The way up here, and the nose's elevation over the horizon, rad.</summary>
    private float Elevation(FootPlayer p, Airliner jet)
    {
        var up = _origin.Frame.UpAt(p.GlobalPosition);
        return Mathf.Asin(Mathf.Clamp((-jet.State.Attitude.Z).Dot(up), -1f, 1f));
    }

    /// <summary>The heading error to a place, rad: + it is to the right of the nose.</summary>
    private float Bearing(FootPlayer p, Airliner jet, EcefPos to)
    {
        var at = p.GlobalPosition;
        var here = Here(p);
        var d = _origin.Frame.WorldDirection(at, new EcefPos(to.X - here.X, to.Y - here.Y, to.Z - here.Z));
        var up = _origin.Frame.UpAt(at);
        var flat = (d - up * d.Dot(up)).Normalized();
        var nose = -jet.State.Attitude.Z;
        nose = (nose - up * nose.Dot(up)).Normalized();
        return Mathf.Atan2(nose.Cross(flat).Dot(-up), nose.Dot(flat));
    }

    /// <summary>The stick for a nose elevation and a bank (the reaction jets', or the law's in the air).</summary>
    private static Vector2 Attitude(float elevation, float wantElevation, float bank, float wantBank) =>
        new(Mathf.Clamp((wantBank - bank) * 2.5f, -1f, 1f), Mathf.Clamp((wantElevation - elevation) * 3f, -1f, 1f));

    private void Fly(Vector2 stick, float lever, float brake = 0f)
    {
        var jet = _shuttle!;
        float now = jet.State.Lever;
        float up = Mathf.Clamp((lever - now) * 6f, 0f, 1f), down = Mathf.Clamp((now - lever) * 6f, 0f, 1f);
        _player!.FlyControls = () => new FlightInput(stick, 0f, 0f, up, down, false, false, 0f, Brake: brake);
    }

    // ---- the stages ---------------------------------------------------------------------------

    private void Spawn()
    {
        if (_player == null)
        {
            _player = new FootPlayer { Name = "Probe", Origin = _origin };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(0, TestWorld.GroundY + 1f, 0);
            _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
            _player.FlyControls = () => new FlightInput(Vector2.Zero, 0f, 0f, 0f, 0f, false, false, 0f, Brake: 1f);
            return;
        }
        if (_t < 1 || !_player.IsOnFloor()) return;
        if (_shuttle == null)
        {
            if (!_player.SetRide(RideKind.Shuttle) || _player.Vehicle is not Airliner { Spec.Spacecraft: true } jet) { Fail("no shuttle"); return; }
            _shuttle = jet;
            var g = _origin.Frame.ToGlobal(_player.GlobalPosition);
            _homeE = g.E;
            _homeN = g.N;
            _home = SwissGeodesy.ToEcef(g.E, g.N, 0);
            return;
        }
        if (_t < 3) return;
        if (CmdArgs.Value("--shuttlecheck", notFlag: true) == "glide")
        {
            // the landing alone: where the drive leaves it, 80 km out on the runway's line, 52 km up, 300 m/s north
            var at = _origin.Frame.ToWorld(_homeE, _homeN - EntryOut, 52_000);
            _player.DebugLaunch(at, new Vector3(0, -30f, -300f));
            _shuttle.State.Lever = _shuttle.State.Spool = 0f;
            Next(Stage.Fall, Fast);
            return;
        }
        Next(Stage.Roll, Near);
    }

    private void Pilot(FootPlayer p, Airliner jet, float dt)
    {
        var s = jet.State;
        double alt = _origin.Frame.AltitudeOf(p.GlobalPosition);
        float elev = Elevation(p, jet), bank = AirlinerFlight.BankOf(s.Attitude);
        switch (_stage)
        {
            case Stage.Roll:
                // full thrust, rotate at 1.15 Vs, to a 50° climb
                Fly(new Vector2(-bank, s.Ias > jet.Spec.StallSpeed(s.Mass, 0) * 1.15f ? 1f : 0f), 1f);
                if (!s.OnGround && alt > 30) { jet.Command(AirlinerCommand.Gear); Next(Stage.Climb, Near); }
                if (_stageT > 120) Fail("no lift-off");
                return;
            case Stage.Climb:
                Fly(Attitude(elev, Mathf.DegToRad(50f), bank, 0f), 1f);
                if (alt > 2000) Engine.TimeScale = Fast;
                if (jet.InSpace)
                {
                    GD.Print(FormattableString.Invariant($"[shuttlecheck] in Space at {alt / 1000:F1} km, {p.Velocity.Length():F0} m/s after {_stageT:F0} s of climb"));
                    if (!p.ShuttleDrive(jet)) { Fail("the drive would not switch on"); return; }
                    Next(Stage.DriveUp, Fast);
                }
                if (_stageT > 1200) Fail("not in Space after 20 min of climb");
                return;
            case Stage.DriveUp:
            {
                // the drive up to 400 km, then level
                float want = Mathf.Clamp((float)((400_000 - alt) / 60_000), -1f, 1f) * Mathf.DegToRad(35f);
                Fly(Attitude(elev, want, bank, 0f), 1f);
                if (!jet.DriveOn) { Fail("the drive dropped out on the way up"); return; }
                if (Math.Abs(alt - 400_000) < 3000 && Mathf.Abs(elev) < 0.02f)
                {
                    GD.Print(FormattableString.Invariant($"[shuttlecheck] 400 km reached in the drive, {p.Velocity.Length() / 1000:F1} km/s, after {_stageT:F0} s"));
                    _last = Here(p);
                    Next(Stage.Round, Fast);
                }
                if (_stageT > 600) Fail(FormattableString.Invariant($"not at 400 km after 10 min of drive: {alt / 1000:F0} km"));
                return;
            }
            case Stage.Round:
            {
                float want = Mathf.Clamp((float)((400_000 - alt) / 20_000), -1f, 1f) * Mathf.DegToRad(10f);
                Fly(Attitude(elev, want, bank, 0f), 1f);
                var here = Here(p);
                _travel += Angle(_last, here);
                _last = here;
                if (Math.Abs(alt - 400_000) > 20_000) { Fail(FormattableString.Invariant($"the drive strayed to {alt / 1000:F0} km")); return; }
                if (_travel >= 2 * Math.PI)
                {
                    GD.Print(FormattableString.Invariant($"[shuttlecheck] round the Earth in the drive in {_stageT:F0} s at {p.Velocity.Length() / 1000:F1} km/s; now at {alt / 1000:F1} km"));
                    _drove400 = true;
                    if (!p.ShuttleDrive(jet)) { Fail("the drive would not switch off"); return; }
                    Next(Stage.Orbit, Fast);
                }
                if (_stageT > 3600) Fail("not round the Earth after an hour");
                return;
            }
            case Stage.Orbit:
            {
                Fly(Vector2.Zero, 0f);
                if (jet.DriveOn || jet.Space!.Orbit is not { } o) { if (_stageT > 2) Fail("no orbit after the drive went off"); return; }
                var (peri, apo, _) = Orbit.Apsides(Planet.Earth, o);
                double r0 = Planet.Earth.Shape.A;
                if (_stageT < 0.5) return;
                if (_stageT < 0.6)
                    GD.Print(FormattableString.Invariant($"[shuttlecheck] in orbit: perigee {(peri - r0) / 1000:F1} km, apogee {(apo - r0) / 1000:F1} km, {o.V.Length:F0} m/s"));
                // circular: the apsides (distances from the centre) within 1 km of each other
                if (apo - peri > 1_000) { Fail(FormattableString.Invariant($"not circular: {(peri - r0) / 1000:F1} x {(apo - r0) / 1000:F1} km")); return; }
                if (_stageT > 120) Next(Stage.Retro, Fast);
                return;
            }
            case Stage.Retro:
            {
                // nose to retrograde (the jets pitch it over), then the rockets till the perigee is at 45 km
                var v = p.Velocity.Normalized();
                var nose = -s.Attitude.Z;
                float off = Mathf.Acos(Mathf.Clamp(nose.Dot(-v), -1f, 1f));
                var o = jet.Space!.Orbit;
                double perigee = o is { } orbit ? Orbit.Apsides(Planet.Earth, orbit).Periapsis - Planet.Earth.Shape.A : double.NaN;
                bool burn = off < 0.05f && perigee > 45_000;
                Fly(new Vector2(-bank * 0.5f, off > 0.05f ? Mathf.Clamp(off, 0.3f, 1f) : 0f), burn ? 1f : 0f);
                if (o != null && perigee <= 45_000 && s.Spool <= jet.Spec.IdleSpool + 0.02f)
                {
                    GD.Print(FormattableString.Invariant($"[shuttlecheck] retro burn done: perigee {perigee / 1000:F1} km after {_stageT:F0} s"));
                    Next(Stage.Entry, Fast);
                }
                if (_stageT > 600) Fail("the retro burn took over 10 min");
                return;
            }
            case Stage.Entry:
                // back to prograde, then belly first: the nose 25° over the horizon, down through the heat
                Fly(Attitude(elev, Mathf.DegToRad(25f), bank, 0f) with { Y = PrograteStick(p, jet) }, 0f);
                if (!jet.InSpace)
                {
                    GD.Print(FormattableString.Invariant($"[shuttlecheck] re-entered: out of Space at {alt / 1000:F1} km, {p.Velocity.Length():F0} m/s, heat peak {jet.HeatPeak:F2}"));
                    if (jet.HeatPeak < 0.5f) { Fail(FormattableString.Invariant($"no glow on re-entry: heat peak {jet.HeatPeak:F2}")); return; }
                    Next(Stage.Air, Fast);
                }
                if (_stageT > 4000) Fail("not down out of Space within 4000 s of the burn");
                return;
            case Stage.Air:
                // a minute in the air at hypersonic speed, the law flying level-ish
                Fly(Attitude(elev, Mathf.DegToRad(5f), bank, 0f), 0f);
                if (alt < 15_000) { Fail(FormattableString.Invariant($"fell to {alt / 1000:F1} km in the air")); return; }
                if (_stageT > 60) Next(Stage.BackUp, Fast);
                return;
            case Stage.BackUp:
                Fly(Attitude(elev, Mathf.DegToRad(45f), bank, 0f), 1f);
                if (jet.InSpace && alt > ShuttleFlight.DriveFloorM + 2000)
                {
                    if (!p.ShuttleDrive(jet)) { Fail("the drive would not switch on again"); return; }
                    Next(Stage.DriveHome, Fast);
                }
                if (_stageT > 900) Fail("not back in Space");
                return;
            case Stage.DriveHome:
                DriveHome(p, jet, alt, elev, bank);
                return;
            case Stage.Fall:
                Fall(p, jet, alt, elev, bank);
                return;
            case Stage.Glide:
                Glide(p, jet, alt, elev, bank, dt);
                return;
            case Stage.Rollout:
                Rollout(p, jet);
                return;
        }
    }

    /// <summary>The pitch stick that turns the nose toward the velocity (prograde) first, then holds the elevation asked.</summary>
    private float PrograteStick(FootPlayer p, Airliner jet)
    {
        var v = p.Velocity.Normalized();
        var nose = -jet.State.Attitude.Z;
        float elev = Elevation(p, jet);
        // still pointing back: keep pitching over
        if (nose.Dot(v) < 0.5f) return 1f;
        return Mathf.Clamp((Mathf.DegToRad(25f) - elev) * 3f, -1f, 1f);
    }

    /// <summary>Where the drive leaves it: 80 km before the threshold on the runway's line, 56 km up.</summary>
    private const double EntryOut = 80_000, EntryAlt = 56_000;

    private void DriveHome(FootPlayer p, Airliner jet, double alt, float elev, float bank)
    {
        // first a point 400 km out on the line, then the entry point 150 km out: so it comes in along the runway
        var far = OnLine(-400_000);
        var entry = OnLine(-EntryOut);
        var here = Here(p);
        double toFar = Angle(here, far) * 6_371_000, toEntry = Angle(here, entry) * 6_371_000;
        _passedFar |= toFar < 30_000;
        var target = _passedFar ? entry : far;
        double dist = _passedFar ? toEntry : toFar + 250_000;
        float err = Bearing(p, jet, target);
        float wantBank = Mathf.Clamp(err * 2f, -1f, 1f);
        // the altitude falls with the distance, the speed with it (the lever sets it: v = lever · d / τ)
        double wantAlt = Math.Clamp(EntryAlt + dist * 0.2, EntryAlt, 300_000);
        float wantElev = Mathf.Clamp((float)((wantAlt - alt) / 40_000), -1f, 1f) * Mathf.DegToRad(25f);
        double speed = Math.Clamp(dist / 40, 400, 1e6);
        float lever = Mathf.Clamp((float)(speed * ShuttleFlight.DriveTau / alt), 0.002f, 1f);
        Fly(Attitude(elev, wantElev, bank, wantBank), lever);
        if (_passedFar && toEntry < 8_000)
        {
            GD.Print(FormattableString.Invariant($"[shuttlecheck] over the entry point {Distance(p) / 1000:F0} km out at {alt / 1000:F1} km, {p.Velocity.Length():F0} m/s, after {_stageT:F0} s of drive"));
            Next(Stage.Fall, Fast);
        }
        if (_stageT > 3600) Fail("not home after an hour of drive");
    }

    private bool _passedFar;

    private void Fall(FootPlayer p, Airliner jet, double alt, float elev, float bank)
    {
        // nose down out of the drive (it drops out below 50 km), then the air takes it; pull out to the glide
        float err = Bearing(p, jet, _home);
        if (jet.DriveOn) { Fly(Attitude(elev, Mathf.DegToRad(-20f), bank, Mathf.Clamp(err * 2f, -0.5f, 0.5f)), jet.State.Lever); return; }
        float q = 0.5f * AirlinerFlight.Density((float)alt) * jet.State.Tas * jet.State.Tas;
        Fly(Attitude(elev, q < 3000 ? Mathf.DegToRad(-15f) : Mathf.DegToRad(-8f), bank, Mathf.Clamp(err * 2f, -0.5f, 0.5f)), 0f);
        if (q > 4000 && alt < 30_000)
        {
            GD.Print(FormattableString.Invariant($"[shuttlecheck] flying again at {alt / 1000:F1} km, {jet.State.Ias:F0} m/s IAS, {Distance(p) / 1000:F0} km out"));
            Next(Stage.Glide, Fast);
        }
        if (alt < 8_000) Fail(FormattableString.Invariant($"no pull-out: {alt / 1000:F1} km"));
    }

    /// <summary>The steep glide to the runway: the path aimed at a point short of the threshold, the speed by the speedbrake (and the rockets if low).</summary>
    private void Glide(FootPlayer p, Airliner jet, double alt, float elev, float bank, float dt)
    {
        var s = jet.State;
        // the runway's frame: along (+ past the threshold), across (+ right)
        var g = _origin.Frame.ToGlobal(p.GlobalPosition);
        double along = g.N - _homeN, across = g.E - _homeE;
        // the flat world's ground is put under the runway once the frame is a ground frame near home
        PutGround();
        // the orbiter's way, shortened for a scripted pilot: a steep outer glideslope aimed 1 km short of the
        // threshold, the speed held by the speedbrake; a pre-flare from 400 m that rounds it out onto a 2.5°
        // path by 80 m, the speed held by the rockets (a tight sink loop lags the airframe, balloons and drops)
        float agl = (float)alt;
        if (agl < 3_000) Engine.TimeScale = Near;
        var v = s.Velocity;
        float speed = Mathf.Max(v.Length(), 50f);
        float gamma = Mathf.Asin(Mathf.Clamp(v.Y / speed, -1f, 1f));
        float outer = Mathf.Clamp(-(float)Math.Atan2(alt, Math.Max(-1_000 - along, 300)), -0.36f, -0.05f);
        float k = Mathf.SmoothStep(0f, 1f, (400f - agl) / 320f);
        float gammaWant = Mathf.Lerp(outer, Mathf.Max(outer, -0.044f), k);
        float stickY = Mathf.Clamp((gammaWant - gamma) * 3f, -1f, 1f);
        // the flare as a pilot flies it: the nose to an attitude and held there (a loop on the sink itself lags
        // the airframe, balloons and drops): 8° at once, then the attitude wanted creeps toward the sink wanted, a quarter of
        // the height a second, 1.5 m/s at the wheels, and the rockets hold the speed so it does not drift
        float pitch = AirlinerFlight.PitchOf(s.Attitude);
        if (!_flare && agl < 30f) { _flare = true; _flareAt = Mathf.Max(pitch, 0.14f); }
        if (_flare)
        {
            _flareAt = Mathf.Clamp(_flareAt + (-Mathf.Max(1.5f, agl * 0.25f) - v.Y) * 0.008f * dt, -0.05f, 0.22f);
            stickY = Mathf.Clamp((_flareAt - pitch) * 6f, -0.8f, 0.8f);
        }
        if (agl < 60f && _stageT >= _trace) { _trace = _stageT + 0.5; GD.Print(FormattableString.Invariant($"[shuttlecheck] flare agl {agl:F1} vy {v.Y:F2} stick {stickY:F2} pitch {Mathf.RadToDeg(AirlinerFlight.PitchOf(s.Attitude)):F1} alpha {Mathf.RadToDeg(s.Alpha):F1} ias {s.Ias:F0} q {s.PitchRate:F3} floor {s.OnGround}")); }
        // lateral: onto the line, then along it (a heading clockwise from north is minus the yaw)
        float track = RunwayHeading + (float)Math.Atan2(-across, Math.Max(Math.Abs(along) * 0.25, 1_500));
        float headingErr = Mathf.Wrap(track + s.Yaw, -Mathf.Pi, Mathf.Pi);
        float wantBank = Mathf.Clamp(headingErr * 2.5f, agl < 200 ? -0.1f : -0.6f, agl < 200 ? 0.1f : 0.6f);
        // too high for a 15° glide to the aim point: a turn burns the height off without closing in (the
        // orbiter's heading-alignment circle), and the line brings it back after
        double high = alt - Math.Max(-1_000 - along, 0) * Math.Tan(0.26);
        _circling = along < -8_000 && (high > (_circling ? 0 : 800));
        if (_circling) wantBank = 0.7f;
        // the rockets when it is short of speed (the real one has none on approach; a player has them,
        // unlimited): a 4:1 brick loses its speed fast once the flare has taken the dive off it
        float vLow = agl > 400 ? 130f : 125f;
        if (_flare) vLow = 115f;
        float lever = agl > 2f && s.Ias < vLow ? Mathf.Clamp((vLow + 5f - s.Ias) * 0.08f, 0f, 0.8f) : 0f;
        // the speedbrake holds the speed on the outer slope, and closes for the flare
        bool brakes = agl > 400 && s.Ias > 140f;
        if (brakes != (s.SpeedBrake == 2)) jet.Command(AirlinerCommand.Speedbrake);
        if (agl < 500 && !s.GearDown) jet.Command(AirlinerCommand.Gear);
        Fly(new Vector2(Mathf.Clamp((wantBank - bank) * 2.5f, -1f, 1f), stickY), lever);
        if (s.OnGround)
        {
            GD.Print(FormattableString.Invariant($"[shuttlecheck] touchdown {along:F0} m past the threshold, {across:F1} m off the centreline, sink {s.LastSink:F2} m/s, {s.Ias / 0.514444f:F0} kt"));
            if (s.LastSink > jet.Spec.HardLanding) { Fail(FormattableString.Invariant($"a hard landing: {s.LastSink:F2} m/s")); return; }
            Next(Stage.Rollout, Near);
        }
        if (_stageT > 2400) Fail("no touchdown within 40 min of the glide");
    }

    private bool _grounded, _circling, _flare;

    /// <summary>The flare's attitude wanted, rad.</summary>
    private float _flareAt;
    private double _trace;

    private void PutGround()
    {
        if (_grounded || _origin.Frame.IsAnchor) return;
        if (GetParent().GetNodeOrNull<Node3D>("Ground") is not { } ground) return;
        var home = _origin.Frame.ToWorld(_homeE, _homeN + RunwayLength * 0.5, 0);
        ground.GlobalPosition = new Vector3(home.X, TestWorld.GroundY - 0.5f - _origin.Frame.ToWorld(_homeE, _homeN, 0).Y * 0f, home.Z);
        _grounded = true;
    }

    private void Rollout(FootPlayer p, Airliner jet)
    {
        var s = jet.State;
        if (s.SpeedBrake != 2) jet.Command(AirlinerCommand.Speedbrake);
        Fly(new Vector2(0f, 0f), 0f, brake: 1f);
        if (s.Velocity.Length() > 0.5f) { if (_stageT > 300) Fail("not stopped"); return; }
        var g = _origin.Frame.ToGlobal(p.GlobalPosition);
        double along = g.N - _homeN, across = g.E - _homeE;
        GD.Print(FormattableString.Invariant($"[shuttlecheck] stopped {along:F0} m past the threshold, {across:F1} m off the centreline, health {p.VehicleHealth:F0}, heat peak {jet.HeatPeak:F2}, drove round {_drove400}"));
        if (along < 0 || along > RunwayLength || Math.Abs(across) > RunwayHalfWidth) { Fail("stopped off the runway"); return; }
        Finish(0, "ok");
    }

    private void Fail(string why) => Finish(1, "FAILED " + why);

    private void Finish(int code, string result)
    {
        if (_player != null && _shuttle != null) Report(_player, _shuttle);
        Engine.TimeScale = 1.0;
        _done = true;
        GD.Print($"[shuttlecheck] RESULT: {result}");
        GetTree().Quit(code);
    }
}
