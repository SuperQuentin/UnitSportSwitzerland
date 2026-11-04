using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Where everything on the space shuttle orbiter is (#1492): one set of numbers for the drawn model
/// (<see cref="ShuttleMeshBuilder"/>), its cockpit (<see cref="CockpitLayout.Shuttle"/>) and its seats
/// (<c>Airliner.ShuttleShape</c>). AUTHORED frame like every avatar mesh: +Z forward, +X the LEFT side
/// (the commander's), origin on the ground between the main wheels; <see cref="AircraftMeshBuilder.Flip"/>
/// turns it to −Z forward. Metres, rounded from the orbiter's: 37.24 m long, 23.79 m span, ~17.3 m tall
/// to the fin's tip on its gear, a 5.2 m wide fuselage, a double delta (the glove swept 81°, the wing
/// 45°), main gear track 6.91 m, wheelbase 22.4 m.
/// </summary>
public static class ShuttleLayout
{
    // ---- the fuselage: boxy under its chine, the payload bay doors' arch over it ----
    /// <summary>The nose cap's tip and the aft fuselage's base (the engines stand out behind it).</summary>
    public const float NoseZ = 29.2f, BaseZ = -4.3f;
    /// <summary>The crew module's aft bulkhead (the payload bay's front) and the payload bay's aft end.</summary>
    public const float CabinRearZ = 19.9f, BayRearZ = 1.6f;
    /// <summary>Ahead of this the section closes into the nose; behind it, it is constant to the base.</summary>
    public const float NoseFromZ = 21.5f;
    /// <summary>The flat belly, the half width at the chine, the chine's height (the widest), the doors' crown.</summary>
    public const float BellyY = 2.0f, HalfWidth = 2.6f, ChineY = 5.4f, TopY = 8.0f;
    /// <summary>The nose cap's middle, and where the grey nose cap (reinforced carbon) starts.</summary>
    public const float NoseTipY = 3.75f, NoseCapZ = 28.3f;
    /// <summary>The skin's thickness round the crew module.</summary>
    public const float Skin = 0.1f;

    // ---- the crew module: the mid deck under the flight deck ----
    public const float MidDeckY = 3.0f, FlightDeckY = 5.2f;
    /// <summary>The flight deck's aft stations and the mid deck's forward lockers.</summary>
    public const float AftStationZ = 20.3f, MidDeckFrontZ = 23.4f;
    /// <summary>The commander (left, +X, flies) and the pilot side by side.</summary>
    public static readonly Vector3 CommanderHip = new(0.6f, FlightDeckY + 0.5f, 22.9f);
    public static readonly Vector3 PilotHip = new(-0.6f, FlightDeckY + 0.5f, 22.9f);
    /// <summary>The two mission specialists' seats behind them on the flight deck.</summary>
    public static readonly Vector3[] SpecialistHips = { new(0.3f, FlightDeckY + 0.5f, 21.55f), new(-0.65f, FlightDeckY + 0.5f, 21.55f) };
    /// <summary>Three seats on the mid deck, facing forward.</summary>
    public static readonly Vector3[] MidDeckHips = { new(0.85f, MidDeckY + 0.48f, 22.4f), new(0f, MidDeckY + 0.48f, 22.4f), new(-0.85f, MidDeckY + 0.48f, 22.4f) };

    /// <summary>The forward panel's aft face, its middle and the glareshield over it.</summary>
    public const float PanelZ = 23.85f, PanelY = FlightDeckY + 0.67f, GlareY = FlightDeckY + 1.08f;
    /// <summary>The centre console between the pilots (the thrust levers' quadrant on it).</summary>
    public const float PedestalZ0 = 22.85f, PedestalZ1 = 23.75f, PedestalTop = FlightDeckY + 0.6f, PedestalHalf = 0.19f;
    /// <summary>The overhead panel: its switches' plate.</summary>
    public const float OverheadY = FlightDeckY + 2.15f, OverheadZ = 22.75f;
    /// <summary>
    /// The cruise drive's guarded toggle on the centre console, forward of the levers on the commander's
    /// side (its pivot, authored): the VR switch for the drive grips it.
    /// </summary>
    public static readonly Vector3 DriveSwitch = new(0.1f, PedestalTop, 23.5f);

    // ---- the side hatch: round, on the left of the mid deck; the one door ----
    public const float HatchZ = 21.0f, HatchY = 3.95f, HatchRadius = 0.5f;
    public const int DoorCount = 1;

    // ---- the wing: a double delta, its underside flat with the belly ----
    /// <summary>The wing's root (in the fuselage's side), the kink between the glove and the wing, the tip.</summary>
    public const float WingRootX = 2.5f, WingKinkX = 4.4f, WingTipX = 11.895f;
    /// <summary>Leading edge: the glove from the root (81°) to the kink, then 45° to the tip.</summary>
    public const float GloveRootZ = 19.6f, KinkZ = 7.6f, TipLeadingZ = 0.1f;
    /// <summary>Trailing edge (the elevons'), swept slightly forward.</summary>
    public const float RootTrailingZ = -4.1f, TipTrailingZ = -3.0f;
    public const float WingRootThickness = 1.9f, WingTipThickness = 0.4f;
    /// <summary>3.5°.</summary>
    public const float Dihedral = 0.061f;
    /// <summary>The elevons: their chord, the inboard pair's span and the outboard pair's (|x|).</summary>
    public const float ElevonChord = 1.7f;
    public const float ElevonInFrom = 3.0f, ElevonInTo = 7.15f, ElevonOutFrom = 7.25f, ElevonOutTo = 11.4f;

    // ---- the tail: the fin with its split rudder (the speedbrake), the OMS pods either side ----
    public const float FinRootY = 7.9f, FinTopY = 17.3f;
    public const float FinRootFrontZ = 1.6f, FinRootRearZ = -5.4f, FinTopFrontZ = -6.0f, FinTopRearZ = -8.0f;
    /// <summary>The rudder's span (heights) and its hinge line's chord fraction.</summary>
    public const float RudderFrom = 9.0f, RudderTo = 16.9f, RudderF = 0.6f;
    public const float OmsX = 1.75f, OmsY = 8.05f, OmsFrontZ = 2.6f, OmsRearZ = -4.6f, OmsHalfWidth = 0.78f, OmsHalfHeight = 0.62f;

    // ---- the three main engines in a triangle, the body flap under them ----
    /// <summary>Each engine's axis (x, y): the top one first, then the left and the right.</summary>
    public static readonly Vector2[] Engines = { new(0f, 6.55f), new(1.33f, 4.35f), new(-1.33f, 4.35f) };
    public const float NozzleThroatZ = -4.75f, NozzleExitZ = -7.55f, NozzleExitRadius = 1.15f;
    public const float BodyFlapHalf = 2.25f, BodyFlapY = 2.2f, BodyFlapLength = 2.1f;

    // ---- the gear: the mains and the nose leg swing forward into the belly ----
    public const float MainGearX = 3.455f, MainWheelRadius = 0.565f, MainWheelWidth = 0.34f, MainWheelSpread = 0.21f;
    public const float MainHingeY = 2.9f;
    public const float NoseGearZ = 22.4f, NoseWheelRadius = 0.405f, NoseWheelWidth = 0.24f, NoseWheelSpread = 0.19f, NoseHingeY = 3.15f;
    /// <summary>How far the legs turn forward to stow, rad.</summary>
    public const float MainStow = 1.5f, NoseStow = 1.55f;
}
