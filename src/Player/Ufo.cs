using System;
using Godot;
using UnitSport.Terrain.Format;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// The flying saucer (#861, docs/notes/player/ufo.md): a vehicle <see cref="Flyer"/> flown by
/// <see cref="UfoFlight"/> (arcade hover: holds its height, moves in any direction, turns on the spot
/// to the look, boost, a wobble), landing on its legs with a ramp to walk out on, and a tractor beam
/// (<see cref="TractorBeams"/>). Its state lives here, like an airliner's; the four numbers a remote
/// copy needs travel in <see cref="Rideable.WritePose"/>.
/// </summary>
public sealed class Ufo : Flyer, ISpacecraft, IAimable
{
    public UfoFlight.State State = UfoFlight.Parked(0f);

    public override RideKind Kind => RideKind.Ufo;
    public override string Label => "UFO";
    public override string Blurb =>
        "Look to turn, {move_forward}{move_left}{move_back}{move_right} to fly, {jump} up, {crouch_slide} down, {sprint} boost; {tractor_beam} beam, {car_door} ramp / legs, {lights_toggle} lights";

    public override bool LookSteers => true;
    public override bool IsVehicle => true;
    /// <summary>
    /// The capsule the craft flies as: 3 m round, so it lands on its middle and its rim (4.6 m) may
    /// clip a slope, as a plane's wings do.
    /// </summary>
    public override float BodyRadius => 3.0f;
    public override float BodyHeight => 6.0f;
    /// <summary>A wall at boost speed is a crash; a knock at cruising speed is not.</summary>
    public override float CrashSpeed => 24f;
    public override float CameraDistance => 17f;
    public override float CameraHeight => 4.5f;
    public override float CameraPivot => 3f;
    public override bool CameraFollowsPitch => false;
    public override Vector3 Pivot => new(0, UfoMeshBuilder.RimY, 0);
    public override (float Calm, float Fast) Thrill => (25f, 60f);
    public override float BaseFov => 72f;
    public override float MaxFov => 92f;
    public override float FovSpeed => 70f;
    public override float DismountSpeed => 3f;
    public override float EyeHeight => UfoMeshBuilder.DomeBase + 1.1f;
    public override Vector3 FirstPersonEye => UfoRig.SeatHips[0] + new Vector3(0, 0.75f, -0.05f);

    /// <summary>Getting in and out at the foot of the ramp (node space, −Z forward).</summary>
    public override Vector3 EntryPoint => AircraftMeshBuilder.Flip(UfoMeshBuilder.RampFoot);
    public override Vector3? ExitSpot => EntryPoint;

    /// <summary>
    /// Parked, the hull only (legs and ramp left out): a person walks under the belly and up the
    /// ramp, and the box stops above their head.
    /// </summary>
    public override (Vector3 Centre, Vector3 Size) ParkedBox =>
        (new Vector3(0, (UfoMeshBuilder.Belly + 0.05f + UfoMeshBuilder.DomeBase + 1.2f) / 2f, 0),
         new Vector3(8.4f, UfoMeshBuilder.DomeBase + 1.2f - UfoMeshBuilder.Belly - 0.05f, 8.4f));

    /// <summary>
    /// The three legs, parked: a post round each strut, from its pad up into the hull box. The hull
    /// box alone starts at the belly, and a parked saucer sank 2.25 m onto it (#1006). Axis-aligned:
    /// a settled vehicle's extra boxes are put back by their centre only (<c>VehicleBody</c>).
    /// </summary>
    public override System.Collections.Generic.IEnumerable<(Transform3D Pose, Vector3 Centre, Vector3 Size)> ExtraBoxes()
    {
        // the pad is 0.8 m across, the hinge's block 0.5
        const float pad = 0.4f, block = 0.25f;
        float top = UfoMeshBuilder.Belly + 0.1f;
        for (int i = 0; i < UfoRig.LegCount; i++)
        {
            var (hinge, foot) = UfoRig.LegDown(i);
            var lo = new Vector3(Mathf.Min(hinge.X - block, foot.X - pad), foot.Y, Mathf.Min(hinge.Z - block, foot.Z - pad));
            var hi = new Vector3(Mathf.Max(hinge.X + block, foot.X + pad), top, Mathf.Max(hinge.Z + block, foot.Z + pad));
            yield return (Transform3D.Identity, (lo + hi) * 0.5f, hi - lo);
        }
    }

