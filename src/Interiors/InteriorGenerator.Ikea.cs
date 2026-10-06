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

        // No windows, deliberately: the facade has none (BuildingMeshBuilder forces the grid off
        // for a store), so a cut here would be daylight through a blank wall. It is also what a
        // big-box store is — the hall is lit by its own high bays, which RoomLights gives any room
        // with no window.
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

        int budget = Math.Min(PieceBudget(r), MaxBins);
        float step = BinSize + BinGap, pitch = BinSize + BinAisle;

        // Spread the budget over the whole floor rather than filling rows until it runs out. At
        // the natural pitch a 120 x 110 m hall holds about 2 000 bins and the cap is 110, so
        // marching row by row laid two dense rows across the front wall and left the other
        // hundred metres bare. Widening both spacings by the square root of the overshoot keeps
        // the grid square and covers the hall, which is what the floor of one really looks like:
        // islands of stock with room to walk and push a trolley between them.
        int Cols(float by) => Math.Max(1, (int)((r.Width - 1.2f) / by));
        int Rows(float by) => Math.Max(1, (int)((r.Depth - 1.6f) / by));
        int fits = Cols(step) * Rows(pitch);
        if (fits > budget)
        {
            float spread = MathF.Sqrt((float)fits / budget);
            step *= spread;
            pitch *= spread;
        }
        int cols = Cols(step), rows = Rows(pitch);

        // centre the grid in the hall, so the gap at the walls is even
        float x0 = r.X0 + (r.Width - (cols - 1) * step - BinSize) / 2;
        float z0 = r.Z0 + (r.Depth - (rows - 1) * pitch - BinSize) / 2;

        for (int row = 0; row < rows && budget > 0; row++)
            for (int col = 0; col < cols && budget > 0; col++)
            {
                float x = x0 + col * step, z = z0 + row * pitch;
                if (!Free(r, new RectPlan(x, z, x + BinSize, z + BinSize), placed, blocked, 0f)) continue;
                Put(l, f, FurnitureType.BlahajBin, x + BinSize / 2, z + BinSize / 2, 0,
                    BinSize, BinSize, BinHeight, placed);
                budget--;
            }
    }
}
