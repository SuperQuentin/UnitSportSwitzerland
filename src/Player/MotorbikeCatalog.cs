using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// One motorbike as numbers: the published figures of the real machine, each entry's source or
/// assumption next to it in <see cref="MotorbikeCatalog"/>. Adding a bike is adding an entry there;
/// <see cref="Motorbike"/> and <see cref="Motorcyclist"/> read everything from this.
/// </summary>
public sealed record MotorbikeSpec
{
    /// <summary>Assigned by <see cref="MotorbikeCatalog"/> from the entry's position; never set by hand.</summary>
    public RideKind Kind { get; init; }
    public required string Label { get; init; }
    public required string Blurb { get; init; }
    /// <summary>The picker's folders (#410): the brand, then the model when it comes in several variants.</summary>
    public string Brand { get; init; } = "";
    public string Family { get; init; } = "";
    /// <summary>Shape for the mesh: bodywork style, engine shape, tyre sizes, geometry, contact points, colours.</summary>
    public required MotoLook Look { get; init; }
    /// <summary>What it sounds like (<see cref="EngineProfile.For"/>, at this bike's idle and redline).</summary>
    public EngineLayout Engine { get; init; } = EngineLayout.Crossplane4;

    /// <summary>Wet (ready to ride) mass of the bike, kg; <see cref="Motorbike.RiderMass"/> is added on top.</summary>
    public float WetMass { get; init; }
    /// <summary>Centre of mass height with the rider aboard, m, and the rear axle's share of the weight.</summary>
    public float CgHeight { get; init; }
    public float RearShare { get; init; } = 0.5f;
    /// <summary>Peak tyre friction on dry tarmac (street sport tyres ~1.1-1.2, dual-sport ~0.95).</summary>
    public float Grip { get; init; } = 1.15f;
    /// <summary>
    /// How much of a road tyre's off-tarmac grip loss this bike's tyres claw back, 0..1
    /// (<see cref="Motorbike.SurfaceGrip"/>): 0 for sport tyres, ~0.3 for the dual-purpose tyres an
    /// adventure bike leaves the factory on, more for knobblies.
    /// </summary>
    public float OffroadTyre { get; init; }
    /// <summary>Official colour names for the model years (data; the mesh wears <see cref="MotoLook"/>'s livery).</summary>
    public string Colours { get; init; } = "";
    /// <summary>Maximum lean the tyres and the ground clearance allow, radians.</summary>
    public float MaxLean { get; init; }
    /// <summary>Drag area Cd·A with the rider in position, m².</summary>
    public float DragArea { get; init; }
    /// <summary>Best braking, m/s² (both brakes, ABS), before the stoppie limit.</summary>
    public float BrakeDecel { get; init; }

    public float IdleRpm { get; init; }
    public float PeakRpm { get; init; }
    public float Redline { get; init; }
    /// <summary>Clutch-slip launch rpm: what the engine is held at while the clutch takes up.</summary>
    public float LaunchRpm { get; init; }
    /// <summary>Crank torque curve (rpm, N·m), ascending.</summary>
    public required (float Rpm, float Nm)[] Torque { get; init; }
    public required float[] Gears { get; init; }
    public float Primary { get; init; }
    public float FinalDrive { get; init; }
    /// <summary>Dual-clutch box: shifts itself with no drive cut.</summary>
    public bool Dct { get; init; }
    /// <summary>Clutchless up-shifts (a quickshifter): a short cut rather than a rider's clutch-and-lift.</summary>
    public bool QuickShifter { get; init; }
    /// <summary>
    /// A scooter's belt CVT (#410): the ratio runs from <c>Gears[0]</c> (low) to <see cref="CvtHigh"/>
    /// on its own, holding the engine near <see cref="CvtRpm"/> under load; <see cref="Primary"/> and
    /// <see cref="FinalDrive"/> are the fixed reductions around it.
    /// </summary>
    public bool Cvt { get; init; }
    public float CvtHigh { get; init; }
    /// <summary>The engine speed the variator holds flat out (peak power); part throttle holds less.</summary>
    public float CvtRpm { get; init; }

    /// <summary>Published figures the model is checked against (<c>--motocheck</c>).</summary>
    public float RefZeroTo100 { get; init; }
    public float RefTopKmh { get; init; }

    /// <summary>Drive cut per up-shift, s. Every box shifts itself here (there is no clutch input); how long it takes is the difference.</summary>
    public float ShiftCut => Dct || Cvt ? 0.02f : QuickShifter ? 0.07f : 0.25f;

    public float TorqueAt(float rpm)
    {
        if (rpm <= Torque[0].Rpm) return Torque[0].Nm;
        for (int i = 1; i < Torque.Length; i++)
            if (rpm <= Torque[i].Rpm)
                return Mathf.Lerp(Torque[i - 1].Nm, Torque[i].Nm, (rpm - Torque[i - 1].Rpm) / Mathf.Max(1f, Torque[i].Rpm - Torque[i - 1].Rpm));
        return Torque[^1].Nm;
    }
}

/// <summary>
/// Every motorbike. <b>Append-only</b>: a bike's <see cref="RideKind"/> is <c>First + its index
/// here</c>, replicated as an int, so inserting or reordering renumbers every bike after it on every
/// peer. Motorbikes own <see cref="RideKind"/> 64..95 (the first 32 entries) and then 124..187
/// (#410: the Africa Twins filled the first range); the next other mount is 188.
/// </summary>
public static partial class MotorbikeCatalog
{
    public const int First = 64, Last = 95;
    /// <summary>The second range, entries 32 onwards.</summary>
    public const int First2 = 124, Last2 = 187;
    private const int FirstRangeSize = Last - First + 1;