    private static SeatAnchor[]? _seats;
    /// <summary>Two seats under the dome, the pilot's on the left.</summary>
    public override SeatAnchor[] Seats => _seats ??= new[]
    {
        new SeatAnchor(0, UfoRig.SeatHips[0], 0.15f, UfoMeshBuilder.DomeBase),
        new SeatAnchor(0, UfoRig.SeatHips[1], 0.15f, UfoMeshBuilder.DomeBase),
    };

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) =>
        UfoRig.Create(HumanPalette.ForRider(riderIndex) with { Outfit = outfit });

    public override Node3D BuildParkedVisual(int riderIndex) => UfoRig.Create(null);

    /// <summary>The beam's apex in the body's frame: the hatch, on the axis under the belly.</summary>
    public static Vector3 HatchOffset => UfoMeshBuilder.Hatch;

    /// <summary>The free height under the legs, m, measured by the body (a ray down) before each step.</summary>
    public float Below { get; set; } = 999f;

    public override void Begin(ref FlightMotion m, Vector3 velocity, float yaw)
    {
        base.Begin(ref m, velocity, yaw);
        // a craft taken out of the menu or back from the world stands on its legs, ramp as it was
        var keep = State;
        State = UfoFlight.Parked(yaw) with { Ramp = keep.Ramp, Lights = keep.Lights, Velocity = velocity };
        if (velocity.LengthSquared() > 1f) State = State with { Landed = false, Legs = 0f, Ramp = 0f };
        m.Spool = State.Hum;
    }

    public override FlightEvent Fly(in FlightInput input, in FlightEnv env, float dt, ref FlightMotion m)
    {
        // what the world did to the craft since last step (a wall, the ground) is the truth
        State.Velocity = m.Velocity;
        State.Yaw = m.Yaw;
        float below = Mathf.Min(Below, env.Clearance);
        UfoFlight.Controls c;
        if (input.Piloted)
            c = new UfoFlight.Controls(input.Stick, input.Up, input.Down, input.Boost || input.Effort && input.ViewPitch == null,
                // a script with no look (ViewPitch null) and no heading of its own (ViewYaw 0) keeps the heading
                input.ViewPitch == null && input.ViewYaw == 0f ? m.Yaw : input.ViewYaw,
                input.Hatch, input.Beam, input.Lights);
        else
            // nobody aboard: an empty saucer sinks gently onto its legs and stays there, beam off
            c = new UfoFlight.Controls(Vector2.Zero, 0f, env.OnFloor ? 0f : 0.3f, false, m.Yaw);
        // between bodies (#1851): the cruise drive; above the Space handover its orbit (#1430 H5); below, the arcade hover
        if (DriveStep(input, c, dt)) { }
        else if (!SpaceFly(input.Piloted ? c : c with { Up = 0f, Down = 0f }, dt, ref m))
            State = UfoFlight.Step(State, c, new UfoFlight.Env(env.OnFloor, below), dt);
        if (!input.Piloted) State.Beam = false;

        m.Velocity = State.Velocity;
        m.Yaw = State.Yaw;
        m.Attitude = UfoFlight.Attitude(State);
        m.Bank = State.Tilt.Y;
        m.Control = State.Tilt.X;
        // a parked one goes quiet (and its vehicle body to sleep)
        m.Spool = input.Piloted ? State.Hum : State.Landed ? 0f : State.Hum;
        return FlightEvent.None;
    }

    // ---- in Space: an orbit in doubles (#1430 H5) --------------------------------------------------

    /// <summary>The orbit seam (<see cref="SpaceOrbit"/>), shared with the shuttle (#1492).</summary>
    public SpaceOrbit Space { get; } = new("ufo");
    SpaceOrbit? ISpacecraft.Space => Space;

    /// <summary>The body's place before this step (null where there is no origin: no Space).</summary>
    public SpacePlace? Place { get => Space.Place; set => Space.Place = value; }

    /// <summary>Its orbit, in doubles in the inertial frame (<see cref="Core.Orbit"/>), while in the Space regime; null below it.</summary>
    public Core.OrbitState? Orbit => Space.Orbit;

    /// <summary>Times a contact moved the body off its orbit and the orbit restarted from it (the orbit check wants none in free flight).</summary>
    public int Contacts => Space.Contacts;

    /// <summary>
    /// Mass over drag area (m / Cd·A), kg/m²: a 15 t saucer 9.2 m across. In the thin air at the top of the
    /// Space regime it slows: 11 m/s² at 7 km/s and 60 km, nothing above 150 km.
    /// </summary>
    public const double BallisticCoefficient = 230;

    /// <summary>Seconds the orbit has been stepped, summed in doubles (the orbit check times its period by it).</summary>
    public double OrbitSeconds => Space.Seconds;

    /// <summary>Puts the craft on <paramref name="state"/> (an inertial orbit) from its next step: <c>/orbit</c>, a probe, a bot.</summary>
    public void SetOrbit(Core.OrbitState state, Core.CelestialBody? round = null) => Space.Set(state, round);

    /// <summary>
    /// One step in the Space regime (<see cref="SpaceOrbit.Step"/>): the orbit under GM/r², the pilot's thrust
    /// and the air's drag, the body sent where it says. False below the regime.
    /// </summary>
    private bool SpaceFly(in UfoFlight.Controls c, float dt, ref FlightMotion m)
    {
        var thrustWorld = UfoFlight.SpaceThrust(c, State.Yaw, Vector3.Up);
        if (Space.Step(thrustWorld, BallisticCoefficient, m.Velocity, dt) is not { } step) return false;
        State = UfoFlight.SpaceStep(State, c, thrustWorld, step.Track, dt);
        State.Velocity = step.Velocity;
        return true;
    }

    // ---- the cruise drive (#1851): the shuttle's, the saucer's way to the Moon and beyond -------------

    /// <summary>The cruise drive is on: the saucer carried at v = lever · d / τ (<see cref="ShuttleFlight.DriveSpeed"/>).</summary>
    public bool DriveOn { get; private set; }

    /// <summary>The drive's lever, 0..1: the climb and descend keys move it (half travel a second), boost puts it full.</summary>
    public float DriveLever { get; private set; }

    /// <summary>The distance the drive measures last step: to the nearest surface of any body (NaN out of Space).</summary>
    public double DriveDistance { get; private set; } = double.NaN;

    /// <summary>The target hold (<see cref="SpectatorTargets"/>): the way the drive goes while held; null: the look's.</summary>
    public Vector3? Aim { get; set; }

    /// <summary>The way the drive carries it now (world axes): the look's, or the hold's.</summary>
    public Vector3 Nose => _driveDir != Vector3.Zero ? _driveDir : UfoFlight.Heading(State.Yaw);

    /// <summary>In Space: on its orbit or in the drive (the target cue is the pilot's then).</summary>
    public bool InSpace => Space.Orbit != null || DriveOn;

    /// <summary>The drive may be thrown now: above the Space handover of the body it is near, or it is on.</summary>
    public bool DriveAvailable => DriveOn || Space.Place is { } p && p.Altitude > Space.BodyHere.SpaceEnterM;

    private Vector3 _driveDir;
    private bool _driveToggle;

    /// <summary>The drive switch (Z / D-pad ↑ in its seat, a script, a bot): taken by the next step.</summary>
    public void ToggleDrive() => _driveToggle = true;

    /// <summary>The drive's lever set directly (a script, a bot), 0..1.</summary>
    public void SetDriveLever(float lever) => DriveLever = Mathf.Clamp(lever, 0f, 1f);

    /// <summary>How fast the drive's way turns to the look or the hold, rad/s.</summary>
    private const float DriveTurn = 0.6f;

    /// <summary>
    /// One step of the drive; false when it is not on (the orbit or the hover flies it). On above the handover at the speed
    /// it has; off, a circular orbit where it is along its way; taken below the handover it drops out at its speed, and the
    /// hover's dampers brake it.
    /// </summary>
    private bool DriveStep(in FlightInput input, in UfoFlight.Controls c, float dt)
    {
        bool toggle = _driveToggle;
        _driveToggle = false;
        if (Space.Place is not { } place)
        {
            DriveOn = false;
            DriveDistance = double.NaN;
            return false;
        }
        var body = Space.BodyHere;
        DriveDistance = SurfaceReach.Of(place.Frame, place.At, place.Altitude);
        if (toggle && DriveOn)
        {
            DriveOn = false;
            var dir = _driveDir;
            double angle = Core.Orbit.RotationAngle(body, place.SimTime);
            var r = Core.Orbit.ToEci(place.Frame.ToEcef(place.At), angle);
            var toward = Core.Orbit.ToEci(place.Frame.EcefDirection(place.At, dir), angle);
            var orbit = Core.Orbit.Circular(body, r, toward);
            Space.Set(orbit, body);
            GD.Print(FormattableString.Invariant($"[ufo] cruise drive off at {place.Altitude / 1000:F1} km: a circular orbit round {body.Name}, {orbit.V.Length:F0} m/s"));
            return false;
        }
        if (toggle && !DriveOn && place.Altitude > body.SpaceEnterM)
        {
            DriveOn = true;
            Space.Drop();
            float speed = State.Velocity.Length();
            _driveDir = speed > 1f ? State.Velocity / speed : UfoFlight.Heading(State.Yaw);
            DriveLever = ShuttleFlight.DriveLever(speed, DriveDistance);
            GD.Print(FormattableString.Invariant($"[ufo] cruise drive on at {place.Altitude / 1000:F1} km"));
        }
        if (DriveOn && !body.SpaceRegime(true, place.Altitude))
        {
            DriveOn = false;
            GD.Print(FormattableString.Invariant($"[ufo] out of the drive at {place.Altitude / 1000:F1} km, {State.Velocity.Length():F0} m/s"));
        }
        if (!DriveOn) return false;
        if (!input.Piloted)
        {
            // nobody aboard: off onto an orbit, as the shuttle
            _driveToggle = true;
            return true;
        }
        DriveLever = c.Boost ? 1f : Mathf.Clamp(DriveLever + (c.Up - c.Down) * 0.5f * dt, 0f, 1f);
        // the way: the hold's aim, or where the pilot looks (a script with no look keeps its way)
        var want = Aim ?? (input.ViewPitch is { } pitch
            ? new Vector3(-Mathf.Sin(input.ViewYaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), -Mathf.Cos(input.ViewYaw) * Mathf.Cos(pitch))
            : _driveDir);
        _driveDir = TurnTo(_driveDir, want, DriveTurn * dt);
        State.Velocity = _driveDir * ShuttleFlight.DriveSpeed(DriveLever, DriveDistance);
        State.Yaw += UfoFlight.TurnStep(State.Yaw, c.ViewYaw, dt);
        State.Landed = false;
        State.Hum = Mathf.Lerp(State.Hum, 0.4f + 0.6f * DriveLever, Mathf.Min(1f, 3f * dt));
        return true;
    }

    /// <summary>A unit direction turned toward another by at most <paramref name="maxAngle"/>.</summary>
    private static Vector3 TurnTo(Vector3 from, Vector3 to, float maxAngle)
    {
        to = to.Normalized();
        if (from == Vector3.Zero) return to;
        var axis = from.Cross(to);
        float sin = axis.Length(), angle = Mathf.Atan2(sin, from.Dot(to));
        if (angle <= maxAngle) return to;
        if (sin < 1e-6f) axis = Mathf.Abs(from.Y) < 0.9f ? from.Cross(Vector3.Up) : from.Cross(Vector3.Right);
        return from.Rotated(axis.Normalized(), maxAngle).Normalized();
    }

    // ---- switches set directly: scripts (the trailer director), bots, the server ------------------

    /// <summary>The tractor beam on or off, as a held switch rather than a press (a script, a bot).</summary>
    public void SetBeam(bool on) => State.Beam = on;

    /// <summary>The rim lamps on or off.</summary>
    public void SetLights(bool on) => State.Lights = on;

    /// <summary>The ramp's wish on the ground: open, shut, or null for automatic (opens once settled).</summary>
    public void SetRamp(bool? open) => State.RampWanted = open;

    /// <summary>The legs' wish in the air: down, up, or null for automatic (down near the ground).</summary>
    public void SetLegs(bool? down) => State.LegsWanted = down;

    // ---- what is drawn, and what the others get --------------------------------------------

    /// <summary>Bits of <see cref="Vector4.W"/> in the pose.</summary>
    private const int BeamBit = 1, LightsOffBit = 2, LandedBit = 4;

    /// <summary>
    /// The look a remote copy needs: X the hum, Y the legs' fold (0 = down), Z the ramp shut (0 = open),
    /// W bits (beam, lights off, landed). Zero everywhere is a parked saucer: legs down, ramp open,
    /// lamps on, quiet.
    /// </summary>
    public static Vector4 Pack(in UfoFlight.State s) => new(s.Hum, 1f - s.Legs, 1f - s.Ramp,
        (s.Beam ? BeamBit : 0) | (s.Lights ? 0 : LightsOffBit) | (s.Landed ? LandedBit : 0));

    public static UfoRig.Look LookOf(Vector4 pose)
    {
        int bits = Mathf.RoundToInt(pose.W);
        return new UfoRig.Look(pose.X, 1f - pose.Y, 1f - pose.Z, (bits & BeamBit) != 0, (bits & LightsOffBit) == 0,
            (bits & LandedBit) != 0);
    }

    /// <summary>The beam is on in this pose (any peer, from the replicated <c>Anim</c>): what the beam service reads.</summary>
    public static bool BeamOn(Vector4 pose) => (Mathf.RoundToInt(pose.W) & BeamBit) != 0;

    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) => Pack(State);

    public override void AnimateFlight(Node3D visual, in FlightMotion m, float dt)
    {
        // the hum is the spool: the pilot's step sets it, a parked one's body replicates it (quiet on the ground)
        if (visual is UfoRig rig) rig.Show(LookOf(Pack(State)) with { Hum = m.Spool }, dt);
    }

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        if (visual is UfoRig rig) rig.Show(LookOf(pose), dt);
    }

    // ---- parked: the ramp and lamps as they were left -----------------------------------------

    /// <summary>The ramp shut (bit 0) and the lamps off (bit 1), for a parked one's <c>VehicleState.Flags</c>.</summary>
    public int PackFlags() => (State.Ramp < 0.5f ? 1 : 0) | (State.Lights ? 0 : 2);

    public void UnpackFlags(int flags)
    {
        State.Ramp = (flags & 1) != 0 ? 0f : 1f;
        State.RampWanted = (flags & 1) != 0 ? false : null;
        State.Lights = (flags & 2) == 0;
    }
}
