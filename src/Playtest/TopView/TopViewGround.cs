using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Playtest.TopView;

/// <summary>A road, rail, river or ropeway under the top view, in LV95 metres: drawn as a line of its width.</summary>
public sealed record TopRoad(List<EN> Points, double Width, string Fill, string? Casing, bool Dashed, int Order);

/// <summary>The ground under a top view's square: a raster (row 0 north) and the lines drawn over it.</summary>
public sealed class TopGround
{
    public EN Min;
    public double Side;
    public int Px;
    /// <summary>RGBA8, <see cref="Px"/>², row 0 the north edge.</summary>
    public byte[] Rgba = [];
    public readonly List<TopRoad> Roads = new();
    /// <summary>Tiles the square found, of the ones it touches: 0 when the source has none here.</summary>
    public int Tiles;
}

/// <summary>
/// The top view's ground (#851, docs/notes/general/topview.md): the square's tiles read through
/// the game's own <see cref="IChunkSource"/> (a fixture course or the map), rasterised as shaded
/// relief tinted by the ground cover (forest, water, rock, paved...) with 10 m contours and the
/// buildings' footprints, and its roads, rails, rivers and ropeways kept as lines of their width
/// for the SVG and the live overlay to draw. No engine: Tier 0 tests it on a fixture course.
/// </summary>
public static class TopViewGround
{
    /// <summary>Buildings are drawn up to this many metres a pixel, contours up to this.</summary>
    private const double BuildingsUpTo = 3, ContoursUpTo = 2.5;

    public static async Task<TopGround> LoadAsync(IChunkSource source, EN min, double side, int px, CancellationToken ct = default)
    {
        var ground = new TopGround { Min = min, Side = side, Px = px, Rgba = new byte[px * px * 4] };
        double mpp = side / px;
        var grids = new Dictionary<TileId, ChunkGrid>();
        var covers = new Dictionary<TileId, byte[]>();
        var buildings = new List<(TileId Tile, BuildingTile B)>();
        var first = TileId.FromLv95(min.E, min.N);
        var last = TileId.FromLv95(min.E + side, min.N + side);
        for (int te = first.E; te <= last.E; te++)
            for (int tn = first.N; tn <= last.N; tn++)
            {
                var id = new TileId(te, tn);
                // past 5 m a pixel the 1 m grid is wasted: the companion at stride 10 does
                var grid = mpp > 5 ? await source.LoadCoarseChunkAsync(id, ct) ?? await source.LoadChunkAsync(id, ct) : await source.LoadChunkAsync(id, ct);
                if (grid == null) continue;
                ground.Tiles++;
                grids[id] = grid;
                if (await source.LoadCoverAsync(id, ct) is { } cover) covers[id] = cover;
                if (mpp <= BuildingsUpTo && await source.LoadBuildingsAsync(id, ct) is { } b) buildings.Add((id, b));
                if (await source.LoadRoadsAsync(id, ct) is { } roads) AddRoads(ground, id, roads);
            }
        Rasterise(ground, grids, covers);
        foreach (var (tile, b) in buildings) Footprints(ground, tile, b);
        ground.Roads.Sort((a, b) => a.Order.CompareTo(b.Order));
        return ground;
    }

    // ---- the raster ------------------------------------------------------------------------