    public static readonly IReadOnlyList<MotorbikeSpec> All = Number(new[]
    {
        // ---- 64 ----
        // Yamaha YZF-R1 (2020+). Yamaha EU spec sheet: 998 cc crossplane inline four, 200 PS
        // (147.1 kW) at 13,500 rpm, 112.4 N·m at 11,500 rpm; primary 65/43 = 1.512, gears
        // 2.600/2.176/1.842/1.579/1.381/1.250, final 41/16 = 2.563; wet 201 kg; wheelbase 1,405 mm;
        // rake 24°, trail 102 mm; seat 855 mm; tyres 120/70ZR17 and 190/55ZR17; quickshifter.
        // Top speed 299 km/h (the Japanese makers' limiter), magazine 0-100 km/h ~3.1 s.
        // Assumed (not published): the torque curve between its peaks, redline 14,000 (limiter),
        // CG 0.62 m with a tucked rider, 50/50 weight split with the rider, CdA 0.34 m² tucked (this
        // lands the drag-limited top speed ~2% over the limiter, as the unrestricted bike is), 55°
        // lean on road tyres, braking 10.5 m/s² (~37 m from 100 km/h in tests), launch at 7,000 rpm.
        new MotorbikeSpec
        {
            Label = "Yamaha YZF-R1 (2020+)",
            Brand = "Yamaha", Family = "YZF-R1",
            Blurb = "998 cc crossplane four, 200 PS, 201 kg: {throttle} gas, {brake} brake, {move_left}/{move_right} lean. Wheelies if you let it",
            Engine = EngineLayout.Crossplane4,
            Look = new MotoLook
            {
                Style = MotoStyle.Sport, EngineShape = MotoEngineShape.InlineFour,
                Wheelbase = 1.405f, RakeDeg = 24f, Trail = 0.102f, ForkLength = 0.62f,
                FrontTyre = "120/70ZR17", RearTyre = "190/55ZR17", FrontTravel = 0.12f, RearTravel = 0.12f,
                // clip-ons low and far forward, rearsets high: the rider folds onto the tank
                Seat = new Vector3(0, 0.855f, -0.26f), Grip = new Vector3(0.33f, 0.83f, 0.38f), Peg = new Vector3(0.17f, 0.39f, -0.2f),
                Paint = new Color(0.05f, 0.2f, 0.72f), Trim = new Color(0.9f, 0.91f, 0.93f),
                Frame = new Color(0.12f, 0.12f, 0.14f), Wheel = new Color(0.1f, 0.25f, 0.8f),
            },
            WetMass = 201f, CgHeight = 0.62f, RearShare = 0.5f, Grip = 1.2f, MaxLean = Mathf.DegToRad(55f),
            DragArea = 0.34f, BrakeDecel = 10.5f,
            IdleRpm = 1300f, PeakRpm = 13500f, Redline = 14000f, LaunchRpm = 7000f,
            Torque = new (float, float)[] { (1300f, 45f), (3000f, 62f), (5000f, 74f), (7000f, 86f), (9000f, 99f), (10500f, 108f), (11500f, 112.4f), (12500f, 110f), (13500f, 104.1f), (14000f, 97f) },
            Gears = new[] { 2.6f, 2.176f, 1.842f, 1.579f, 1.381f, 1.25f }, Primary = 65f / 43f, FinalDrive = 41f / 16f,
            QuickShifter = true,
            RefZeroTo100 = 3.1f, RefTopKmh = 299f,
        },
        // ---- 65 ----
        // Ducati Monster (2021+, 937 Testastretta 11°). Ducati spec sheet: 111 hp (82 kW) at
        // 9,250 rpm, 93 N·m at 6,500 rpm; 188 kg kerb (90% fuel, the published "wet"); wheelbase
        // 1,474 mm; rake 24°, trail 93 mm; seat 820 mm; tyres 120/70ZR17 and 180/55ZR17; Ducati
        // Quick Shift standard. Assumed (uncertain): gear ratios carried over from the 821/939 box
        // (37/15, 30/17, 28/20, 26/22, 24/23, 23/24), primary 1.85 and final 43/15; redline ~10,500;
        // the torque curve between the peaks; CG 0.66 m with an upright rider, 49% rear; CdA
        // 0.44 m² naked and upright; top speed ~230 km/h and 0-100 km/h ~3.4 s from magazine tests;
        // 50° lean, braking 10 m/s², launch at 5,000 rpm.
        new MotorbikeSpec
        {
            Label = "Ducati Monster (2021+)",
            Brand = "Ducati", Family = "Monster",
            Blurb = "937 cc 90° V-twin, 111 hp, 188 kg, upright bars: {throttle} gas, {brake} brake, {move_left}/{move_right} lean",
            Engine = EngineLayout.VTwin90,
            Look = new MotoLook
            {
                Style = MotoStyle.Naked, EngineShape = MotoEngineShape.VTwin,
                Wheelbase = 1.474f, RakeDeg = 24f, Trail = 0.093f, ForkLength = 0.64f,
                FrontTyre = "120/70ZR17", RearTyre = "180/55ZR17", FrontTravel = 0.13f, RearTravel = 0.14f,
                // wide bar high and close, pegs under the hips: sits up
                Seat = new Vector3(0, 0.82f, -0.2f), Grip = new Vector3(0.38f, 1.0f, 0.3f), Peg = new Vector3(0.16f, 0.35f, -0.06f),
                Paint = new Color(0.78f, 0.05f, 0.05f), Trim = new Color(0.12f, 0.12f, 0.13f),
                Frame = new Color(0.75f, 0.06f, 0.06f), Wheel = new Color(0.08f, 0.08f, 0.09f),
            },
            WetMass = 188f, CgHeight = 0.66f, RearShare = 0.51f, Grip = 1.15f, MaxLean = Mathf.DegToRad(50f),
            DragArea = 0.44f, BrakeDecel = 10f,
            IdleRpm = 1350f, PeakRpm = 9250f, Redline = 10500f, LaunchRpm = 5000f,
            Torque = new (float, float)[] { (1350f, 50f), (2500f, 62f), (4000f, 77f), (5500f, 89f), (6500f, 93f), (7500f, 91f), (8500f, 88f), (9250f, 84.7f), (10000f, 77f), (10500f, 68f) },
            Gears = new[] { 37f / 15f, 30f / 17f, 28f / 20f, 26f / 22f, 24f / 23f, 23f / 24f }, Primary = 1.85f, FinalDrive = 43f / 15f,
            QuickShifter = true,
            RefZeroTo100 = 3.4f, RefTopKmh = 230f,
        },
        // ======== Honda Africa Twin, every variant (#41) ========
        // Every published value is from docs/data/africa_twin_specs.json (one object per entry below,
        // named by its "id", each field's source URL in its "sources"). What no source gives is
        // assumed, per family, as follows (and marked again in each entry):
        //   xrv650 / xrv750 (52° V-twin, offset dual-pin crank): the firing 232-488 (EngineProfile.VTwin52);
        //           redline 8,500 (650, whose EU peak is quoted at 8,000) / 8,200 (750: no source; the
        //           measured 4.6 s 0-100 needs 2nd gear to reach 100 km/h, i.e. > 7,900 rpm); idle 1,200;
        //           clutch launch at 5,500; CG 0.74 m with the rider, 50% rear; grip 0.95 on tarmac,
        //           OffroadTyre 0.4 (period dual-sport tyres); 42° lean; CdA 0.52 / 0.50 m² behind the
        //           half fairing (0.50 puts the RD07A at its measured 180 km/h); braking 8.5 m/s² (twin
        //           276 mm, no ABS), 7.5 on the 650's single disc.
        //   crf1000 / crf1100 (270° parallel twin): redline 8,200 (limiter; the power peak is 7,500);
        //           idle 1,250; launch 4,000; CG 0.76 m with the rider (0.72 on the lowered Type LD),
        //           50% rear; grip 1.0, OffroadTyre 0.3 (stock 90/10 adventure tyres); 45° lean; CdA
        //           0.57 m² upright behind the short screen (199 km/h measured, CRF1000L 2016), 0.59 with
        //           the Adventure Sports' tall screen and wider fairing (202.8 km/h measured, 2024 AS ES
        //           DCT); braking 9.5 m/s² (twin 310 mm radial four-pots, ABS, dual-purpose tyres).
        //   All: the torque between the published power and torque peaks; the look's contact points
        //   (seat at the published seat height, standard position = first value the dataset lists).
        // ---- 66 ----
        // Honda XRV650 Africa Twin (RD03, 1988-89). docs/data/africa_twin_specs.json "XRV650-RD03-1988-EU" (1988-1989 (EU), year letters J/K; EU full power).
        // 647 cc 52° V-twin, 57 PS (MOTORRAD); rpm and torque not published for the EU bike: assumed at 8,000 rpm and 55 N·m at 6,000 (Wikipedia, unverified); manual box, primary 1.888, final 2.687; kerb 221 kg; wheelbase 1550 mm; rake 28.0°, trail 113 mm;
        // seat 880 mm; clearance 200 mm; travel ?/210 mm; 24 L; 90/90-21 + 130/90-17; single 296 mm disc front, single 240 mm disc rear.
        // https://www.honda.co.jp/news/1988/2880512a.html
        // https://www.motorradonline.de/ratgeber/gebrauchtberatung-honda-africa-twin-leidlose-leidenschaft/
        // https://global.honda/en/AfricaTwin/history/stories/1988/
        // Assumed: the family values under "xrv650" at the top of the Africa Twins; front travel 230 mm (Wikipedia / databases, unverified); livery Shasta White tricolour.
        new MotorbikeSpec
        {
            Label = "Honda XRV650 Africa Twin (RD03, 1988-89)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "647 cc 52° V-twin, 57 PS, 221 kg, 21/17 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.VTwin52,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.VTwin, VAngleDeg = 52f, Spoked = true,
                Wheelbase = 1.55f, RakeDeg = 28f, Trail = 0.113f, ForkLength = 0.84f,
                FrontTyre = "90/90-21", RearTyre = "130/90-17", FrontTravel = 0.23f, RearTravel = 0.21f,
                FrontDiscs = 1, FrontDiscMm = 296f, TankLitres = 24f, GroundClearance = 0.2f, ScreenHeight = 0.2f,
                HalfFairing = true,
                Seat = new Vector3(0, 0.88f, -0.2f), Grip = new Vector3(0.43f, 1.18f, 0.3f), Peg = new Vector3(0.19f, 0.4f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.93f, 0.93f, 0.9f), Wheel = new Color(0.66f, 0.67f, 0.7f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Shasta White NH-138 base with blue (PB-200) tank graphic and red HRC-style graphics (tricolour); red paint code not found",
            WetMass = 221f, CgHeight = 0.74f, RearShare = 0.5f, Grip = 0.95f, OffroadTyre = 0.4f, MaxLean = Mathf.DegToRad(42f),
            DragArea = 0.52f, BrakeDecel = 7.5f,
            IdleRpm = 1200f, PeakRpm = 8000f, Redline = 8500f, LaunchRpm = 5500f,
            Torque = new (float, float)[] { (1200f, 33f), (2500f, 42f), (3500f, 48f), (4500f, 52f), (5500f, 54.4f), (6000f, 55f), (7000f, 53.5f), (8000f, 50f), (8500f, 45f) },
            Gears = new[] { 2.769f, 1.882f, 1.45f, 1.173f, 0.965f }, Primary = 1.888f, FinalDrive = 2.687f,
        },
        // ---- 67 ----
        // Honda XRV650 Africa Twin (RD03, 1988, Japan). docs/data/africa_twin_specs.json "XRV650-RD03-1988-JP" (1988; Japan-market).
        // 647 cc 52° V-twin, 52 PS at 7,500 rpm, 55.9 N·m at 6,000 (Honda JP); manual box, primary 1.888, final 2.687; kerb 221 kg; wheelbase 1550 mm; rake 28.0°, trail 113 mm;
        // seat 880 mm; clearance 200 mm; travel ?/210 mm; 24 L; 90/90-21 + 130/90-17; single 296 mm disc front, single 240 mm disc rear.
        // https://www.honda.co.jp/news/1988/2880512a.html
        // Assumed: the family values under "xrv650" at the top of the Africa Twins; front travel 230 mm (Wikipedia / databases, unverified); livery Shasta White tricolour.
        new MotorbikeSpec
        {
            Label = "Honda XRV650 Africa Twin (RD03, 1988, Japan)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "647 cc 52° V-twin, 52 PS, 221 kg, 21/17 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.VTwin52,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.VTwin, VAngleDeg = 52f, Spoked = true,
                Wheelbase = 1.55f, RakeDeg = 28f, Trail = 0.113f, ForkLength = 0.84f,
                FrontTyre = "90/90-21", RearTyre = "130/90-17", FrontTravel = 0.23f, RearTravel = 0.21f,
                FrontDiscs = 1, FrontDiscMm = 296f, TankLitres = 24f, GroundClearance = 0.2f, ScreenHeight = 0.2f,
                HalfFairing = true,
                Seat = new Vector3(0, 0.88f, -0.2f), Grip = new Vector3(0.43f, 1.18f, 0.3f), Peg = new Vector3(0.19f, 0.4f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.93f, 0.93f, 0.9f), Wheel = new Color(0.66f, 0.67f, 0.7f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Shasta White NH-138 base with blue (PB-200) tank graphic and red HRC-style graphics (tricolour); red paint code not found",
            WetMass = 221f, CgHeight = 0.74f, RearShare = 0.5f, Grip = 0.95f, OffroadTyre = 0.4f, MaxLean = Mathf.DegToRad(42f),
            DragArea = 0.52f, BrakeDecel = 7.5f,
            IdleRpm = 1200f, PeakRpm = 7500f, Redline = 8500f, LaunchRpm = 5500f,
            Torque = new (float, float)[] { (1200f, 34f), (2500f, 43f), (3500f, 49f), (4500f, 53f), (5500f, 55.3f), (6000f, 55.9f), (6750f, 52.8f), (7500f, 48.7f), (8500f, 40f) },
            Gears = new[] { 2.769f, 1.882f, 1.45f, 1.173f, 0.965f }, Primary = 1.888f, FinalDrive = 2.687f,
        },
        // ---- 68 ----
        // Honda XRV750 Africa Twin (RD04, 1990-92). docs/data/africa_twin_specs.json "XRV750-RD04-1990" (1990-1992 (year letters L/M/N); full power).
        // 742 cc 52° V-twin, 59 PS (EU, MOTORRAD) at 7,500 rpm (JP rpm), 59.8 N·m at 5,500 (Honda JP); manual box, primary 1.763, final 2.687; kerb 236 kg; wheelbase 1560 mm; rake 27.6°, trail 113 mm;
        // seat 880 mm; clearance 190 mm; travel ?/210 mm; 24 L; 90/90-21 + 130/90-17; twin 276 mm discs front, single 256 mm disc rear.
        // https://www.honda.co.jp/news/1990/2900220a.html
        // https://www.motorradonline.de/ratgeber/gebrauchtberatung-honda-africa-twin-leidlose-leidenschaft/
        // https://global.honda/en/AfricaTwin/history/stories/1990/
        // Assumed: the family values under "xrv750" at the top of the Africa Twins; front travel 220 mm (Wikipedia / databases, unverified); livery Shasta White NH-138 tricolour, blue seat.
        new MotorbikeSpec
        {
            Label = "Honda XRV750 Africa Twin (RD04, 1990-92)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "742 cc 52° V-twin, 59 PS, 236 kg, 21/17 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.VTwin52,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.VTwin, VAngleDeg = 52f, Spoked = true,
                Wheelbase = 1.56f, RakeDeg = 27.6f, Trail = 0.113f, ForkLength = 0.831f,
                FrontTyre = "90/90-21", RearTyre = "130/90-17", FrontTravel = 0.22f, RearTravel = 0.21f,
                FrontDiscs = 2, FrontDiscMm = 276f, TankLitres = 24f, GroundClearance = 0.19f, ScreenHeight = 0.26f,
                HalfFairing = true,
                Seat = new Vector3(0, 0.88f, -0.2f), Grip = new Vector3(0.43f, 1.18f, 0.3f), Peg = new Vector3(0.19f, 0.39f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.93f, 0.93f, 0.9f), Wheel = new Color(0.66f, 0.67f, 0.7f), SeatColor = new Color(0.07f, 0.17f, 0.55f),
            },
            Colours = "1990: Shasta White NH-138 tricolour (white frame, blue seat, red carrier R-134); 1991: Shasta White NH-138; Florida Blue PB-182; Black NH-1; 1992: Shasta White NH-138; Space Blue PB-136; Black NH-1",
            WetMass = 236f, CgHeight = 0.74f, RearShare = 0.5f, Grip = 0.95f, OffroadTyre = 0.4f, MaxLean = Mathf.DegToRad(42f),
            DragArea = 0.5f, BrakeDecel = 8.5f,
            IdleRpm = 1200f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 5500f,
            Torque = new (float, float)[] { (1200f, 37f), (2500f, 47f), (3500f, 53f), (4500f, 57.5f), (5500f, 59.8f), (6500f, 58.5f), (7500f, 55.25f), (8000f, 50.5f) },
            Gears = new[] { 3.083f, 2.062f, 1.55f, 1.272f, 1.083f }, Primary = 1.763f, FinalDrive = 2.687f,
        },
        // ---- 69 ----
        // Honda XRV750 Africa Twin 50 PS (RD04, 1990-92, Germany). docs/data/africa_twin_specs.json "XRV750-RD04-DE-50PS" (1990-1992 (year letters L/M/N); 50 PS restricted (insurance class)).
        // 742 cc 52° V-twin, 36.5 kW (50 PS) at 7,000 rpm (one German database); torque not published: assumed ~55 N·m at 4,500; manual box, primary 1.763, final 2.687; kerb 236 kg; wheelbase 1560 mm; rake 27.6°, trail 113 mm;
        // seat 880 mm; clearance 190 mm; travel ?/210 mm; 24 L; 90/90-21 + 130/90-17; twin 276 mm discs front, single 256 mm disc rear.
        // https://www.motorradundreisen.de/motorraddatenbank/honda/xrv-750-africa-twin-13072.html
        // https://www.honda.co.jp/news/1990/2900220a.html
        // Assumed: the family values under "xrv750" at the top of the Africa Twins; front travel 220 mm (Wikipedia / databases, unverified); livery Florida Blue PB-182 (1991).
        new MotorbikeSpec
        {
            Label = "Honda XRV750 Africa Twin 50 PS (RD04, 1990-92, Germany)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "742 cc 52° V-twin, 50 PS, 236 kg, 21/17 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.VTwin52,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.VTwin, VAngleDeg = 52f, Spoked = true,
                Wheelbase = 1.56f, RakeDeg = 27.6f, Trail = 0.113f, ForkLength = 0.831f,
                FrontTyre = "90/90-21", RearTyre = "130/90-17", FrontTravel = 0.22f, RearTravel = 0.21f,
                FrontDiscs = 2, FrontDiscMm = 276f, TankLitres = 24f, GroundClearance = 0.19f, ScreenHeight = 0.26f,
                HalfFairing = true,
                Seat = new Vector3(0, 0.88f, -0.2f), Grip = new Vector3(0.43f, 1.18f, 0.3f), Peg = new Vector3(0.19f, 0.39f, -0.05f),
                Paint = new Color(0.1f, 0.35f, 0.7f), Trim = new Color(0.93f, 0.93f, 0.9f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.93f, 0.93f, 0.9f), Wheel = new Color(0.66f, 0.67f, 0.7f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "1990: Shasta White NH-138 tricolour (white frame, blue seat, red carrier R-134); 1991: Shasta White NH-138; Florida Blue PB-182; Black NH-1; 1992: Shasta White NH-138; Space Blue PB-136; Black NH-1",
            WetMass = 236f, CgHeight = 0.74f, RearShare = 0.5f, Grip = 0.95f, OffroadTyre = 0.4f, MaxLean = Mathf.DegToRad(42f),
            DragArea = 0.5f, BrakeDecel = 8.5f,
            IdleRpm = 1200f, PeakRpm = 7000f, Redline = 8200f, LaunchRpm = 5500f,
            Torque = new (float, float)[] { (1200f, 36f), (2500f, 45f), (3500f, 51f), (4500f, 55f), (5500f, 54.5f), (6250f, 52f), (7000f, 49.8f), (7500f, 44f), (8000f, 38f) },
            Gears = new[] { 3.083f, 2.062f, 1.55f, 1.272f, 1.083f }, Primary = 1.763f, FinalDrive = 2.687f,
        },
        // ---- 70 ----
        // Honda XRV750 Africa Twin (RD07, 1993-95). docs/data/africa_twin_specs.json "XRV750-RD07-1993" (1993-1995 (year letters P/R/S); full power).
        // 742 cc 52° V-twin, 44 kW (60 PS) at 7,500 rpm, 62 N·m at 6,000; manual box, primary 1.763, final 2.687; kerb 234 kg; wheelbase 1555 mm; rake 27.5°, trail 108 mm;
        // seat 865 mm; clearance 195 mm; travel 220/220 mm; 23 L; 90/90-21 + 140/80R17; twin 276 mm discs front, single 256 mm disc rear.
        // https://www.honda.co.jp/news/1993/2930322.html
        // https://www.motorradonline.de/ratgeber/gebrauchtberatung-honda-africa-twin-leidlose-leidenschaft/
        // https://global.honda/en/AfricaTwin/history/stories/1993/
        // Assumed: the family values under "xrv750" at the top of the Africa Twins; livery Shasta White NH-138 tricolour, silver frame.
        new MotorbikeSpec
        {
            Label = "Honda XRV750 Africa Twin (RD07, 1993-95)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "742 cc 52° V-twin, 60 PS, 234 kg, 21/17 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.VTwin52,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.VTwin, VAngleDeg = 52f, Spoked = true,
                Wheelbase = 1.555f, RakeDeg = 27.5f, Trail = 0.108f, ForkLength = 0.831f,
                FrontTyre = "90/90-21", RearTyre = "140/80R17", FrontTravel = 0.22f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 276f, TankLitres = 23f, GroundClearance = 0.195f, ScreenHeight = 0.3f,
                HalfFairing = true,
                Seat = new Vector3(0, 0.865f, -0.2f), Grip = new Vector3(0.43f, 1.165f, 0.3f), Peg = new Vector3(0.19f, 0.395f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.66f, 0.67f, 0.7f), Wheel = new Color(0.66f, 0.67f, 0.7f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "1993: Shasta White NH-138 tricolour (confirmed by Honda JP); Black NH-1; Toscana Green G-130; 1994: Shasta White NH-138; Space Blue PB-136; Black NH-1 (frame silver NH-146M); 1995: Shasta White NH-138; Black NH-1; Tasmania Green Metallic G-142 (frame silver NH-146M)",
            WetMass = 234f, CgHeight = 0.74f, RearShare = 0.5f, Grip = 0.95f, OffroadTyre = 0.4f, MaxLean = Mathf.DegToRad(42f),
            DragArea = 0.5f, BrakeDecel = 8.5f,
            IdleRpm = 1200f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 5500f,
            Torque = new (float, float)[] { (1200f, 38f), (2500f, 48f), (3500f, 54f), (4500f, 58.5f), (5500f, 61.5f), (6000f, 62f), (6750f, 59.5f), (7500f, 56f), (8000f, 51f) },
            Gears = new[] { 3.083f, 2.062f, 1.55f, 1.272f, 1.083f }, Primary = 1.763f, FinalDrive = 2.687f,
        },
        // ---- 71 ----
        // Honda XRV750 Africa Twin (RD07A, 1996-2003). docs/data/africa_twin_specs.json "XRV750-RD07A-1996" (1996-2003 (production to about 2000, sold until 2003; year letters T onward); full power).
        // 742 cc 52° V-twin, 44 kW (60 PS) at 7,500 rpm, 62 N·m at 6,000; manual box, primary 1.763, final 2.687; kerb 236 kg; wheelbase 1565 mm; rake 27.5°, trail 112 mm;
        // seat 860 mm; clearance 195 mm; travel 220/214 mm; 23 L; 90/90-21 + 140/80R17; twin 276 mm discs front, single 256 mm disc rear.
        // Measured: 180 km/h, 0-100 4.6 s (measured (MOTORRAD 14/1999, solo; 178 two-up)).
        // https://www.motorradonline.de/ratgeber/gebrauchtberatung-honda-africa-twin-leidlose-leidenschaft/
        // https://oem-motordecals.com/en/honda-xrv750-africa-twin-1997-blue/87135may640zc/
        // https://www.megazip.net/zapchasti-dlya-motocyklov/honda/xrv650-14904/xrv650-africa-twin-28806/xrv650j-713005
        // Assumed: the family values under "xrv750" at the top of the Africa Twins; livery Sparkling Red R-127 (1996).
        new MotorbikeSpec
        {
            Label = "Honda XRV750 Africa Twin (RD07A, 1996-2003)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "742 cc 52° V-twin, 60 PS, 236 kg, 21/17 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.VTwin52,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.VTwin, VAngleDeg = 52f, Spoked = true,
                Wheelbase = 1.565f, RakeDeg = 27.5f, Trail = 0.112f, ForkLength = 0.831f,
                FrontTyre = "90/90-21", RearTyre = "140/80R17", FrontTravel = 0.22f, RearTravel = 0.214f,
                FrontDiscs = 2, FrontDiscMm = 276f, TankLitres = 23f, GroundClearance = 0.195f, ScreenHeight = 0.32f,
                HalfFairing = true,
                Seat = new Vector3(0, 0.86f, -0.2f), Grip = new Vector3(0.43f, 1.16f, 0.3f), Peg = new Vector3(0.19f, 0.395f, -0.05f),
                Paint = new Color(0.85f, 0.08f, 0.08f), Trim = new Color(0.08f, 0.08f, 0.09f), Accent = new Color(0.93f, 0.93f, 0.9f),
                Frame = new Color(0.66f, 0.67f, 0.7f), Wheel = new Color(0.66f, 0.67f, 0.7f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "1996: Black NH-1; Sparkling Red R-127; Shine Silver Metallic NH-232; 1997: Sahara Blue Metallic PB-273; Black NH-1; Boon Silver Metallic NH-373; 1998: Ross White NH-196; Black NH-1; Minotauros Green Metallic GY-112; 1999-2000: Sahara Blue Metallic PB-273; Black NH-1; 2001-2003: no new colourways found (leftover stock)",
            WetMass = 236f, CgHeight = 0.74f, RearShare = 0.5f, Grip = 0.95f, OffroadTyre = 0.4f, MaxLean = Mathf.DegToRad(42f),
            DragArea = 0.5f, BrakeDecel = 8.5f,
            IdleRpm = 1200f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 5500f,
            Torque = new (float, float)[] { (1200f, 38f), (2500f, 48f), (3500f, 54f), (4500f, 58.5f), (5500f, 61.5f), (6000f, 62f), (6750f, 59.5f), (7500f, 56f), (8000f, 51f) },
            Gears = new[] { 3.083f, 2.062f, 1.55f, 1.272f, 1.083f }, Primary = 1.763f, FinalDrive = 2.687f,
            RefZeroTo100 = 4.6f, RefTopKmh = 180f,
        },
        // ---- 72 ----
        // Honda CRF1000L Africa Twin (2016-17). docs/data/africa_twin_specs.json "CRF1000L-SD04-2016-2017-STD" (2016-2017; Manual, standard (no ABS)).
        // 998 cc 270° parallel twin, 70 kW at 7,500 rpm, 98 N·m at 6,000; manual box, primary 1.733, final 2.625; kerb 228 kg; wheelbase 1575 mm; rake 27.5°, trail 115 mm;
        // seat 870 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90R21 + 150/70R18; Twin 310mm wave floating discs front, 256mm wave disc rear.
        // Measured: 199 km/h, 0-100 3.8 s (measured (MOTORRAD Top-Test 03/2016, manual)).
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/58407/the-new-16ym-honda-crf1000l-africa-twin
        // https://hondanews.eu/gb/en/media/pressreleases/62875/16ym-honda-crf1000l-africa-twin8
        // https://www.honda.co.jp/CRF1000L/spec/
        // Assumed: the family values under "crf1000" at the top of the Africa Twins; livery Victory Red / Graphite Black ('CRF Rally').
        new MotorbikeSpec
        {
            Label = "Honda CRF1000L Africa Twin (2016-17)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "998 cc 270° parallel twin, 95 PS, 228 kg, 21/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.115f, ForkLength = 0.86f,
                FrontTyre = "90/90R21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.2f,
                Seat = new Vector3(0, 0.87f, -0.2f), Grip = new Vector3(0.43f, 1.17f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.8f, 0.06f, 0.07f), Trim = new Color(0.08f, 0.08f, 0.09f), Accent = new Color(0.93f, 0.93f, 0.9f),
                Frame = new Color(0.8f, 0.06f, 0.07f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Victory Red / Graphite Black ('CRF Rally' HRC scheme); Pearl Glare White / Pearl Spencer Blue (Tricolour); Digital Silver Metallic (EU name unverified for 2016; US lists 'Silver'); 2017 additions: Matte Ballistic Black Metallic, Candy Prominence Red (AU/EU std model)",
            WetMass = 228f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 55f), (2500f, 70f), (3500f, 81f), (4500f, 90f), (5500f, 96f), (6000f, 98f), (6750f, 94.5f), (7500f, 89.1f), (8200f, 78f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.1f, 0.968f }, Primary = 1.733f, FinalDrive = 2.625f,
            RefZeroTo100 = 3.8f, RefTopKmh = 199f,
        },
        // ---- 73 ----
        // Honda CRF1000L Africa Twin ABS (2016-17). docs/data/africa_twin_specs.json "CRF1000L-SD04-2016-2017-ABS" (2016-2017; Manual, ABS (HSTC)).
        // 998 cc 270° parallel twin, 70 kW at 7,500 rpm, 98 N·m at 6,000; manual box, primary 1.733, final 2.625; kerb 232 kg; wheelbase 1575 mm; rake 27.5°, trail 115 mm;
        // seat 870 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90R21 + 150/70R18; Twin 310mm wave floating discs front, 256mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/58407/the-new-16ym-honda-crf1000l-africa-twin
        // https://hondanews.eu/gb/en/media/pressreleases/62875/16ym-honda-crf1000l-africa-twin8
        // https://www.honda.co.jp/CRF1000L/spec/
        // Assumed: the family values under "crf1000" at the top of the Africa Twins; livery Pearl Glare White Tricolour, black rims.
        new MotorbikeSpec
        {
            Label = "Honda CRF1000L Africa Twin ABS (2016-17)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "998 cc 270° parallel twin, 95 PS, 232 kg, 21/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.115f, ForkLength = 0.86f,
                FrontTyre = "90/90R21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.2f,
                Seat = new Vector3(0, 0.87f, -0.2f), Grip = new Vector3(0.43f, 1.17f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.8f, 0.06f, 0.07f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Victory Red / Graphite Black ('CRF Rally' HRC scheme); Pearl Glare White / Pearl Spencer Blue (Tricolour); Digital Silver Metallic (EU name unverified for 2016; US lists 'Silver'); 2017 additions: Matte Ballistic Black Metallic, Candy Prominence Red (AU/EU std model)",
            WetMass = 232f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 55f), (2500f, 70f), (3500f, 81f), (4500f, 90f), (5500f, 96f), (6000f, 98f), (6750f, 94.5f), (7500f, 89.1f), (8200f, 78f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.1f, 0.968f }, Primary = 1.733f, FinalDrive = 2.625f,
        },
        // ---- 74 ----
        // Honda CRF1000L Africa Twin DCT (2016-17). docs/data/africa_twin_specs.json "CRF1000L-SD04-2016-2017-DCT" (2016-2017; DCT (ABS + HSTC)).
        // 998 cc 270° parallel twin, 70 kW at 7,500 rpm, 98 N·m at 6,000; DCT box, primary 1.883, final 2.625; kerb 242 kg; wheelbase 1575 mm; rake 27.5°, trail 115 mm;
        // seat 870 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90R21 + 150/70R18; Twin 310mm wave floating discs front, 256mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/58407/the-new-16ym-honda-crf1000l-africa-twin
        // https://hondanews.eu/gb/en/media/pressreleases/62875/16ym-honda-crf1000l-africa-twin8
        // https://www.honda.co.jp/CRF1000L/spec/
        // Assumed: the family values under "crf1000" at the top of the Africa Twins; livery Digital Silver Metallic, white frame, gold rims.
        new MotorbikeSpec
        {
            Label = "Honda CRF1000L Africa Twin DCT (2016-17)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "998 cc 270° parallel twin, 95 PS, 242 kg, 21/18 spokes, DCT automatic. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.115f, ForkLength = 0.86f,
                FrontTyre = "90/90R21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.2f,
                Seat = new Vector3(0, 0.87f, -0.2f), Grip = new Vector3(0.43f, 1.17f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.6f, 0.62f, 0.66f), Trim = new Color(0.08f, 0.08f, 0.09f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.88f, 0.88f, 0.86f), Wheel = new Color(0.78f, 0.6f, 0.2f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Victory Red / Graphite Black ('CRF Rally' HRC scheme); Pearl Glare White / Pearl Spencer Blue (Tricolour); Digital Silver Metallic (EU name unverified for 2016; US lists 'Silver'); 2017 additions: Matte Ballistic Black Metallic, Candy Prominence Red (AU/EU std model)",
            WetMass = 242f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 55f), (2500f, 70f), (3500f, 81f), (4500f, 90f), (5500f, 96f), (6000f, 98f), (6750f, 94.5f), (7500f, 89.1f), (8200f, 78f) },
            Gears = new[] { 2.562f, 1.761f, 1.375f, 1.133f, 0.972f, 0.882f }, Primary = 1.883f, FinalDrive = 2.625f,
            Dct = true,
        },
        // ---- 75 ----
        // Honda CRF1000L Africa Twin (2018-19). docs/data/africa_twin_specs.json "CRF1000L-SD04-2018-2019-MT" (2018-2019; Manual (ABS, TBW)).
        // 998 cc 270° parallel twin, 70 kW at 7,500 rpm, 99 N·m at 6,000; manual box, primary 1.733, final 2.625; kerb 230 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 870 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90R21 + 150/70R18; Twin 310mm wave floating discs front, 256mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/118505/2018-honda-crf1000l-africa-twin
        // https://www.honda.co.jp/CRF1000L/spec/
        // https://www.advpulse.com/?p=52671
        // Assumed: the family values under "crf1000" at the top of the Africa Twins; livery Grand Prix Red.
        new MotorbikeSpec
        {
            Label = "Honda CRF1000L Africa Twin (2018-19)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "998 cc 270° parallel twin, 95 PS, 230 kg, 21/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90R21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.2f,
                Seat = new Vector3(0, 0.87f, -0.2f), Grip = new Vector3(0.43f, 1.17f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.8f, 0.06f, 0.07f), Trim = new Color(0.93f, 0.93f, 0.9f), Accent = new Color(0.08f, 0.08f, 0.09f),
                Frame = new Color(0.08f, 0.08f, 0.09f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Matt Ballistic Black Metallic; Pearl Glare White (Tricolor); Grand Prix Red (Team HRC Rally colour); Candy Chromosphere Red; 2019: matte black (gold rims) and dark blue/white/red tricolour (gold rims) per US press (official EU names not found)",
            WetMass = 230f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 55f), (2500f, 70f), (3500f, 81f), (4500f, 90f), (5500f, 97f), (6000f, 99f), (6750f, 95f), (7500f, 89.1f), (8200f, 78f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.1f, 0.968f }, Primary = 1.733f, FinalDrive = 2.625f,
        },
        // ---- 76 ----
        // Honda CRF1000L Africa Twin DCT (2018-19). docs/data/africa_twin_specs.json "CRF1000L-SD04-2018-2019-DCT" (2018-2019; DCT (ABS, TBW)).
        // 998 cc 270° parallel twin, 70 kW at 7,500 rpm, 99 N·m at 6,000; DCT box, primary 1.883, final 2.625; kerb 240 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 870 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90R21 + 150/70R18; Twin 310mm wave floating discs front, 256mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/118505/2018-honda-crf1000l-africa-twin
        // https://www.honda.co.jp/CRF1000L/spec/
        // https://www.advpulse.com/?p=52671
        // Assumed: the family values under "crf1000" at the top of the Africa Twins; livery Pearl Glare White Tricolour, gold rims.
        new MotorbikeSpec
        {
            Label = "Honda CRF1000L Africa Twin DCT (2018-19)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "998 cc 270° parallel twin, 95 PS, 240 kg, 21/18 spokes, DCT automatic. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90R21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.2f,
                Seat = new Vector3(0, 0.87f, -0.2f), Grip = new Vector3(0.43f, 1.17f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.8f, 0.06f, 0.07f), Wheel = new Color(0.78f, 0.6f, 0.2f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Matt Ballistic Black Metallic; Pearl Glare White (Tricolor); Grand Prix Red (Team HRC Rally colour); Candy Chromosphere Red; 2019: matte black (gold rims) and dark blue/white/red tricolour (gold rims) per US press (official EU names not found)",
            WetMass = 240f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 55f), (2500f, 70f), (3500f, 81f), (4500f, 90f), (5500f, 97f), (6000f, 99f), (6750f, 95f), (7500f, 89.1f), (8200f, 78f) },
            Gears = new[] { 2.562f, 1.761f, 1.375f, 1.133f, 0.972f, 0.882f }, Primary = 1.883f, FinalDrive = 2.625f,
            Dct = true,
        },
        // ---- 77 ----
        // Honda CRF1000L2 Africa Twin Adventure Sports (2018-19). docs/data/africa_twin_specs.json "CRF1000L2-SD04-2018-2019-AS-MT" (2018-2019; Adventure Sports manual).
        // 998 cc 270° parallel twin, 70 kW at 7,500 rpm, 99 N·m at 6,000; manual box, primary 1.733, final 2.625; kerb 243 kg; wheelbase 1580 mm; rake 27.5°, trail 115 mm;
        // seat 920 mm; clearance 270 mm; travel 252/240 mm; 24.2 L; 90/90R21 + 150/70R18; Twin 310mm wave floating discs front, 256mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/118570/2018-honda-crf1000l-africa-twin-adventure-sports
        // https://www.honda.co.jp/CRF1000L/spec/
        // https://mcnews.com.au/?p=203130
        // Assumed: the family values under "crf1000" at the top of the Africa Twins; livery Pearl Glare White Tricolour, gold rims.
        new MotorbikeSpec
        {
            Label = "Honda CRF1000L2 Africa Twin Adventure Sports (2018-19)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "998 cc 270° parallel twin, 95 PS, 243 kg, 21/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.58f, RakeDeg = 27.5f, Trail = 0.115f, ForkLength = 0.8798f,
                FrontTyre = "90/90R21", RearTyre = "150/70R18", FrontTravel = 0.252f, RearTravel = 0.24f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 24.2f, GroundClearance = 0.27f, ScreenHeight = 0.28f,
                CrashBars = true,
                Seat = new Vector3(0, 0.92f, -0.2f), Grip = new Vector3(0.43f, 1.22f, 0.3f), Peg = new Vector3(0.19f, 0.47f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.8f, 0.06f, 0.07f), Wheel = new Color(0.78f, 0.6f, 0.2f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Tricolore (30th anniversary paint scheme, 2018); Digital Silver Metallic (2019, white frame, gold rims per US press)",
            WetMass = 243f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.59f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 55f), (2500f, 70f), (3500f, 81f), (4500f, 90f), (5500f, 97f), (6000f, 99f), (6750f, 95f), (7500f, 89.1f), (8200f, 78f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.1f, 0.968f }, Primary = 1.733f, FinalDrive = 2.625f,
        },
        // ---- 78 ----
        // Honda CRF1000L2 Africa Twin Adventure Sports DCT (2018-19). docs/data/africa_twin_specs.json "CRF1000L2-SD04-2018-2019-AS-DCT" (2018-2019; Adventure Sports DCT).
        // 998 cc 270° parallel twin, 70 kW at 7,500 rpm, 99 N·m at 6,000; DCT box, primary 1.883, final 2.625; kerb 253 kg; wheelbase 1580 mm; rake 27.5°, trail 115 mm;
        // seat 920 mm; clearance 270 mm; travel 252/240 mm; 24.2 L; 90/90R21 + 150/70R18; Twin 310mm wave floating discs front, 256mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/118570/2018-honda-crf1000l-africa-twin-adventure-sports
        // https://www.honda.co.jp/CRF1000L/spec/
        // https://mcnews.com.au/?p=203130
        // Assumed: the family values under "crf1000" at the top of the Africa Twins; livery Digital Silver Metallic, white frame, gold rims.
        new MotorbikeSpec
        {
            Label = "Honda CRF1000L2 Africa Twin Adventure Sports DCT (2018-19)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "998 cc 270° parallel twin, 95 PS, 253 kg, 21/18 spokes, DCT automatic. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.58f, RakeDeg = 27.5f, Trail = 0.115f, ForkLength = 0.8798f,
                FrontTyre = "90/90R21", RearTyre = "150/70R18", FrontTravel = 0.252f, RearTravel = 0.24f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 24.2f, GroundClearance = 0.27f, ScreenHeight = 0.28f,
                CrashBars = true,
                Seat = new Vector3(0, 0.92f, -0.2f), Grip = new Vector3(0.43f, 1.22f, 0.3f), Peg = new Vector3(0.19f, 0.47f, -0.05f),
                Paint = new Color(0.6f, 0.62f, 0.66f), Trim = new Color(0.08f, 0.08f, 0.09f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.88f, 0.88f, 0.86f), Wheel = new Color(0.78f, 0.6f, 0.2f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Tricolore (30th anniversary paint scheme, 2018); Digital Silver Metallic (2019, white frame, gold rims per US press)",
            WetMass = 253f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.59f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 55f), (2500f, 70f), (3500f, 81f), (4500f, 90f), (5500f, 97f), (6000f, 99f), (6750f, 95f), (7500f, 89.1f), (8200f, 78f) },
            Gears = new[] { 2.562f, 1.761f, 1.375f, 1.133f, 0.972f, 0.882f }, Primary = 1.883f, FinalDrive = 2.625f,
            Dct = true,
        },
        // ---- 79 ----
        // Honda CRF1000L2 Africa Twin Adventure Sports Type LD (2018-19, Japan). docs/data/africa_twin_specs.json "CRF1000L2-SD04-2018-2019-AS-TypeLD-JP" (2018-2019; Adventure Sports Type LD (low-down, Japan only, manual/DCT)).
        // 998 cc 270° parallel twin, 70 kW at 7,500 rpm, 98 N·m at 6,000; manual box, primary 1.733, final 2.625; kerb 242 kg; wheelbase 1560 mm; rake 27.5°, trail 113 mm;
        // seat 830 mm; clearance 210 mm; travel ?/? mm; 24 L; 90/90R21 + 150/70R18; Twin 310mm wave floating discs front, 256mm wave disc rear.
        // https://www.honda.co.jp/CRF1000L/spec/
        // Assumed: the family values under "crf1000" at the top of the Africa Twins; front travel 200 mm, rear travel 190 mm; livery Pearl Glare White Tricolour, gold rims.
        new MotorbikeSpec
        {
            Label = "Honda CRF1000L2 Africa Twin Adventure Sports Type LD (2018-19, Japan)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "998 cc 270° parallel twin, 95 PS, 242 kg, 21/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.56f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.833f,
                FrontTyre = "90/90R21", RearTyre = "150/70R18", FrontTravel = 0.2f, RearTravel = 0.19f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 24f, GroundClearance = 0.21f, ScreenHeight = 0.28f,
                CrashBars = true,
                Seat = new Vector3(0, 0.83f, -0.2f), Grip = new Vector3(0.43f, 1.13f, 0.3f), Peg = new Vector3(0.19f, 0.41f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.8f, 0.06f, 0.07f), Wheel = new Color(0.78f, 0.6f, 0.2f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Tricolore (30th anniversary paint scheme, 2018); Digital Silver Metallic (2019, white frame, gold rims per US press)",
            WetMass = 242f, CgHeight = 0.72f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.59f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 55f), (2500f, 70f), (3500f, 81f), (4500f, 90f), (5500f, 96f), (6000f, 98f), (6750f, 94.5f), (7500f, 89.1f), (8200f, 78f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.1f, 0.968f }, Primary = 1.733f, FinalDrive = 2.625f,
        },
        // ---- 80 ----
        // Honda CRF1100L Africa Twin (2020-21). docs/data/africa_twin_specs.json "CRF1100L-2020-2021-MT" (2020-2021; standard, manual).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 105 N·m at 6,250; manual box, primary 1.717, final 2.625; kerb 226 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/329006/2021-honda-africa-twin-16
        // https://global.honda/factbook/motor/CRF1100L-Africa_Twin/201910/CRF1100L-Africa_Twin_1910.pdf
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/190877/new-crf1100l-africa-twin-and-africa-twin-adventure-sports-to-arrive-in-europe-in-2019
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Grand Prix Red.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin (2020-21)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 226 kg, 21/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.2f,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.8f, 0.06f, 0.07f), Trim = new Color(0.93f, 0.93f, 0.9f), Accent = new Color(0.08f, 0.08f, 0.09f),
                Frame = new Color(0.08f, 0.08f, 0.09f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Grand Prix Red (red subframe) - 2020; Matte Ballistic Black Metallic (red subframe) - 2020; Pearl Glare White Tricolour - NEW 2021",
            WetMass = 226f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 58f), (2500f, 74f), (3500f, 86f), (4500f, 96f), (5500f, 102f), (6250f, 105f), (7000f, 100.5f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.064f, 0.972f }, Primary = 1.717f, FinalDrive = 2.625f,
        },
        // ---- 81 ----
        // Honda CRF1100L Africa Twin DCT (2020-21). docs/data/africa_twin_specs.json "CRF1100L-2020-2021-DCT" (2020-2021; standard, DCT).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 105 N·m at 6,250; DCT box, primary 1.863, final 2.625; kerb 236 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/329006/2021-honda-africa-twin-16
        // https://global.honda/factbook/motor/CRF1100L-Africa_Twin/201910/CRF1100L-Africa_Twin_1910.pdf
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/190877/new-crf1100l-africa-twin-and-africa-twin-adventure-sports-to-arrive-in-europe-in-2019
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Matt Ballistic Black Metallic, red subframe.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin DCT (2020-21)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 236 kg, 21/18 spokes, DCT automatic. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.2f,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.13f, 0.13f, 0.14f), Trim = new Color(0.33f, 0.34f, 0.36f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.08f, 0.08f, 0.09f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Grand Prix Red (red subframe) - 2020; Matte Ballistic Black Metallic (red subframe) - 2020; Pearl Glare White Tricolour - NEW 2021",
            WetMass = 236f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 58f), (2500f, 74f), (3500f, 86f), (4500f, 96f), (5500f, 102f), (6250f, 105f), (7000f, 100.5f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.562f, 1.761f, 1.375f, 1.133f, 0.972f, 0.882f }, Primary = 1.863f, FinalDrive = 2.625f,
            Dct = true,
        },
        // ---- 82 ----
        // Honda CRF1100L Africa Twin Adventure Sports (2020-23). docs/data/africa_twin_specs.json "CRF1100L-AS-2020-2023-MT" (2020-2023; Adventure Sports, manual).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 105 N·m at 6,250; manual box, primary 1.717, final 2.625; kerb 238 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 24.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/341396/22ym-crf1100l-africa-twin-adventure-sports
        // https://global.honda/factbook/motor/CRF1100L-Africa_Twin/201910/CRF1100L-Africa_Twin_1910.pdf
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/190877/new-crf1100l-africa-twin-and-africa-twin-adventure-sports-to-arrive-in-europe-in-2019
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Pearl Glare White Tricolour, gold rims.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin Adventure Sports (2020-23)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 238 kg, 21/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 24.8f, GroundClearance = 0.25f, ScreenHeight = 0.36f,
                CrashBars = true,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.8f, 0.06f, 0.07f), Wheel = new Color(0.78f, 0.6f, 0.2f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Pearl Glare White Tricolour (gold rims) - all years; Darkness Black Metallic (2020-2021); Matte Ballistic Black Metallic (2022); Mat Iridium Gray Metallic (NEW 2023, black wheels)",
            WetMass = 238f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.59f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 58f), (2500f, 74f), (3500f, 86f), (4500f, 96f), (5500f, 102f), (6250f, 105f), (7000f, 100.5f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.064f, 0.972f }, Primary = 1.717f, FinalDrive = 2.625f,
        },
        // ---- 83 ----
        // Honda CRF1100L Africa Twin Adventure Sports DCT (2020-23). docs/data/africa_twin_specs.json "CRF1100L-AS-2020-2023-DCT" (2020-2023; Adventure Sports, DCT).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 105 N·m at 6,250; DCT box, primary 1.863, final 2.625; kerb 248 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 24.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/341396/22ym-crf1100l-africa-twin-adventure-sports
        // https://global.honda/factbook/motor/CRF1100L-Africa_Twin/201910/CRF1100L-Africa_Twin_1910.pdf
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/190877/new-crf1100l-africa-twin-and-africa-twin-adventure-sports-to-arrive-in-europe-in-2019
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Darkness Black Metallic.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin Adventure Sports DCT (2020-23)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 248 kg, 21/18 spokes, DCT automatic. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 24.8f, GroundClearance = 0.25f, ScreenHeight = 0.36f,
                CrashBars = true,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.05f, 0.05f, 0.06f), Trim = new Color(0.33f, 0.34f, 0.36f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.08f, 0.08f, 0.09f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Pearl Glare White Tricolour (gold rims) - all years; Darkness Black Metallic (2020-2021); Matte Ballistic Black Metallic (2022); Mat Iridium Gray Metallic (NEW 2023, black wheels)",
            WetMass = 248f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.59f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 58f), (2500f, 74f), (3500f, 86f), (4500f, 96f), (5500f, 102f), (6250f, 105f), (7000f, 100.5f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.562f, 1.761f, 1.375f, 1.133f, 0.972f, 0.882f }, Primary = 1.863f, FinalDrive = 2.625f,
            Dct = true,
        },
        // ---- 84 ----
        // Honda CRF1100L Africa Twin Adventure Sports ES (2020-23). docs/data/africa_twin_specs.json "CRF1100L-AS-2020-2023-ES-MT" (2020-2023; Adventure Sports ES (Showa EERA), manual).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 105 N·m at 6,250; manual box, primary 1.717, final 2.625; kerb 240 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 24.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/341396/22ym-crf1100l-africa-twin-adventure-sports
        // https://global.honda/factbook/motor/CRF1100L-Africa_Twin/201910/CRF1100L-Africa_Twin_1910.pdf
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/190877/new-crf1100l-africa-twin-and-africa-twin-adventure-sports-to-arrive-in-europe-in-2019
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Pearl Glare White Tricolour, gold rims.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin Adventure Sports ES (2020-23)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 240 kg, 21/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 24.8f, GroundClearance = 0.25f, ScreenHeight = 0.36f,
                CrashBars = true,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.8f, 0.06f, 0.07f), Wheel = new Color(0.78f, 0.6f, 0.2f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Pearl Glare White Tricolour (gold rims) - all years; Darkness Black Metallic (2020-2021); Matte Ballistic Black Metallic (2022); Mat Iridium Gray Metallic (NEW 2023, black wheels)",
            WetMass = 240f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.59f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 58f), (2500f, 74f), (3500f, 86f), (4500f, 96f), (5500f, 102f), (6250f, 105f), (7000f, 100.5f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.064f, 0.972f }, Primary = 1.717f, FinalDrive = 2.625f,
        },
        // ---- 85 ----
        // Honda CRF1100L Africa Twin Adventure Sports ES DCT (2020-23). docs/data/africa_twin_specs.json "CRF1100L-AS-2020-2023-ES-DCT" (2020-2023; Adventure Sports ES (Showa EERA), DCT).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 105 N·m at 6,250; DCT box, primary 1.863, final 2.625; kerb 250 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 24.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/341396/22ym-crf1100l-africa-twin-adventure-sports
        // https://global.honda/factbook/motor/CRF1100L-Africa_Twin/201910/CRF1100L-Africa_Twin_1910.pdf
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/190877/new-crf1100l-africa-twin-and-africa-twin-adventure-sports-to-arrive-in-europe-in-2019
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Mat Iridium Gray Metallic, black wheels.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin Adventure Sports ES DCT (2020-23)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 250 kg, 21/18 spokes, DCT automatic. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 24.8f, GroundClearance = 0.25f, ScreenHeight = 0.36f,
                CrashBars = true,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.33f, 0.34f, 0.36f), Trim = new Color(0.08f, 0.08f, 0.09f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.08f, 0.08f, 0.09f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "Pearl Glare White Tricolour (gold rims) - all years; Darkness Black Metallic (2020-2021); Matte Ballistic Black Metallic (2022); Mat Iridium Gray Metallic (NEW 2023, black wheels)",
            WetMass = 250f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.59f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 58f), (2500f, 74f), (3500f, 86f), (4500f, 96f), (5500f, 102f), (6250f, 105f), (7000f, 100.5f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.562f, 1.761f, 1.375f, 1.133f, 0.972f, 0.882f }, Primary = 1.863f, FinalDrive = 2.625f,
            Dct = true,
        },
        // ---- 86 ----
        // Honda CRF1100L Africa Twin (2022-23). docs/data/africa_twin_specs.json "CRF1100L-2022-2023-MT" (2022-2023; standard, manual).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 105 N·m at 6,250; manual box, primary 1.717, final 2.625; kerb 229 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/341394/22ym-crf1100l-africa-twin
        // https://global.honda/factbook/motor/CRF1100L-Africa_Twin/201910/CRF1100L-Africa_Twin_1910.pdf
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Grand Prix Red.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin (2022-23)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 229 kg, 21/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.2f,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.8f, 0.06f, 0.07f), Trim = new Color(0.93f, 0.93f, 0.9f), Accent = new Color(0.08f, 0.08f, 0.09f),
                Frame = new Color(0.08f, 0.08f, 0.09f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "2022: Pearl Glare White Tricolour, Grand Prix Red, Matte Ballistic Black Metallic; 2023: Grand Prix Red; Mat Ballistic Black Metallic (black frame + black subframe); Glint Wave Blue Metallic Tricolour (blue cowl/fender/tail, black wheels per MCN)",
            WetMass = 229f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 58f), (2500f, 74f), (3500f, 86f), (4500f, 96f), (5500f, 102f), (6250f, 105f), (7000f, 100.5f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.064f, 0.972f }, Primary = 1.717f, FinalDrive = 2.625f,
        },
        // ---- 87 ----
        // Honda CRF1100L Africa Twin DCT (2022-23). docs/data/africa_twin_specs.json "CRF1100L-2022-2023-DCT" (2022-2023; standard, DCT).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 105 N·m at 6,250; DCT box, primary 1.863, final 2.625; kerb 240 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/341394/22ym-crf1100l-africa-twin
        // https://global.honda/factbook/motor/CRF1100L-Africa_Twin/201910/CRF1100L-Africa_Twin_1910.pdf
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Glint Wave Blue Metallic Tricolour (2023), black wheels.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin DCT (2022-23)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 240 kg, 21/18 spokes, DCT automatic. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.2f,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.1f, 0.28f, 0.62f), Trim = new Color(0.93f, 0.93f, 0.9f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.8f, 0.06f, 0.07f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "2022: Pearl Glare White Tricolour, Grand Prix Red, Matte Ballistic Black Metallic; 2023: Grand Prix Red; Mat Ballistic Black Metallic (black frame + black subframe); Glint Wave Blue Metallic Tricolour (blue cowl/fender/tail, black wheels per MCN)",
            WetMass = 240f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 58f), (2500f, 74f), (3500f, 86f), (4500f, 96f), (5500f, 102f), (6250f, 105f), (7000f, 100.5f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.562f, 1.761f, 1.375f, 1.133f, 0.972f, 0.882f }, Primary = 1.863f, FinalDrive = 2.625f,
            Dct = true,
        },
        // ---- 88 ----
        // Honda CRF1100L Africa Twin (2024-26). docs/data/africa_twin_specs.json "CRF1100L-2024plus-MT" (2024-2026; standard, manual).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 112 N·m at 5,500; manual box, primary 1.717, final 2.625; kerb 231 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/451543/24ym-crf1100l-africa-twin
        // https://www.fr.honda.ch/motorcycles/range/adventure/crf1100l-africa-twin/specifications-and-price.html
        // https://global.honda/content/dam/site/global-jp/news/cq_img/2024-new/02/2240229-crf1100l_link.pdf
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Grand Prix Red.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin (2024-26)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 231 kg, 21/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.24f,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.8f, 0.06f, 0.07f), Trim = new Color(0.93f, 0.93f, 0.9f), Accent = new Color(0.08f, 0.08f, 0.09f),
                Frame = new Color(0.08f, 0.08f, 0.09f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "2024: Grand Prix Red; Matt Ballistic Black Metallic (Pearl Glare White / Glint Wave Blue Metallic Tricolour only on ES); 2025: Grand Prix Red (revised graphics); Matt Ballistic Black Metallic (red accents); Pearl Glare White / Pearl Hawkseye Blue Metallic Tricolour (ES only); 2026: Grand Prix Red, Matt Ballistic Black Metallic (gold accents), Pearl Glare White HRC Tricolore with gold wheels - all with a revised (unnamed) frame colour",
            WetMass = 231f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 62f), (2500f, 80f), (3500f, 95f), (4500f, 106f), (5500f, 112f), (6250f, 108f), (7000f, 101f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.064f, 0.972f }, Primary = 1.717f, FinalDrive = 2.625f,
        },
        // ---- 89 ----
        // Honda CRF1100L Africa Twin DCT (2024-26). docs/data/africa_twin_specs.json "CRF1100L-2024plus-DCT" (2024-2026; standard, DCT).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 112 N·m at 5,500; DCT box, primary 1.863, final 2.625; kerb 242 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/451543/24ym-crf1100l-africa-twin
        // https://www.fr.honda.ch/motorcycles/range/adventure/crf1100l-africa-twin/specifications-and-price.html
        // https://global.honda/content/dam/site/global-jp/news/cq_img/2024-new/02/2240229-crf1100l_link.pdf
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Matt Ballistic Black Metallic, red subframe.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin DCT (2024-26)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 242 kg, 21/18 spokes, DCT automatic. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.24f,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.13f, 0.13f, 0.14f), Trim = new Color(0.33f, 0.34f, 0.36f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.08f, 0.08f, 0.09f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "2024: Grand Prix Red; Matt Ballistic Black Metallic (Pearl Glare White / Glint Wave Blue Metallic Tricolour only on ES); 2025: Grand Prix Red (revised graphics); Matt Ballistic Black Metallic (red accents); Pearl Glare White / Pearl Hawkseye Blue Metallic Tricolour (ES only); 2026: Grand Prix Red, Matt Ballistic Black Metallic (gold accents), Pearl Glare White HRC Tricolore with gold wheels - all with a revised (unnamed) frame colour",
            WetMass = 242f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 62f), (2500f, 80f), (3500f, 95f), (4500f, 106f), (5500f, 112f), (6250f, 108f), (7000f, 101f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.562f, 1.761f, 1.375f, 1.133f, 0.972f, 0.882f }, Primary = 1.863f, FinalDrive = 2.625f,
            Dct = true,
        },
        // ---- 90 ----
        // Honda CRF1100L Africa Twin ES (2024-26). docs/data/africa_twin_specs.json "CRF1100L-ES-2024plus-MT" (2024-2026; standard ES (Showa EERA), manual).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 112 N·m at 5,500; manual box, primary 1.717, final 2.625; kerb 233 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/451543/24ym-crf1100l-africa-twin
        // https://www.fr.honda.ch/motorcycles/range/adventure/crf1100l-africa-twin/specifications-and-price.html
        // https://global.honda/content/dam/site/global-jp/news/cq_img/2024-new/02/2240229-crf1100l_link.pdf
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Pearl Glare White Tricolour, gold rims.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin ES (2024-26)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 233 kg, 21/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.24f,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.8f, 0.06f, 0.07f), Wheel = new Color(0.78f, 0.6f, 0.2f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "2024: Pearl Glare White / Glint Wave Blue Metallic Tricolour (ES only) + standard colours; 2025: Pearl Glare White / Pearl Hawkseye Blue Metallic Tricolour (ES only); 2026: see standard model",
            WetMass = 233f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 62f), (2500f, 80f), (3500f, 95f), (4500f, 106f), (5500f, 112f), (6250f, 108f), (7000f, 101f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.064f, 0.972f }, Primary = 1.717f, FinalDrive = 2.625f,
        },
        // ---- 91 ----
        // Honda CRF1100L Africa Twin ES DCT (2024-26). docs/data/africa_twin_specs.json "CRF1100L-ES-2024plus-DCT" (2024-2026; standard ES (Showa EERA), DCT).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 112 N·m at 5,500; DCT box, primary 1.863, final 2.625; kerb 244 kg; wheelbase 1575 mm; rake 27.5°, trail 113 mm;
        // seat 850 mm; clearance 250 mm; travel 230/220 mm; 18.8 L; 90/90-21 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/451543/24ym-crf1100l-africa-twin
        // https://www.fr.honda.ch/motorcycles/range/adventure/crf1100l-africa-twin/specifications-and-price.html
        // https://global.honda/content/dam/site/global-jp/news/cq_img/2024-new/02/2240229-crf1100l_link.pdf
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Pearl Glare White Tricolour, black rims.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin ES DCT (2024-26)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 244 kg, 21/18 spokes, DCT automatic. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.575f, RakeDeg = 27.5f, Trail = 0.113f, ForkLength = 0.86f,
                FrontTyre = "90/90-21", RearTyre = "150/70R18", FrontTravel = 0.23f, RearTravel = 0.22f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 18.8f, GroundClearance = 0.25f, ScreenHeight = 0.24f,
                Seat = new Vector3(0, 0.85f, -0.2f), Grip = new Vector3(0.43f, 1.15f, 0.3f), Peg = new Vector3(0.19f, 0.45f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.8f, 0.06f, 0.07f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "2024: Pearl Glare White / Glint Wave Blue Metallic Tricolour (ES only) + standard colours; 2025: Pearl Glare White / Pearl Hawkseye Blue Metallic Tricolour (ES only); 2026: see standard model",
            WetMass = 244f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.57f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 62f), (2500f, 80f), (3500f, 95f), (4500f, 106f), (5500f, 112f), (6250f, 108f), (7000f, 101f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.562f, 1.761f, 1.375f, 1.133f, 0.972f, 0.882f }, Primary = 1.863f, FinalDrive = 2.625f,
            Dct = true,
        },
        // ---- 92 ----
        // Honda CRF1100L Africa Twin Adventure Sports ES (2024-26). docs/data/africa_twin_specs.json "CRF1100L-AS-ES-2024plus-MT" (2024-2026; Adventure Sports ES, manual).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 112 N·m at 5,500; manual box, primary 1.717, final 2.625; kerb 243 kg; wheelbase 1550 mm; rake 27.5°, trail 106 mm;
        // seat 835 mm; clearance 220 mm; travel 210/200 mm; 24.8 L; 110/80R19 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/483985/25ym-honda-crf1100l-africa-twin-adventure-sports
        // https://hondanews.eu/gb/en/motorcycles/media/pressreleases/568699/26ym-honda-crf1100l-africa-twin-adventure-sports
        // https://global.honda/content/dam/site/global-jp/news/cq_img/2024-new/02/2240229-crf1100l_link.pdf
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Matt Ballistic Black Metallic, red subframe.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin Adventure Sports ES (2024-26)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 243 kg, 19/18 spokes. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.55f, RakeDeg = 27.5f, Trail = 0.106f, ForkLength = 0.842f,
                FrontTyre = "110/80R19", RearTyre = "150/70R18", FrontTravel = 0.21f, RearTravel = 0.2f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 24.8f, GroundClearance = 0.22f, ScreenHeight = 0.36f,
                CrashBars = true,
                Seat = new Vector3(0, 0.835f, -0.2f), Grip = new Vector3(0.43f, 1.135f, 0.3f), Peg = new Vector3(0.19f, 0.42f, -0.05f),
                Paint = new Color(0.13f, 0.13f, 0.14f), Trim = new Color(0.33f, 0.34f, 0.36f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.08f, 0.08f, 0.09f), Wheel = new Color(0.08f, 0.08f, 0.09f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "2024: Pearl Glare White Tricolour; Matt Ballistic Black Metallic; 2025: Pearl Glare White Tricolour; Matt Iridium Gray Metallic; 2026: Pearl Glare White with revised frame colour + tricolour graphic; Matt Iridium Gray Metallic with revised frame colour and graphics",
            WetMass = 243f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.59f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 62f), (2500f, 80f), (3500f, 95f), (4500f, 106f), (5500f, 112f), (6250f, 108f), (7000f, 101f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.866f, 1.888f, 1.48f, 1.23f, 1.064f, 0.972f }, Primary = 1.717f, FinalDrive = 2.625f,
        },
        // ---- 93 ----
        // Honda CRF1100L Africa Twin Adventure Sports ES DCT (2024-26). docs/data/africa_twin_specs.json "CRF1100L-AS-ES-2024plus-DCT" (2024-2026; Adventure Sports ES, DCT).
        // 1084 cc 270° parallel twin, 75 kW at 7,500 rpm, 112 N·m at 5,500; DCT box, primary 1.863, final 2.625; kerb 253 kg; wheelbase 1550 mm; rake 27.5°, trail 106 mm;
        // seat 835 mm; clearance 220 mm; travel 210/200 mm; 24.8 L; 110/80R19 + 150/70R18; dual 310 mm wave floating discs front, 256 mm wave disc rear.
        // Measured: 202.8 km/h, 0-100 3.44 s (measured (inSella, Italy, Sport map)).
        // https://hondanews.eu/eu/en/motorcycles/media/pressreleases/483985/25ym-honda-crf1100l-africa-twin-adventure-sports
        // https://hondanews.eu/gb/en/motorcycles/media/pressreleases/568699/26ym-honda-crf1100l-africa-twin-adventure-sports
        // https://global.honda/content/dam/site/global-jp/news/cq_img/2024-new/02/2240229-crf1100l_link.pdf
        // Assumed: the family values under "crf1100" at the top of the Africa Twins; livery Pearl Glare White Tricolour, gold rims.
        new MotorbikeSpec
        {
            Label = "Honda CRF1100L Africa Twin Adventure Sports ES DCT (2024-26)",
            Brand = "Honda", Family = "Africa Twin",
            Blurb = "1084 cc 270° parallel twin, 102 PS, 253 kg, 19/18 spokes, DCT automatic. Adventure tyres: grips on gravel and grass",
            Engine = EngineLayout.ParallelTwin270,
            Look = new MotoLook
            {
                Style = MotoStyle.Adventure, EngineShape = MotoEngineShape.ParallelTwin, VAngleDeg = 90f, Spoked = true,
                Wheelbase = 1.55f, RakeDeg = 27.5f, Trail = 0.106f, ForkLength = 0.842f,
                FrontTyre = "110/80R19", RearTyre = "150/70R18", FrontTravel = 0.21f, RearTravel = 0.2f,
                FrontDiscs = 2, FrontDiscMm = 310f, TankLitres = 24.8f, GroundClearance = 0.22f, ScreenHeight = 0.36f,
                CrashBars = true,
                Seat = new Vector3(0, 0.835f, -0.2f), Grip = new Vector3(0.43f, 1.135f, 0.3f), Peg = new Vector3(0.19f, 0.42f, -0.05f),
                Paint = new Color(0.93f, 0.93f, 0.9f), Trim = new Color(0.07f, 0.17f, 0.55f), Accent = new Color(0.8f, 0.06f, 0.07f),
                Frame = new Color(0.8f, 0.06f, 0.07f), Wheel = new Color(0.78f, 0.6f, 0.2f), SeatColor = new Color(0.08f, 0.08f, 0.09f),
            },
            Colours = "2024: Pearl Glare White Tricolour; Matt Ballistic Black Metallic; 2025: Pearl Glare White Tricolour; Matt Iridium Gray Metallic; 2026: Pearl Glare White with revised frame colour + tricolour graphic; Matt Iridium Gray Metallic with revised frame colour and graphics",
            WetMass = 253f, CgHeight = 0.76f, RearShare = 0.5f, Grip = 1f, OffroadTyre = 0.3f, MaxLean = Mathf.DegToRad(45f),
            DragArea = 0.59f, BrakeDecel = 9.5f,
            IdleRpm = 1250f, PeakRpm = 7500f, Redline = 8200f, LaunchRpm = 4000f,
            Torque = new (float, float)[] { (1250f, 62f), (2500f, 80f), (3500f, 95f), (4500f, 106f), (5500f, 112f), (6250f, 108f), (7000f, 101f), (7500f, 95.5f), (8200f, 84f) },
            Gears = new[] { 2.562f, 1.761f, 1.375f, 1.133f, 0.972f, 0.882f }, Primary = 1.863f, FinalDrive = 2.625f,
            Dct = true,
            RefZeroTo100 = 3.44f, RefTopKmh = 202.8f,
        },

    }.Concat(Imported()).ToArray());

    /// <summary>The spec for a motorbike kind, or null when the kind is not a motorbike.</summary>
    public static MotorbikeSpec? For(RideKind kind)
    {
        int i = IndexOf(kind);
        return i >= 0 && i < All.Count ? All[i] : null;
    }

    public static bool IsMotorbike(RideKind kind) => IndexOf(kind) >= 0;

    /// <summary>The catalog index a kind stands for (whether or not that entry exists yet), or −1.</summary>
    private static int IndexOf(RideKind kind)
    {
        int k = (int)kind;
        return k >= First && k <= Last ? k - First : k >= First2 && k <= Last2 ? FirstRangeSize + k - First2 : -1;
    }

    private static RideKind KindAt(int index) =>
        (RideKind)(index < FirstRangeSize ? First + index : First2 + index - FirstRangeSize);

    private static IReadOnlyList<MotorbikeSpec> Number(MotorbikeSpec[] bikes)
    {
        if (bikes.Length > FirstRangeSize + Last2 - First2 + 1)
            throw new System.InvalidOperationException($"{bikes.Length} motorbikes overflow RideKind {First}..{Last} + {First2}..{Last2}");
        for (int i = 0; i < bikes.Length; i++) bikes[i] = bikes[i] with { Kind = KindAt(i) };
        return bikes;
    }
}
