using System.Collections.Generic;
using Godot;
using UnitSport.Audio;

namespace UnitSport.Player;

/// <summary>
/// Every drivable truck and bus (#70): a few representative Swiss-road machines, each with its
/// real figures (dimensions, masses, engine, gearbox) and a Swiss operator's colours — colours
/// only, no names or logos are drawn.
///
/// <para>
/// <b>Append-only.</b> A heavy vehicle's <see cref="RideKind"/> is <c>First + its index here</c>,
/// replicated as an int. Heavy vehicles own 96..119; <see cref="RideKind.Trailer"/> is 120.
/// </para>
///
/// <para>
/// Positions are metres behind the front of each section, as a data sheet lists them (front
/// overhang, wheelbase). Where a figure is not published the entry says it is assumed. Swiss
/// limits the trains respect: 40 t, 16.5 m articulated, 18.75 m truck and trailer, 2.55 m wide.
/// </para>
/// </summary>
public static class HeavyCatalog
{
    public const int First = 96, Last = 119;

    // colours in sRGB, as every palette here
    private static readonly Color PostYellow = new(1.0f, 0.80f, 0.0f);
    private static readonly Color Black = new(0.07f, 0.07f, 0.08f);
    private static readonly Color Graphite = new(0.22f, 0.23f, 0.25f);
    private static readonly Color White = new(0.95f, 0.95f, 0.94f);
    private static readonly Color MigrosOrange = new(1.0f, 0.40f, 0.0f);
    private static readonly Color VbzBlue = new(0.0f, 0.35f, 0.65f);
    private static readonly Color BernRed = new(0.80f, 0.07f, 0.12f);
    private static readonly Color RaptorOrange = new(1.0f, 0.42f, 0.08f);
    private static readonly Color FendtGreen = new(0.24f, 0.45f, 0.16f);
    private static readonly Color ClaasGreen = new(0.47f, 0.63f, 0.1f);

    public static readonly IReadOnlyList<HeavySpec> All = Number(new[]
    {
        // ---- 96: a long-haul tractor for the semi-trailers ----
        new HeavySpec
        {
            Label = "Scania R 450 tractor",
            Blurb = "4x2 tractor, 450 hp, 12-speed Opticruise, retarder. Takes a semi-trailer: reverse the fifth wheel under its kingpin and {couple}",
            Class = HeavyClass.Tractor,
            Look = new HeavyLook { Paint = PostYellow, Accent = Black, Lower = Graphite, Operator = "Swiss Post" },
            // source: Scania R 450 A4x2NA (2016+ "NTG"): DC13 155, 331 kW at 1,900 rpm, 2,350 N·m at
            // 1,000-1,300; GRS905 range-splitter 11.32..1.00; R780 axle 2.59; 3,700 mm wheelbase.
            // Assumed: kerb 7,400 kg with driver and fuel split 5,050 / 2,350, CG height, the fifth
            // wheel 550 mm ahead of the drive axle, the torque curve between the published points,
            // the reverse ratio, the exhaust brake and the R4100D retarder's power.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "tractor", Length = 5.9f, Height = 3.95f, Mass = 7400f, CgAt = 2.625f, CgHeight = 1.15f,
                    Axles = new[]
                    {
                        new AxleSpec(1.45f, 0, Steer: 1f),
                        new AxleSpec(5.15f, 1, Driven: true, Twin: true),
                    },
                    HitchAt = 4.60f, HitchHeight = 1.15f, Hitch = Coupling.FifthWheel,
                    DragArea = 5.6f, Track = 2.0f,
                },
            },
            PeakKw = 331f, PeakRpm = 1900f, IdleRpm = 550f, Redline = 2150f,
            Torque = new (float, float)[] { (500f, 1150f), (800f, 1800f), (1000f, 2350f), (1300f, 2350f), (1400f, 2250f), (1600f, 1975f), (1900f, 1664f), (2100f, 1300f), (2150f, 1100f) },
            EngineBrakeNm = 1300f, RetarderNm = 4100f, RetarderKw = 500f,
            Gears = new[] { 11.32f, 9.16f, 7.19f, 5.82f, 4.58f, 3.70f, 3.02f, 2.44f, 1.92f, 1.55f, 1.24f, 1.00f },
            Reverse = 11.0f, FinalDrive = 2.59f, ShiftTime = 0.5f,
            MaxSteer = 0.78f, LimiterKmh = 89f,
        },