    private static void Rasterise(TopGround g, Dictionary<TileId, ChunkGrid> grids, Dictionary<TileId, byte[]> covers)
    {
        double mpp = g.Side / g.Px;
        // the slope over a pixel, but never finer than the grid itself (a coarse tile is 10 m)
        double d = Math.Max(mpp, grids.Values.Select(x => x.Spacing).DefaultIfEmpty(1).Min());
        double? H(double e, double n) => grids.TryGetValue(TileId.FromLv95(e, n), out var grid) ? grid.SampleHeight(e, n) : null;
        // light from the north-west, 45° up: the cartographers' convention
        double lx = -Math.Cos(Math.PI / 4) * Math.Sin(Math.PI / 4), ly = Math.Cos(Math.PI / 4) * Math.Cos(Math.PI / 4), lz = Math.Sin(Math.PI / 4);
        var heights = new double[g.Px];
        double[]? above = null;
        for (int row = 0; row < g.Px; row++)
        {
            double n = g.Min.N + g.Side - (row + 0.5) * mpp;
            for (int col = 0; col < g.Px; col++)
            {
                double e = g.Min.E + (col + 0.5) * mpp;
                int at = (row * g.Px + col) * 4;
                if (H(e, n) is not { } h)
                {
                    // no tile here: a neutral grey, so "no ground" never reads as a flat meadow
                    (g.Rgba[at], g.Rgba[at + 1], g.Rgba[at + 2], g.Rgba[at + 3]) = ((byte)226, (byte)224, (byte)220, (byte)255);
                    heights[col] = double.NaN;
                    continue;
                }
                heights[col] = h;
                double dzdx = ((H(e + d, n) ?? h) - (H(e - d, n) ?? h)) / (2 * d);
                double dzdy = ((H(e, n + d) ?? h) - (H(e, n - d) ?? h)) / (2 * d);
                double len = Math.Sqrt(dzdx * dzdx + dzdy * dzdy + 1);
                double shade = Math.Max(0, (-dzdx * lx - dzdy * ly + lz) / len);
                var (r, gr, b) = CoverColor(CoverAt(covers, e, n));
                // flat ground at full colour, the slopes facing away darker, those facing the light a little lighter
                double k = 0.62 + 0.53 * shade;
                if (mpp <= ContoursUpTo)
                {
                    bool contour = col > 0 && !double.IsNaN(heights[col - 1]) && Math.Floor(heights[col - 1] / 10) != Math.Floor(h / 10)
                        || above != null && !double.IsNaN(above[col]) && Math.Floor(above[col] / 10) != Math.Floor(h / 10);
                    if (contour) k *= 0.82;
                }
                g.Rgba[at] = Clamp(r * k);
                g.Rgba[at + 1] = Clamp(gr * k);
                g.Rgba[at + 2] = Clamp(b * k);
                g.Rgba[at + 3] = 255;
            }
            above = (double[])heights.Clone();
        }
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static CoverClass CoverAt(Dictionary<TileId, byte[]> covers, double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        if (!covers.TryGetValue(id, out var cells)) return CoverClass.Open;
        int size = (int)Math.Round(Math.Sqrt(cells.Length));
        if (size < 2) return CoverClass.Open;
        double step = ChunkFormat.TileSizeM / (size - 1);
        int col = Math.Clamp((int)Math.Round((e - id.MinE) / step), 0, size - 1);
        int row = Math.Clamp((int)Math.Round((id.MaxN - n) / step), 0, size - 1);
        return (CoverClass)cells[row * size + col];
    }

    /// <summary>The ground cover's tint, as a map draws it.</summary>
    public static (int R, int G, int B) CoverColor(CoverClass c) => c switch
    {
        CoverClass.Forest or CoverClass.Woodland => (150, 182, 128),
        CoverClass.OpenForest or CoverClass.Shrub or CoverClass.Nursery => (174, 198, 148),
        CoverClass.Rock or CoverClass.LooseRock or CoverClass.Boulders => (196, 190, 180),
        CoverClass.Scree or CoverClass.LooseScree => (212, 206, 194),
        CoverClass.Water => (160, 198, 232),
        CoverClass.Wetland => (186, 214, 200),
        CoverClass.Glacier or CoverClass.Snowfield => (236, 242, 247),
        CoverClass.Vineyard or CoverClass.Orchard => (214, 214, 164),
        CoverClass.ParkingPublic or CoverClass.ParkingPrivate or CoverClass.RestArea or CoverClass.PavedArea => (212, 208, 202),
        CoverClass.Park or CoverClass.Allotment or CoverClass.Cemetery => (198, 222, 176),
        CoverClass.Institution or CoverClass.Industrial or CoverClass.Quarry or CoverClass.Landfill => (216, 206, 198),
        _ => (230, 236, 210),
    };

    /// <summary>Every building's roof and walls seen from above, filled: its footprint.</summary>
    private static void Footprints(TopGround g, TileId tile, BuildingTile tileBuildings)
    {
        double k = g.Px / g.Side;
        foreach (var b in tileBuildings.Buildings)
        {
            var t = b.Triangles;
            for (int i = 0; i + 8 < t.Length; i += 9)
            {
                // tile-local metres: X east, Z south
                double X(int v) => (tile.MinE + t[i + v * 3] - g.Min.E) * k;
                double Y(int v) => (g.Min.N + g.Side - (tile.MaxN - t[i + v * 3 + 2])) * k;
                FillTriangle(g, X(0), Y(0), X(1), Y(1), X(2), Y(2), (168, 156, 148));
            }
        }
    }

    private static void FillTriangle(TopGround g, double x0, double y0, double x1, double y1, double x2, double y2, (int R, int G, int B) c)
    {
        double area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(area) < 1e-6) return;   // a wall, edge on from above
        int minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)))), maxX = Math.Min(g.Px - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        int minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2)))), maxY = Math.Min(g.Px - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) / area;
                double w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) / area;
                if (w0 < 0 || w1 < 0 || w0 + w1 > 1) continue;
                int at = (y * g.Px + x) * 4;
                (g.Rgba[at], g.Rgba[at + 1], g.Rgba[at + 2]) = ((byte)c.R, (byte)c.G, (byte)c.B);
            }
    }

    // ---- the lines -------------------------------------------------------------------------

    private static void AddRoads(TopGround g, TileId tile, RoadTile roads)
    {
        double pad = g.Side * 0.05;
        foreach (var s in roads.Segments)
        {
            if (Style(s) is not { } style) continue;
            var pts = new List<EN>(s.PointCount);
            bool inside = false;
            for (int i = 0; i < s.PointCount; i++)
            {
                var (e, n) = s.Lv95(tile, i);
                pts.Add(new EN(e, n));
                inside |= e >= g.Min.E - pad && e <= g.Min.E + g.Side + pad && n >= g.Min.N - pad && n <= g.Min.N + g.Side + pad;
            }
            if (inside && pts.Count > 1) g.Roads.Add(new TopRoad(pts, style.Width, style.Fill, style.Casing, style.Dashed, style.Order));
        }
    }

    /// <summary>How a segment is drawn: width (its own when it has one), fill, casing, dashes, and what goes over what. Null: not drawn.</summary>
    private static (double Width, string Fill, string? Casing, bool Dashed, int Order)? Style(RoadSegment s)
    {
        double w = s.Width > 0 ? s.Width : 0;
        return s.Class switch
        {
            RoadClass.Watercourse or RoadClass.DryChannel or RoadClass.Bisse => (Math.Max(w, 2), "#6fa8dc", null, s.Class == RoadClass.DryChannel, 0),
            RoadClass.Track or RoadClass.Path => (Math.Max(w, 1.5), "#a0825f", null, true, 1),
            RoadClass.Railway => (Math.Max(w, 1.5), "#555555", "#ffffff", true, 3),
            RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp => (Math.Max(w, 8), "#f2c063", "#9a7a3a", false, 4),
            RoadClass.Major or RoadClass.Road or RoadClass.Minor or RoadClass.Lane or RoadClass.Link or RoadClass.Square or RoadClass.Unknown
                => (Math.Max(w, 3), "#fcfbf7", "#8e8a84", false, 2),
            RoadClass.Cableway or RoadClass.Chairlift or RoadClass.SkiLift => (1, "#333333", null, true, 5),
            _ => null,
        };
    }
}
