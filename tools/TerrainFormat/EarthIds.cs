using System.Globalization;

namespace UnitSport.Terrain.Format;

/// <summary>
/// A building's identity on the Earth (#1514, G2 C5, decided with the user Nov 5 2026): its footprint's centre as a
/// latitude and longitude in units of 1e-5° (1.1 m north, 0.76 m east at 46°), the same whatever tiling or leaf build
/// laid it out. It replaces <c>BuildingKey(TileE, TileN, Index)</c> as the seed and save key on leaves: loot, shop,
/// lock and gift rolls hash its text (<see cref="ToString"/>), so they reshuffle once. Two buildings whose centres
/// fall in the same unit (a duplicate solid) are told apart by <see cref="Dup"/>, in their tile's index order
/// (<see cref="Assign"/>).
///
/// <para>
/// The centre is the area-weighted centroid of the solid's roof and floor triangles seen from above: the same point
/// in any frame (a turn or a shift of the plan moves it with the building), unlike a box's centre, which a frame
/// turned by the convergence moves. Two builds agree to the 0.3 mm they place vertices to (B3), so a building whose
/// centre lies that close to a unit's edge can change id between builds: 1 in ~2,000. Every peer reads the same
/// build, so they always agree. The text decodes back to a place (<see cref="LatDeg"/>, <see cref="LonDeg"/>):
/// G2 D's store migration finds the building from it.
/// </para>
/// </summary>
public readonly record struct BuildingId(int LatE5, int LonE5, byte Dup = 0)
{
    public const double Unit = 1e-5;

    public double LatDeg => LatE5 * Unit;
    public double LonDeg => LonE5 * Unit;

    /// <summary>The id of a building centred on an ECEF point.</summary>
    public static BuildingId Of(EcefPos centre)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(centre);
        return new((int)Math.Floor(g.LatDeg / Unit), (int)Math.Floor(g.LonDeg / Unit));
    }

    /// <summary>
    /// The footprint's centre in the triangles' own frame: plan (x, z) and the solid's lowest y. Null for a solid with
    /// no face seen from above (walls only).
    /// </summary>
    public static (double X, double Y, double Z)? FootprintCentre(float[] triangles)
    {
        double sx = 0, sz = 0, sa = 0, minY = double.MaxValue;
        for (int i = 0; i + 8 < triangles.Length; i += 9)
        {
            minY = Math.Min(minY, Math.Min(triangles[i + 1], Math.Min(triangles[i + 4], triangles[i + 7])));
            double ax = triangles[i], az = triangles[i + 2], bx = triangles[i + 3], bz = triangles[i + 5], cx = triangles[i + 6], cz = triangles[i + 8];
            double area = Math.Abs((bx - ax) * (cz - az) - (bz - az) * (cx - ax)) * 0.5;
            if (area < 0.005) continue; // a wall, edge-on from above (Building.Covers' rule)
            sx += area * (ax + bx + cx) / 3;
            sz += area * (az + bz + cz) / 3;
            sa += area;
        }
        return sa > 0 ? (sx / sa, minY, sz / sa) : null;
    }

    /// <summary>A building on a cube leaf: its triangles are in the leaf's anchor frame (x east, y up, z south).</summary>
    public static BuildingId? OnLeaf(CubeKey leaf, Building b) =>
        FootprintCentre(b.Triangles) is var (x, y, z) ? Of(leaf.Anchor.ToEcef(x, y, z)) : null;

    /// <summary>A building on an LV95 tile: tile-local metres from the north-west corner (x east, z south), y the LHN95 altitude.</summary>
    public static BuildingId? OnLv95Tile(TileId tile, Building b)
    {
        if (FootprintCentre(b.Triangles) is not var (x, y, z)) return null;
        var (e, n) = tile.Global(x, z);
        return Of(SwissGeodesy.ToEcef(e, n, y));
    }

    /// <summary>
    /// Every building of a tile's list its id (null for a solid with no footprint), duplicates numbered in index order.
    /// </summary>
    public static BuildingId?[] Assign(IReadOnlyList<Building> buildings, Func<Building, BuildingId?> idOf)
    {
        var ids = new BuildingId?[buildings.Count];
        var seen = new Dictionary<(int, int), byte>();
        for (int i = 0; i < buildings.Count; i++)
        {
            if (idOf(buildings[i]) is not { } id) continue;
            var cell = (id.LatE5, id.LonE5);
            seen.TryGetValue(cell, out byte dup);
            seen[cell] = (byte)Math.Min(255, dup + 1);
            ids[i] = id with { Dup = dup };
        }
        return ids;
    }

    /// <summary><c>b4620412_614871</c>, or with its duplicate's number <c>b4620412_614871_1</c>.</summary>
    public override string ToString() => Dup == 0
        ? string.Create(CultureInfo.InvariantCulture, $"b{LatE5}_{LonE5}")
        : string.Create(CultureInfo.InvariantCulture, $"b{LatE5}_{LonE5}_{Dup}");

    public static bool TryParse(string s, out BuildingId id)
    {
        id = default;
        if (s.Length < 4 || s[0] != 'b') return false;
        var p = s[1..].Split('_');
        if (p.Length is not (2 or 3)
            || !int.TryParse(p[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int lat)
            || !int.TryParse(p[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int lon)) return false;
        byte dup = 0;
        if (p.Length == 3 && !byte.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out dup)) return false;
        id = new BuildingId(lat, lon, dup);
        return true;
    }
}