        // ---- 97: a distribution rigid with a swap body, for the drawbar trailer ----
        new HeavySpec
        {
            Label = "MAN TGS 26.440 rigid",
            Blurb = "6x2 rigid with a swap body and a steered tag axle, 440 hp, 12-speed TipMatic. Takes a drawbar trailer on its hitch ({couple})",
            Class = HeavyClass.Rigid,
            Look = new HeavyLook { Paint = White, Accent = MigrosOrange, Lower = Graphite, Cargo = White, Operator = "Migros" },
            // source: MAN TGS 26.440 6x2-4 BL (D2676 LF, 324 kW at 1,800 rpm, 2,100 N·m at 930-1,350;
            // TipMatic 12 = ZF TraXon 12TX direct, 16.69..1.00); 4,800 + 1,350 mm wheelbase, 26 t GVW.
            // Assumed: 11,800 kg with the 7.45 m swap body split 6,200 / 5,600, CG height, the axle
            // ratio 3.08, the reverse ratio, the tag axle steering 35% against the front, the
            // engine brake and intarder figures. Payload capped at 12 t so a full train is 40 t.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "rigid", Length = 10.0f, Height = 4.0f, Mass = 11800f, CgAt = 4.0f, CgHeight = 1.35f,
                    Axles = new[]
                    {
                        new AxleSpec(1.40f, 0, Steer: 1f, Tyre: "315/80R22.5"),
                        new AxleSpec(6.20f, 1, Driven: true, Twin: true),
                        new AxleSpec(7.55f, 1, Steer: -0.35f, Tyre: "385/55R22.5"),
                    },
                    HitchAt = 9.85f, HitchHeight = 0.95f, Hitch = Coupling.Drawbar,
                    DragArea = 6.3f, Track = 2.0f,
                    PayloadMax = 12000f, PayloadAt = 6.3f, PayloadHeight = 2.2f,
                },
            },
            PeakKw = 324f, PeakRpm = 1800f, IdleRpm = 550f, Redline = 2000f,
            Torque = new (float, float)[] { (500f, 1100f), (700f, 1700f), (930f, 2100f), (1350f, 2100f), (1500f, 1990f), (1800f, 1719f), (1900f, 1550f), (2000f, 1300f) },
            EngineBrakeNm = 1300f, RetarderNm = 3200f, RetarderKw = 400f,
            Gears = new[] { 16.69f, 12.92f, 9.93f, 7.67f, 5.90f, 4.57f, 3.66f, 2.83f, 2.17f, 1.68f, 1.29f, 1.00f },
            Reverse = 15.5f, FinalDrive = 3.08f, ShiftTime = 0.5f,
            MaxSteer = 0.72f, LimiterKmh = 89f,
        },

        // ---- 98: a 12 m city bus ----
        new HeavySpec
        {
            Label = "Citaro city bus",
            Blurb = "12 m low-floor city bus, three double doors ({car_door}), kneeling ({kneel}), destination display ({destination}). Converter automatic",
            Class = HeavyClass.CityBus,
            Look = new HeavyLook
            {
                Paint = White, Accent = VbzBlue, Lower = VbzBlue, Operator = "VBZ Zürich",
                Doors = new[] { (1.15f, 1.2f), (5.8f, 1.2f), (10.3f, 1.0f) },
                Destinations = new[] { "Zürich HB", "Bahnhof Oerlikon", "Bahnhof Altstetten", "Bellevue", "Extrafahrt", "Ausser Betrieb" },
            },
            // source: Mercedes-Benz Citaro (O 530, C2): 12,135 x 2,550 x 3,120 mm, wheelbase 5,845,
            // overhangs 2,805 / 3,485; OM 936 h 220 kW at 2,200 rpm, 1,200 N·m at 1,200-1,600; ZF
            // EcoLife 6AP 3.36..0.62. Assumed: 11,200 kg split 3,900 / 7,300, CG height, the axle
            // ratio 5.74, 100 passengers at 75 kg, the retarder and a small exhaust brake.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "bus", Length = 12.135f, Height = 3.12f, Mass = 11200f, CgAt = 6.615f, CgHeight = 1.15f,
                    Axles = new[]
                    {
                        new AxleSpec(2.805f, 0, Steer: 1f, Tyre: "275/70R22.5"),
                        new AxleSpec(8.65f, 1, Driven: true, Twin: true, Tyre: "275/70R22.5"),
                    },
                    DragArea = 5.2f, Track = 2.05f,
                    PayloadMax = 7500f, PayloadAt = 6.3f, PayloadHeight = 1.35f,
                },
            },
            PeakKw = 220f, PeakRpm = 2200f, IdleRpm = 600f, Redline = 2400f,
            Torque = new (float, float)[] { (600f, 750f), (900f, 1000f), (1200f, 1200f), (1600f, 1200f), (1800f, 1100f), (2200f, 955f), (2400f, 700f) },
            EngineBrakeNm = 500f, RetarderNm = 2600f, RetarderKw = 350f,
            Box = Transmission.TorqueConverter,
            Gears = new[] { 3.36f, 1.91f, 1.42f, 1.00f, 0.72f, 0.62f },
            Reverse = 4.24f, FinalDrive = 5.74f, ShiftTime = 0.3f,
            MaxSteer = 0.69f, BrakeDecel = 6.0f, LimiterKmh = 80f, Passengers = 100,
        },

        // ---- 99: an 18 m articulated pusher ----
        new HeavySpec
        {
            Label = "Citaro G articulated",
            Blurb = "18 m articulated pusher: the engine drives the rear half through a damped joint. Four doors ({car_door}), kneeling ({kneel}), destination ({destination})",
            Class = HeavyClass.ArticulatedBus,
            Look = new HeavyLook
            {
                Paint = BernRed, Accent = White, Lower = Graphite, Operator = "Bernmobil",
                Doors = new[] { (1.15f, 1.2f), (5.8f, 1.2f), (12.1f, 1.2f), (16.4f, 1.2f) },
                Destinations = new[] { "Bern Bahnhof", "Bümpliz", "Ostermundigen", "Wankdorf", "Extrafahrt", "Ausser Betrieb" },
            },
            // source: Mercedes-Benz Citaro G (O 530 G, C2): 18,125 mm long, wheelbases 5,845 + 5,990,
            // overhangs 2,805 / 3,485; the rear axle driven (pusher); OM 470 h 265 kW, 1,700 N·m; ZF
            // EcoLife. Assumed: the joint 1.40 m behind the middle axle, the 7,700 / 8,800 kg split
            // of the 16.5 t kerb mass, CG heights, the axle ratio 6.21, the torque curve, 150
            // passengers, the joint's damping and its 54° stop.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "front", Length = 9.6f, Height = 3.12f, Mass = 7700f, CgAt = 5.39f, CgHeight = 1.15f,
                    Axles = new[]
                    {
                        new AxleSpec(2.805f, 0, Steer: 1f, Tyre: "275/70R22.5"),
                        new AxleSpec(8.65f, 1, Twin: true, Tyre: "275/70R22.5"),
                    },
                    HitchAt = 10.05f, HitchHeight = 0.9f, Hitch = Coupling.BusJoint,
                    DragArea = 5.2f, Track = 2.05f,
                    PayloadMax = 6400f, PayloadAt = 6.0f, PayloadHeight = 1.35f,
                },
                new SectionSpec
                {
                    Name = "rear", Length = 8.525f, Height = 3.12f, Mass = 8800f, CgAt = 4.2f, CgHeight = 1.2f,
                    Axles = new[] { new AxleSpec(5.04f, 1, Driven: true, Twin: true, Tyre: "275/70R22.5") },
                    PivotAt = 0.45f, Pivot = Coupling.BusJoint, MaxArticulation = 0.95f, JointDamping = 60000f,
                    DragArea = 0.4f, Track = 2.05f,
                    PayloadMax = 4300f, PayloadAt = 4.0f, PayloadHeight = 1.35f,
                },
            },
            PeakKw = 265f, PeakRpm = 1800f, IdleRpm = 550f, Redline = 2100f,
            Torque = new (float, float)[] { (550f, 900f), (800f, 1400f), (1100f, 1700f), (1400f, 1700f), (1800f, 1406f), (2100f, 1000f) },
            EngineBrakeNm = 700f, RetarderNm = 3000f, RetarderKw = 400f,
            Box = Transmission.TorqueConverter,
            Gears = new[] { 3.36f, 1.91f, 1.42f, 1.00f, 0.72f, 0.62f },
            Reverse = 4.24f, FinalDrive = 6.21f, ShiftTime = 0.3f,
            MaxSteer = 0.72f, BrakeDecel = 6.0f, LimiterKmh = 80f, Passengers = 150,
        },

        // ---- 100: an intercity coach ----
        new HeavySpec
        {
            Label = "Setra S 516 HD coach",
            Blurb = "12.2 m high-deck coach for the passes, 428 hp, 8-speed PowerShift, retarder. Two doors ({car_door}), destination ({destination})",
            Class = HeavyClass.Coach,
            Look = new HeavyLook
            {
                Paint = PostYellow, Accent = Black, Lower = Graphite, Operator = "PostAuto",
                Doors = new[] { (1.3f, 0.95f), (6.7f, 0.95f) },
                Destinations = new[] { "Chur", "Flims", "Andermatt", "Grimselpass", "Sustenpass", "Extrafahrt" },
            },
            // source: Setra S 516 HD (ComfortClass): 12,200 x 2,550 x 3,730 mm, wheelbase 6,080,
            // 53 seats; OM 470 315 kW, 2,100 N·m at 1,100 rpm; GO 250-8 PowerShift. Assumed: the
            // gearbox ratios (8 speeds, 9.30..1.00) and axle 2.73, 13,800 kg split 5,300 / 8,500, CG
            // height, the retarder and engine brake, 75 kg per passenger with luggage.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "coach", Length = 12.2f, Height = 3.73f, Mass = 13800f, CgAt = 6.345f, CgHeight = 1.45f,
                    Axles = new[]
                    {
                        new AxleSpec(2.6f, 0, Steer: 1f, Tyre: "295/80R22.5"),
                        new AxleSpec(8.68f, 1, Driven: true, Twin: true, Tyre: "295/80R22.5"),
                    },
                    DragArea = 3.4f, Track = 2.05f,
                    PayloadMax = 4000f, PayloadAt = 6.2f, PayloadHeight = 2.0f,
                },
            },
            PeakKw = 315f, PeakRpm = 1800f, IdleRpm = 550f, Redline = 2100f,
            Torque = new (float, float)[] { (550f, 1100f), (800f, 1700f), (1100f, 2100f), (1400f, 2100f), (1600f, 1880f), (1800f, 1671f), (2000f, 1350f), (2100f, 1100f) },
            EngineBrakeNm = 1500f, RetarderNm = 3200f, RetarderKw = 500f,
            Gears = new[] { 9.30f, 6.60f, 4.70f, 3.35f, 2.40f, 1.72f, 1.30f, 1.00f },
            Reverse = 8.5f, FinalDrive = 2.73f, ShiftTime = 0.45f,
            MaxSteer = 0.70f, LimiterKmh = 100f, Passengers = 53,
        },

        // ---- 101: a full-size pickup with a tow ball, for the boat trailers (#463) ----
        new HeavySpec
        {
            Label = "Ford F-150 Raptor",
            Blurb = "SuperCrew desert pickup, 3.5 L twin-turbo V6, 450 hp, 10-speed automatic, 4x4 on 35-inch tyres. A 50 mm tow ball: back it up to a boat trailer's coupler and {couple}",
            Class = HeavyClass.Pickup,
            Look = new HeavyLook { Paint = RaptorOrange, Accent = Black, Lower = Graphite, Cargo = Black },
            Engine = EngineLayout.V6Turbo,
            // source: Ford F-150 Raptor (P702, 2021+): 3.5 L EcoBoost HO V6, 450 hp (336 kW) at
            // 5,850 rpm, 510 lb-ft (691 N·m) at 3,500; 10R80 10-speed automatic 4.696..0.636, reverse
            // 4.866, 4.10 axle; 5,890 x 2,200 x 1,990 mm, wheelbase 3,686 (SuperCrew 5.5 ft bed),
            // 315/70R17 tyres, track 1.86 m, ~2,600 kg, 3.7 t braked towing, governed 172 km/h.
            // Assumed: the 57/43 split, CG height, the torque curve between the published points,
            // the converter's stall, the engine's inertia and braking, the ball 0.55 m up on a drop
            // hitch 0.12 m behind the bumper, a 600 kg bed load.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "pickup", Length = 5.89f, Width = 2.2f, Height = 1.99f, Mass = 2650f, CgAt = 2.55f, CgHeight = 0.75f,
                    Axles = new[]
                    {
                        new AxleSpec(0.97f, 0, Steer: 1f, Driven: true, Tyre: "315/70R17"),
                        new AxleSpec(4.656f, 1, Driven: true, Tyre: "315/70R17"),
                    },
                    HitchAt = 6.01f, HitchHeight = 0.55f, Hitch = Coupling.Ball,
                    DragArea = 1.65f, Track = 1.86f,
                    PayloadMax = 600f, PayloadAt = 4.9f, PayloadHeight = 1.15f,
                },
            },
            PeakKw = 336f, PeakRpm = 5850f, IdleRpm = 650f, Redline = 6250f,
            Torque = new (float, float)[] { (650f, 300f), (1500f, 560f), (2500f, 670f), (3500f, 691f), (4500f, 640f), (5850f, 548f), (6250f, 470f) },
            EngineBrakeNm = 90f, EngineInertia = 0.3f, StallRpm = 2600f, AirBrakes = false,
            Box = Transmission.TorqueConverter,
            Gears = new[] { 4.696f, 2.985f, 2.146f, 1.769f, 1.520f, 1.275f, 1.000f, 0.854f, 0.689f, 0.636f },
            Reverse = 4.866f, FinalDrive = 4.10f, ShiftTime = 0.25f,
            MaxSteer = 0.6f, Grip = 0.92f, BrakeDecel = 8.5f, LimiterKmh = 172f, Passengers = 4,
        },

        // ---- 102: a mid-size farm tractor (#494) ----
        new HeavySpec
        {
            Label = "Fendt 724 Vario",
            Blurb = "4WD farm tractor, 246 hp, stepless Vario, 40 km/h. A drawbar for the tipping trailer and a three-point linkage for the plough, seed drill or mower: back up to one and {couple}, {kneel} lowers and raises it",
            Class = HeavyClass.FarmTractor,
            Look = new HeavyLook { Paint = FendtGreen, Accent = Graphite, Lower = Black, Cargo = FendtGreen },
            // source: Fendt 724 Vario Gen6 (technical data sheet): Deutz TCD 6.1 L6, 174 kW rated,
            // 181 kW (246 hp) max, 1,072 N·m at 1,500 rpm; ML 220 Vario CVT, 40 km/h; wheelbase
            // 2,900 mm; 9,250 kg operating weight; 540/65R30 front, 650/65R42 rear.
            // Assumed: 5.05 m long and 3.1 m to the cab roof, the 45/55 split, CG height, the
            // torque curve between the published points, the Vario as sixteen close ratios walked
            // through without a pause (HeavySpec.Stepless; no CVT model), the hitch 0.85 m behind
            // the rear axle and 0.55 m up (drawbar and lower links at one point), hydraulic brakes
            // to 4.5 m/s², a 55° lock, the engine brake.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "tractor", Length = 5.05f, Width = 2.55f, Height = 3.1f, Mass = 9250f, CgAt = 2.895f, CgHeight = 1.0f,
                    Axles = new[]
                    {
                        new AxleSpec(1.30f, 0, Steer: 1f, Driven: true, Tyre: "540/65R30"),
                        new AxleSpec(4.20f, 1, Driven: true, Tyre: "650/65R42"),
                    },
                    HitchAt = 5.05f, HitchHeight = 0.55f, Hitch = Coupling.Drawbar,
                    DragArea = 4.5f, Track = 1.95f,
                },
            },
            Mount = Coupling.ThreePoint,
            PeakKw = 181f, PeakRpm = 1700f, IdleRpm = 800f, Redline = 2100f,
            Torque = new (float, float)[] { (800f, 700f), (1000f, 920f), (1200f, 1040f), (1500f, 1072f), (1700f, 1015f), (1900f, 840f), (2100f, 500f) },
            EngineBrakeNm = 300f, EngineInertia = 1.6f, AirBrakes = false,
            Stepless = true,
            // 40 km/h at 1,700 rpm on the 0.93 m rear wheels: 14.9 overall in the top ratio
            Gears = SteplessRatios(26.8f, TractorRatios),
            Reverse = 3.0f, FinalDrive = 14.9f, ShiftTime = 0.05f,
            MaxSteer = 0.95f, Grip = 0.85f, BrakeDecel = 4.5f, LimiterKmh = 40f, Passengers = 1,
        },

        // ---- 103: a combine harvester (#494) ----
        new HeavySpec
        {
            Label = "Claas Lexion 6800",
            Blurb = "Combine harvester, 462 hp, 6 m header, steered at the rear, hydrostatic drive to 25 km/h. {kneel} lowers the header and threshes, {destination} swings the auger out over a tipping trailer",
            Class = HeavyClass.Combine,
            Look = new HeavyLook { Paint = ClaasGreen, Accent = White, Lower = Graphite, Cargo = Graphite },
            // source: Claas Lexion 6800 (2020+): 340 kW (462 hp); a Vario 620 header cuts 6.2 m;
            // 800/65R32 drive wheels, 600/70R28 steered rear; hydrostatic drive with a range box,
            // 25 km/h on the road. Assumed: 10.4 m with the header on (the section's front is the
            // cutter bar), 3.3 m body width, 3.95 m tall, a 3.95 m wheelbase, 18 t with the header
            // split 65/35, CG height, the torque curve, the hydrostat as a converter with three
            // ranges, hydraulic brakes, a 140-sack (7 t) tank where the brochure gives ~11,000 l.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "combine", Length = 10.4f, Width = 3.3f, Height = 3.95f, Mass = 18000f, CgAt = 4.98f, CgHeight = 1.6f,
                    Axles = new[]
                    {
                        new AxleSpec(3.6f, 0, Driven: true, Tyre: "800/65R32"),
                        new AxleSpec(7.55f, 1, Steer: -1f, Tyre: "600/70R28"),
                    },
                    DragArea = 8f, Track = 2.6f,
                    PayloadMax = 7000f, PayloadAt = 5.4f, PayloadHeight = 3.0f,
                },
            },
            Tool = Farming.FarmTool.Harvest, WorkWidth = 6.0f, WorkAt = 0.35f, TankItems = 140, WorkKmh = 10f,
            PeakKw = 340f, PeakRpm = 1900f, IdleRpm = 900f, Redline = 2100f,
            Torque = new (float, float)[] { (900f, 1300f), (1200f, 1900f), (1500f, 2100f), (1700f, 1950f), (1900f, 1709f), (2100f, 900f) },
            EngineBrakeNm = 250f, EngineInertia = 3.0f, AirBrakes = false,
            Stepless = true,
            // 25 km/h at ~1,850 rpm on the 0.93 m drive wheels; the lowest ratio crawls at 2 km/h
            Gears = SteplessRatios(12f, CombineRatios),
            Reverse = 3.0f, FinalDrive = 25.7f, ShiftTime = 0.3f,
            MaxSteer = 0.75f, Grip = 0.8f, BrakeDecel = 3.5f, LimiterKmh = 25f, Passengers = 1,
        },
    });

    /// <summary>
    /// How many close ratios stand in for the Vario CVT and the combine's hydrostat (#494). A
    /// stepless box walks them one by one at its own pace (<c>HeavyDriveline</c>: a short hold each),
    /// so their number is how fast the ratio sweeps: a real Vario takes ~15-20 s to 40 km/h on its
    /// default acceleration stage, a combine's hydrostat lever longer to its 25 km/h.
    /// </summary>
    private const int TractorRatios = 84, CombineRatios = 64;

    /// <summary><paramref name="count"/> ratios from <paramref name="low"/> down to 1, evenly spaced on a log scale (a stepless box, #494).</summary>
    private static float[] SteplessRatios(float low, int count)
    {
        var r = new float[count];
        for (int i = 0; i < count; i++) r[i] = Mathf.Pow(low, 1f - i / (float)(count - 1));
        return r;
    }

    public static HeavySpec? For(RideKind kind)
    {
        int i = (int)kind - First;
        return i >= 0 && i < All.Count ? All[i] : null;
    }

    private static IReadOnlyList<HeavySpec> Number(HeavySpec[] specs)
    {
        if (specs.Length > Last - First + 1)
            throw new System.InvalidOperationException($"{specs.Length} heavy vehicles overflow RideKind {First}..{Last}");
        for (int i = 0; i < specs.Length; i++) specs[i] = specs[i] with { Kind = (RideKind)(First + i) };
        return specs;
    }
}
