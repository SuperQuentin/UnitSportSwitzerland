using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// How a door's leaf moves, per door rather than per kind of building (#498). The kind sets the
/// default (<see cref="DoorBudget.HangFor"/>), and a single door overrides it: a barn's pedestrian
/// side door is <see cref="Inward"/> on an Agricultural building, and a warehouse's loading bay
/// is <see cref="RollUp"/> on an Industrial one.
/// </summary>
public enum DoorHang
{
    /// <summary>One leaf, in the interior, swinging into the room. Nearly every door.</summary>
    Inward,
    /// <summary>A pair on the facade, swinging out into the yard: a barn's.</summary>
    OutwardPair,
    /// <summary>Slats on the facade, rolling up into the lintel: a garage's.</summary>
    RollUp,
}

/// <summary>
/// How many front doors a building gets and where they sit along its walls (#498). A 100 m block
/// with one door reads as a prop; a real one has an entrance every twenty-odd metres, and a barn
/// has a man-sized door beside its wagon-sized pair.
///
/// <para>
/// Pure arithmetic on the plan box and the wall runs <c>BuildingFootprint</c> already measures —
/// no Godot, so the rules are unit-tested (tier 0) rather than eyeballed in a town.
/// </para>
/// </summary>
public static class DoorBudget
{
    /// <summary>Metres of wall one entrance serves, and so the gap between two doors on one wall.</summary>
    public const float Spacing = 22f;

    /// <summary>Wall left beside a door at each end of a run, for its jambs and a corner.</summary>
    public const float EndMargin = 0.6f;

    /// <summary>Doors on one wall run, however long it is: past this a facade is all doors.</summary>
    public const int MaxPerWall = 5;

    /// <summary>Doors on one building, however big: the plan inside has to take an entrance each.</summary>
    public const int MaxPerBuilding = 8;

    /// <summary>Wall between two doors of one building, edge to edge, on one wall or round a corner.</summary>
    public const float MinGap = 3.0f;

    /// <summary>A secondary door is a plain pedestrian one, whatever the building's main door is.</summary>
    public const float ServiceWidth = 1.0f, ServiceHeight = 2.1f;

    /// <summary>How a kind of building's main door hangs, and so the default for its doors.</summary>
    public static DoorHang HangFor(BuildingKind kind) => kind switch
    {
        BuildingKind.Agricultural => DoorHang.OutwardPair,
        BuildingKind.Garage => DoorHang.RollUp,
        _ => DoorHang.Inward,
    };

    /// <summary>
    /// Whether a kind of building's main door is driven through: a garage's or a barn's. Per door
    /// this is <c>DoorSpot.Vehicle</c>, so a barn's side door is not one and a warehouse's loading
    /// bay is.
    /// </summary>
    public static bool VehicleFor(BuildingKind kind) => kind is BuildingKind.Garage or BuildingKind.Agricultural;

    /// <summary>
    /// How many doors a building of this size and kind carries, its main door included.
    ///
    /// <para>
    /// One per <see cref="Spacing"/> of wall is the density along a street front; spread over the
    /// whole perimeter it would put a door on the blind back of every shed, so the budget counts
    /// one per two spacings of perimeter — and one per three for a home, which keeps every house
    /// and shed in the country on its single front door. A barn or a garage always gets two: its
    /// vehicle door and a pedestrian one beside it.
    /// </para>
    /// </summary>
    public static int Total(BuildingKind kind, float width, float depth)
    {
        float perimeter = 2 * (Math.Max(0f, width) + Math.Max(0f, depth));
        float per = kind is BuildingKind.House or BuildingKind.Annex or BuildingKind.UnderConstruction
            ? 3 * Spacing : 2 * Spacing;
        int many = 1 + (int)(perimeter / per);
        // a barn's or a garage's main door is for the tractor; a person still needs a door
        int least = kind is BuildingKind.Agricultural or BuildingKind.Garage ? 2 : 1;
        return Math.Clamp(Math.Max(least, many), 1, MaxPerBuilding);
    }

    /// <summary>
    /// Where doors go along one continuous run of wall <paramref name="runLength"/> metres long,
    /// as offsets from its middle, at most <paramref name="limit"/> of them.
    ///
    /// <para>
    /// The middle comes first and is where the building's main door already stands, so a wall that
    /// takes one door keeps exactly the door it had before this existed; the rest step out in pairs
    /// either side, <see cref="Spacing"/> apart, as long as a door of <paramref name="doorWidth"/>
    /// still clears the run's ends. Ordered nearest the middle first, so a caller that runs out of
    /// budget drops the outermost.
    /// </para>
    /// </summary>
    public static float[] AlongRun(float runLength, float doorWidth, int limit)
    {
        if (limit <= 0) return [];
        var list = new List<float>(Math.Min(limit, MaxPerWall)) { 0f };
        float reach = runLength / 2 - doorWidth / 2 - EndMargin;
        for (int k = 1; list.Count < limit && list.Count < MaxPerWall; k++)
        {
            float off = k * Spacing;
            if (off > reach) break;
            list.Add(off);
            if (list.Count < limit && list.Count < MaxPerWall) list.Add(-off);
        }
        return list.ToArray();
    }
}
