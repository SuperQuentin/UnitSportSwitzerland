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
/// <b>Append-only.</b> A heavy vehicle's <see cref="RideKind"/> is replicated as an int. Heavy
/// vehicles own 96..119; <see cref="RideKind.Trailer"/> is 120. The first five (96..100) are
/// numbered by their position here; <b>every later entry names its own</b> (<c>Kind = (RideKind)N</c>),
/// so branches appending at the same time keep their numbers: 101 is the F-150 (#470), 102 and 103
/// the farm tractor and the combine (#541), 104 the tipper and 105 the mixer (#613). A clash, or an
/// unnamed entry past the fifth, throws at start-up.
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
    // invented site contractors (#613): colours only, as every operator here
    private static readonly Color ContractorRed = new(0.78f, 0.12f, 0.08f);
    private static readonly Color ContractorBlue = new(0.10f, 0.30f, 0.62f);

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

        // ---- 104: a four-axle tipper for the building sites (#613; 101-103 are #470's and #541's) ----
        new HeavySpec
        {
            Kind = (RideKind)104,
            Label = "Arocs 3245 8x4 tipper",
            Blurb = "Four-axle site tipper, two steered front axles, 450 hp. Stopped, {destination} tips the body up and down; pulling away drops it",
            Class = HeavyClass.Rigid,
            Body = TruckBody.Tipper,
            Look = new HeavyLook { Paint = ContractorRed, Accent = White, Lower = Graphite, Cargo = Graphite, Operator = "Gruber Bau AG" },
            // source: Mercedes-Benz Arocs 3245 K 8x4/4 (OM 471, 330 kW at 1,600 rpm, 2,200 N·m at
            // 1,100; PowerShift 3 G281-12, 14.93..1.00); 1,700 + 3,250 + 1,350 mm wheelbase, 32 t GVW.
            // Assumed: the 14,200 kg kerb with a 16 m³ Meiller body split 7,600 / 6,600, CG heights,
            // the site axle ratio 3.91, the reverse ratio, the second axle steering 75% of the first,
            // the engine brake. The operator is invented.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "tipper", Length = 9.4f, Height = 3.4f, Mass = 14200f, CgAt = 4.3f, CgHeight = 1.45f,
                    Axles = new[]
                    {
                        new AxleSpec(1.45f, 0, Steer: 1f, Tyre: "385/65R22.5"),
                        new AxleSpec(3.15f, 0, Steer: 0.75f, Tyre: "385/65R22.5"),
                        new AxleSpec(6.40f, 1, Driven: true, Twin: true),
                        new AxleSpec(7.75f, 1, Driven: true, Twin: true),
                    },
                    DragArea = 6.0f, Track = 2.05f,
                    PayloadMax = 17800f, PayloadAt = 5.9f, PayloadHeight = 2.1f,
                },
            },
            PeakKw = 330f, PeakRpm = 1600f, IdleRpm = 550f, Redline = 1900f,
            Torque = new (float, float)[] { (500f, 1200f), (800f, 1900f), (1100f, 2200f), (1400f, 2200f), (1600f, 1970f), (1800f, 1650f), (1900f, 1350f) },
            EngineBrakeNm = 1400f, RetarderNm = 0f, RetarderKw = 0f,
            Gears = new[] { 14.93f, 11.67f, 9.02f, 7.06f, 5.61f, 4.39f, 3.40f, 2.66f, 2.11f, 1.65f, 1.28f, 1.00f },
            Reverse = 13.0f, FinalDrive = 3.91f, ShiftTime = 0.5f,
            MaxSteer = 0.70f, LimiterKmh = 85f,
        },

        // ---- 105: a four-axle concrete mixer (#613) ----
        new HeavySpec
        {
            Kind = (RideKind)105,
            Label = "Arocs 3240 8x4 mixer",
            Blurb = "Four-axle truck mixer with a 9 m³ drum that turns while the engine runs. Stopped, {destination} reverses it to discharge down its chute",
            Class = HeavyClass.Rigid,
            Body = TruckBody.Mixer,
            Look = new HeavyLook { Paint = White, Accent = ContractorBlue, Lower = Graphite, Cargo = ContractorBlue, Operator = "Betonwerk Aare" },
            // source: Mercedes-Benz Arocs 3240 B 8x4/4 (OM 470, 290 kW at 1,600 rpm, 1,900 N·m at
            // 1,100) with a Liebherr HTM 905 (9 m³ drum, 0-14 rpm, hydraulic drive off the engine);
            // 4,250 + 1,350 mm wheelbase, 32 t GVW. Assumed: the 13,200 kg kerb split 7,100 / 6,100,
            // the high CG of a loaded drum, the gearbox as the tipper's, the axle ratio 3.91. The
            // operator is invented.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "mixer", Length = 9.2f, Height = 3.9f, Mass = 13200f, CgAt = 4.2f, CgHeight = 1.6f,
                    Axles = new[]
                    {
                        new AxleSpec(1.45f, 0, Steer: 1f, Tyre: "385/65R22.5"),
                        new AxleSpec(3.15f, 0, Steer: 0.75f, Tyre: "385/65R22.5"),
                        new AxleSpec(6.40f, 1, Driven: true, Twin: true),
                        new AxleSpec(7.75f, 1, Driven: true, Twin: true),
                    },
                    DragArea = 6.4f, Track = 2.05f,
                    PayloadMax = 18800f, PayloadAt = 5.6f, PayloadHeight = 2.5f, Liquid = true,
                },
            },
            PeakKw = 290f, PeakRpm = 1600f, IdleRpm = 550f, Redline = 1900f,
            Torque = new (float, float)[] { (500f, 1050f), (800f, 1650f), (1100f, 1900f), (1400f, 1900f), (1600f, 1730f), (1800f, 1450f), (1900f, 1200f) },
            EngineBrakeNm = 1300f, RetarderNm = 0f, RetarderKw = 0f,
            Gears = new[] { 14.93f, 11.67f, 9.02f, 7.06f, 5.61f, 4.39f, 3.40f, 2.66f, 2.11f, 1.65f, 1.28f, 1.00f },
            Reverse = 13.0f, FinalDrive = 3.91f, ShiftTime = 0.5f,
            MaxSteer = 0.70f, LimiterKmh = 85f,
        },
    });

    private static readonly Dictionary<RideKind, HeavySpec> ByKind = All.ToDictionary(s => s.Kind);

    public static HeavySpec? For(RideKind kind) => ByKind.GetValueOrDefault(kind);

    /// <summary>The first entries numbered by their position; every later one names its own kind.</summary>
    private const int Positional = 5;

    private static IReadOnlyList<HeavySpec> Number(HeavySpec[] specs)
    {
        var seen = new HashSet<RideKind>();
        for (int i = 0; i < specs.Length; i++)
        {
            if (i < Positional) specs[i] = specs[i] with { Kind = (RideKind)(First + i) };
            else if ((int)specs[i].Kind is < First or > Last)
                throw new System.InvalidOperationException($"{specs[i].Label}: heavy entries past the fifth name their RideKind in {First}..{Last}");
            if (!seen.Add(specs[i].Kind))
                throw new System.InvalidOperationException($"{specs[i].Label}: RideKind {(int)specs[i].Kind} is taken twice");
        }
        return specs;
    }
}
