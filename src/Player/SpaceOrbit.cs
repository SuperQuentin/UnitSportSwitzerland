using System;
using Godot;

namespace UnitSport.Player;

/// <summary>
/// Where a craft is, set by its body before each step: its world's frame, its world point, its altitude
/// and the sim time. <paramref name="Contacts"/> false: the orbit is the only truth, never restarted from
/// the body (an empty craft on the server, whose frame's floats coarsen far from its origin).
/// </summary>
public readonly record struct SpacePlace(Core.OriginFrame Frame, Vector3 At, double Altitude, double SimTime, bool Contacts = true);

/// <summary>A craft that flies an orbit above the Space handover (#1430 H5): the UFO, the shuttle (#1492).</summary>
public interface ISpacecraft
{
    /// <summary>Its orbit seam; null on a craft of the kind that never reaches space (an A320).</summary>
    SpaceOrbit? Space { get; }
}

/// <summary>
/// The orbit seam (#1430 H5, shared by the UFO and the shuttle, #1492): above the Space handover (entered at
/// 60 km climbing, left below 50 km, the craft's own altitude) the craft's state is an orbit in doubles in the
/// inertial frame (<see cref="Core.Orbit"/>), stepped under GM/r², the craft's thrust and the 1976 air's drag.
/// The body is sent where the orbit says (<c>velocity = (target − here) / dt</c>), so Jolt only has the
/// contacts: a body found more than <see cref="ContactSlack"/> from where it was sent hit something, and the
/// orbit restarts from it. See <c>docs/notes/player/orbits.md</c>.
/// </summary>
public sealed class SpaceOrbit
{
    /// <summary>The log's tag: <c>[ufo]</c>, <c>[shuttle]</c>.</summary>
    private readonly string _tag;

    public SpaceOrbit(string tag) => _tag = tag;

    /// <summary>The body's place before this step (null where there is no origin: no Space).</summary>
    public SpacePlace? Place { get; set; }

    /// <summary>The orbit, in doubles in <see cref="Body"/>'s inertial frame, while in the Space regime; null below it.</summary>
    public Core.OrbitState? Orbit { get; private set; }

    /// <summary>
    /// The body the orbit goes round (#1851): the anchor's, so the one whose sphere of influence the craft is in. Its
    /// GM, spin, Space thresholds and air; the Earth until the anchor crosses into another sphere.
    /// </summary>
    public Core.CelestialBody Body { get; private set; } = Core.CelestialBody.Earth;

    /// <summary>
    /// Where the orbit sent the body last step, in that step's world: found elsewhere, a contact moved it.
    /// Compared in world space, through the shift since (<see cref="Core.OriginFrame.Since"/>), not in ECEF:
    /// the handover from the flat ground frame is a rigid move that puts a point 400 km up 10 m off its
    /// true place (the LV95 and WGS84 verticals are not parallel), and that is no contact.
    /// </summary>
    private Vector3 _expected;
    private Core.OriginFrame? _expectedIn;

    /// <summary>Times a contact moved the body off its orbit and the orbit restarted from it (the orbit checks want none in free flight).</summary>
    public int Contacts { get; private set; }

    /// <summary>How far the body may be from its orbit before a contact is taken as the truth, m (floats hold 3 cm at 400 km).</summary>
    public const double ContactSlack = 1.0;

    /// <summary>Seconds the orbit has been stepped, summed in doubles (the orbit check times its period by it).</summary>
    public double Seconds { get; private set; }

    /// <summary>The orbit's own sim time (#1894): the body's turn and the orbit's steps both go by it; NaN below the regime.</summary>
    private double _clock = double.NaN;

    /// <summary>The lowest lag of <see cref="_clock"/> behind the shared sim clock in the current window, and when the window began.</summary>
    private double _lagMin = double.MaxValue, _windowFrom;

    /// <summary>Drift still to catch up, s (negative: the orbit's clock is ahead).</summary>
    private double _owed;

    /// <summary>How long the lag is watched before a drift is owed, s: several frames, so the within-frame swing is out of it.</summary>
    private const double DriftWindow = 0.25;

    /// <summary>
    /// A drift past this is a jump of the clock (a resync, a pause), s: taken into <see cref="_lagBase"/>, neither flown
    /// nor set (setting it would turn the body under the craft by that much at once).
    /// </summary>
    private const double MaxCatchUp = 2.0;

    /// <summary>
    /// How far behind the shared sim clock the orbit's clock is meant to be, s: 0, but for a jump past
    /// <see cref="MaxCatchUp"/> or a stretch on a local time scale (see <see cref="FollowsSharedClock"/>), whose gap stays.
    /// </summary>
    private double _lagBase;

    /// <summary>A stretch on a local time scale ended: the next window takes the gap it left as the base, not as drift.</summary>
    private bool _rebase;

