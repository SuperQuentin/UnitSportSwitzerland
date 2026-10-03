using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Where everything on the A320 is (#414, #416): one set of numbers for the drawn model
/// (<see cref="A320MeshBuilder"/>), its walkable cabin and its seats, so the floor you walk on is
/// the floor that is drawn. AUTHORED frame like every avatar mesh: +Z forward, +X the LEFT side
/// (port: <see cref="MeshScratch.Build()"/> turns it to −Z forward, +X right), origin on the ground
/// between the main wheels. Metres, rounded from Airbus' airport planning manual: 37.57 m long,
/// 34.1 m span (35.8 over the sharklets), 11.76 m tall, track 7.59 m, wheelbase 12.64 m.
/// </summary>
public static class A320Layout
{
    // ---- the fuselage: a 3.95 × 4.14 m oval, constant from the cabin's front to the tail cone ----
    public const float NoseZ = 17.6f, TailZ = -19.97f;
    /// <summary>The constant section runs between these; ahead it tapers into the nose, behind into the upswept tail cone.</summary>
    public const float BarrelFront = 11.6f, BarrelRear = -9.0f;
    public const float BellyY = 2.0f, CentreY = 4.07f, HalfWidth = 1.975f, HalfHeight = 2.07f;
    public const float TopY = CentreY + HalfHeight;
    /// <summary>The skin's thickness, drawn and walked.</summary>
    public const float Skin = 0.09f;

    /// <summary>Cabin floor (the door sills), ceiling, and the cabin's half width at the floor and at the shoulder.</summary>
    public const float FloorY = 3.3f, CeilingY = 5.45f;
    public const float InnerHalfWidthFloor = 1.77f, InnerHalfWidthShoulder = 1.86f;

    /// <summary>Outer half width of the skin at height <paramref name="y"/> on the constant section (0 above or below it).</summary>
    public static float HalfWidthAt(float y)
    {
        float t = (y - CentreY) / HalfHeight;
        return Mathf.Abs(t) >= 1f ? 0f : HalfWidth * Mathf.Sqrt(1f - t * t);
    }

    // ---- the flight deck and the cabin, front to back (authored z) ----
    /// <summary>The cockpit: its back wall with the door, its floor's front end under the windscreen.</summary>
    public const float CockpitWallZ = 14.55f, CockpitFrontZ = 16.9f;
    /// <summary>Captain's (left, +X) and first officer's seats: the hip, authored.</summary>
    public static readonly Vector3 CaptainHip = new(0.52f, FloorY + 0.48f, 15.55f);
    public static readonly Vector3 FirstOfficerHip = new(-0.52f, FloorY + 0.48f, 15.55f);
    /// <summary>The cockpit door in its wall: centred, this wide.</summary>
    public const float CockpitDoorWidth = 0.75f;

    /// <summary>Forward galley (right side, across from L1's lavatory) and the forward lavatory (left), z ranges.</summary>
    public const float ForwardGalleyFrom = 13.75f, ForwardGalleyTo = 14.5f;
    public const float ForwardLavFrom = 11.75f, ForwardLavTo = 12.75f;

    /// <summary>Seat rows: the first row's seat front, the pitch (31"), the count; 3-3 either side of the aisle.</summary>
    public const float FirstRowZ = 11.45f, RowPitch = 0.787f;
    public const int Rows = 27;
    /// <summary>Seat centres across the cabin, authored x (+ the left side): window, middle, aisle.</summary>
    public static readonly float[] SeatX = { 1.39f, 0.93f, 0.47f, -0.47f, -0.93f, -1.39f };
    public const float SeatWidth = 0.44f, SeatHeight = 0.45f, SeatBackHeight = 1.15f, AisleHalfWidth = 0.24f;

    /// <summary>Front of row <paramref name="row"/>'s seat cushion (0-based), authored z.</summary>
    public static float RowZ(int row) => FirstRowZ - row * RowPitch;

    /// <summary>Aft lavatories (both sides), the aft door area, the aft galley, the rear pressure bulkhead.</summary>
    public const float AftLavFrom = -11.0f, AftLavTo = -10.45f;
    public const float AftGalleyFrom = -13.4f, AftGalleyTo = -12.15f;
    public const float RearBulkheadZ = -13.5f;

    // ---- doors: L1, R1, L2, R2 (index order); the two overwing exits each side are drawn only ----
    public const float DoorWidth = 0.81f, DoorHeight = 1.85f;
    public const float ForwardDoorZ = 13.2f, AftDoorZ = -11.6f;
    public static readonly float[] OverwingExitZ = { 0.65f, -0.15f };
    /// <summary>Door <paramref name="i"/>: its centre station and side (+1 left / −1 right, authored x).</summary>
    public static (float Z, int Side) Door(int i) => (i < 2 ? ForwardDoorZ : AftDoorZ, i % 2 == 0 ? 1 : -1);
    public const int DoorCount = 4;

    // ---- windows: a band each side at this pitch, skipping the doors and the exits' frames ----
    public const float WindowLow = 4.12f, WindowHigh = 4.47f, WindowPitch = 0.533f, WindowWidth = 0.24f;
    public const float FirstWindowZ = 12.3f, LastWindowZ = -10.7f;

    // ---- the wing: low, 25° sweep, 5° dihedral ----
    public const float WingRootY = 2.35f, WingRootX = 1.9f, WingTipX = 17.05f;
    public const float WingRootLeadingZ = 5.2f, WingRootTrailingZ = -2.0f;
    /// <summary>The trailing edge's kink (the "yehudi"), outboard of which it runs parallel to the leading edge.</summary>
    public const float WingKinkX = 6.3f, WingKinkTrailingZ = -1.1f;
    public const float WingTipLeadingZ = -2.6f, WingTipTrailingZ = -4.1f;
    public const float WingRootThickness = 0.75f, WingTipThickness = 0.2f;
    public const float Dihedral = 0.087f;   // 5°
    /// <summary>Height of the wing's mid plane at span <paramref name="x"/> (authored, either side).</summary>
    public static float WingY(float x) => WingRootY + (Mathf.Abs(x) - WingRootX) * Mathf.Tan(Dihedral);
    public const float SharkletHeight = 2.4f;

    /// <summary>Trailing-edge surfaces by span (authored |x|): inner flap, outer flap, aileron; spoilers ahead of the flaps.</summary>
    public const float InnerFlapFrom = 2.0f, InnerFlapTo = 6.2f, OuterFlapFrom = 6.4f, OuterFlapTo = 12.7f;
    public const float AileronFrom = 12.9f, AileronTo = 16.3f;
    public const float SpoilerFrom = 3.0f, SpoilerTo = 12.4f;

    // ---- engines: CFM56 nacelles under the wing ----
    public const float EngineX = 5.75f, EngineY = 1.62f, EngineInletZ = 7.7f, EngineExhaustZ = 3.2f;
    public const float NacelleRadius = 1.05f, FanRadius = 0.86f;
    public const float FanZ = 7.35f;

    // ---- the tail ----
    public const float FinRootFrontZ = -12.6f, FinRootRearZ = -19.0f, FinRootY = 5.9f;
    public const float FinTopFrontZ = -17.3f, FinTopRearZ = -19.6f, FinTopY = 11.76f;
    public const float StabY = 5.25f, StabRootX = 0.6f, StabTipX = 6.22f;
    public const float StabRootFrontZ = -15.2f, StabRootRearZ = -18.7f, StabTipFrontZ = -18.1f, StabTipRearZ = -19.7f;

    // ---- the gear: main legs under the wing roots, folding inward; the nose leg forward ----
    public const float MainGearX = 3.795f, WheelSpread = 0.46f, MainWheelRadius = 0.57f, MainWheelWidth = 0.4f;
    public const float NoseGearZ = 12.64f, NoseWheelRadius = 0.38f, NoseWheelWidth = 0.25f;
    /// <summary>Retraction hinges, authored: the mains fold inward about a fore-and-aft line, the nose forward about a transverse one.</summary>
    public static readonly Vector3 MainHinge = new(MainGearX - 0.1f, 2.55f, 0.15f);
    public static readonly Vector3 NoseHinge = new(0f, 2.45f, NoseGearZ + 0.25f);
}
