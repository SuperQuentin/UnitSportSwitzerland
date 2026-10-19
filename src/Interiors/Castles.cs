using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// What a castle is (#859), from OSM's <c>castle_type</c> and <c>ruins</c>, its name, or its
/// solids' years: a medieval fortress, a stately home (a country seat, a bailiff's house), or a ruin.
/// </summary>
public enum CastleKind : byte { Fortress = 0, Manor = 1, Ruin = 2 }

/// <summary>
/// The building tradition a castle belongs to, from its canton: what its walls and roofs are made
/// of and how they are dressed (docs/notes/terrain/castles.md has the sources).
/// </summary>
public enum CastleRegion : byte
{
    /// <summary>Vaud, Geneva, Fribourg: the Savoyard castles, grey-beige limestone rubble, brown tiles (Chillon, Grandson, Morges).</summary>
    Savoy = 0,
    /// <summary>Neuchâtel, Jura: the yellow Hauterive limestone of the Jura lakes.</summary>
    Jura = 1,
    /// <summary>The plateau, Bern to Zürich: whitewashed walls, sandstone corners and surrounds, red tiles (Thun, Lenzburg, the bailiffs' castles).</summary>
    Plateau = 2,
    /// <summary>Thurgau, St. Gallen, Schaffhausen, Appenzell, Glarus: rendered, a half-timbered top storey, red tiles.</summary>
    East = 3,
    /// <summary>Valais and central Switzerland: dark gneiss rubble under stone-slab (lauze) roofs (Valère, Tourbillon).</summary>
    Alpine = 4,
    /// <summary>Graubünden: whitewashed rubble under grey slab roofs (Tarasp).</summary>
    Raetia = 5,
    /// <summary>Ticino: grey granite rubble, gneiss slab (piode) roofs, swallowtail merlons (Bellinzona).</summary>
    Ticino = 6,
}

/// <summary>
/// Castles (#859): the solids <see cref="List"/> names, by a point inside each one's footprint.
///
/// <para>
/// The data has no "castle": swissBUILDINGS3D draws Chillon as one solid and the GWR files it as an
/// "other minor structure". <c>TerrainPreprocessor --castles</c> resolves OpenStreetMap's
/// <c>historic=castle</c> and the GWR's castle names against the built tiles and writes one point
/// per solid into <c>Castles.List.cs</c>. Like <see cref="Landmarks"/>, a solid is matched only if
/// its real footprint (<see cref="Building.Covers"/>) holds the point, so the answer is unique and a
/// pure function of the tile: server and clients agree without a byte sent. Rules, numbers and how
/// to regenerate: <c>docs/notes/terrain/castles.md</c>.
/// </para>
/// </summary>
public static partial class Castles
{
    /// <summary>
    /// How far outside a tile a point may lie for that tile to look at it: a solid belongs to the tile
    /// holding its centre, and a castle's footprint can reach well into the next one.
    /// </summary>
    public const float Reach = 300f;

    /// <summary>A canton's building tradition. Across the border (no canton) a castle is Savoyard, as Geneva's neighbours are.</summary>
    public static CastleRegion RegionOf(string canton) => canton switch
    {
        "NE" or "JU" => CastleRegion.Jura,
        "BE" or "SO" or "AG" or "LU" or "ZH" or "ZG" or "BS" or "BL" => CastleRegion.Plateau,
        "TG" or "SG" or "SH" or "AR" or "AI" or "GL" => CastleRegion.East,
        "VS" or "UR" or "SZ" or "OW" or "NW" => CastleRegion.Alpine,
        "GR" => CastleRegion.Raetia,
        "TI" => CastleRegion.Ticino,
        _ => CastleRegion.Savoy,
    };

    public static CastleRegion RegionOf(int castle) => RegionOf(List[castle].Canton);

    /// <summary>
    /// Castles built of brick, against the stone of their region. OSM records almost no castle's
    /// material (six in Switzerland, none of them brick), so they are named here. Vufflens (c. 1415,
    /// Henri de Colombier, back from the Duke of Savoy's wars in Italy) is brick and Piedmontese,
    /// "unique to this region" (en.wikipedia.org/wiki/Vufflens_Castle, hls-dhs-dss.ch 002458).
    /// </summary>
    public static readonly HashSet<string> Brick = new(StringComparer.Ordinal) { "Château de Vufflens" };

    public static bool IsBrick(int castle) => Brick.Contains(List[castle].Name);

    /// <summary>
    /// For each building of the tile, the index in <see cref="List"/> of the castle it belongs to,
    /// or -1. Null when the tile holds no castle (nearly all of them).
    /// </summary>
    public static int[]? Match(BuildingTile tile)
    {
        var id = tile.Id;
        var near = new List<(int Castle, float X, float Z)>();
        for (int c = 0; c < List.Length; c++)
        {
            var points = List[c].Points;
            for (int p = 0; p + 1 < points.Length; p += 2)
            {
                double e = points[p], n = points[p + 1];
                if (e < id.MinE - Reach || e > id.MinE + ChunkFormat.TileSizeM + Reach
                    || n < id.MinN - Reach || n > id.MaxN + Reach) continue;
                near.Add((c, (float)(e - id.MinE), (float)(id.MaxN - n)));
            }
        }
        if (near.Count == 0) return null;

        // each solid's plan bounds, so only the few solids around a point run the triangle test
        int count = tile.Buildings.Count;
        var bounds = new (float X0, float Z0, float X1, float Z1)[count];
        for (int i = 0; i < count; i++)
        {
            var t = tile.Buildings[i].Triangles;
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                x0 = MathF.Min(x0, t[k]); x1 = MathF.Max(x1, t[k]);
                z0 = MathF.Min(z0, t[k + 2]); z1 = MathF.Max(z1, t[k + 2]);
            }
            bounds[i] = (x0, z0, x1, z1);
        }

        int[]? castle = null;
        foreach (var (c, x, z) in near)
            for (int i = 0; i < count; i++)
            {
                var (x0, z0, x1, z1) = bounds[i];
                if (x < x0 || x > x1 || z < z0 || z > z1 || castle != null && castle[i] >= 0) continue;
                if (!tile.Buildings[i].Covers(x, z)) continue;
                castle ??= Enumerable.Repeat(-1, count).ToArray();
                castle[i] = c;
                break;
            }
        return castle;
    }
}
