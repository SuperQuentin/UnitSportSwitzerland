using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// A leaf as an LV95-shaped tile (#1510, G2 C1 PR 2): the frame today's builders take, so they furnish a leaf
/// unchanged. The tile's local frame is the leaf's anchor frame shifted by <see cref="Offset"/> along x (east) and
/// z (south), as C2's road blend does (<see cref="LeafRoadBlend"/>): the leaf (~610 m) lies in the middle of the
/// 1000 m square. Its name is a <see cref="TileId"/> no LV95 tile has (<see cref="Id"/>), so a
/// <c>BuildingKey</c>, a <c>DoorKey</c>, the door index and a chunk source all name a leaf's buildings the way they
/// name a tile's, and its ground is a 1 m <see cref="ChunkGrid"/> sampled from the leaf's mesh (<see cref="Grid"/>).
///
/// <para>
/// The records are copied, never changed in place: a leaf's content is shared with its collision. D may replace the
/// grid with a builder input of its own; until then this is the one place a leaf turns into a tile.
/// </para>
/// </summary>
public static class LeafStandIn
{
    /// <summary>Where the leaf's anchor origin sits in its tile, metres east and south of the tile's north-west corner.</summary>
    public const double Offset = 500;   // LeafRoadBlend.Offset: the same square

    private const int N = ChunkFormat.GridSize;

    /// <summary>A leaf's tile name: unique, the same every run (interior layouts and loot are stored under it).</summary>
    public static TileId Id(CubeKey leaf) => LeafTileName.Id(leaf);

    /// <summary>The leaf a stand-in tile names; false for a real LV95 tile.</summary>
    public static bool TryLeaf(TileId id, out CubeKey leaf) => LeafTileName.TryLeaf(id, out leaf);

    public static bool IsStandIn(TileId id) => TryLeaf(id, out _);

    /// <summary>
    /// The 1 m grid of a leaf's ground (its lattice vertices in its anchor frame, x y z a vertex, row-major, NaN where
    /// it has none): each grid vertex takes the height of the mesh triangle over it (split as the drawn mesh and
    /// <see cref="CubeGeometry.MeshHeightAt"/> split it). Grid vertices off the leaf take the nearest height on their
    /// row, or the nearest row's. Quantised as a tile's are.
    /// </summary>
    public static ChunkGrid Grid(TileId id, float[] vertices, int side) => Grid(id, vertices, side, null);

    /// <summary>As <see cref="Grid(TileId, float[], int)"/>; also, at every 2 m sample, the lattice vertex nearest it (−1 off the leaf).</summary>
    public static ChunkGrid Grid(TileId id, float[] vertices, int side, int[]? nearestVertex)
    {
        var h = new float[N * N];
        Array.Fill(h, float.NaN);
        if (nearestVertex != null) Array.Fill(nearestVertex, -1);
        for (int j = 0; j + 1 < side; j++)
            for (int i = 0; i + 1 < side; i++)
            {
                int a = j * side + i, b = a + 1, c = a + side, d = c + 1;
                // the lower triangle (i, j), (i+1, j), (i+1, j+1) and the upper (i, j), (i+1, j+1), (i, j+1)
                Raster(vertices, a, b, d, h, nearestVertex);
                Raster(vertices, a, d, c, h, nearestVertex);
            }
        Fill(h);

        float min = float.MaxValue, max = float.MinValue;
        var q = new ushort[N * N];
        for (int k = 0; k < q.Length; k++)
        {
            q[k] = ChunkFormat.Quantize(h[k]);
            float m = (float)ChunkFormat.Dequantize(q[k]);
            if (m < min) min = m;
            if (m > max) max = m;
        }
        return new ChunkGrid(id, q, min, max);
    }