    /// <summary>
    /// Whether the physics runs at the shared clock's rate, so the orbit's clock may follow it. A probe or a film that sets
    /// <see cref="Engine.TimeScale"/> itself (--orbitcheck coasts at 8x) runs the physics apart from the shared clock, and
    /// following it there owed the 7x back a tick per step: the orbit stood still (#1894). Then the physics owns the time.
    /// </summary>
    private static bool FollowsSharedClock => Math.Abs(Engine.TimeScale - Core.SimClock.Scale) < 1e-3;

    /// <summary>The longest single integration step, s: a catch-up step stays as precise as the ticks around it.</summary>
    private const double MaxSubstep = 1.0 / 30.0;

    private void ResetClock(double simTime)
    {
        _clock = simTime;
        _lagBase = 0;
        _rebase = false;
        _owed = 0;
        _lagMin = double.MaxValue;
        _windowFrom = simTime;
    }

    /// <summary>The next step goes straight to the orbit's place, however far: a set orbit, not a contact.</summary>
    private bool _jump;

    /// <summary>Whether it is in the Space regime now (its orbit is live), with the regime's hysteresis.</summary>
    public bool InSpace(double altitude) => BodyOf(Place).SpaceRegime(Orbit != null || _jump, altitude);

    /// <summary>The body a place's frame is on (the Earth for a flat frame or none).</summary>
    private static Core.CelestialBody BodyOf(SpacePlace? place) =>
        Core.CelestialBody.Of(place?.Frame.Axes?.Body ?? Core.BodyId.Earth);

    /// <summary>Puts the craft on <paramref name="state"/> (an inertial orbit) from its next step: <c>/orbit</c>, a probe, a bot, the drive switched off.</summary>
    public void Set(Core.OrbitState state)
    {
        Orbit = state;
        _jump = true;
    }

    /// <summary>
    /// Leaves the orbit without stepping it (the shuttle's drive carries the body kinematically): the next
    /// <see cref="Step"/> starts again from the body's place and velocity, as entering Space does.
    /// </summary>
    public void Drop()
    {
        Orbit = null;
        _jump = false;
        _expectedIn = null;
    }

    /// <summary>The result of one step in the Space regime.</summary>
    /// <param name="Velocity">The body's velocity this step: to where the orbit puts it.</param>
    /// <param name="Track">The orbit's ground-relative velocity at the step's end, world axes at the body.</param>
    public readonly record struct Result(Vector3 Velocity, Vector3 Track);

