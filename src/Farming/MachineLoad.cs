using UnitSport.Terrain.Format;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

namespace UnitSport.Farming;

/// <summary>
/// What a farm machine holds (#494): a combine's grain tank or a tipping trailer's body. One crop
/// at a time, counted in whole items (sacks) plus the fraction a pass has gathered toward the next
/// one (owner only, never replicated).
/// </summary>
public readonly record struct Tank(CropKind Crop, int Items, float Partial = 0f)
{
    public bool Empty => Items <= 0 && Partial <= 0f;
}

/// <summary>Why a harvest stroke did not all go into a tank.</summary>
public enum TankRefusal { None, OtherCrop, Full }

/// <summary>
/// The numbers of the farm machines (#494): filling and emptying a tank, the trailer code's farm
/// bits, seed owed by a drill, and the draft (pull) a lowered implement asks of the tractor.
/// </summary>
public static class MachineLoad
{
    // ---- a tank ----

    /// <summary>
    /// Harvest <paramref name="units"/> of <paramref name="crop"/> into <paramref name="t"/> (capacity
    /// <paramref name="capacity"/> items). A tank holds one crop: another is refused while anything is
    /// in it; a full tank takes no more (the rest is left on the field).
    /// </summary>
    public static Tank Add(Tank t, CropKind crop, float units, int capacity, out TankRefusal refused)
    {
        refused = TankRefusal.None;
        if (units <= 0f || crop == CropKind.None) return t;
        if (!t.Empty && t.Crop != crop) { refused = TankRefusal.OtherCrop; return t; }
        if (t.Items >= capacity) { refused = TankRefusal.Full; return t with { Crop = crop }; }
        float sum = t.Partial + units;
        int whole = (int)MathF.Floor(sum);
        int items = t.Items + whole;
        float partial = sum - whole;
        if (items >= capacity) { items = capacity; partial = 0f; refused = TankRefusal.Full; }
        return new Tank(crop, items, partial);
    }

    /// <summary>Take up to <paramref name="n"/> items out; empty, the tank forgets its crop.</summary>
    public static Tank Take(Tank t, int n, out int taken)
    {
        taken = Math.Clamp(n, 0, t.Items);
        int left = t.Items - taken;
        return left <= 0 && t.Partial <= 0f ? default : t with { Items = left };
    }

    /// <summary>
    /// Unload <paramref name="from"/> into <paramref name="to"/> (capacity <paramref name="toCapacity"/>):
    /// as much as fits, only into an empty body or one holding the same crop.
    /// </summary>
    public static (Tank From, Tank To, int Moved) Transfer(Tank from, Tank to, int toCapacity)
    {
        if (from.Items <= 0) return (from, to, 0);
        if (to.Items > 0 && to.Crop != from.Crop) return (from, to, 0);
        int moved = Math.Min(from.Items, Math.Max(0, toCapacity - to.Items));
        if (moved == 0) return (from, to, 0);
        var rest = Take(from, moved, out _);
        return (rest, new Tank(from.Crop, to.Items + moved, to.Partial), moved);
    }

    // ---- the farm bits of a tipping trailer's code (TrailerCatalog.Code) ----
    // bits 0..7 the trailer, 8..14 its load %, 16..20 the crop, 21..30 the items (0..1023)

    public const int FarmShift = 16, ItemsShift = 21, MaxItems = 1023;
    public const int FarmMask = 0x7FFF0000;

    /// <summary>The crop and item count as a code's farm bits.</summary>
    public static int FarmBits(Tank t) => t.Items <= 0 ? 0
        : ((int)t.Crop & 31) << FarmShift | Math.Clamp(t.Items, 0, MaxItems) << ItemsShift;

    /// <summary>The tank a code's farm bits say (whole items only).</summary>
    public static Tank TankOf(int code)
    {
        int items = (code >> ItemsShift) & MaxItems;
        var crop = (CropKind)((code >> FarmShift) & 31);
        return items <= 0 || crop == CropKind.None ? default : new Tank(crop, items);
    }

    // ---- the combine's tank in its pose flags (Truck.PackFlags): items in 8..15, crop in 19..23 ----

    public const int FlagItemsShift = 8, FlagCropShift = 19;
    /// <summary>The flags' bits the tank takes.</summary>
    public const int FlagMask = 0xFF << FlagItemsShift | 31 << FlagCropShift;

    public static int TankFlags(Tank t) => t.Items <= 0 ? 0
        : Math.Clamp(t.Items, 0, 255) << FlagItemsShift | ((int)t.Crop & 31) << FlagCropShift;

    public static Tank TankFromFlags(int flags)
    {
        int items = (flags >> FlagItemsShift) & 0xFF;
        var crop = (CropKind)((flags >> FlagCropShift) & 31);
        return items <= 0 || crop == CropKind.None ? default : new Tank(crop, items);
    }

    // ---- seed ----

    /// <summary>
    /// A drill's stroke used <paramref name="units"/> seed items (cells / cells per seed): added to
    /// what is owed; the whole items due are taken from the pack now, the fraction waits.
    /// </summary>
    public static int SeedDue(ref float owed, float units)
    {
        owed += MathF.Max(units, 0f);
        int whole = (int)MathF.Floor(owed);
        owed -= whole;
        return whole;
    }

    // ---- draft ----

    /// <summary>
    /// Draft of a mouldboard plough, N (ASABE D497: F = Fi·(A + C·S²)·W·T, loam: Fi 0.70, A 652,
    /// C 5.1, S km/h, W m, T cm). A 1.8 m four-furrow plough 25 cm deep: ~28 kN at 7 km/h.
    /// </summary>
    public static float PloughDraft(float kmh, float widthM, float depthCm = 25f) =>
        0.70f * (652f + 5.1f * kmh * kmh) * widthM * depthCm;

    /// <summary>Draft of a grain drill with disc openers, N (ASABE D497: 400 N a row, speed-independent).</summary>
    public static float DrillDraft(float widthM, float rowSpacingM = 0.125f) => 400f * MathF.Round(widthM / rowSpacingM);

    /// <summary>
    /// A disc mower's pull plus its PTO's power as an equivalent draft, N: ~1 kN of drag and
    /// ~4 kW a metre of cut (published 3 m mowers need 50-70 kW at the PTO; most of it is not
    /// draft but it is still the engine's), taken at the speed it runs.
    /// </summary>
    public static float MowerDraft(float kmh, float widthM) => 1000f + 4000f * widthM / MathF.Max(kmh / 3.6f, 1.5f);
}
