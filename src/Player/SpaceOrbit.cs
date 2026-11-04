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

    /// <summary>The orbit, in doubles in the inertial frame, while in the Space regime; null below it.</summary>
    public Core.OrbitState? Orbit { get; private set; }

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

    /// <summary>The next step goes straight to the orbit's place, however far: a set orbit, not a contact.</summary>
    private bool _jump;

    /// <summary>Whether it is in the Space regime now (its orbit is live), with the regime's hysteresis.</summary>
    public bool InSpace(double altitude) => Core.Curvature.SpaceRegime(Orbit != null || _jump, altitude);

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
            return null;
        }
        var earth = Core.Planet.Earth;
        double angle = Core.Orbit.RotationAngle(earth, place.SimTime);
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
        // the air turns with the Earth: drag against the ground-relative velocity
        double rho = Core.Atmosphere.Density(place.Altitude);
        if (rho > 0)
        {
            var (_, air) = Core.Orbit.ToEcef(earth, s, angle);
            thrust += Core.Orbit.ToEci(air * (-0.5 * rho * air.Length / ballisticCoefficient), angle);
        }
        s = Core.Orbit.Step(earth, s, thrust, dt);
        Seconds += dt;
        var (r, v) = Core.Orbit.ToEcef(earth, s, angle + earth.RotationRate * dt);
        Orbit = s;

        var target = place.Frame.FromEcef(r.AsEcef);
        _expected = target;
        _expectedIn = place.Frame;
        // the body goes where the orbit says; what stops it on the way is a contact
        return new Result((target - place.At) / dt, place.Frame.WorldDirection(place.At, v.AsEcef));
    }
}
