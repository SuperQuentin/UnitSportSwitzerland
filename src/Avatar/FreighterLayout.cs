using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Where everything on the military cargo plane is (#420, the Battle Royale's aircraft, #207): one set
/// of numbers for the drawn model (<see cref="FreighterMeshBuilder"/>), its walkable hold and cockpit
/// and its seats (<see cref="FreighterDeck"/>), so the floor you walk on is the floor that is drawn.
/// A C-130-sized four-turboprop high-wing lifter (29.8 m long, 40.4 m span, 11.7 m tall, a 12.5 m
/// hold 3.1 m wide behind a rear ramp). AUTHORED frame like every avatar mesh: +Z forward, +X the LEFT
/// side, origin on the ground between the main wheels.
/// </summary>
public static class FreighterLayout
{
    // ---- the fuselage: a rounded-square section (a superellipse), constant along the hold ----
    public const float NoseZ = 12.6f, TailZ = -17.2f;
    /// <summary>The constant section runs between these; ahead it tapers into the nose, behind the belly sweeps up over the ramp.</summary>
    public const float BarrelFront = 7.4f, BarrelRear = RampHingeZ;
    public const float BellyY = 0.75f, TopY = 4.75f, HalfWidth = 2.2f;
    public const float CentreY = (BellyY + TopY) * 0.5f, HalfHeight = (TopY - BellyY) * 0.5f;
    /// <summary>The section's exponent: |x/a|^n + |y/b|^n = 1 (2 an oval, higher squarer).</summary>
    public const float Squareness = 3.5f;
    public const float Skin = 0.1f;

    /// <summary>Half width of a superellipse of half width <paramref name="a"/> at vertical fraction <paramref name="h"/> (−1 bottom .. 1 top).</summary>
    public static float Across(float a, float h) =>
        a * Mathf.Pow(Mathf.Max(0f, 1f - Mathf.Pow(Mathf.Min(1f, Mathf.Abs(h)), Squareness)), 1f / Squareness);

    /// <summary>The section at station <paramref name="z"/>: half width, bottom and top.</summary>
    public static (float A, float Bottom, float Top) Section(float z)
    {
        if (z > BarrelFront)
        {
            // the nose: the flight deck's roof slopes down to the radome, the chin rises to it
            float t = Mathf.Clamp((z - BarrelFront) / (NoseZ - BarrelFront), 0f, 1f);
            float top = TopY - 2.15f * Mathf.Pow(t, 1.7f);
            float bottom = BellyY + 1.5f * t * t;
            float a = HalfWidth * Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Pow(t, 2.4f)));
            return (Mathf.Max(0.12f, a), bottom, Mathf.Max(bottom + 0.2f, top));
        }
        if (z < BarrelRear)
        {
            // the tail: the belly up the closed ramp and the upper door, then the cone under the fin
            float u = Mathf.Clamp((BarrelRear - z) / (BarrelRear - TailZ), 0f, 1f);
            float bottom = BellyLine(z);
            float top = z > -10f ? TopY : Mathf.Lerp(TopY, 4.35f, (-10f - z) / (-10f - TailZ));
            float a = HalfWidth * (1f - 0.82f * Mathf.Pow(u, 1.6f));
            return (a, bottom, top);
        }
        return (HalfWidth, BellyY, TopY);
    }

    /// <summary>The underside behind the hold, closed: the ramp, the upper door, the cone (authored y at station z).</summary>
    public static float BellyLine(float z)
    {
        if (z >= RampHingeZ) return BellyY;
        if (z >= RampClosedEndZ) return Mathf.Lerp(BellyY, RampClosedEndBelly, (RampHingeZ - z) / (RampHingeZ - RampClosedEndZ));
        if (z >= UpperDoorHingeZ) return Mathf.Lerp(RampClosedEndBelly, UpperDoorHingeBelly, (RampClosedEndZ - z) / (RampClosedEndZ - UpperDoorHingeZ));
        return Mathf.Lerp(UpperDoorHingeBelly, 3.95f, (UpperDoorHingeZ - z) / (UpperDoorHingeZ - TailZ));
    }

    /// <summary>Outer half width of the skin at (z, y); 0 above or below it.</summary>
    public static float OuterX(float z, float y)
    {
        var (a, b0, b1) = Section(z);
        float h = (y - (b0 + b1) * 0.5f) / ((b1 - b0) * 0.5f);
        return Mathf.Abs(h) >= 1f ? 0f : Across(a, h);
    }

    // ---- the hold, the stairs, the flight deck (authored z) ----
    /// <summary>Hold floor (the ramp's hinge line and the doors' sills) and its ceiling (under the wing box).</summary>
    public const float FloorY = 1.05f, HoldCeilingY = 3.95f;
    /// <summary>Inner half width of the hold's walls, the same at the floor and the shoulder (the walk's walls).</summary>
    public const float HoldHalfWidth = 1.6f;
    /// <summary>The hold's front: the flight deck's floor edge and the stairs up to it.</summary>
    public const float HoldFrontZ = 6.0f, StairFootZ = 3.9f, StairWidth = 1.1f;
    /// <summary>The flight deck floor (0.8 m up: the nose gear's well is under it), its ceiling, its front (the panel).</summary>
    public const float FlightDeckY = 1.85f, FlightDeckCeilingY = 4.4f, PanelZ = 10.35f, FlightDeckFrontZ = 10.75f;
    /// <summary>Captain's (left, +X) and first officer's seats: the hip, authored.</summary>
    public static readonly Vector3 CaptainHip = new(0.62f, FlightDeckY + 0.5f, 9.35f);
    public static readonly Vector3 FirstOfficerHip = new(-0.62f, FlightDeckY + 0.5f, 9.35f);

    // ---- troop seats: red webbing benches along both walls, facing across ----
    public const float FirstTroopZ = 3.55f, TroopPitch = 0.56f;
    public const int TroopRows = 14;
    public const float BenchDepth = 0.48f, BenchHeight = 0.44f;
    /// <summary>Station of troop seat row <paramref name="row"/> (0 forward).</summary>
    public static float TroopZ(int row) => FirstTroopZ - row * TroopPitch;

    // ---- doors: 0 the crew door (front left, folds down into steps), 1 and 2 the para doors (aft,
    // left and right), 3 the ramp with the upper door over it ----
    public const int DoorCount = 4, CrewDoor = 0, ParaDoorL = 1, ParaDoorR = 2, RampDoor = 3;
    public const float CrewDoorZ = 5.05f, ParaDoorZ = -4.95f, DoorWidth = 0.9f, DoorHeight = 1.8f;
    /// <summary>Door <paramref name="i"/>'s centre station and side (+1 left / −1 right, authored x); the ramp has none.</summary>
    public static (float Z, int Side) Door(int i) => i switch
    {
        CrewDoor => (CrewDoorZ, 1),
        ParaDoorL => (ParaDoorZ, 1),
        ParaDoorR => (ParaDoorZ, -1),
        _ => (RampHingeZ, 0),
    };

    /// <summary>
    /// The ramp: the belly's two bottom skin rows between its hinge (across the hold's rear at floor
    /// level) and <see cref="RampClosedEndZ"/>; shut it slopes up aft, open its lip rests on the ground
    /// (about 18°), a slope a car drives up. The upper door is the same rows on aft to its hinge under
    /// the tail, and swings up inside.
    /// </summary>
    public const float RampHingeZ = -6.3f, RampClosedEndZ = -9.5f, RampClosedEndBelly = 2.18f;
    public const float UpperDoorHingeZ = -12.4f, UpperDoorHingeBelly = 3.3f, UpperDoorOpen = 0.75f;
    /// <summary>The fraction of the section's height (from −1, the bottom) where the ramp's rows end: its top face.</summary>
    public const float RampRowTop = -0.85f, RampRowMid = -0.925f;

    /// <summary>The ramp's (or the upper door's) top face at station z, shut: the bottom rows' upper edge.</summary>
    public static float RampTop(float z)
    {
        var (_, b0, b1) = Section(z);
        return (b0 + b1) * 0.5f + RampRowTop * (b1 - b0) * 0.5f;
    }

    /// <summary>The ramp's top face from the hinge to its end, shut: length and angle up aft.</summary>
    public static float RampLength => new Vector2(RampHingeZ - RampClosedEndZ, RampTop(RampClosedEndZ) - FloorY).Length();
    public static float RampClosedAngle => Mathf.Atan2(RampTop(RampClosedEndZ) - FloorY, RampHingeZ - RampClosedEndZ);
    /// <summary>
    /// The ramp's toes: plates hinged at its lip, folded back on it while shut, unfolded along it when
    /// it opens. With them the slope is 11°, not 17°: a car's hull box does not pitch with a deck (it
    /// stays level at 0.45 m), and at 17° its nose met the hold's floor at the hinge (#420).
    /// </summary>
    public const float ToeLength = 2.0f;

    /// <summary>Open, the angle down aft that puts its toes' ends on the ground.</summary>
    public static float RampOpenAngle => -Mathf.Asin(FloorY / (RampLength + ToeLength));
    /// <summary>How far the ramp turns about its hinge from shut to open, radians (its lip going down).</summary>
    public static float RampTravel => RampClosedAngle - RampOpenAngle;
    /// <summary>The open ramp's toes' ends on the ground, authored z.</summary>
    public static float RampToeZ => RampHingeZ - (RampLength + ToeLength) * Mathf.Cos(RampOpenAngle);
    /// <summary>The hold's rear wall, over the ramp's end: nothing to walk into in the tail cone.</summary>
    public const float RearWallZ = RampClosedEndZ - 0.05f;

    /// <summary>The hold a vehicle is carried in (#418): between the benches, from the ramp's hinge to the stairs.</summary>
    public static float BayWidth => 2 * (HoldHalfWidth - BenchDepth) - 0.1f;

    // ---- windows: the flight deck's glass, small round ones along the hold ----
    public const float HoldWindowY = 2.9f;
    public static readonly float[] HoldWindowZ = { 2.6f, 0.0f, -2.6f };

    // ---- the wing: high, on the roof, straight leading edge, 2.5° dihedral ----
    public const float WingRootY = 5.05f, WingRootX = 1.6f, WingTipX = 20.2f;
    public const float WingRootLeadingZ = 3.95f, WingRootTrailingZ = -0.95f;
    public const float WingTipLeadingZ = 3.1f, WingTipTrailingZ = 0.45f;
    public const float WingRootThickness = 0.8f, WingTipThickness = 0.28f;
    public const float Dihedral = 0.044f;
    public static float WingY(float x) => WingRootY + Mathf.Max(0f, Mathf.Abs(x) - WingRootX) * Mathf.Tan(Dihedral);
    public static float WingLeading(float x) => Mathf.Lerp(WingRootLeadingZ, WingTipLeadingZ, Mathf.Clamp((Mathf.Abs(x) - WingRootX) / (WingTipX - WingRootX), 0f, 1f));
    public static float WingTrailing(float x) => Mathf.Lerp(WingRootTrailingZ, WingTipTrailingZ, Mathf.Clamp((Mathf.Abs(x) - WingRootX) / (WingTipX - WingRootX), 0f, 1f));

    /// <summary>Trailing-edge surfaces by span (|x|): inner and outer Fowler flaps, the aileron.</summary>
    public const float InnerFlapFrom = 2.3f, InnerFlapTo = 8.4f, OuterFlapFrom = 8.6f, OuterFlapTo = 14.6f;
    public const float AileronFrom = 14.8f, AileronTo = 19.6f;
    public const float FlapChord = 0.70f;

    // ---- engines: four turboprops, the propellers ahead of the leading edge ----
    public static readonly float[] EngineX = { 10.0f, 4.95f, -4.95f, -10.0f };
    public const float NacelleDrop = 0.55f, PropZ = 6.15f, PropRadius = 2.05f, NacelleRearZ = -1.6f;
    public static float EngineY(int i) => WingY(EngineX[i]) - NacelleDrop;

    // ---- the tail ----
    public const float FinRootFrontZ = -9.6f, FinRootRearZ = -16.6f, FinRootY = 4.45f;
    public const float FinTopFrontZ = -14.2f, FinTopRearZ = -17.05f, FinTopY = 11.7f;
    public const float StabY = 4.25f, StabRootX = 0.4f, StabTipX = 7.95f;
    public const float StabRootFrontZ = -13.1f, StabRootRearZ = -16.95f, StabTipFrontZ = -15.4f, StabTipRearZ = -17.15f;

    // ---- the gear: tandem mains in the sponsons, rising straight up; twin nose wheels folding forward ----
    public const float MainGearX = 2.2f, MainWheelRadius = 0.7f, MainWheelWidth = 0.42f, MainTandem = 0.78f;
    public const float MainStowLift = 0.62f;
    public const float NoseGearZ = 9.8f, NoseWheelRadius = 0.45f, NoseWheelWidth = 0.3f;
    public static readonly Vector3 NoseHinge = new(0f, 1.45f, NoseGearZ + 0.2f);
    /// <summary>The sponsons the mains live in: along the lower flanks.</summary>
    public const float SponsonFrontZ = 2.9f, SponsonRearZ = -2.9f, SponsonOutX = 2.85f, SponsonTopY = 2.15f, SponsonBottomY = 0.95f;
}
