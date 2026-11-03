namespace UnitSport.Interiors;

/// <summary>
/// Groups of pieces over the middle of a big room (#1250): a foyer's high tables and sofas, an arcade's
/// snack corner, a billiard hall's tables. What the walls' pieces leave of a hall several hundred m2 is
/// otherwise bare carpet, which reads as a hangar.
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>One piece of a group: its offset from the group's middle in room axes, its quarter turns (0 or 2) and size.</summary>
    private readonly record struct GroupPiece(FurnitureType Type, float Dx, float Dz, int Turns, float W, float D, float H);

    /// <summary>A group of pieces standing together, <see cref="W"/> by <see cref="D"/> m over all.</summary>
    private sealed record Group(string Name, float W, float D, GroupPiece[] Pieces);

    /// <summary>A round high table with its stools [S: foyers, 0.6 m tops at 1.05 m].</summary>
    private static readonly Group HighTables = new("high table", 1.4f, 1.4f, new[]
    {
        new GroupPiece(FurnitureType.HighTable, 0, 0, 0, 1.4f, 1.4f, 1.05f),
    });

    /// <summary>A table for four with its chairs.</summary>
    private static readonly Group TableForFour = new("table", 1.7f, 1.7f, new[]
    {
        new GroupPiece(FurnitureType.TableSet, 0, 0, 0, 1.7f, 1.7f, 0.76f),
    });

    /// <summary>A sofa facing two armchairs across a low table.</summary>
    private static readonly Group LoungeGroup = new("lounge", 2.0f, 2.9f, new[]
    {
        new GroupPiece(FurnitureType.Sofa, 0, -1.0f, 0, 2.0f, 0.85f, 0.8f),
        new GroupPiece(FurnitureType.CoffeeTable, 0, 0, 0, 1.0f, 0.55f, 0.4f),
        new GroupPiece(FurnitureType.Armchair, -0.5f, 1.05f, 2, 0.8f, 0.8f, 0.9f),
        new GroupPiece(FurnitureType.Armchair, 0.5f, 1.05f, 2, 0.8f, 0.8f, 0.9f),
    });

    /// <summary>Two cardboard standees of the coming films side by side.</summary>
    private static readonly Group Standees = new("standees", 2.0f, 0.4f, new[]
    {
        new GroupPiece(FurnitureType.Standee, -0.55f, 0, 0, 0.8f, 0.4f, 1.9f),
        new GroupPiece(FurnitureType.Standee, 0.55f, 0, 2, 0.8f, 0.4f, 1.9f),
    });

    /// <summary>A game table: air hockey or table football, with a plant at its end.</summary>
    private static Group GameTable(bool hockey) => hockey
        ? new Group("air hockey", 1.2f, 2.1f, new[] { new GroupPiece(FurnitureType.AirHockey, 0, 0, 0, 1.2f, 2.1f, 0.9f) })
        : new Group("table football", 0.75f, 1.4f, new[] { new GroupPiece(FurnitureType.TableFootball, 0, 0, 0, 0.75f, 1.4f, 0.9f) });

    /// <summary>
    /// A pool table along Z with the cue's swing round it [S: 8 ft table 1.4 x 2.7 m outside, a 1.47 m
    /// cue: 1.5 m clear each side, Bluff Billard Geneva]; the clearance is the group's, so nothing else
    /// stands in it.
    /// </summary>
    private static readonly Group PoolBay = new("pool", 1.4f + 2 * 1.3f, 2.7f + 2 * 1.3f, new[]
    {
        new GroupPiece(FurnitureType.PoolTable, 0, 0, 0, 1.4f, 2.7f, 0.8f),
    });

    /// <summary>
    /// Fills the middle of <paramref name="r"/> with groups on a grid of <paramref name="cellX"/> by
    /// <paramref name="cellZ"/> m cells, the grid kept <paramref name="wallClear"/> off the walls and
    /// centred; <paramref name="pick"/> gives the group for a cell (column, row) or null. A group goes in
    /// whole, where it and <paramref name="gap"/> round it are free and it leaves a way through, or not at
    /// all. Returns how many went in.
    /// </summary>
    private static int FillFloor(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked,
        float cellX, float cellZ, float wallClear, Func<int, int, Group?> pick, float gap = 0.6f)
    {
        int nx = (int)((r.Width - 2 * wallClear) / cellX), nz = (int)((r.Depth - 2 * wallClear) / cellZ);
        if (nx < 1 || nz < 1) return 0;
        float x0 = (r.X0 + r.X1) / 2 - (nx - 1) * cellX / 2, z0 = (r.Z0 + r.Z1) / 2 - (nz - 1) * cellZ / 2;
        int n = 0;
        for (int i = 0; i < nx; i++)
            for (int k = 0; k < nz; k++)
            {
                if (pick(i, k) is not { } g) continue;
                float x = x0 + i * cellX, z = z0 + k * cellZ;
                var rect = new RectPlan(x - g.W / 2, z - g.D / 2, x + g.W / 2, z + g.D / 2);
                if (!Free(r, rect, placed, blocked, gap) || !KeepsWay(r, placed, rect)) continue;
                foreach (var p in g.Pieces) Put(l, f, p.Type, x + p.Dx, z + p.Dz, p.Turns, p.W, p.D, p.H, placed);
                // a pool table's swing is its group's: nothing placed after stands in it, though people walk there
                if (g == PoolBay) blocked.Add(rect);
                n++;
            }
        return n;
    }
}
