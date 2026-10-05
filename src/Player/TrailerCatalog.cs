using System.Collections.Generic;
using Godot;

namespace UnitSport.Player;

/// <summary>
/// The trailers a truck can pick up (#70): three semi-trailers for the tractor's fifth wheel and a
/// drawbar trailer for the rigid's hitch; and a boat trailer for every boat that goes on the road
/// (#463), on a tow ball: the pickup's, or the ball of the rigid's combination coupling. Not mounts: a trailer is driven only behind a truck, so it
/// has no <see cref="RideKind"/> of its own, only a <see cref="Code"/>, which carries the load too.
///
/// <para>
/// <b>Append-only</b>: a trailer's code is its index here, replicated.
/// </para>
/// </summary>
public static class TrailerCatalog
{
    private static readonly Color PostYellow = new(1.0f, 0.80f, 0.0f);
    private static readonly Color White = new(0.95f, 0.95f, 0.94f);
    private static readonly Color MigrosOrange = new(1.0f, 0.40f, 0.0f);
    private static readonly Color Aluminium = new(0.78f, 0.79f, 0.8f);
    private static readonly Color FuelRed = new(0.75f, 0.1f, 0.08f);
    private static readonly Color ForestGreen = new(0.16f, 0.3f, 0.18f);
    private static readonly Color Galvanised = new(0.66f, 0.68f, 0.68f);
    private static readonly Color BunkCarpet = new(0.15f, 0.17f, 0.3f);
    private static readonly Color Graphite = new(0.22f, 0.23f, 0.25f);
    private static readonly Color PloughBlue = new(0.1f, 0.3f, 0.6f);
    private static readonly Color DrillGreen = new(0.2f, 0.5f, 0.25f);
    private static readonly Color MowerRed = new(0.8f, 0.12f, 0.1f);
    private static readonly Color TipperRed = new(0.7f, 0.12f, 0.1f);