/// <summary>
/// Earth-anchored ids and seeds of what is generated per leaf (#1514, G2 C5): a reward spot's id is its ~10 m cube
/// cell (level <see cref="SpotLevel"/>), as LV95's <c>RewardRules.CellId</c> was its 10 m LV95 corner; the
/// lotteries run per cube cell of level <see cref="LotteryLevel"/> (~150 m, 16 to an L14 leaf), each with its chance
/// scaled to its area so a square kilometre holds as many draws as on LV95's 250 m cells.
/// </summary>
public static class EarthCells
{
    /// <summary>Level of a spot's cell: 2^20 cells along a face's ~10,000 km, ~9.5 m at a face's middle.</summary>
    public const int SpotLevel = 20;

    /// <summary>Level of a lottery cell: ~150 m.</summary>
    public const int LotteryLevel = 16;

    /// <summary>LV95's lottery cell, the area the plan's chances are tuned to (250 m)².</summary>
    public const double Lv95CellArea = 250.0 * 250.0;

    /// <summary>A spot's id: <c>ch:F0L20x123456y654321</c>. No '_' in it, so LV95's corner parsers refuse it.</summary>
    public static string SpotId(string prefix, EcefPos at)
    {
        var k = CubeKey.FromEcef(at, SpotLevel);
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}:F{k.Face}L{k.Level}x{k.X}y{k.Y}");
    }

    /// <summary>The cell a spot id names (its prefix cut off), or null for any other id.</summary>
    public static CubeKey? CellOf(string id)
    {
        int colon = id.IndexOf(':');
        if (colon < 0) return null;
        var s = id.AsSpan(colon + 1);
        int l = s.IndexOf('L'), x = s.IndexOf('x'), y = s.IndexOf('y');
        if (s.Length < 2 || s[0] != 'F' || l < 2 || x < l || y < x) return null;
        var inv = CultureInfo.InvariantCulture;
        if (!int.TryParse(s[1..l], NumberStyles.None, inv, out int face) || !int.TryParse(s[(l + 1)..x], NumberStyles.None, inv, out int level)
            || !int.TryParse(s[(x + 1)..y], NumberStyles.None, inv, out int cx) || !int.TryParse(s[(y + 1)..], NumberStyles.None, inv, out int cy)) return null;
        var k = new CubeKey(face, level, cx, cy);
        return k.IsValid ? k : null;
    }

    /// <summary>The lottery cells of a leaf (its descendants at <see cref="LotteryLevel"/>), in (y, x) order.</summary>
    public static IEnumerable<CubeKey> LotteryCells(CubeKey leaf)
    {
        if (leaf.Level >= LotteryLevel) { yield return leaf.AncestorAt(LotteryLevel); yield break; }
        int shift = LotteryLevel - leaf.Level, n = 1 << shift;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                yield return new CubeKey(leaf.Face, LotteryLevel, (leaf.X << shift) + x, (leaf.Y << shift) + y);
    }

    /// <summary>A cell's area on the ellipsoid, m²: its two triangles between the four corners (to 1e-5 at ~150 m).</summary>
    public static double Area(CubeKey cell)
    {
        var a = cell.Vertex(0, 0, 0, 0);
        var b = cell.Vertex(0, 1, 0, 0);
        var c = cell.Vertex(0, 1, 1, 0);
        var d = cell.Vertex(0, 0, 1, 0);
        return Triangle(a, b, c) + Triangle(a, c, d);
    }

    private static double Triangle(EcefPos a, EcefPos b, EcefPos c)
    {
        var u = b - a;
        var v = c - a;
        double x = u.Y * v.Z - u.Z * v.Y, y = u.Z * v.X - u.X * v.Z, z = u.X * v.Y - u.Y * v.X;
        return 0.5 * Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>
    /// A draw's chance on a cell of <paramref name="area"/> m² for one tuned to <see cref="Lv95CellArea"/>: one draw a
    /// cell, so the same expected draws per square kilometre is p · area / 250² (capped at 1).
    /// </summary>
    public static double Chance(double p, double area) => Math.Min(1, p * area / Lv95CellArea);

    /// <summary>A cell's seed for a salt: FNV-1a 64 over (face, level, x, y, salt), the rewards' own hash.</summary>
    public static ulong Seed(CubeKey cell, ulong salt)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        ulong h = offset;
        void Mix(ulong v) { for (int i = 0; i < 8; i++) { h ^= (v >> (i * 8)) & 0xff; h *= prime; } }
        Mix((ulong)cell.Face);
        Mix((ulong)cell.Level);
        Mix((ulong)cell.X);
        Mix((ulong)cell.Y);
        Mix(salt);
        return h;
    }
}
