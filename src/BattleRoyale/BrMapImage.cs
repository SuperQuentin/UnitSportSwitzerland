using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The match region as a map image (#190): a hillshade from the 100 m horizon lattice, coloured
/// by land cover (forest, water, rock, glacier, paved), with roads, railways, rivers and building
/// footprints drawn in. Everything comes from the same chunk source the terrain streams from, so
/// it works on real and generated ground alike. Built off the main thread, once per match.
/// Pixel (0, 0) is the north-west corner of the square; docs/notes/br/map.md.
/// </summary>
public static class BrMapImage
{
    public const int Size = 1024;

    /// <summary>Builds the map of <paramref name="area"/>; the bytes are RGBA8, <see cref="Size"/> square.</summary>
    public static async Task<byte[]> BuildAsync(IChunkSource source, BrArea area, CancellationToken ct = default)
    {
        double half = area.Side * 0.5, west = area.E - half, north = area.N + half;
        double mpp = area.Side / Size;
        var horizon = await source.LoadHorizonAsync(ct);

        // the tiles under the square
        var tiles = new List<TileId>();
        for (int e = (int)Math.Floor(west / 1000); e <= (int)Math.Floor((area.E + half - 1) / 1000); e++)
            for (int n = (int)Math.Floor((area.N - half) / 1000); n <= (int)Math.Floor((north - 1) / 1000); n++)
                tiles.Add(new TileId(e, n));

        var covers = new Dictionary<TileId, byte[]>();
        var roads = new List<(TileId, RoadTile)>();
        var buildings = new List<(TileId, BuildingTile)>();
        foreach (var id in tiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await source.LoadCoverAsync(id, ct) is { } c) covers[id] = c;
                if (await source.LoadRoadsAsync(id, ct) is { } r) roads.Add((id, r));
                if (await source.LoadBuildingsAsync(id, ct) is { } b) buildings.Add((id, b));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                GD.PushWarning($"[br map] tile {id}: {e.Message}");
            }
        }

        return await Task.Run(() =>
        {
            var px = new Color[Size * Size];
            double H(double e, double n) => Height(horizon, e, n);

            // ---- ground: cover colour times hillshade ---------------------------------------
            var light = new Vector3(-1f, 1.4f, -1f).Normalized();   // from the north-west, high
            for (int y = 0; y < Size; y++)
            {
                double n = north - (y + 0.5) * mpp;
                for (int x = 0; x < Size; x++)
                {
                    double e = west + (x + 0.5) * mpp;
                    double h = H(e, n);
                    const double d = 30;
                    float dx = (float)(H(e + d, n) - H(e - d, n)) / (2f * (float)d);
                    float dn = (float)(H(e, n + d) - H(e, n - d)) / (2f * (float)d);
                    // normal in (east, up, south)
                    var normal = new Vector3(-dx, 1f, dn).Normalized();
                    float shade = Mathf.Clamp(normal.Dot(light), 0f, 1f);
                    var id = TileId.FromLv95(e, n);
                    var cover = covers.TryGetValue(id, out var cells)
                        ? (CoverClass)cells[Math.Clamp((int)(id.MaxN - n), 0, CoverFormat.Size - 1) * CoverFormat.Size
                                            + Math.Clamp((int)(e - id.MinE), 0, CoverFormat.Size - 1)]
                        : CoverClass.Open;
                    var c = Ground(cover, (float)h);
                    px[y * Size + x] = cover == CoverClass.Water ? c : (c * (0.45f + 0.75f * shade)) with { A = 1f };
                }
            }

            // ---- rivers, roads, railways ---------------------------------------------------
            foreach (var (id, tile) in roads)
                foreach (var seg in tile.Segments.OrderBy(s => Rank(s.Class)))
                {
                    if (seg.Flags.HasFlag(RoadFlags.Tunnel) || Style(seg.Class) is not { } style) continue;
                    for (int i = 0; i + 1 < seg.PointCount; i++)
                    {
                        if (style.Dashed && i % 2 == 1) continue;
                        var a = ToPixel(id, seg.Points[i * 3], seg.Points[i * 3 + 2], west, north, mpp);
                        var b = ToPixel(id, seg.Points[i * 3 + 3], seg.Points[i * 3 + 5], west, north, mpp);
                        Line(px, a, b, style.Width, style.Color);
                    }
                }

            // ---- building footprints: every triangle of the shell, seen from above ----------
            var roof = new Color(0.36f, 0.30f, 0.27f);
            foreach (var (id, tile) in buildings)
                foreach (var bld in tile.Buildings)
                {
                    var t = bld.Triangles;
                    for (int i = 0; i + 8 < t.Length; i += 9)
                        Triangle(px, ToPixel(id, t[i], t[i + 2], west, north, mpp), ToPixel(id, t[i + 3], t[i + 5], west, north, mpp),
                            ToPixel(id, t[i + 6], t[i + 8], west, north, mpp), roof);
                }

            // ---- out to bytes, five bits a channel: the PS1 palette --------------------------
            var bytes = new byte[Size * Size * 4];
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                bytes[i * 4] = Q(c.R);
                bytes[i * 4 + 1] = Q(c.G);
                bytes[i * 4 + 2] = Q(c.B);
                bytes[i * 4 + 3] = 255;
            }
            return bytes;
        }, ct);
    }

    private static byte Q(float v) => (byte)(((int)(Mathf.Clamp(v, 0f, 1f) * 255f) & 0xF8) | 0x04);

    /// <summary>Terrain height from the 100 m lattice, bilinear; 500 m where there is no data.</summary>
    internal static double Height(HorizonIndex? horizon, double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        if (horizon == null || !horizon.Contains(id)) return 500;
        double u = (e - id.MinE) / HorizonFormat.SpacingM, v = (id.MaxN - n) / HorizonFormat.SpacingM;
        int last = HorizonFormat.SamplesPerSide - 1;
        int c0 = Math.Clamp((int)u, 0, last - 1), r0 = Math.Clamp((int)v, 0, last - 1);
        double fu = Math.Clamp(u - c0, 0, 1), fv = Math.Clamp(v - r0, 0, 1);
        double h00 = horizon.HeightMetersAt(id, c0, r0), h10 = horizon.HeightMetersAt(id, c0 + 1, r0);
        double h01 = horizon.HeightMetersAt(id, c0, r0 + 1), h11 = horizon.HeightMetersAt(id, c0 + 1, r0 + 1);
        return (h00 * (1 - fu) + h10 * fu) * (1 - fv) + (h01 * (1 - fu) + h11 * fu) * fv;
    }

    /// <summary>Whether the lattice sample nearest (<paramref name="e"/>, <paramref name="n"/>) is under water (#477).</summary>
    internal static bool Wet(HorizonIndex? horizon, double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        if (horizon == null || !horizon.TryGet(id, out var heights) || !horizon.TryGetWater(id, out var levels)) return false;
        int last = HorizonFormat.SamplesPerSide - 1;
        int c = Math.Clamp((int)Math.Round((e - id.MinE) / HorizonFormat.SpacingM), 0, last);
        int r = Math.Clamp((int)Math.Round((id.MaxN - n) / HorizonFormat.SpacingM), 0, last);
        int i = r * HorizonFormat.SamplesPerSide + c;
        HorizonIndex.Surface(heights[i], levels, i, out bool wet);
        return wet;
    }

    private static Color Ground(CoverClass cover, float h) => cover switch
    {
        CoverClass.Forest or CoverClass.OpenForest or CoverClass.Shrub => new Color(0.30f, 0.46f, 0.24f),
        CoverClass.Woodland => new Color(0.42f, 0.56f, 0.30f),
        CoverClass.Water => new Color(0.33f, 0.55f, 0.80f),
        CoverClass.Wetland => new Color(0.50f, 0.64f, 0.52f),
        CoverClass.Glacier or CoverClass.Snowfield => new Color(0.93f, 0.95f, 0.97f),
        CoverClass.Rock or CoverClass.LooseRock or CoverClass.Boulders => new Color(0.60f, 0.57f, 0.54f),
        CoverClass.Scree or CoverClass.LooseScree => new Color(0.68f, 0.65f, 0.60f),
        CoverClass.Vineyard => new Color(0.60f, 0.62f, 0.34f),
        CoverClass.ParkingPublic or CoverClass.ParkingPrivate or CoverClass.RestArea or CoverClass.PavedArea => new Color(0.74f, 0.72f, 0.69f),
        // meadow: lush in the valleys, browner on the alps
        _ => new Color(0.62f, 0.76f, 0.45f).Lerp(new Color(0.70f, 0.66f, 0.50f), Mathf.Clamp((h - 1300f) / 1200f, 0f, 1f)),
    };

    private readonly record struct Stroke(int Width, Color Color, bool Dashed = false);

    private static Stroke? Style(RoadClass c) => c switch
    {
        RoadClass.Motorway or RoadClass.Expressway => new Stroke(4, new Color(0.95f, 0.62f, 0.25f)),
        RoadClass.Ramp or RoadClass.Major => new Stroke(3, new Color(0.98f, 0.85f, 0.45f)),
        RoadClass.Road => new Stroke(2, new Color(0.98f, 0.97f, 0.93f)),
        RoadClass.Minor or RoadClass.Lane or RoadClass.Square => new Stroke(2, new Color(0.92f, 0.90f, 0.86f)),
        RoadClass.Track => new Stroke(1, new Color(0.84f, 0.76f, 0.58f)),
        RoadClass.Railway => new Stroke(2, new Color(0.22f, 0.22f, 0.24f), Dashed: true),
        RoadClass.Watercourse => new Stroke(2, new Color(0.33f, 0.55f, 0.80f)),
        _ => null,
    };

    /// <summary>Small things first, so a motorway is drawn over the lane that crosses it.</summary>
    private static int Rank(RoadClass c) => c switch
    {
        RoadClass.Watercourse => 0, RoadClass.Track => 1, RoadClass.Lane or RoadClass.Minor => 2, RoadClass.Road => 3,
        RoadClass.Railway => 4, RoadClass.Major or RoadClass.Ramp => 5, _ => 6,
    };

    /// <summary>Tile-local metres (X east, Z south from the NW corner) to map pixels.</summary>
    private static Vector2 ToPixel(TileId id, float x, float z, double west, double north, double mpp) =>
        new((float)((id.MinE + x - west) / mpp), (float)((north - (id.MaxN - z)) / mpp));

    private static void Line(Color[] px, Vector2 a, Vector2 b, int width, Color color)
    {
        float len = a.DistanceTo(b);
        int steps = Math.Max(1, (int)(len * 1.5f));
        int r0 = -(width - 1) / 2, r1 = width / 2;
        for (int s = 0; s <= steps; s++)
        {
            var p = a.Lerp(b, s / (float)steps);
            int cx = (int)p.X, cy = (int)p.Y;
            for (int dy = r0; dy <= r1; dy++)
                for (int dx = r0; dx <= r1; dx++)
                    Plot(px, cx + dx, cy + dy, color);
        }
    }

    private static void Triangle(Color[] px, Vector2 a, Vector2 b, Vector2 c, Color color)
    {
        float area = (b - a).Cross(c - a);
        if (Mathf.Abs(area) < 1e-4f) return;   // a wall, edge-on from above
        int x0 = (int)Mathf.Floor(Mathf.Min(a.X, Mathf.Min(b.X, c.X))), x1 = (int)Mathf.Ceil(Mathf.Max(a.X, Mathf.Max(b.X, c.X)));
        int y0 = (int)Mathf.Floor(Mathf.Min(a.Y, Mathf.Min(b.Y, c.Y))), y1 = (int)Mathf.Ceil(Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y)));
        if (x1 < 0 || y1 < 0 || x0 >= Size || y0 >= Size) return;
        // smaller than a pixel: one dot, so small houses still show
        if (x1 - x0 <= 1 && y1 - y0 <= 1) { Plot(px, x0, y0, color); return; }
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float w0 = (b - a).Cross(p - a), w1 = (c - b).Cross(p - b), w2 = (a - c).Cross(p - c);
                if ((w0 >= 0 && w1 >= 0 && w2 >= 0) || (w0 <= 0 && w1 <= 0 && w2 <= 0)) Plot(px, x, y, color);
            }
    }

    private static void Plot(Color[] px, int x, int y, Color color)
    {
        if (x >= 0 && y >= 0 && x < Size && y < Size) px[y * Size + x] = color;
    }
}
