using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// The flying saucer (#861, docs/notes/player/ufo.md): a vehicle <see cref="Flyer"/> flown by
/// <see cref="UfoFlight"/> (arcade hover: holds its height, moves in any direction, turns on the spot
/// to the look, boost, a wobble), landing on its legs with a ramp to walk out on, and a tractor beam
/// (<see cref="TractorBeams"/>). Its state lives here, like an airliner's; the four numbers a remote
/// copy needs travel in <see cref="Rideable.WritePose"/>.
/// </summary>
public sealed class Ufo : Flyer
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
        // the pad is 0.8 m across
        const float pad = 0.4f;
        float top = UfoMeshBuilder.Belly + 0.1f;
        for (int i = 0; i < UfoRig.LegCount; i++)
        {
            var (hinge, foot) = UfoRig.LegDown(i);
            var lo = new Vector3(Mathf.Min(hinge.X, foot.X) - pad, foot.Y, Mathf.Min(hinge.Z, foot.Z) - pad);
            var hi = new Vector3(Mathf.Max(hinge.X, foot.X) + pad, top, Mathf.Max(hinge.Z, foot.Z) + pad);
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
