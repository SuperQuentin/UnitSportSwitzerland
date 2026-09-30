using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>One thing the garage can change on a car. Each is independent; every car offers every one.</summary>
public enum TuneSlot { Tyres, Wing, FrontBumper, RearBumper, Skirts, Scoop, Bonnet, Paint, Lower, RimColour, RimSize, RideHeight, Tint, Underglow, Doors }

/// <summary>
/// What a set of tyres does to the car model: peak grip against the stock tyre, the tyre curve's
/// fall past its peak (C: higher lets go more suddenly, which is what a drift tyre wants), the rear
/// grip left with the handbrake on, how much the Game profile's throttle keeps the rear sliding,
/// and how much of the gap to full grip the tread claws back off tarmac
/// (<see cref="Motorbike.SurfaceGrip"/>; 0 is a road tyre).
/// </summary>
public readonly record struct TyreModel(float Grip, float CurveC, float Handbrake, float Sustain, float Offroad)
{
    public static readonly TyreModel Stock = new(1f, 1f, 0.35f, 1f, 0f);
    public static readonly TyreModel Drift = new(0.9f, 1.2f, 0.22f, 2f, 0f);
    public static readonly TyreModel Rally = new(0.92f, 1f, 0.3f, 1.1f, 0.7f);
    public static readonly TyreModel Race = new(1.1f, 0.95f, 0.4f, 0.8f, 0f);
    public static readonly TyreModel Slicks = new(1.3f, 0.9f, 0.45f, 0.6f, 0f);
}