    /// <summary>
    /// One step: the orbit (taken from the body on entering Space or after a contact) stepped under GM/r², a
    /// thrust acceleration <paramref name="thrustWorld"/> (m/s², world axes at the body) and the air's drag at
    /// <paramref name="ballisticCoefficient"/> (mass over Cd·A, kg/m², against the ground-relative velocity).
    /// Null below the regime (or with no place), where the orbit is dropped.
    /// </summary>
    public Result? Step(Vector3 thrustWorld, double ballisticCoefficient, Vector3 bodyVelocity, float dt)
    {
        if (Place is not { } place || dt <= 0f) return null;
        if (!InSpace(place.Altitude))
        {
            _expectedIn = null;
            if (Orbit != null) GD.Print(FormattableString.Invariant($"[{_tag}] out of orbit at {place.Altitude / 1000:F1} km"));
            Orbit = null;
            _clock = double.NaN;
            return null;
        }
        // the anchor crossed into another body's sphere of influence (#1851): the orbit goes with it
        var earth = BodyOf(place);
        if (earth != Body)
        {
            if (Orbit is { } before)
            {
                Orbit = Cross(Body, earth, before, place.SimTime, Core.SkyClock.Now);
                GD.Print(FormattableString.Invariant($"[{_tag}] the orbit goes round {earth.Name} now: {Orbit.Value.V.Length:F0} m/s, {Orbit.Value.R.Length / 1000:F0} km from its centre"));
                _jump = true;
            }
            Body = earth;
        }
        // the orbit's own clock (#1894): the body turns by it and the orbit is stepped by it, so each step's move (and the
        // velocity published from it) is one consistent thing. The frame's sim clock stands still across the several physics
        // steps of one frame: turning the body by it put a frame's turn into one step's velocity and none into the next
        // (60-170 m/s of noise at 7.7 km/s), while stepping the orbit by dt alone let a client that drops physics time fall
        // behind on its track. The clock follows the shared sim clock: the drift (the lowest lag over a window, out of the
        // within-frame swing) is caught up at most a tick per step; a jump past MaxCatchUp, or a stretch on a local time
        // scale, becomes the new base.
        if (_jump || Orbit == null || double.IsNaN(_clock)) ResetClock(place.SimTime);
        else if (!FollowsSharedClock)
        {
            _owed = 0;
            _rebase = true;
        }
        else if (place.SimTime - _windowFrom >= DriftWindow)
        {
            if (_rebase || Math.Abs(_lagMin) > MaxCatchUp)
            {
                _lagBase += _lagMin;
                _owed = 0;
                _rebase = false;
            }
            else if (Math.Abs(_lagMin) > 2.0 * dt) _owed = _lagMin;
            _lagMin = double.MaxValue;
            _windowFrom = place.SimTime;
        }
        double take = Math.Clamp(_owed, -dt, dt);
        _owed -= take;
        double elapsed = dt + take;
        double angle = Core.Orbit.RotationAngle(earth, _clock);
        var here = place.Frame.ToEcef(place.At);
        float off = _expectedIn == null ? float.MaxValue
            : place.At.DistanceTo(place.Frame.SameFrame(_expectedIn) ? _expected : place.Frame.Since(_expectedIn).Point(_expected));
        Core.OrbitState s;
        if (Orbit is { } o && (_jump || !place.Contacts || off <= ContactSlack)) s = o;
        else
        {
            if (Orbit == null) GD.Print(FormattableString.Invariant($"[{_tag}] into orbit at {place.Altitude / 1000:F1} km"));
            else
            {
                Contacts++;
                GD.Print(FormattableString.Invariant($"[{_tag}] a contact: the body is {off:F2} m off its orbit at {place.Altitude / 1000:F1} km, the orbit restarts from it"));
            }
            // into Space, or a contact moved the body: its place and velocity are the truth
            s = Core.Orbit.FromEcef(earth, here, place.Frame.EcefDirection(place.At, bodyVelocity), angle);
        }
        _jump = false;

        var thrust = Core.Orbit.ToEci(place.Frame.EcefDirection(place.At, thrustWorld), angle);
        // the air turns with its body: drag against the ground-relative velocity (none round an airless body; Mars'
        // thin CO2 by its own scale height, #1853)
        double rho = earth.AirDensity(place.Altitude);
        if (rho > 0)
        {
            var (_, air) = Core.Orbit.ToEcef(earth, s, angle);
            thrust += Core.Orbit.ToEci(air * (-0.5 * rho * air.Length / ballisticCoefficient), angle);
        }
        int steps = (int)Math.Ceiling(elapsed / MaxSubstep);
        for (int i = 0; i < steps; i++) s = Core.Orbit.Step(earth, s, thrust, elapsed / steps);
        Seconds += elapsed;
        _clock += elapsed;
        _lagMin = Math.Min(_lagMin, place.SimTime - _clock - _lagBase);
        var (r, v) = Core.Orbit.ToEcef(earth, s, angle + earth.Spin * elapsed);
        Orbit = s;

        var target = place.Frame.FromEcef(r.AsEcef);
        _expected = target;
        _expectedIn = place.Frame;
        // the body goes where the orbit says; what stops it on the way is a contact
        return new Result((target - place.At) / dt, place.Frame.WorldDirection(place.At, v.AsEcef));
    }

    /// <summary>
    /// An orbit carried from one body's sphere of influence into another's (#1851, docs/plans/planets.md section 2):
    /// the place re-expressed exactly through the frame tree; the velocity, inertial in the old body's axes, turned into
    /// the ICRF, less the new body's <b>true</b> orbital velocity about the old one (<see cref="Core.Frames.Velocity"/>,
    /// ~1 km/s for the Moon), turned into the new body's axes. Never the sky clock's swing: at the 24-minute day the
    /// Moon sweeps through the Earth's frame at ~1,650 km/s, and taking that off would fling the craft at it. With
    /// <c>/time speed real</c> this is plain patched conics.
    /// </summary>
    public static Core.OrbitState Cross(Core.CelestialBody from, Core.CelestialBody to, Core.OrbitState s, double simTime, Core.SkyMoment sky)
    {
        double angleFrom = Core.Orbit.RotationAngle(from, simTime), angleTo = Core.Orbit.RotationAngle(to, simTime);
        var (r, groundV) = Core.Orbit.ToEcef(from, s, angleFrom);
        var inertialFixed = groundV + new Core.DVec3(0, 0, from.Spin).Cross(r);
        var vIcrf = Core.Frames.Orientation(from.Id, sky).Apply(inertialFixed) - Core.Frames.Velocity(from.Id, to.Id, sky.Utc);
        var r2 = Core.Frames.Convert(from.Id, r, to.Id, sky);
        var v2Fixed = Core.Frames.Orientation(to.Id, sky).ApplyInverse(vIcrf) - new Core.DVec3(0, 0, to.Spin).Cross(r2);
        return Core.Orbit.FromEcef(to, r2, v2Fixed, angleTo);
    }
}
