using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Stadiums;

/// <summary>
/// The stadiums we build, one row each (#885). A row is placed by its centre spot, so like the
/// IKEA landmarks it is a pure function of the tile on every peer: the tile holding the centre spot
/// builds the whole bowl, mesh and collision, and nothing is sent.
///
/// <para>
/// Adding a stadium: take the centre spot and axis from the pitch's <c>SportsField</c> cells in its
/// cover tile (the centroid and the principal axis), the seating front, depths and roof from the
/// swissimage aerial (WMS <c>ch.swisstopo.swissimage</c>, 3 px a metre is plenty), and the tiers
/// from photos and the published capacity; then check it with <c>--stadiumcheck</c> and look at
/// it in the model viewer. How each figure of a row was found: <c>docs/notes/terrain/stadiums.md</c>.
/// </para>
/// </summary>
public static class StadiumCatalog
{
    /// <summary>
    /// Stade de Genève (La Praille, Lancy), 2003, Bernard Mocellin with RSA: 30,084 seats, all
    /// covered, garnet since the 2019-2020 renovation. Pitch from cover_2498_1114 (centroid and
    /// principal axis of its SportsField cells: 104.4 x 67.8 m, 1.35 degrees west of north); roof
    /// 153 x 206 m outside, opening 83 x 133 m, from the aerial; 39 m overall with the roof at 32 m
    /// (pss-archi data sheet); 48 trusses on 48 pillars with a translucent polycarbonate ring along
    /// the inner edge (builder's dossier).
    /// </summary>
    public static readonly StadiumSpec Geneva = new()
    {
        Name = "Stade de Genève",
        E = 2498751.2, N = 1114890.3, AzimuthDeg = -1.35f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 40f, FrontHalfV = 62f, FrontCorner = 4f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 17, RowDepth: 0.8f, Riser: 0.38f, Gap: 0f, Step: 1.2f),
            // the boxes' and clubs' glazing under the upper tier
            new StadiumTier(Rows: 18, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 3.4f),
        },
        DepthNorth = 41f, DepthEast = 32.5f, DepthSouth = 39f, DepthWest = 38.3f,
        RoofOuter = 34f, RoofInner = 30f, RoofInset = 2.5f, RoofRing = 9f, Solar = true,
        Columns = 48, ColumnTop = 39f,
    };

    public static readonly StadiumSpec[] All = { Geneva };

    /// <summary>The tile that builds a stadium: the one holding its centre spot.</summary>
    public static TileId Owner(StadiumSpec s) => TileId.FromLv95(s.E, s.N);

    /// <summary>The stadiums this tile builds, usually none.</summary>
    public static IEnumerable<StadiumSpec> OwnedBy(TileId id)
    {
        foreach (var s in All)
            if (Owner(s) == id) yield return s;
    }
}