    public static readonly IReadOnlyList<TrailerSpec> All = new[]
    {
        // ---- 0: the standard European semi-trailer ----
        new TrailerSpec
        {
            Label = "Curtainsider 13.6 m",
            Blurb = "Tri-axle curtainsider, 13.62 m: 16.5 m and up to 40 t behind a tractor",
            Body = TrailerBody.Curtainsider, Couples = Coupling.FifthWheel,
            Paint = PostYellow, Accent = new Color(0.12f, 0.12f, 0.13f), Operator = "Swiss Post",
            // source: a Krone Profi Liner / Schmitz S.CS class trailer: 13,620 x 2,550 x 4,000 mm,
            // kingpin 1,600 mm from the front, SAF tridem 1,310 mm spacing, tare ~6.8 t.
            // Assumed: the tridem centre 7.7 m behind the kingpin, CG heights, 25 t of pallets.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "curtainsider", Length = 13.62f, Height = 4.0f, Mass = 6800f, CgAt = 7.0f, CgHeight = 1.0f,
                    Axles = new[]
                    {
                        new AxleSpec(7.99f, 1, Tyre: "385/65R22.5"),
                        new AxleSpec(9.30f, 1, Tyre: "385/65R22.5"),
                        new AxleSpec(10.61f, 1, Tyre: "385/65R22.5"),
                    },
                    PivotAt = 1.6f, Pivot = Coupling.FifthWheel, MaxArticulation = 1.45f,
                    DragArea = 1.3f, Track = 2.04f,
                    PayloadMax = 25000f, PayloadAt = 6.9f, PayloadHeight = 2.1f,
                },
            },
        },

        // ---- 1: a fuel tanker, whose load sloshes ----
        new TrailerSpec
        {
            Label = "Fuel tanker",
            Blurb = "Tri-axle aluminium tanker, 30,000 l of diesel: a part-full tank sloshes, and its high load rolls it over sooner",
            Body = TrailerBody.Tanker, Couples = Coupling.FifthWheel,
            Paint = Aluminium, Accent = FuelRed,
            // Assumed throughout (a typical aluminium fuel semi): 11.8 m, kingpin 1.2 m from the front,
            // tare 6.5 t, 30,000 l at 0.84 kg/l, the tank's centre 2.05 m up.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "tanker", Length = 11.8f, Height = 3.6f, Mass = 6500f, CgAt = 6.2f, CgHeight = 1.3f,
                    Axles = new[]
                    {
                        new AxleSpec(7.40f, 1, Tyre: "385/65R22.5"),
                        new AxleSpec(8.71f, 1, Tyre: "385/65R22.5"),
                        new AxleSpec(10.02f, 1, Tyre: "385/65R22.5"),
                    },
                    PivotAt = 1.2f, Pivot = Coupling.FifthWheel, MaxArticulation = 1.45f,
                    DragArea = 1.0f, Track = 2.04f,
                    PayloadMax = 25200f, PayloadAt = 6.0f, PayloadHeight = 2.05f, Liquid = true,
                },
            },
        },

        // ---- 2: a timber trailer ----
        new TrailerSpec
        {
            Label = "Timber trailer",
            Blurb = "Tri-axle stake trailer loaded with spruce logs from the forest roads",
            Body = TrailerBody.Timber, Couples = Coupling.FifthWheel,
            Paint = new Color(0.55f, 0.36f, 0.2f), Frame = ForestGreen,
            // Assumed: 13 m, kingpin 1.6 m from the front, tare 6.2 t with stakes, 25 t of logs whose
            // centre is 2.2 m up.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "timber", Length = 13.0f, Height = 3.9f, Mass = 6200f, CgAt = 6.8f, CgHeight = 1.0f,
                    Axles = new[]
                    {
                        new AxleSpec(8.0f, 1, Tyre: "385/65R22.5"),
                        new AxleSpec(9.31f, 1, Tyre: "385/65R22.5"),
                        new AxleSpec(10.62f, 1, Tyre: "385/65R22.5"),
                    },
                    PivotAt = 1.6f, Pivot = Coupling.FifthWheel, MaxArticulation = 1.45f,
                    DragArea = 1.5f, Track = 2.04f,
                    PayloadMax = 25000f, PayloadAt = 7.0f, PayloadHeight = 2.2f,
                },
            },
        },

        // ---- 3: the Swiss road train's drawbar trailer ----
        new TrailerSpec
        {
            Label = "Drawbar trailer",
            Blurb = "Two-axle turntable trailer with a 7.45 m swap body: behind the rigid, an 18.75 m road train. Two pivots, so it reverses like nothing else",
            Body = TrailerBody.SwapBody, Couples = Coupling.Drawbar,
            Paint = White, Accent = MigrosOrange, Operator = "Migros",
            // Assumed: a turntable trailer for a 7.45 m swap body: the dolly (axle, turntable,
            // 2.6 m drawbar) 900 kg, the body 5.1 t with its swap body, wheelbase 4.8 m, 10 t
            // payload so the full road train is 40 t.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "dolly", Length = 2.9f, Width = 2.4f, Height = 1.1f, Mass = 900f, CgAt = 2.2f, CgHeight = 0.7f,
                    Axles = new[] { new AxleSpec(2.6f, 1, Tyre: "385/65R22.5") },
                    PivotAt = 0f, Pivot = Coupling.Drawbar, MaxArticulation = 1.2f,
                    HitchAt = 2.6f, HitchHeight = 1.0f, Hitch = Coupling.Turntable,
                    Track = 2.04f,
                },
                new SectionSpec
                {
                    Name = "swap body", Length = 7.45f, Height = 4.0f, Mass = 5100f, CgAt = 3.9f, CgHeight = 1.4f,
                    Axles = new[] { new AxleSpec(6.2f, 1, Tyre: "385/65R22.5") },
                    PivotAt = 1.4f, Pivot = Coupling.Turntable, MaxArticulation = 0.9f,
                    DragArea = 1.2f, Track = 2.04f,
                    PayloadMax = 10000f, PayloadAt = 3.725f, PayloadHeight = 2.3f,
                },
            },
        },

        // ---- 4: the jetski's road trailer (#463) ----
        new TrailerSpec
        {
            Label = "Jetski trailer",
            Blurb = "Galvanised single-axle trailer with the jetski on its bunks, 750 kg unbraked: on a tow ball. Back it into the lake and {car_door} to launch",
            Body = TrailerBody.Boat, Couples = Coupling.Ball,
            Paint = Galvanised, Accent = BunkCarpet, Frame = Galvanised,
            Boat = RideKind.Jetski, BoatAt = 1.0f + BoatCatalog.Jetski.Length - BoatCatalog.Jetski.Shape.SternZ, BoatKeel = 0.6f,
            // Assumed (a typical PWC trailer, ~4.4 m, 750 kg gross): tare 230 kg, 155/80R13 wheels,
            // the axle placed for ~10% nose weight with the jetski aboard (350 kg dry, 50 kg of fuel),
            // the bow 1.0 m behind the coupler, the keel on the bunks 0.6 m up.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "jetski trailer", Length = 4.45f, Width = 1.75f, Height = 1.75f, Mass = 230f, CgAt = 2.3f, CgHeight = 0.45f,
                    Axles = new[] { new AxleSpec(2.95f, 1, Tyre: "155/80R13") },
                    PivotAt = 0f, Pivot = Coupling.Ball, PivotHeight = 0.5f, MaxArticulation = 1.4f,
                    DragArea = 0.6f, Track = 1.45f,
                    PayloadMax = 400f, PayloadAt = 1.0f + BoatCatalog.Jetski.Length - BoatCatalog.Jetski.Shape.SternZ,
                    PayloadHeight = 0.6f + BoatCatalog.Jetski.CentreHeight,
                },
            },
        },

        // ---- 5: the speedboat's road trailer (#463) ----
        new TrailerSpec
        {
            Label = "Speedboat trailer",
            Blurb = "Braked tandem trailer with the 7 m runabout on its bunks, 2.6 t: on a tow ball. Back it into the lake and {car_door} to launch",
            Body = TrailerBody.Boat, Couples = Coupling.Ball,
            Paint = Galvanised, Accent = BunkCarpet, Frame = Galvanised,
            Boat = RideKind.Speedboat, BoatAt = 1.0f + BoatCatalog.Speedboat.Length - BoatCatalog.Speedboat.Shape.SternZ, BoatKeel = 0.55f,
            // Assumed (a braked tandem for a 7 m boat, 2.6-2.7 t gross): 8 m, tare 750 kg, 185/70R14
            // wheels 0.82 m apart, the pair placed for ~7% nose weight with the boat aboard (1.8 t
            // without its crew), the bow 1.0 m behind the coupler, the keel on the bunks 0.55 m up.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "speedboat trailer", Length = 8.0f, Width = 2.4f, Height = 2.1f, Mass = 750f, CgAt = 4.6f, CgHeight = 0.5f,
                    Axles = new[]
                    {
                        new AxleSpec(4.88f, 1, Tyre: "185/70R14"),
                        new AxleSpec(5.70f, 1, Tyre: "185/70R14"),
                    },
                    PivotAt = 0f, Pivot = Coupling.Ball, PivotHeight = 0.5f, MaxArticulation = 1.4f,
                    DragArea = 1.3f, Track = 1.9f,
                    PayloadMax = 1800f, PayloadAt = 1.0f + BoatCatalog.Speedboat.Length - BoatCatalog.Speedboat.Shape.SternZ,
                    PayloadHeight = 0.55f + BoatCatalog.Speedboat.CentreHeight,
                },
            },
        },

        // ---- 6: a mounted reversible plough (#494) ----
        new TrailerSpec
        {
            Label = "Reversible plough",
            Blurb = "Mounted four-furrow reversible plough, 1.8 m: on a tractor's three-point linkage. {kneel} lowers it into the ground and it turns stubble or grass over; it pulls hard",
            Body = TrailerBody.Plough, Couples = Coupling.ThreePoint,
            Paint = PloughBlue, Accent = Galvanised, Frame = Graphite,
            Tool = Farming.FarmTool.Plough, WorkWidth = 1.8f, WorkAt = 2.0f, LiftHeight = 0.45f, DepthCm = 25f,
            // Assumed (a Lemken Juwel / Kverneland class 4-furrow, 45 cm furrows): 3.6 m from the
            // linkage to the last body, 1,250 kg, 25 cm deep. No wheels: raised, all of it on the
            // tractor's linkage; lowered, on its bodies in the furrow.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "plough", Length = 3.6f, Width = 2.0f, Height = 1.5f, Mass = 1250f, CgAt = 1.5f, CgHeight = 0.7f,
                    PivotAt = 0f, Pivot = Coupling.ThreePoint, MaxArticulation = 0f, Track = 2.0f,
                },
            },
        },

        // ---- 7: a mounted seed drill (#494) ----
        new TrailerSpec
        {
            Label = "Seed drill 3 m",
            Blurb = "Mounted 3 m mechanical seed drill: lowered ({kneel}) on ploughed ground it sows the seed in your pack, the hotbar's first",
            Body = TrailerBody.SeedDrill, Couples = Coupling.ThreePoint,
            Paint = DrillGreen, Accent = Galvanised, Frame = Graphite,
            Tool = Farming.FarmTool.Sow, WorkWidth = 3.0f, WorkAt = 1.3f, LiftHeight = 0.35f,
            // Assumed (an Amazone D9 3000 class): 1.7 m deep, 850 kg with its hopper part full,
            // 24 coulters at 12.5 cm.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "seed drill", Length = 1.7f, Width = 3.0f, Height = 1.6f, Mass = 850f, CgAt = 0.9f, CgHeight = 0.9f,
                    PivotAt = 0f, Pivot = Coupling.ThreePoint, MaxArticulation = 0f, Track = 3.0f,
                },
            },
        },

        // ---- 8: a mounted disc mower (#494) ----
        new TrailerSpec
        {
            Label = "Disc mower 3 m",
            Blurb = "Rear-mounted 3 m disc mower: lowered ({kneel}) on grass it cuts hay, the bales straight into your pack",
            Body = TrailerBody.Mower, Couples = Coupling.ThreePoint,
            Paint = MowerRed, Accent = Galvanised, Frame = Graphite,
            Tool = Farming.FarmTool.Mow, WorkWidth = 3.0f, WorkAt = 0.9f, LiftHeight = 0.4f,
            // Assumed (a Pöttinger Novacat / Kuhn GMD class, drawn centred behind rather than
            // offset): 1.3 m deep, 750 kg, PTO driven.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "mower", Length = 1.3f, Width = 3.0f, Height = 1.2f, Mass = 750f, CgAt = 0.6f, CgHeight = 0.5f,
                    PivotAt = 0f, Pivot = Coupling.ThreePoint, MaxArticulation = 0f, Track = 3.0f,
                },
            },
        },

        // ---- 9: a two-axle tipping trailer (#494) ----
        new TrailerSpec
        {
            Label = "Tipping trailer",
            Blurb = "Two-axle turntable tipping trailer for the harvest, 200 sacks (10 t): on a tractor's drawbar. A combine's auger fills it; drive it to a farm co-op to sell the load",
            Body = TrailerBody.Tipper, Couples = Coupling.Drawbar,
            Paint = TipperRed, Accent = Galvanised, Frame = Graphite,
            TankItems = 200,
            // Assumed (a Swiss two-axle turntable tipper, Brantner / Krone class): the dolly with
            // its 2.2 m drawbar 700 kg, the 5.4 m body 3.3 t, wheelbase 3.6 m, 10 t of grain.
            Sections = new[]
            {
                new SectionSpec
                {
                    Name = "dolly", Length = 2.6f, Width = 2.3f, Height = 1.0f, Mass = 700f, CgAt = 2.1f, CgHeight = 0.6f,
                    Axles = new[] { new AxleSpec(2.3f, 1, Tyre: "500/50R17") },
                    PivotAt = 0f, Pivot = Coupling.Drawbar, MaxArticulation = 1.2f,
                    HitchAt = 2.3f, HitchHeight = 0.95f, Hitch = Coupling.Turntable,
                    Track = 1.9f,
                },
                new SectionSpec
                {
                    Name = "tipping body", Length = 5.4f, Width = 2.45f, Height = 2.45f, Mass = 3300f, CgAt = 2.8f, CgHeight = 1.3f,
                    Axles = new[] { new AxleSpec(4.4f, 1, Tyre: "500/50R17") },
                    PivotAt = 0.8f, Pivot = Coupling.Turntable, MaxArticulation = 0.9f,
                    DragArea = 1.4f, Track = 1.9f,
                    PayloadMax = 10000f, PayloadAt = 2.7f, PayloadHeight = 1.8f,
                },
            },
        },
    };

    // ---- trailer codes: which trailer and how full, in one replicated int ----
    // 0 = none; otherwise (index + 1) | (load percent << 8).

    /// <summary>The code of a trailer at a load, 0..1. A boat trailer's boat is aboard or not: half or more is aboard.</summary>
    public static int Code(int index, float load)
    {
        if (index < 0 || index >= All.Count) return 0;
        if (All[index].Boat != 0) load = load >= 0.5f ? 1f : 0f;
        return (index + 1) | (Mathf.Clamp(Mathf.RoundToInt(load * 100f), 0, 100) << 8);
    }

    /// <summary>Which trailer a code names, or null for none (or one this build does not know).</summary>
    public static TrailerSpec? For(int code)
    {
        int i = (code & 0xFF) - 1;
        return i >= 0 && i < All.Count ? All[i] : null;
    }

    public static int Index(int code) => (code & 0xFF) - 1;

    /// <summary>A code's load, 0..1.</summary>
    public static float Load(int code) => Mathf.Clamp(((code >> 8) & 0x7F) / 100f, 0f, 1f);

    /// <summary>
    /// A code from another peer, cleaned: an unknown trailer reads as none. A tipping trailer keeps
    /// its harvest (#494: the crop and sacks in the farm bits, the load from them).
    /// </summary>
    public static int Clean(int code) => For(code) is not { } t ? 0
        : t.TankItems > 0 ? WithTank(code, Farming.MachineLoad.TankOf(code))
        : Code(Index(code), Load(code));

    /// <summary>A tipping trailer's harvest (#494), whole sacks, from its code (empty for any other trailer).</summary>
    public static Farming.Tank TankOf(int code) => For(code) is { TankItems: > 0 } ? Farming.MachineLoad.TankOf(code) : default;

    /// <summary>The same tipping trailer holding <paramref name="tank"/>: its load (weight) follows the sacks.</summary>
    public static int WithTank(int code, Farming.Tank tank)
    {
        if (For(code) is not { TankItems: > 0 } t) return For(code) == null ? 0 : code;
        var held = tank with { Items = System.Math.Clamp(tank.Items, 0, t.TankItems), Partial = 0f };
        return Code(Index(code), held.Items / (float)t.TankItems) | Farming.MachineLoad.FarmBits(held);
    }

    /// <summary>The boat a code's trailer has aboard (#463), or 0: a boat trailer that has launched it is empty.</summary>
    public static RideKind BoatAboard(int code) => For(code) is { Boat: not 0 } t && Load(code) >= 0.5f ? t.Boat : 0;

    /// <summary>The same code with the boat aboard or gone.</summary>
    public static int WithBoat(int code, bool aboard) => For(code) == null ? 0 : Code(Index(code), aboard ? 1f : 0f);

    /// <summary>The boat trailer that carries this kind of boat, or −1.</summary>
    public static int TrailerFor(RideKind boat)
    {
        for (int i = 0; i < All.Count; i++)
            if (All[i].Boat == boat && boat != 0) return i;
        return -1;
    }
}