/// <summary>
/// A car's garage parts, one small number per <see cref="TuneSlot"/>, 4 bits each in one
/// <c>long</c> — which is how it travels (<c>FootPlayer.TuningBits</c>, <c>VehicleState.Tuning</c>).
/// 0 in a slot is Stock: the catalog's own look for that car, so 0 overall is the car as it came.
///
/// <para>
/// The bits come from another peer, so <see cref="Unpack"/> is the trust boundary: a value past a
/// slot's options reads as Stock, never as an index out of a table.
/// </para>
/// </summary>
public readonly record struct CarTuning(long Bits)
{
    public static readonly string[] Colours =
        { "White", "Black", "Silver", "Red", "Blue", "Yellow", "Green", "Orange", "Purple", "Midnight", "Pink", "Teal" };

    private static readonly Color[] Palette =
    {
        new(0.93f, 0.93f, 0.91f), new(0.05f, 0.05f, 0.06f), new(0.66f, 0.68f, 0.71f), new(0.78f, 0.06f, 0.07f),
        new(0.08f, 0.26f, 0.72f), new(0.98f, 0.8f, 0.08f), new(0.08f, 0.5f, 0.22f), new(0.96f, 0.45f, 0.06f),
        new(0.42f, 0.14f, 0.62f), new(0.06f, 0.1f, 0.24f), new(0.95f, 0.45f, 0.68f), new(0.05f, 0.58f, 0.56f),
    };

    private static readonly string[] Neon = { "Blue", "Green", "Pink", "Red", "White", "Purple" };
    private static readonly Color[] NeonColour =
    {
        new(0.15f, 0.45f, 1f), new(0.2f, 1f, 0.35f), new(1f, 0.25f, 0.75f), new(1f, 0.12f, 0.1f), new(0.9f, 0.95f, 1f), new(0.62f, 0.2f, 1f),
    };

    /// <summary>What each slot can be set to; index 0 is always Stock (the catalog look).</summary>
    public static readonly string[][] Options =
    {
        new[] { "Stock", "Drift", "Rally", "Race", "F1 slicks" },
        new[] { "Stock", "None", "Lip", "Small", "Big", "GT" },
        new[] { "Stock", "Lip", "Splitter" },
        new[] { "Stock", "Diffuser" },
        new[] { "Stock", "Side skirts" },
        new[] { "Stock", "None", "Scoop" },
        new[] { "Stock", "Paint", "Carbon", "Primer" },
        new[] { "Stock" }.Concat(Colours).ToArray(),
        new[] { "Stock", "One colour" }.Concat(Colours).ToArray(),
        new[] { "Stock" }.Concat(Colours).ToArray(),
        new[] { "Stock", "+1 inch", "+2 inch" },
        new[] { "Stock", "Lowered", "Slammed" },
        new[] { "Stock", "Light", "Dark", "Limo" },
        new[] { "Off" }.Concat(Neon).ToArray(),
        new[] { "Stock", "Conventional", "Suicide", "Scissor", "Butterfly", "Gull-wing" },
    };

    public static readonly int SlotCount = Options.Length;

    /// <summary>The setting of one slot.</summary>
    public int this[TuneSlot slot] => (int)((Bits >> (4 * (int)slot)) & 15);

    /// <summary>The same parts with one slot changed.</summary>
    public CarTuning With(TuneSlot slot, int value)
    {
        int shift = 4 * (int)slot;
        return Unpack((Bits & ~(15L << shift)) | ((long)(value & 15) << shift));
    }

    /// <summary>The wire form.</summary>
    public long Pack() => Bits;

    /// <summary>From the wire: any slot set past its options is Stock, unused bits are dropped.</summary>
    public static CarTuning Unpack(long bits)
    {
        long clean = 0;
        for (int i = 0; i < SlotCount; i++)
        {
            long v = (bits >> (4 * i)) & 15;
            if (v < Options[i].Length) clean |= v << (4 * i);
        }
        return new CarTuning(clean);
    }

    public TyreModel Tyres => this[TuneSlot.Tyres] switch
    {
        1 => TyreModel.Drift,
        2 => TyreModel.Rally,
        3 => TyreModel.Race,
        4 => TyreModel.Slicks,
        _ => TyreModel.Stock,
    };

    /// <summary>The car as tuned: the same numbers, another body.</summary>
    public CarSpec Apply(CarSpec spec)
    {
        if (Bits == 0) return spec;
        var b = spec.Body;
        var t = this;
        int Slot(TuneSlot s) => t[s];
        return spec with
        {
            Body = b with
            {
                Wing = Slot(TuneSlot.Wing) is > 0 and var w ? (WingSize)(w - 1) : b.Wing,
                FrontAero = Slot(TuneSlot.FrontBumper),
                Diffuser = Slot(TuneSlot.RearBumper) == 1 || b.Diffuser,
                Skirts = Slot(TuneSlot.Skirts) == 1 || b.Skirts,
                Scoop = Slot(TuneSlot.Scoop) switch { 1 => false, 2 => true, _ => b.Scoop },
                Bonnet = Slot(TuneSlot.Bonnet) switch
                {
                    1 => null,
                    2 => new Color(0.09f, 0.09f, 0.1f),
                    3 => new Color(0.46f, 0.46f, 0.44f),
                    _ => b.Bonnet,
                },
                Paint = Slot(TuneSlot.Paint) is > 0 and var p ? Palette[p - 1] : b.Paint,
                Lower = Slot(TuneSlot.Lower) switch { 0 => b.Lower, 1 => null, var l => Palette[l - 2] },
                Rim = Slot(TuneSlot.RimColour) is > 0 and var r ? Palette[r - 1] : b.Rim,
                RimSize = Slot(TuneSlot.RimSize),
                Drop = Slot(TuneSlot.RideHeight) * 0.045f,
                Glass = Slot(TuneSlot.Tint) switch
                {
                    1 => new Color(0.22f, 0.28f, 0.34f),
                    2 => new Color(0.1f, 0.12f, 0.15f),
                    3 => new Color(0.03f, 0.03f, 0.04f),
                    _ => b.Glass,
                },
                Underglow = Slot(TuneSlot.Underglow) is > 0 and var n ? NeonColour[n - 1] : b.Underglow,
                Doors = Slot(TuneSlot.Doors) is > 0 and var d ? (DoorStyle)(d - 1) : b.Doors,
                Slicks = Slot(TuneSlot.Tyres) == 4 || b.Slicks,
            },
        };
    }

    /// <summary><see cref="Rideable.Create"/>, with these parts on it if it is a car.</summary>
    public static Rideable? Ride(RideKind kind, long bits) =>
        bits != 0 && CarCatalog.For(kind) is { } spec ? new Car(spec, Unpack(bits)) : Rideable.Create(kind);

    /// <summary>
    /// Self-check, run by <c>--tuningcheck</c>: every slot round-trips through the wire form on its
    /// own and all together, and garbage decodes to Stock rather than past a table.
    /// </summary>
    public static bool Check(out string report)
    {
        var fails = new List<string>();
        var all = new CarTuning(0);
        for (int i = 0; i < SlotCount; i++)
        {
            var slot = (TuneSlot)i;
            for (int v = 0; v < Options[i].Length; v++)
            {
                var one = new CarTuning(0).With(slot, v);
                if (Unpack(one.Pack())[slot] != v) fails.Add($"{slot}={v} did not round-trip");
            }
            all = all.With(slot, Options[i].Length - 1);
        }
        var back = Unpack(all.Pack());
        for (int i = 0; i < SlotCount; i++)
            if (back[(TuneSlot)i] != Options[i].Length - 1) fails.Add($"{(TuneSlot)i} lost when packed with the others");
        // every nibble 15: past the options of every slot, so all Stock; bit 63 set as well
        if (Unpack(-1L).Bits != 0) fails.Add($"all-ones decoded to {Unpack(-1L).Bits:X}, not Stock");
        if (Unpack(1L << 62)[TuneSlot.Tyres] != 0) fails.Add("an unused high bit leaked into a slot");
        // the car as tuned: every slot maxed builds a spec without throwing
        foreach (var spec in CarCatalog.All) _ = back.Apply(spec);
        if (!ReferenceEquals(new CarTuning(0).Apply(CarCatalog.All[0]), CarCatalog.All[0])) fails.Add("Stock changed the spec");
        report = fails.Count == 0 ? $"{SlotCount} slots, {Options.Sum(o => o.Length)} options round-trip; garbage decodes to Stock"
            : string.Join("; ", fails);
        return fails.Count == 0;
    }
}
