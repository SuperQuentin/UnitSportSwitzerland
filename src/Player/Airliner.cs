using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>A pressed airliner control (#414): one step's edges, consumed by the next <see cref="Airliner.Fly"/>.</summary>
public enum AirlinerCommand { FlapsDown, FlapsUp, Gear, Speedbrake, ParkingBrake, Lights }

/// <summary>
/// A heavy aircraft as a ride (#414): the A320 now, the AN-124 and the military freighter later
/// (#419, #420). Flown by <see cref="AirlinerFlight"/> (its <see cref="State"/>), on the yaw-only body
/// that stands on its main wheels; <see cref="Flyer.Fly"/> maps that state onto the
/// <see cref="FlightMotion"/> every craft shares. Unlike the light plane it collides as drawn (its
/// fuselage box, <see cref="HullBoxes"/>), keeps its flaps, gear and brakes when parked
/// (<see cref="PackFlags"/>) and shows them on every peer (<see cref="WritePose"/>).
/// </summary>
public sealed class Airliner : Flyer
{
    public AirlinerSpec Spec { get; }
    private readonly RideKind _kind;

    /// <summary>The live aircraft: its motion, levers, flaps, gear, brakes.</summary>
    public AirlinerFlight.State State;

    /// <summary>How it flies (Settings -> Vehicles "Airliner handling", <c>--airliner</c>).</summary>
    public static AirlinerHandling Handling => Core.GameSettings.Current.Airliner;

    /// <summary>Lights the pilot switched (landing lights on L); nav, beacon and strobes are automatic.</summary>
    public bool LandingLights = true;

    /// <summary>Door leaves the crew opened, one bit per door (#416); 0 until it has doors.</summary>
    public byte DoorsOpen;

    private int _flapsDelta;
    private bool _gear, _speedbrake, _parking;

    public Airliner(RideKind kind, AirlinerSpec spec)
    {
        _kind = kind;
        Spec = spec;
        State = AirlinerFlight.Parked(spec, 0f);
    }

    public static Airliner? For(RideKind kind) => kind switch
    {
        RideKind.A320 => new Airliner(kind, AirlinerCatalog.A320),
        _ => null,
    };

    public static bool IsAirliner(RideKind kind) => kind == RideKind.A320;

    public override RideKind Kind => _kind;
    public override string Label => Spec.Name;
    public override string Blurb =>
        "{sprint}/{crouch_slide} thrust levers, {move_forward}{move_back} pitch, {move_left}{move_right} roll and steer, {jump} brakes, {flaps_down}/{flaps_up} flaps, {car_door} gear, {speedbrake} speedbrake, {parking_brake} parking brake";

    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override float MaxHealth => 400f;
    /// <summary>A gear leg is the capsule: what stands on the runway between the main wheels.</summary>
    public override float BodyRadius => 1.4f;
    public override float BodyHeight => 3.2f;
    public override float CrashSpeed => 16f;
    public override float DismountSpeed => 1f;
    public override float CameraDistance => Spec.Length * 1.35f;
    public override float CameraHeight => Spec.Length * 0.22f;
    public override float CameraPivot => 4f;
    public override Vector3 Pivot => Vector3.Zero;
    public override (float Calm, float Fast) Thrill => (70f, 140f);
    public override float BaseFov => 65f;
    public override float MaxFov => 78f;
    public override float FovSpeed => 160f;

    // ---- the hull: the fuselage's constant section, so a tail-down rotation never touches it ----
    private static readonly Aabb Fuselage = new(
        new Vector3(-A320Layout.HalfWidth, A320Layout.BellyY, -A320Layout.BarrelFront + 0.6f),
        new Vector3(A320Layout.HalfWidth * 2f, A320Layout.HalfHeight * 2f, A320Layout.BarrelFront - 0.6f - A320Layout.BarrelRear + 3f));

    public override (Aabb Lower, Aabb Upper)? HullBoxes => (Fuselage, new Aabb(Fuselage.Position, Vector3.Zero));

    /// <summary>Parked it stands on its gear: the box reaches the ground, or it would sink to its belly.</summary>
    public override (Vector3 Centre, Vector3 Size) ParkedBox =>
        (Fuselage.GetCenter() with { Y = A320Layout.TopY * 0.5f }, Fuselage.Size with { Y = A320Layout.TopY });

    /// <summary>The wing, parked: something to walk under, not through (at the root's underside).</summary>
    public override IEnumerable<(Transform3D Pose, Vector3 Centre, Vector3 Size)> ExtraBoxes()
    {
        float y = A320Layout.WingRootY;
        yield return (Transform3D.Identity, new Vector3(0, y, -(A320Layout.WingRootLeadingZ + A320Layout.WingTipTrailingZ) * 0.5f),
            new Vector3(A320Layout.WingTipX * 2f, 0.5f, A320Layout.WingRootLeadingZ - A320Layout.WingRootTrailingZ));
    }

    public override Vector3 EntryPoint => AircraftMeshBuilder.Flip(new Vector3(A320Layout.HalfWidth, 0f, A320Layout.ForwardDoorZ));
    public override bool ExitLeft => true;

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) => BuildParkedVisual(riderIndex);

    public override Node3D BuildParkedVisual(int riderIndex)
    {
        var rig = AirlinerRig.CreateA320(Color.FromHsv((riderIndex * 0.37f) % 1f, 0.65f, 0.7f));
        rig.Show(Look(State), 0f);
        return rig;
    }

    /// <summary>A pilot's key press, applied on the next step.</summary>
    public void Command(AirlinerCommand c)
    {
        switch (c)
        {
            case AirlinerCommand.FlapsDown: _flapsDelta++; break;
            case AirlinerCommand.FlapsUp: _flapsDelta--; break;
            case AirlinerCommand.Gear: _gear = !_gear; break;
            case AirlinerCommand.Speedbrake: _speedbrake = !_speedbrake; break;
            case AirlinerCommand.ParkingBrake: _parking = !_parking; break;
            case AirlinerCommand.Lights: LandingLights = !LandingLights; break;
        }
    }

    public override void Begin(ref FlightMotion m, Vector3 velocity, float yaw)
    {
        State.Velocity = velocity;
        State.Yaw = yaw;
        if (State.OnGround || State.Attitude == default) State.Attitude = new Basis(Vector3.Up, yaw);
        State.Damage = 0f;
        // put down in the world (got into, spawned): its first contact is settling, not a landing
        if (State.OnGround) State.Settle = 2f;
        ToMotion(ref m);
    }

    public override FlightEvent Fly(in FlightInput input, in FlightEnv env, float dt, ref FlightMotion m)
    {
        // the body's word for how it moves: a wall, a slope, the solver (FootPlayer adopts real velocity)
        State.Velocity = m.Velocity;
        var c = new AirlinerFlight.Controls(
            Stick: input.Stick,
            LeverUp: input.LeverUp,
            LeverDown: input.LeverDown,
            Brake: input.Brake,
            FlapsDelta: _flapsDelta,
            GearToggle: _gear,
            SpeedbrakeCycle: _speedbrake,
            ParkingToggle: _parking,
            EnginesOff: !input.Engine,
            NoPilot: !input.Piloted,
            Handling: Handling);
        _flapsDelta = 0;
        _gear = _speedbrake = _parking = false;
        var ev = AirlinerFlight.Step(Spec, ref State, c, new AirlinerFlight.Env(env.OnFloor, env.Altitude), dt);
        ToMotion(ref m);
        return ev == AirlinerFlight.Event.Crashed ? FlightEvent.Crashed : FlightEvent.None;
    }

    private void ToMotion(ref FlightMotion m)
    {
        m.Velocity = State.Velocity;
        m.Yaw = State.Yaw;
        m.Attitude = State.Attitude;
        m.Bank = AirlinerFlight.BankOf(State.Attitude);
        m.Control = State.Lever;
        m.Spool = State.Spool;
        m.Airspeed = State.Tas;
    }

    /// <summary>Damage the airframe took since the last call (hard landings, scrapes): the vehicle's health pays it.</summary>
    public float TakeDamage()
    {
        float d = State.Damage;
        State.Damage = 0f;
        return d;
    }

    // ---- what others see -------------------------------------------------------------------

    private AirlinerLights LightsFor(in AirlinerFlight.State s)
    {
        var l = AirlinerLights.None;
        if (s.Spool > 0.05f) l |= AirlinerLights.Nav | AirlinerLights.Beacon;
        if (!s.OnGround || s.Lever > 0.5f) l |= AirlinerLights.Strobe;
        if (LandingLights && s.Spool > 0.05f) l |= AirlinerLights.Landing;
        return l;
    }

    /// <summary>The parts as drawn from the live state (owner, parked).</summary>
    public AirlinerLook Look(in AirlinerFlight.State s) => new()
    {
        Gear = s.Gear,
        Flaps = s.Flaps,
        Spoilers = s.Spoilers,
        Stick = new Vector2(s.RollRate / Spec.MaxRollRate, s.PitchRate / Spec.MaxPitchRate).LimitLength(1f),
        Spool = s.Spool,
        Lights = LightsFor(s),
        Doors = DoorsOpen,
    };

    public override void AnimateFlight(Node3D visual, in FlightMotion m, float dt)
    {
        if (visual is AirlinerRig rig) rig.Show(Look(State) with { Spool = m.Spool }, dt);
    }

    /// <summary>Bits of <see cref="WritePose"/>'s W and <see cref="PackFlags"/>: the levers, so remote copies travel the parts at the aircraft's own rates.</summary>
    public int Bits()
    {
        var s = State;
        bool ground = s.Spoilers > s.SpeedBrake * 0.5f + 0.25f;
        return (s.FlapLever & 7)
            | (s.GearDown ? 1 << 3 : 0)
            | (s.GearBroken ? 1 << 4 : 0)
            | (s.SpeedBrake & 3) << 5
            | (ground ? 1 << 7 : 0)
            | (s.Reverse > 0.5f ? 1 << 8 : 0)
            | ((int)LightsFor(s) & 15) << 9
            | (DoorsOpen & 15) << 13
            | (s.ParkingBrake ? 1 << 17 : 0);
    }

    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight)
    {
        var look = Look(State);
        // the stick as two quantised halves of one float: −1..1 each, 1/50 steps
        float stick = Mathf.Round((look.Stick.X + 1f) * 50f) * 101f + Mathf.Round((look.Stick.Y + 1f) * 50f);
        return new Vector4(State.Spool, State.Lever, stick, Bits());
    }

    /// <summary>The look a remote copy draws from what <see cref="WritePose"/> sent.</summary>
    public static AirlinerLook LookOf(Vector4 pose)
    {
        int bits = (int)pose.W;
        int st = (int)pose.Z;
        var stick = new Vector2(st / 101 / 50f - 1f, st % 101 / 50f - 1f);
        int speedbrake = bits >> 5 & 3;
        return new AirlinerLook
        {
            Gear = (bits & 1 << 3) != 0 && (bits & 1 << 4) == 0 ? 1f : 0f,
            Flaps = bits & 7,
            Spoilers = (bits & 1 << 7) != 0 ? 1f : speedbrake * 0.5f,
            Stick = stick,
            Spool = pose.X,
            Lights = (AirlinerLights)(bits >> 9 & 15),
            Doors = (byte)(bits >> 13 & 15),
        };
    }

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        if (visual is AirlinerRig rig) rig.Show(LookOf(pose), dt);
    }

    // ---- parked ----------------------------------------------------------------------------

    /// <summary>The levers it was left with, for <c>VehicleState.Flags</c>.</summary>
    public int PackFlags() => Bits();

    /// <summary>A parked state's levers: the gear (and whether it gave way), flaps, speedbrake, parking brake, doors.</summary>
    public void UnpackFlags(int bits)
    {
        State.FlapLever = Mathf.Clamp(bits & 7, 0, Spec.FlapSettings - 1);
        State.Flaps = State.FlapLever;
        State.GearDown = (bits & 1 << 3) != 0 || bits == 0;
        State.GearBroken = (bits & 1 << 4) != 0;
        State.Gear = State.GearDown && !State.GearBroken ? 1f : 0f;
        State.SpeedBrake = bits >> 5 & 3;
        State.ParkingBrake = (bits & 1 << 17) != 0;
        DoorsOpen = (byte)(bits >> 13 & 15);
    }
}
