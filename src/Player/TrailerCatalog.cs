using System.Collections.Generic;
using Godot;

namespace UnitSport.Player;

/// <summary>
/// The trailers a truck can pick up (#70): three semi-trailers for the tractor's fifth wheel and a
/// drawbar trailer for the rigid's hitch. Not mounts: a trailer is driven only behind a truck, so it
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
    };

    // ---- trailer codes: which trailer and how full, in one replicated int ----
    // 0 = none; otherwise (index + 1) | (load percent << 8).

    /// <summary>The code of a trailer at a load, 0..1.</summary>
    public static int Code(int index, float load) =>
        index < 0 || index >= All.Count ? 0 : (index + 1) | (Mathf.Clamp(Mathf.RoundToInt(load * 100f), 0, 100) << 8);

    /// <summary>Which trailer a code names, or null for none (or one this build does not know).</summary>
    public static TrailerSpec? For(int code)
    {
        int i = (code & 0xFF) - 1;
        return i >= 0 && i < All.Count ? All[i] : null;
    }

    public static int Index(int code) => (code & 0xFF) - 1;

    /// <summary>A code's load, 0..1.</summary>
    public static float Load(int code) => Mathf.Clamp(((code >> 8) & 0x7F) / 100f, 0f, 1f);

    /// <summary>A code from another peer, cleaned: an unknown trailer reads as none.</summary>
    public static int Clean(int code) => For(code) == null ? 0 : Code(Index(code), Load(code));
}
