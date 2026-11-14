using System;
using Godot;

namespace UnitSport.Player;

/// <summary>
/// The space shuttle (#1492, #1430 C2, <c>docs/notes/player/shuttle.md</c>): an <see cref="Airliner"/> on
/// rockets that flies its orbit above the Space handover through <see cref="SpaceOrbit"/> (the seam the UFO
/// flies, #1430 H5), its reaction jets turning it (<see cref="ShuttleFlight.Jets"/>), and the cruise drive:
/// switched on above 60 km, it carries the body at v = lever · d / τ along the nose, steered like a plane;
/// switched off it leaves the craft on a circular orbit where it is. Taken down out of Space (below 50 km)
/// the drive drops out and the craft flies on at its drive speed: an aircraft again, gliding home.
/// </summary>
public sealed partial class Airliner : ISpacecraft
{
    private SpaceOrbit? _space;

    /// <summary>The orbit seam, on a spacecraft only.</summary>
    public SpaceOrbit? Space => Spec.Spacecraft ? _space ??= new SpaceOrbit("shuttle") : null;

    /// <summary>The cruise drive is on: the body is carried kinematically, no orbit stepped.</summary>
    public bool DriveOn { get; private set; }

    /// <summary>The drive switch pressed since the last step (<see cref="AirlinerCommand.Drive"/>).</summary>
    private bool _drive;

    /// <summary>In the Space regime last step: an orbit or the drive.</summary>
    public bool InSpace { get; private set; }

    /// <summary>Re-entry's glow, 0 .. 1 (<see cref="ShuttleFlight.Heat"/>), and the highest seen (the check reads it).</summary>
    public float Heat { get; private set; }
    public float HeatPeak { get; private set; }

    /// <summary>Altitude above sea level last step, m (from <see cref="SpaceOrbit.Place"/>; NaN with none).</summary>
    public double Altitude { get; private set; } = double.NaN;

    /// <summary>The drive may be switched on now: above <see cref="ShuttleFlight.DriveFloorM"/>, or it is on (to switch it off).</summary>
    public bool DriveAvailable => Spec.Spacecraft && (DriveOn || Altitude > ShuttleFlight.DriveFloorM);

    /// <summary>The way up last step, world axes: the attitude is carried with it as the craft moves round.</summary>
    private Vector3 _up = Vector3.Zero;

    /// <summary>
    /// Above the Space handover: the orbit (or the drive) instead of <see cref="AirlinerFlight.Step"/>. False
    /// below it, where the air flies it; the hand-over back sets the law's path to the one it is on.
    /// </summary>
    private bool SpaceStep(SpaceOrbit space, in FlightInput input, bool gearToggle, float dt, ref FlightMotion m)
    {
        bool toggle = _drive;
        _drive = false;
        if (space.Place is not { } place)
        {
            DriveOn = InSpace = false;
            Altitude = double.NaN;
            return false;
        }
        Altitude = place.Altitude;
        var up = place.Frame.UpAt(place.At);
        if (_up != Vector3.Zero) State.Attitude = ShuttleFlight.Relevel(State.Attitude, _up, up);
        _up = up;

        // nobody at the controls in the drive (a pilot gone, a client dropped): it leaves the drive on an orbit
        if (DriveOn && (toggle || !input.Piloted)) DriveOff(space, place);
        else if (toggle && !DriveOn && place.Altitude > ShuttleFlight.DriveFloorM)
        {
            DriveOn = true;
            space.Drop();
            // it takes over at the speed the craft has (the lever set to match), not from a standstill
            State.Lever = ShuttleFlight.DriveLever(State.Tas, place.Altitude);
            GD.Print(FormattableString.Invariant($"[shuttle] cruise drive on at {place.Altitude / 1000:F1} km"));
        }
        // taken down out of Space the drive drops out: an aircraft again, at the speed it had
        if (DriveOn && !space.Body.SpaceRegime(true, place.Altitude))
        {
            DriveOn = false;
            GD.Print(FormattableString.Invariant($"[shuttle] out of the drive at {place.Altitude / 1000:F1} km, {m.Velocity.Length():F0} m/s"));
        }

        bool running = input.Engine && (Handling == AirlinerHandling.Arcade || State.Lit >= 1f);
        // in the drive the levers set its speed and the rockets are quiet
        float thrust = ShuttleFlight.Levers(Spec, ref State, input.LeverUp, input.LeverDown, !input.Piloted, running && !DriveOn, dt);
        if (DriveOn)
        {
            ShuttleFlight.Jets(ref State, input.Piloted ? input.Stick : Vector2.Zero, drive: true, dt);
            var nose = -State.Attitude.Z;
            State.Velocity = nose * ShuttleFlight.DriveSpeed(State.Lever, place.Altitude);
            Air(place.Altitude, State.Velocity.Length());
        }
        else
        {
            var thrustWorld = -State.Attitude.Z * (thrust / State.Mass);
            if (space.Step(thrustWorld, ShuttleFlight.BallisticCoefficient, m.Velocity, dt) is not { } step)
            {
                if (InSpace) LeaveSpace();
                return false;
            }
            ShuttleFlight.Jets(ref State, input.Piloted ? input.Stick : Vector2.Zero, drive: false, dt);
            State.Velocity = step.Velocity;
            Air(place.Altitude, step.Track.Length());
        }
        InSpace = true;
        State.OnGround = false;
        if (gearToggle) State.GearDown = !State.GearDown;
        State.Gear = Mathf.MoveToward(State.Gear, State.GearDown ? 1f : 0f, dt / Spec.GearTransit);
        ToMotion(ref m);
        return true;
    }