    /// <summary>One triangle's grid vertices: barycentric in the plan (x + offset, z + offset).</summary>
    private static void Raster(float[] v, int ka, int kb, int kc, float[] h, int[]? nearest)
    {
        double ax = v[3 * ka] + Offset, ay = v[3 * ka + 1], az = v[3 * ka + 2] + Offset;
        double bx = v[3 * kb] + Offset, by = v[3 * kb + 1], bz = v[3 * kb + 2] + Offset;
        double cx = v[3 * kc] + Offset, cy = v[3 * kc + 1], cz = v[3 * kc + 2] + Offset;
        if (double.IsNaN(ay) || double.IsNaN(by) || double.IsNaN(cy)) return;
        double det = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
        if (Math.Abs(det) < 1e-12) return;
        int c0 = Math.Max(0, (int)Math.Ceiling(Math.Min(ax, Math.Min(bx, cx)) - 1e-9));
        int c1 = Math.Min(N - 1, (int)Math.Floor(Math.Max(ax, Math.Max(bx, cx)) + 1e-9));
        int r0 = Math.Max(0, (int)Math.Ceiling(Math.Min(az, Math.Min(bz, cz)) - 1e-9));
        int r1 = Math.Min(N - 1, (int)Math.Floor(Math.Max(az, Math.Max(bz, cz)) + 1e-9));
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                double wa = ((bz - cz) * (c - cx) + (cx - bx) * (r - cz)) / det;
                double wb = ((cz - az) * (c - cx) + (ax - cx) * (r - cz)) / det;
                double wc = 1 - wa - wb;
                if (wa < -1e-9 || wb < -1e-9 || wc < -1e-9) continue;
                int k = r * N + c;
                h[k] = (float)(wa * ay + wb * by + wc * cy);
                if (nearest != null && (c & 1) == 0 && (r & 1) == 0)
                    nearest[(r >> 1) * WaterTile.Size + (c >> 1)] = wa >= wb && wa >= wc ? ka : wb >= wc ? kb : kc;
            }
    }

    /// <summary>The grid vertices no triangle covered: the nearest covered one on their row, else the nearest covered row's.</summary>
    private static void Fill(float[] h)
    {
        var full = new bool[N];
        for (int r = 0; r < N; r++)
        {
            int row = r * N, first = -1, last = -1;
            for (int c = 0; c < N; c++)
                if (!float.IsNaN(h[row + c])) { if (first < 0) first = c; last = c; }
            if (first < 0) continue;
            full[r] = true;
            for (int c = 0; c < first; c++) h[row + c] = h[row + first];
            for (int c = last + 1; c < N; c++) h[row + c] = h[row + last];
            // a gap inside the row (a hole in the data): the nearer side
            for (int c = first + 1; c < last; c++)
            {
                if (!float.IsNaN(h[row + c])) continue;
                int e = c;
                while (float.IsNaN(h[row + e])) e++;
                for (int g = c; g < e; g++) h[row + g] = g - (c - 1) <= e - g ? h[row + c - 1] : h[row + e];
                c = e;
            }
        }
        int near = -1;
        for (int r = 0; r < N; r++)
            if (full[r]) { near = r; break; }
        if (near < 0)
        {
            Array.Fill(h, 0f);
            return;
        }
        for (int r = 0; r < N; r++)
        {
            if (full[r]) { near = r; continue; }
            // the nearest covered row, below or above
            int below = r + 1;
            while (below < N && !full[below]) below++;
            int from = below < N && (near > r || below - r < r - near) ? below : near;
            Array.Copy(h, from * N, h, r * N, N);
        }
    }

    /// <summary>A leaf's roads in its stand-in tile (a copy).</summary>
    public static RoadTile Roads(RoadTile leafRoads, TileId id)
    {
        var copy = LeafRoads.Decode(LeafRoads.Encode(leafRoads));
        LeafRecords.Map(copy, new Shift());
        return copy.WithId(id);
    }

    /// <summary>A leaf's buildings in its stand-in tile (a copy; the solids in the leaf's order, so an index names the same solid).</summary>
    public static BuildingTile Buildings(BuildingTile leafBuildings, TileId id)
    {
        // every choice seeded from a building's key seeds from its Earth identity instead (#1658, C5's BuildingId),
        // registered before any footprint, door or plan of this tile can exist
        if (TryLeaf(id, out var leaf))
        {
            var ids = BuildingId.Assign(leafBuildings.Buildings, b => BuildingId.OnLeaf(leaf, b));
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] is { } bid) Core.Fnv.Alias($"{id.E}_{id.N}_{i}", bid.ToString());
        }
        var list = new List<Building>(leafBuildings.Buildings.Count);
        foreach (var b in leafBuildings.Buildings)
        {
            var t = (float[])b.Triangles.Clone();
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                t[k] += (float)Offset;
                t[k + 2] += (float)Offset;
            }
            list.Add(new Building { Kind = b.Kind, Egid = b.Egid, YearBuilt = b.YearBuilt, Floors = b.Floors, MinY = b.MinY, MaxY = b.MaxY, Triangles = t });
        }
        return new BuildingTile { Id = id, Buildings = list };
    }

    /// <summary>A leaf's trees in its stand-in tile.</summary>
    public static List<TreeInstance> Trees(IReadOnlyList<TreeInstance> trees)
    {
        var list = new List<TreeInstance>(trees.Count);
        foreach (var t in trees) list.Add(t with { X = t.X + (float)Offset, Z = t.Z + (float)Offset });
        return list;
    }

    /// <summary>
    /// A leaf's still water as its stand-in tile's 2 m water layer: each sample takes the level of the leaf's water
    /// sample nearest it (the lattice vertex <paramref name="nearestVertex"/> gives, rounded to the water's every second
    /// vertex), as an anchor-frame height; its wave scale from the fetch and the depth over <paramref name="bed"/>.
    /// Null when the leaf is dry. The samples reach <see cref="WaterMarginSamples"/> past the leaf, taking the nearest
    /// sample's vertex on it, so the surface covers the leaf to its edges once clipped there (#1632,
    /// <c>Earth/LeafWaterClip</c>); a sample off the leaf further out stays dry.
    /// </summary>
    public static WaterLayer? Water(CubeWater water, LeafTransform t, int side, int[] nearestVertex, ChunkGrid bed)
    {
        nearestVertex = Grown(nearestVertex, WaterMarginSamples);
        int ws = water.Side;
        // each wet water sample's level as a local height, once
        float[]? local = null;
        for (int s = 0; s < water.Level.Length; s++)
        {
            if (float.IsNaN(water.Level[s])) continue;
            if (local == null)
            {
                local = new float[water.Level.Length];
                Array.Fill(local, float.NaN);
            }
            int si = s % ws, sj = s / ws;
            local[s] = (float)t.LatticeToLocal(si * CubeWater.Stride, sj * CubeWater.Stride, water.Level[s]).Y;
        }
        if (local == null) return null;

        var level = new float[WaterTile.Size * WaterTile.Size];
        var fetch = new float[level.Length];
        Array.Fill(level, float.NaN);
        bool any = false;
        for (int k = 0; k < level.Length; k++)
        {
            int v = nearestVertex[k];
            if (v < 0) continue;
            int i = v % side, j = v / side;
            int si = Math.Min((i + 1) / CubeWater.Stride, ws - 1), sj = Math.Min((j + 1) / CubeWater.Stride, ws - 1);
            int s = sj * ws + si;
            if (float.IsNaN(local[s])) continue;
            level[k] = local[s];
            fetch[k] = (float)(water.Fetch[s] * WaterFormat.FetchUnitM);
            any = true;
        }
        return any ? WaterLayer.Create(new WaterTile { Level = level, FetchM = fetch }, bed) : null;
    }

    /// <summary>How far a leaf's water samples reach past it, in 2 m samples.</summary>
    public const int WaterMarginSamples = 2;

    /// <summary>The samples' vertices grown outward by <paramref name="rings"/> samples: an unset one takes a set 8-neighbour's.</summary>
    internal static int[] Grown(int[] nearest, int rings)
    {
        const int m = WaterTile.Size;
        var cur = nearest;
        for (int ring = 0; ring < rings; ring++)
        {
            var next = (int[])cur.Clone();
            for (int r = 0; r < m; r++)
                for (int c = 0; c < m; c++)
                {
                    if (cur[r * m + c] >= 0) continue;
                    for (int dr = -1; dr <= 1 && next[r * m + c] < 0; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            int rr = r + dr, cc = c + dc;
                            if ((uint)rr >= m || (uint)cc >= m || cur[rr * m + cc] < 0) continue;
                            next[r * m + c] = cur[rr * m + cc];
                            break;
                        }
                }
            cur = next;
        }
        return cur;
    }

    /// <summary>Anchor frame to stand-in tile: a shift along x and z, headings unchanged.</summary>
    private sealed class Shift : LeafRecords.IMap
    {
        public (float X, float Y, float Z) Point(float x, float y, float z) => ((float)(x + Offset), y, (float)(z + Offset));
        public float Godot(float heading, float x, float y, float z) => heading;
        public float Math(float angle, float x, float y, float z) => angle;
    }
}
