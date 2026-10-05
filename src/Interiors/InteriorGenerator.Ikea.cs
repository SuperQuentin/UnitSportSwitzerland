using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// IKEA rules (#501): what is inside one of the nine stores <see cref="Landmarks"/> recognises.
/// Reached through <see cref="LaysItselfOut"/> and <see cref="HallLayout"/>, which it shares with
/// the industrial sites of #497, along with <c>Put</c>, <c>PieceBudget</c>, <c>EntryLane</c> and
/// <c>Free</c> — a shop floor of bins and a warehouse of racking are the same problem.
///
/// <para>
/// Deliberately thin. The real showroom — the marked one-way route, the room sets, the Småland, the
/// restaurant, the self-serve racking and a row of tills — is its own issue. What this builds is the
/// one thing that has to be there: a market hall the size of the real slab, and
/// <b>bins of Blåhajs</b> in rows across it, with the till by the door.
/// </para>
///
/// <para>
/// Frame: the entrance wall at −Z, as everywhere else. The hall is one room through the building's
/// full height, because that is what the inside of a big-box store looks like — a 20 m shed with a
/// mezzanine, not a stack of 2.9 m storeys. <see cref="InteriorLayout.StoreyHeight"/> stays what
/// the shell computed, so loot and the probes that count floors keep working unchanged.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>Bumped when these rules change, so stored stores are planned again.</summary>
    public const int IkeaRules = 1;

    /// <summary>A bin is about a metre square and knee-to-waist high; the sharks heap above it.</summary>
    private const float BinSize = 1.15f, BinHeight = 0.72f;
    /// <summary>Between bins along a row, and between the rows: a trolley has to pass.</summary>
    private const float BinGap = 0.55f, BinAisle = 2.4f;
    /// <summary>A hall below this cannot hold a row and an aisle, so there is no store to plan.</summary>
    private const float HallMin = 8f;
    /// <summary>
    /// The most bins in one hall. A 28 000 m² slab would otherwise take about 3 000 of them, which
    /// is more geometry than the rest of the tile put together — and the follow-up issue is going to
    /// want that budget for the room sets.
    /// </summary>
    private const int MaxBins = 120;

    /// <summary>
    /// Plans the inside of a store. False when the solid is too small to lay a hall out in, which
    /// leaves the caller's ordinary rules to furnish it as whatever else it is.
    /// </summary>
    internal static bool TryIkea(InteriorLayout l, float doorHeight)
    {
        float w = l.Width, d = l.Depth;
        if (w < HallMin || d < HallMin) return false;

        // this plans the whole building, so whatever the house rules made is replaced outright
        l.Type = BuildingType.Ikea;
        l.Shop = Loot.ShopType.Ikea;
        l.Below = 0;
        l.Floors.Clear();
        var floor = new FloorPlan();
        l.Floors.Add(floor);

        float hw = w / 2, hd = d / 2;
        var hall = new RoomPlan { X0 = -hw, Z0 = -hd, X1 = hw, Z1 = hd, Type = RoomType.IkeaMarket };
        floor.Rooms.Add(hall);

        // the entrance: a store's is a wall of glass, so as wide as the shell will give, and tall
        l.EntryWidth = Math.Min(Math.Max(l.DoorWidth, 3.2f), w - 1.2f);
        l.EntryX = Fit(l.EntryX, -hw + l.EntryWidth / 2 + 0.3f, hw - l.EntryWidth / 2 - 0.3f);
        float clear = l.ClearOf(hall);
        hall.Openings.Add(new OpeningPlan
        {
            Side = Side.Front,
            Center = l.EntryX,
            Width = l.EntryWidth,
            Bottom = 0,
            Top = Math.Min(Math.Max(doorHeight, 3.0f), clear - 0.3f),
            Kind = OpeningKind.Entry,
        });

        AddWindows(l, floor, 0);
        return true;
    }

    /// <summary>
    /// Rows of bins of Blåhajs across the shop floor, with aisles between them and the lane in
    /// from the door left clear.
    ///
    /// <para>
    /// The bins of a row stand <see cref="BinGap"/> apart rather than touching, because a bin is a
    /// free-standing wire basket and a continuous run of them would read as one long trough.
    /// </para>
    /// </summary>
    private static void BlahajBins(InteriorLayout l, int f, RoomPlan r,
        List<RectPlan> placed, List<RectPlan> blocked)
    {
        blocked.Add(EntryLane(l, r));

        float pitch = BinSize + BinAisle;
        int rows = Math.Max(1, (int)((r.Depth - 1.6f) / pitch));
        float used = rows * pitch;
        float z = r.Z0 + (r.Depth - used) / 2 + BinAisle / 2;
        int budget = Math.Min(PieceBudget(r), MaxBins);
        float step = BinSize + BinGap;

        for (int row = 0; row < rows && budget > 0; row++, z += pitch)
            for (float x = r.X0 + 0.6f; x + BinSize < r.X1 - 0.6f && budget > 0; x += step)
            {
                if (!Free(r, new RectPlan(x, z, x + BinSize, z + BinSize), placed, blocked, 0f)) continue;
                Put(l, f, FurnitureType.BlahajBin, x + BinSize / 2, z + BinSize / 2, 0,
                    BinSize, BinSize, BinHeight, placed);
                budget--;
            }
    }
}