    /// <summary>The read-outs the air gives in Space: the airspeed it has there, and the heat.</summary>
    private void Air(double altitude, float speed)
    {
        float rho = AirlinerFlight.Density((float)altitude);
        State.Tas = speed;
        State.Ias = speed * Mathf.Sqrt(rho / AirlinerFlight.SeaLevelDensity);
        State.Alpha = 0f;
        Heat = ShuttleFlight.Heat(rho, speed);
        HeatPeak = Mathf.Max(HeatPeak, Heat);
    }

    /// <summary>The drive off: a circular orbit where it is, along the nose's heading (the plan's arcade convenience).</summary>
    private void DriveOff(SpaceOrbit space, SpacePlace place)
    {
        DriveOn = false;
        // the levers set the drive's speed: back to idle, or the rockets would fire off the new orbit
        State.Lever = 0f;
        State.Spool = Mathf.Min(State.Spool, Spec.IdleSpool);
        // round the body it is near (#1851): the Earth's or the Moon's
        var earth = space.Body;
        double angle = Core.Orbit.RotationAngle(earth, place.SimTime);
        var r = Core.Orbit.ToEci(place.Frame.ToEcef(place.At), angle);
        var toward = Core.Orbit.ToEci(place.Frame.EcefDirection(place.At, -State.Attitude.Z), angle);
        var orbit = Core.Orbit.Circular(earth, r, toward);
        space.Set(orbit);
        GD.Print(FormattableString.Invariant($"[shuttle] cruise drive off at {place.Altitude / 1000:F1} km: a circular orbit, {orbit.V.Length:F0} m/s"));
    }

    /// <summary>Back in the air below the Space handover: the law holds the path it is on, the heat from the air's own step.</summary>
    private void LeaveSpace()
    {
        InSpace = false;
        DriveOn = false;
        var v = State.Velocity;
        float gamma = v.LengthSquared() > 1f ? Mathf.Asin(Mathf.Clamp(v.Y / v.Length(), -1f, 1f)) : 0f;
        State.PathTarget = State.LastGamma = gamma;
    }

    /// <summary>After an airliner step below Space: re-entry's heat from the air it flies through.</summary>
    private void AirHeat(float altitude)
    {
        if (!Spec.Spacecraft) return;
        Heat = ShuttleFlight.Heat(AirlinerFlight.Density(altitude), State.Tas);
        HeatPeak = Mathf.Max(HeatPeak, Heat);
    }

    /// <summary>
    /// <c>/orbit &lt;km&gt;</c> for the shuttle (#1492): on a circular orbit there, the drive off.
    /// </summary>
    public void SetOrbit(Core.OrbitState state)
    {
        DriveOn = false;
        Space?.Set(state);
        State.OnGround = false;
        State.GearDown = false;
        State.Gear = 0f;
    }

    /// <summary>The anchor frame turned (#1430 H3): the attitude, the yaw and the way up turn with the world.</summary>
    public override void OnOriginShifted(Core.OriginShift shift)
    {
        if (!shift.Turns) return;
        State.Attitude = shift.Transform.Basis * State.Attitude;
        State.Velocity = shift.Direction(State.Velocity);
        State.Yaw += shift.Yaw;
        if (_up != Vector3.Zero) _up = shift.Direction(_up);
    }
}
