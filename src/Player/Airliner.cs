using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>A pressed airliner control (#414): one step's edges, consumed by the next <see cref="Airliner.Fly"/>.</summary>
public enum AirlinerCommand { FlapsDown, FlapsUp, Gear, Speedbrake, ParkingBrake, Lights, Engines, Autopilot }

/// <summary>
/// A heavy aircraft as a ride (#414): the A320, the military freighter (#420) and the AN-124 (#419). Flown by <see cref="AirlinerFlight"/> (its <see cref="State"/>), on the yaw-only body
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
    private bool _gear, _speedbrake, _parking, _engines, _autopilot;

    /// <summary>The pitch trim held this step, −1 nose down .. +1 nose up (Sim, conventional types, #415).</summary>
    public float TrimHeld;

    public Airliner(RideKind kind, AirlinerSpec spec)
    {
        _kind = kind;
        Spec = spec;
        // in Sim a fresh one is cold and dark: start it (#415); Arcade hands it over ready to taxi
        State = AirlinerFlight.Parked(spec, 0f, enginesRunning: Handling == AirlinerHandling.Arcade);
    }

    public static Airliner? For(RideKind kind) => kind switch
    {
        RideKind.A320 => new Airliner(kind, AirlinerCatalog.A320),
        RideKind.Freighter => new Airliner(kind, AirlinerCatalog.Freighter),
        RideKind.An124 => new Airliner(kind, AirlinerCatalog.An124),
        _ => null,
    };

    public static bool IsAirliner(RideKind kind) => kind is RideKind.A320 or RideKind.Freighter or RideKind.An124;

    /// <summary>Every airliner kind, in the picker's order.</summary>
    public static readonly RideKind[] Kinds = { RideKind.A320, RideKind.Freighter, RideKind.An124 };

    public override RideKind Kind => _kind;
    public override string Label => Spec.Name;
    public override string Blurb =>
        "{sprint}/{crouch_slide} thrust levers, {move_forward}{move_back} pitch, {move_left}{move_right} roll and steer, {jump} brakes, {flaps_down}/{flaps_up} flaps, {car_door} gear, {speedbrake} speedbrake, {parking_brake} parking brake";

    /// <summary>What its engines sound like: the A320's turbofans, the freighter's turboprops (#420).</summary>
    public Audio.EngineProfile Sound => Kind == RideKind.Freighter ? Audio.EngineProfile.Turboprop : Audio.EngineProfile.Turbofan;

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

    // ---- what each type is: its hull, its deck, its seats, its doors (#416, #420) ----

    /// <summary>One type's body as the walk and the collision see it, built once per kind from its layout.</summary>
    private sealed record Shape(
        Aabb Fuselage, (Vector3 Centre, Vector3 Size) Parked, (Vector3 Centre, Vector3 Size) Wing,
        VehicleDeck[] Decks, SeatAnchor[] Seats, Vector3 Entry, int Doors, int GDoors, string GDoorsName,
        System.Func<int, Vector3?> Stand);

    private static Shape? _a320, _freighter, _an124;
    private Shape Body => Kind switch
    {
        RideKind.Freighter => _freighter ??= FreighterShape(),
        RideKind.An124 => _an124 ??= An124Shape(),
        _ => _a320 ??= A320Shape(),
    };

    private static Shape A320Shape()
    {
        // the hull: the fuselage's constant section, so a tail-down rotation never touches it
        var fuselage = new Aabb(
            new Vector3(-A320Layout.HalfWidth, A320Layout.BellyY, -A320Layout.BarrelFront + 0.6f),
            new Vector3(A320Layout.HalfWidth * 2f, A320Layout.HalfHeight * 2f, A320Layout.BarrelFront - 0.6f - A320Layout.BarrelRear + 3f));
        // parked it stands on its gear: the box reaches the ground, and runs forward past the front doors
        float front = -(A320Layout.ForwardDoorZ + 1.2f), rear = fuselage.End.Z;
        var parked = (new Vector3(0, A320Layout.TopY * 0.5f, (front + rear) * 0.5f), new Vector3(fuselage.Size.X, A320Layout.TopY, rear - front));
        var wing = (new Vector3(0, A320Layout.WingRootY, -(A320Layout.WingRootLeadingZ + A320Layout.WingTipTrailingZ) * 0.5f),
            new Vector3(A320Layout.WingTipX * 2f, 0.5f, A320Layout.WingRootLeadingZ - A320Layout.WingRootTrailingZ));
        var seats = A320Deck.Seats;
        Vector3? Stand(int i)
        {
            if (i < 0 || i >= seats.Length) return null;
            var hip = seats[i].Hip;
            // the aisle beside a passenger's row; behind a pilot's seat, on its side of the pedestal
            return i < 2
                ? new Vector3(hip.X * 0.9f, seats[i].Floor + 0.05f, -(A320Layout.CockpitWallZ + 0.4f))
                : new Vector3(0f, seats[i].Floor + 0.05f, hip.Z);
        }
        return new Shape(fuselage, parked, wing, new[] { A320Deck.Deck }, seats,
            AircraftMeshBuilder.Flip(new Vector3(A320Layout.HalfWidth, 0f, A320Layout.ForwardDoorZ)), A320Layout.DoorCount,
            1 | 4, "doors", Stand);
    }

    private static Shape FreighterShape()
    {
        // the hull: the hold's section from the ramp's hinge to the flight deck, short of the nose and the tail
        var fuselage = new Aabb(
            new Vector3(-FreighterLayout.HalfWidth, FreighterLayout.BellyY, -FreighterLayout.BarrelFront - 1.0f),
            new Vector3(FreighterLayout.HalfWidth * 2f, FreighterLayout.TopY - FreighterLayout.BellyY, FreighterLayout.BarrelFront + 1.0f - FreighterLayout.RampHingeZ));
        float front = -(FreighterLayout.BarrelFront + 1.0f), rear = -FreighterLayout.RampHingeZ;
        var parked = (new Vector3(0, FreighterLayout.TopY * 0.5f, (front + rear) * 0.5f), new Vector3(FreighterLayout.SponsonOutX * 2f, FreighterLayout.TopY, rear - front));
        var wing = (new Vector3(0, FreighterLayout.WingRootY, -(FreighterLayout.WingRootLeadingZ + FreighterLayout.WingRootTrailingZ) * 0.5f),
            new Vector3(FreighterLayout.WingTipX * 2f, FreighterLayout.WingRootThickness, FreighterLayout.WingRootLeadingZ - FreighterLayout.WingRootTrailingZ));
        return new Shape(fuselage, parked, wing, new[] { FreighterDeck.Deck }, FreighterDeck.Seats,
            AircraftMeshBuilder.Flip(new Vector3(FreighterLayout.HalfWidth, 0f, FreighterLayout.CrewDoorZ)), FreighterLayout.DoorCount,
            1 << FreighterLayout.RampDoor | 1 << FreighterLayout.CrewDoor, "ramp", FreighterDeck.StandSpot);
    }

    private static Shape An124Shape()
    {
        // driven, a belly slab hinge to hinge under the kneeling floor: the hold stays open at both ends
        // for what drives in (a fuselage box met a bus's nose on the ramp), cars pass under the belly
        float low = An124Layout.FloorY - An124Layout.KneelDrop - 0.1f, front = An124Layout.NoseHingeZ, rear = An124Layout.RampHingeZ;
        var belly = new Aabb(new Vector3(-An124Layout.HalfWidth * 0.9f, 1.5f, -front), new Vector3(An124Layout.HalfWidth * 1.8f, low - 1.5f, front - rear));
        // parked it stands on that from the ground; the walls and the roof are extra boxes
        var parked = (new Vector3(0, low * 0.5f, -(front + rear) * 0.5f), new Vector3(An124Layout.FairingOutX * 2f, low, front - rear));
        var wing = (new Vector3(0, An124Layout.WingRootY, -(An124Layout.WingRootLeadingZ + An124Layout.WingRootTrailingZ) * 0.5f),
            new Vector3(An124Layout.WingTipX * 2f, An124Layout.WingRootThickness, An124Layout.WingRootLeadingZ - An124Layout.WingRootTrailingZ));
        return new Shape(belly, parked, wing, new[] { An124Deck.Deck }, An124Deck.Seats,
            AircraftMeshBuilder.Flip(new Vector3(An124Layout.HalfWidth, 0f, An124Layout.CrewDoorZ)), An124Layout.DoorCount,
            15, "cargo doors", An124Deck.StandSpot);
    }

    public override (Aabb Lower, Aabb Upper)? HullBoxes => (Body.Fuselage, new Aabb(Body.Fuselage.Position, Vector3.Zero));

    /// <summary>
    /// Parked it stands on its gear: the box reaches the ground, or it would sink to its belly; and it
    /// runs forward past the front doors (the nose's taper), where a player gets in.
    /// </summary>
    public override (Vector3 Centre, Vector3 Size) ParkedBox => Body.Parked;

    /// <summary>The wing, parked: something to walk under, not through (at the root's height).</summary>
    public override IEnumerable<(Transform3D Pose, Vector3 Centre, Vector3 Size)> ExtraBoxes()
    {
        yield return (Transform3D.Identity, Body.Wing.Centre, Body.Wing.Size);
        if (Kind != RideKind.An124) yield break;
        // the AN-124's flanks and roof over its parked box, hinge to hinge (its ends are the hold's way in)
        float low = Body.Parked.Size.Y, z = Body.Parked.Centre.Z, len = Body.Parked.Size.Z, top = An124Layout.TopY;
        foreach (int side in new[] { 1, -1 })
            yield return (Transform3D.Identity, new Vector3(side * (An124Layout.HalfWidth - 0.25f), (low + top) * 0.5f, z), new Vector3(0.5f, top - low, len));
        yield return (Transform3D.Identity, new Vector3(0, top - 0.3f, z), new Vector3(An124Layout.HalfWidth * 2f, 0.6f, len));
    }

    /// <summary>The cabin and the cockpit (the A320, #416), the hold and the flight deck (the freighter, #420): one deck in the drawn aircraft's frame.</summary>
    public override VehicleDeck[] Decks => Kind == RideKind.A320 && _probeHold != null ? A320ProbeDecks : Body.Decks;

    private static VehicleDeck[]? _a320ProbeDecks;
    private static VehicleDeck[] A320ProbeDecks => _a320ProbeDecks ??= new[] { A320Deck.Deck, _probeHold! };

    /// <summary>
    /// A hold the checks give the A320 (#418, <c>HoldNetProbe.TestHold</c>), set on every peer of the
    /// check before any A320 is made: a test carrier besides the freighter's own hold (#420).
    /// Null in the game: an A320 carries no vehicles.
    /// </summary>
    public static VehicleDeck? ProbeHold
    {
        get => _probeHold;
        set { _probeHold = value; _a320ProbeDecks = null; }
    }
    private static VehicleDeck? _probeHold;

    /// <summary>The captain's seat flies; the first officer's, then the cabin row by row or the troop seats (#416, #420).</summary>
    public override SeatAnchor[] Seats => Body.Seats;

    /// <summary>Where one stands for seat <paramref name="i"/>: the aisle beside it, behind a pilot's seat.</summary>
    public override Vector3? StandSpot(int i) => Body.Stand(i);

    /// <summary>With people aboard and nobody flying it, it stands on its brakes (a passenger cannot taxi it).</summary>
    public override bool Driverless => true;

    /// <summary>How many doors it has (bits of <see cref="DoorsOpen"/>).</summary>
    public int DoorCount => Body.Doors;

    /// <summary>The doors G works at the controls on the ground: the A320's left ones, the freighter's ramp and crew door.</summary>
    public int GDoors => Body.GDoors;
    public string GDoorsName => Body.GDoorsName;

    /// <summary>The doors open only standing still: a door is not opened rolling, let alone flying.</summary>
    public bool MayOpenDoors => State.OnGround && State.Velocity.Length() < 1f;

    /// <summary>
    /// Whether door <paramref name="door"/> may open now: stopped on the ground, or for the freighter's
    /// ramp and para doors in flight below <see cref="DropSpeed"/> (a drop, #420).
    /// </summary>
    public bool MayOpen(int door) => MayOpenDoors
        || Kind == RideKind.Freighter && door != FreighterLayout.CrewDoor && !State.OnGround && State.Ias < DropSpeed;

    /// <summary>The freighter's ramp and para doors open in flight below this indicated airspeed, m/s (150 kt).</summary>
    public const float DropSpeed = 77f;

    /// <summary>The AN-124's kneeling as drawn now, 0 standing .. 1 knelt, eased from its door bit (#419).</summary>
    public float KneelShown;
    /// <summary>The kneeling moved this frame: a parked one's frame is posed again.</summary>
    public bool KneelMoved;

    /// <summary>Kneeling lowers the drawn frame, the deck in it and every sill (#419).</summary>
    public override Vector3 PoseShift => Kind == RideKind.An124 ? Vector3.Down * (KneelShown * An124Layout.KneelDrop) : Vector3.Zero;

    /// <summary>Knelt or kneeling, or rising: it holds its brakes and idles (it rises before it taxis).</summary>
    public bool Kneeling => Kind == RideKind.An124 && (KneelShown > 0f || (DoorsOpen >> An124Layout.KneelDoor & 1) != 0);

    private void EaseKneel(float dt)
    {
        if (Kind != RideKind.An124) return;
        float target = (DoorsOpen >> An124Layout.KneelDoor & 1) != 0 ? 1f : 0f;
        float was = KneelShown;
        KneelShown = Mathf.MoveToward(KneelShown, target, An124MeshBuilder.DoorRate(An124Layout.KneelDoor) * dt);
        KneelMoved = KneelShown != was;
    }

    /// <summary>The doors as the deck's parts see them: the AN-124's ramps have a slope for standing and one for kneeling.</summary>
    public static byte DeckDoors(RideKind kind, byte doors) => kind == RideKind.An124 ? An124Deck.DeckDoors(doors) : doors;

    /// <summary>
    /// Faster than this it is flying, whatever its state says: another peer's copy of a parked aircraft
    /// flying on hands off (its pilot stood up) never steps its flight, so its <c>OnGround</c> is stale (#420).
    /// </summary>
    public const float FlyingSpeed = 30f;

    /// <summary>A door's leaf, open or shut (#416). Shutting always works; opening only when <see cref="MayOpen"/> allows.</summary>
    public void ToggleDoor(int door)
    {
        if (door < 0 || door >= DoorCount) return;
        byte bit = (byte)(1 << door);
        if ((DoorsOpen & bit) == 0 && !MayOpen(door)) return;
        DoorsOpen ^= bit;
    }

    public override Vector3 EntryPoint => Body.Entry;
    public override bool ExitLeft => true;

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) => BuildParkedVisual(riderIndex);

    public override Node3D BuildParkedVisual(int riderIndex)
    {
        var rig = Kind switch
        {
            RideKind.Freighter => AirlinerRig.CreateFreighter(),
            RideKind.An124 => AirlinerRig.CreateAn124(),
            _ => AirlinerRig.CreateA320(Color.FromHsv((riderIndex * 0.37f) % 1f, 0.65f, 0.7f)),
        };
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
            case AirlinerCommand.Engines: _engines = !_engines; break;
            case AirlinerCommand.Autopilot: _autopilot = !_autopilot; break;
        }
    }

    /// <summary>
    /// Left in the air (its pilot stood up in flight, #456): it flies on at the attitude it had. Put down
    /// level and "on the ground" instead, a 10° climb dropped the nose at once and the pilot, stood up in the
    /// pitched cockpit, landed a metre and more above its floor, on the roof at 15°.
    /// </summary>
    public void Aloft(Vector3 angles, Vector3 velocity)
    {
        State.OnGround = false;
        State.Attitude = Basis.FromEuler(angles);
        State.PathTarget = velocity.LengthSquared() > 1f ? Mathf.Asin(Mathf.Clamp(velocity.Normalized().Y, -1f, 1f)) : 0f;
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
        // knelt (or rising) it stands on its brakes at idle: it rises before it taxis (#419)
        bool knelt = Kneeling && State.OnGround;
        var c = new AirlinerFlight.Controls(
            Stick: input.Stick,
            LeverUp: knelt ? 0f : input.LeverUp,
            LeverDown: knelt && State.Lever > 0f ? 1f : input.LeverDown,
            Brake: knelt ? 1f : input.Brake,
            FlapsDelta: _flapsDelta,
            GearToggle: _gear,
            SpeedbrakeCycle: _speedbrake,
            ParkingToggle: _parking,
            EnginesOff: !input.Engine,
            NoPilot: !input.Piloted,
            Handling: Handling,
            StartToggle: _engines,
            AutopilotToggle: _autopilot,
            Trim: TrimHeld);
        _flapsDelta = 0;
        _gear = _speedbrake = _parking = _engines = _autopilot = false;
        Clearance = env.Clearance;
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
        Stick = s.Stick,
        Spool = s.Spool,
        Lights = LightsFor(s),
        Doors = DoorsOpen,
        Airborne = !s.OnGround,
        Lever = s.Lever,
        FlapLever = s.FlapLever,
        SpeedbrakeLever = s.SpeedBrake,
        Lit = (int)s.Lit,
        Reverse = s.Reverse > 0.5f,
        ParkingBrake = s.ParkingBrake,
        GearLever = s.GearDown,
        GearBroken = s.GearBroken,
        Power = s.Battery || s.Lit > 0f,
        Autopilot = s.Autopilot,
        Warning = WarningOf(s),
        Fuel = Spec.FuelCapacity > 0f ? s.Fuel / Spec.FuelCapacity : 0f,
    };

    /// <summary>Height over the ground last step, m: the gear warning's "low" (#421).</summary>
    public float Clearance = 999f;

    /// <summary>The cockpit's red warning (#421), as the HUD's: 1 stall, 2 overspeed (flaps too), 3 low and sinking with the gear not down.</summary>
    private int WarningOf(in AirlinerFlight.State s) =>
        !s.OnGround && s.Alpha > s.AlphaStall - 0.04f ? 1
        : s.Ias > Spec.Vmo || s.FlapLever > 0 && s.Ias > Spec.FlapLimit[s.FlapLever] ? 2
        : !s.OnGround && s.Gear < 1f && Clearance < 230f && s.Velocity.Y < 0f && s.Ias < 93f ? 3 : 0;

    public override void AnimateFlight(Node3D visual, in FlightMotion m, float dt)
    {
        EaseKneel(dt);
        if (visual is AirlinerRig rig) rig.Show(Look(State) with { Spool = m.Spool, Airborne = !State.OnGround || m.Velocity.LengthSquared() > FlyingSpeed * FlyingSpeed }, dt);
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
            | (s.ParkingBrake ? 1 << 17 : 0)
            | (s.Lit >= Spec.Engines ? 1 << 18 : 0)
            | (s.OnGround ? 0 : 1 << 19)
            // the cockpit (#421): power, autopilot, the red warning (2 bits); a float carries 24 bits exactly
            | (s.Battery || s.Lit > 0f ? 1 << 20 : 0)
            | (s.Autopilot ? 1 << 21 : 0)
            | WarningOf(s) << 22;
    }

    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight)
    {
        var look = Look(State);
        // the stick as two quantised halves of one float: −1..1 each, 1/50 steps
        float stick = Mathf.Round((look.Stick.X + 1f) * 50f) * 101f + Mathf.Round((look.Stick.Y + 1f) * 50f);
        // and over it, for the cockpit (#421): the fuel in % and the engines running (under 2^24, exact)
        stick += StickSpan * (Mathf.Clamp(Mathf.RoundToInt(look.Fuel * 100f), 0, 100) + 101 * Mathf.Clamp(look.Lit, 0, 4));
        return new Vector4(State.Spool, State.Lever, stick, Bits());
    }

    /// <summary>The look a remote copy draws from what <see cref="WritePose"/> sent.</summary>
    public static AirlinerLook LookOf(Vector4 pose)
    {
        int bits = (int)pose.W;
        int st = (int)pose.Z;
        int extra = st / StickSpan;
        st %= StickSpan;
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
            Airborne = (bits & 1 << 19) != 0,
            Lever = pose.Y,
            FlapLever = bits & 7,
            SpeedbrakeLever = speedbrake,
            Lit = extra / 101,
            Fuel = extra % 101 / 100f,
            Reverse = (bits & 1 << 8) != 0,
            ParkingBrake = (bits & 1 << 17) != 0,
            GearLever = (bits & 1 << 3) != 0,
            GearBroken = (bits & 1 << 4) != 0,
            Power = (bits & 1 << 20) != 0,
            Autopilot = (bits & 1 << 21) != 0,
            Warning = bits >> 22 & 3,
        };
    }

    /// <summary>The stick's two halves take this many values in the pose's Z; the cockpit's numbers ride over them.</summary>
    private const int StickSpan = 101 * 101;

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
        // left running (#415): its engines still turn, so a Sim pilot need not start them again;
        // Arcade has no start at all, so it is always running (a parked one with its doors open, #417, was not)
        bool running = (bits & 1 << 18) != 0;
        if (running || Handling == AirlinerHandling.Arcade)
        {
            State.Lit = Spec.Engines;
            State.Starting = State.Battery = true;
        }
        DoorsOpen = (byte)(bits >> 13 & 15);
    }
}
