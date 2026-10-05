using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Where everything on the Antonov AN-124 Ruslan is (#419): one set of numbers for the drawn model
/// (<see cref="An124MeshBuilder"/>), its walkable hold, upper deck and cockpit and its seats
/// (<see cref="An124Deck"/>). 69.1 m long, 73.3 m span, a drive-through hold 36 m long, 5.9 m wide and
/// 4.2 m high between the nose ramp under the lifting visor and the rear ramp, the upper deck over its
/// front third behind the cockpit. AUTHORED frame like every avatar mesh: +Z forward, +X the LEFT side,
/// origin on the ground between the main gear, standing (kneeling lowers the whole frame by
/// <see cref="KneelDrop"/>, <c>Airliner.PoseShift</c>).
/// </summary>
public static class An124Layout
{
    // ---- the fuselage: a rounded-square section (superellipse), constant along the barrel ----
    public const float NoseZ = 31.0f, TailZ = -38.1f;
    public const float BarrelFront = 21.5f, BarrelRear = RampHingeZ;
    public const float BellyY = 2.4f, TopY = 10.2f, HalfWidth = 4.0f;
    public const float CentreY = (BellyY + TopY) * 0.5f, HalfHeight = (TopY - BellyY) * 0.5f;
    public const float Squareness = 2.8f, Skin = 0.12f;

    public static float Across(float a, float h) =>
        a * Mathf.Pow(Mathf.Max(0f, 1f - Mathf.Pow(Mathf.Min(1f, Mathf.Abs(h)), Squareness)), 1f / Squareness);

    /// <summary>The section at station <paramref name="z"/>: half width, bottom and top.</summary>
    public static (float A, float Bottom, float Top) Section(float z)
    {
        if (z > BarrelFront)
        {
            // the nose: the cockpit's hump holds the roof up, then it drops to the radome; the chin rises
            float t = Mathf.Clamp((z - BarrelFront) / (NoseZ - BarrelFront), 0f, 1f);
            float top = TopY - 4.4f * Mathf.Pow(Mathf.Max(0f, (t - 0.45f) / 0.55f), 1.5f);
            float bottom = BellyY + 2.6f * t * t;
            float a = HalfWidth * Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Pow(t, 2.6f)));
            return (Mathf.Max(0.15f, a), bottom, Mathf.Max(bottom + 0.3f, top));
        }
        if (z < BarrelRear)
        {
            float u = Mathf.Clamp((BarrelRear - z) / (BarrelRear - TailZ), 0f, 1f);
            float top = z > -26f ? TopY : Mathf.Lerp(TopY, 9.3f, (-26f - z) / (-26f - TailZ));
            return (HalfWidth * (1f - 0.8f * Mathf.Pow(u, 1.5f)), BellyLine(z), top);
        }
        return (HalfWidth, BellyY, TopY);
    }

    /// <summary>The underside behind the hold: up the shut ramp and the rear door to the tail.</summary>
    public static float BellyLine(float z) =>
        z >= RampHingeZ ? BellyY : Mathf.Lerp(BellyY, 8.3f, (RampHingeZ - z) / (RampHingeZ - TailZ));

    public static float OuterX(float z, float y)
    {
        var (a, b0, b1) = Section(z);
        float h = (y - (b0 + b1) * 0.5f) / ((b1 - b0) * 0.5f);
        return Mathf.Abs(h) >= 1f ? 0f : Across(a, h);
    }

    // ---- the hold: drive-through, nose ramp to rear ramp ----
    public const float FloorY = 3.3f, HoldCeilingY = 7.5f, HoldHalfWidth = 2.95f;
    /// <summary>Kneeling lowers every sill this much (3.3 m to 2.45 m): the ramps come down to 12°, the crew door stays in the airstairs' range.</summary>
    public const float KneelDrop = 0.85f;
    /// <summary>The nose ramp's hinge (the hold's front edge) and the visor's cut.</summary>
    public const float NoseHingeZ = 24.0f;
    /// <summary>The visor: rows under the cockpit floor ahead of <see cref="NoseHingeZ"/>, everything ahead of <see cref="VisorCapZ"/>; it swings up about its hinge.</summary>
    public const float VisorCapZ = 28.3f, VisorHingeY = 8.7f, VisorOpen = 2.18f;
    /// <summary>The rear ramp's hinge (the hold's rear edge) and its shut end; the rear door on aft to its hinge.</summary>
    public const float RampHingeZ = -14.0f, RampClosedEndZ = -20.0f, RearDoorHingeZ = -26.0f, RearDoorOpen = 0.6f;
    /// <summary>The ramps' length with their toes: 11.8 m reaches the ground at 12° kneeling (16° standing).</summary>
    public const float RampReach = 11.8f;
    /// <summary>The nose ramp: three plates folding up behind the shut visor, each this long.</summary>
    public static float NosePlate => RampReach / 3f;

    /// <summary>The fraction of the section's height (−1 bottom) where the rear ramp's rows end: the floor's.</summary>
    public static float RampRowTop => (FloorY - CentreY) / HalfHeight;

    /// <summary>The rear ramp's (or the rear door's) top face at z, shut.</summary>
    public static float RampTop(float z)
    {
        var (_, b0, b1) = Section(z);
        return (b0 + b1) * 0.5f + RampRowTop * (b1 - b0) * 0.5f;
    }

    public static float RampLength => new Vector2(RampHingeZ - RampClosedEndZ, RampTop(RampClosedEndZ) - FloorY).Length();
    public static float RampClosedAngle => Mathf.Atan2(RampTop(RampClosedEndZ) - FloorY, RampHingeZ - RampClosedEndZ);
    public static float ToeLength => RampReach - RampLength;

    /// <summary>A ramp's angle down from the floor when open, its toes on the ground: standing or kneeling.</summary>
    public static float OpenSlope(bool kneeling) => Mathf.Asin((FloorY - (kneeling ? KneelDrop : 0f)) / RampReach);
    /// <summary>The rear ramp's travel about its hinge from shut to open (standing), and what kneeling takes off it.</summary>
    public static float RampTravel => RampClosedAngle + OpenSlope(false);
    public static float RampKneelBack => OpenSlope(false) - OpenSlope(true);
    /// <summary>Where an open ramp's toes rest: authored z.</summary>
    public static float RearToeZ(bool kneeling) => RampHingeZ - RampReach * Mathf.Cos(OpenSlope(kneeling));
    public static float NoseToeZ(bool kneeling) => NoseHingeZ + RampReach * Mathf.Cos(OpenSlope(kneeling));
    /// <summary>The hold's rear wall over the ramp's shut end.</summary>
    public const float RearWallZ = RampClosedEndZ - 0.05f;

    /// <summary>The bay a vehicle is carried in (#418): beside the ladder, ramp hinge to ramp hinge.</summary>
    public const float BayLeftX = HoldHalfWidth - 0.05f, BayRightX = -1.45f;

    // ---- doors: 0 the crew door (front left), 1 the visor and nose ramp, 2 the rear ramp and door, 3 kneeling ----
    public const int DoorCount = 4, CrewDoor = 0, NoseDoor = 1, RearDoor = 2, KneelDoor = 3;
    public const float CrewDoorZ = 19.0f, DoorWidth = 1.0f, DoorHeight = 1.95f;

    // ---- the ladder and the upper deck: crew rest and passengers behind the cockpit ----
    public const float UpperFloorY = 7.7f, UpperCeilingY = 9.7f, UpperRearZ = 15.0f, UpperWallX = 2.55f;
    /// <summary>The ladder: a steep ship's ladder along the right wall, its foot aft, rising 45° to the upper deck's rear edge.</summary>
    public const float LadderX = -2.0f, LadderWidth = 0.9f;
    public const float LadderFootZ = UpperRearZ - (UpperFloorY - FloorY);
    public const float CabinFrontZ = 22.9f, CockpitFloorFrontZ = 28.0f, PanelZ = 27.2f;
    public const int CabinRows = 6;
    public const float CabinPitch = 0.95f, FirstRowZ = 21.9f;
    public static readonly float[] CabinSeatX = { 1.55f, 0.85f, -0.85f, -1.55f };
    public static float RowZ(int row) => FirstRowZ - row * CabinPitch;
    public static readonly Vector3 PilotHip = new(0.65f, UpperFloorY + 0.5f, 26.0f);
    public static readonly Vector3 CopilotHip = new(-0.65f, UpperFloorY + 0.5f, 26.0f);
    /// <summary>The flight engineer's seat, right behind the copilot, facing his panel on the right wall.</summary>
    public static readonly Vector3 EngineerHip = new(-1.2f, UpperFloorY + 0.5f, 24.4f);

    // ---- the wing: high, swept, anhedral ----
    public const float WingRootY = 9.75f, WingRootX = 3.2f, WingTipX = 36.65f;
    public const float WingRootLeadingZ = 6.0f, WingRootTrailingZ = -5.8f, WingTipLeadingZ = -13.2f, WingTipTrailingZ = -16.9f;
    public const float WingRootThickness = 1.3f, WingTipThickness = 0.35f, Dihedral = -0.055f;
    public static float WingSpanT(float x) => Mathf.Clamp((Mathf.Abs(x) - WingRootX) / (WingTipX - WingRootX), 0f, 1f);
    public static float WingY(float x) => WingRootY + Mathf.Max(0f, Mathf.Abs(x) - WingRootX) * Mathf.Tan(Dihedral);
    public static float WingLeading(float x) => Mathf.Lerp(WingRootLeadingZ, WingTipLeadingZ, WingSpanT(x));
    public static float WingTrailing(float x) => Mathf.Lerp(WingRootTrailingZ, WingTipTrailingZ, WingSpanT(x));
    public const float InnerFlapFrom = 4.0f, InnerFlapTo = 15.0f, OuterFlapFrom = 15.3f, OuterFlapTo = 27.0f;
    public const float AileronFrom = 27.3f, AileronTo = 35.0f, FlapChord = 0.72f;

    // ---- engines: four D-18T turbofans on pylons under the wing ----
    public static readonly float[] EngineX = { 20.0f, 10.8f, -10.8f, -20.0f };
    public const float EngineRadius = 1.35f, EngineDrop = 2.0f, EngineLength = 7.6f;
    public static float EngineY(int i) => WingY(EngineX[i]) - EngineDrop;
    public static float EngineFrontZ(int i) => WingLeading(EngineX[i]) + 3.6f;

    // ---- the tail: conventional, the stabiliser low on the fuselage ----
    public const float FinRootFrontZ = -28.0f, FinRootRearZ = -37.6f, FinRootY = 9.6f;
    public const float FinTopFrontZ = -35.6f, FinTopRearZ = -39.6f, FinTopY = 21.3f;
    public const float StabY = 9.2f, StabRootX = 0.6f, StabTipX = 12.4f;
    public const float StabRootFrontZ = -28.5f, StabRootRearZ = -36.6f, StabTipFrontZ = -34.6f, StabTipRearZ = -37.8f;

    // ---- the gear: five twin-wheel legs a side in the fairings, rising straight up; two twin nose legs ----
    public const float MainGearX = 2.75f, MainWheelRadius = 0.62f, MainWheelWidth = 0.42f, MainGearLift = 1.45f;
    public static readonly float[] MainLegZ = { 4.8f, 2.4f, 0f, -2.4f, -4.8f };
    public const float FairingFrontZ = 6.4f, FairingRearZ = -6.4f, FairingOutX = 4.6f, FairingBottomY = 1.35f, FairingTopY = 4.2f;
    public const float NoseGearZ = 22.0f, NoseGearX = 0.75f, NoseWheelRadius = 0.6f, NoseStowAngle = 1.6f;
    public static readonly Vector3 NoseHinge = new(0f, 2.2f, NoseGearZ - 0.4f);
    /// <summary>The outside buttons stand on the fairings' ends at this height (standing).</summary>
    public const float OutsideButtonY = 1.75f;
}
