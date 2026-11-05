using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// The ground under and beside a leaf's at-grade roads (#1511, G2 C2): <see cref="TerrainMeshBuilder.ComputeRoadBlend"/>'s
/// embankments (cut and fill slopes, walls, junction caps, pavements, tunnel crowns) on a leaf, shared by its collision
/// patches (clearance 0) and the mesh C1 draws (<see cref="TerrainMeshBuilder.VisualBlendClearance"/>), so what a body
/// stands on is what is drawn.
///
/// <para>
/// The blend runs unchanged on a 1001² grid at 1 m in the leaf's anchor frame, centred on the leaf (a leaf is ~610 m:
/// the box holds it): its records are shifted by <see cref="Offset"/>. The leaf's own roads and its eight neighbours'
/// (moved into its frame) are blended each and merged, the tightest band a cell gets kept, as one tile's roads give it,
/// so the vertices of an edge two leaves share are clamped alike from either side. A lattice vertex (~1.2 m) takes the
/// band interpolated from the four cells round it where all four have one, else the nearest cell's.
/// </para>
/// </summary>
public static class LeafRoadBlend
{
    /// <summary>Where the leaf's centre sits in the blend's grid (the grid's x east and z south, metres).</summary>
    public const double Offset = 500;

    private const int N = ChunkFormat.GridSize;

    /// <summary>A leaf's merged band per grid cell: dense, NaN where no road constrains it.</summary>
    public sealed class Bands(float[] lo, float[] hi, float[] clear)
    {
        public float[] Lo { get; } = lo;
        public float[] Hi { get; } = hi;
        public float[] Clear { get; } = clear;
    }

    /// <summary>
    /// The bands of a leaf from its roads and its neighbours' (each in its own anchor frame; null: none); null when no road
    /// reaches the leaf.
    /// </summary>
    public static Bands? For(CubeKey leaf, RoadTile? own, Func<CubeKey, RoadTile?>? neighbours)
    {
        float[]? lo = null, hi = null, clear = null;
        var anchor = leaf.Anchor;
        void Merge(RoadTile tile)
        {
            var blend = TerrainMeshBuilder.ComputeRoadBlend(tile);
            if (blend.Cells.Length == 0) return;
            if (lo is null)
            {
                lo = new float[N * N];
                hi = new float[N * N];
                clear = new float[N * N];
                Array.Fill(lo, float.NaN);
                Array.Fill(hi, float.NaN);
            }
            for (int k = 0; k < blend.Cells.Length; k++)
            {
                int c = blend.Cells[k];
                if (float.IsNaN(lo[c]))
                {
                    (lo[c], hi[c], clear![c]) = (blend.Lo[k], blend.Hi[k], blend.Clear[k]);
                    continue;
                }
                // the tightest bound any road gives (an envelope): stacked slopes meet half way, as one tile's do
                float l = Math.Max(lo[c], blend.Lo[k]), h = Math.Min(hi[c], blend.Hi[k]);
                if (l > h) l = h = (l + h) * 0.5f;
                (lo[c], hi[c], clear![c]) = (l, h, Math.Max(clear![c], blend.Clear[k]));
            }
        }

        if (own is { Segments.Count: > 0 }) Merge(Shifted(own, null, anchor));
        if (neighbours != null)
            foreach (var k in leaf.Neighbours())
                if (neighbours(k) is { Segments.Count: > 0 } theirs) Merge(Shifted(theirs, k.Anchor, anchor));
        return lo is null ? null : new Bands(lo, hi!, clear!);
    }

    /// <summary>Clamps the leaf's lattice vertices (anchor frame, x y z a vertex) into the bands, in place.</summary>
    public static void Apply(float[] vertices, Bands bands, double clearance)
    {
        for (int v = 0; v + 2 < vertices.Length; v += 3)
        {
            float y = vertices[v + 1];
            if (float.IsNaN(y)) continue;
            double gx = vertices[v] + Offset, gz = vertices[v + 2] + Offset;
            if (gx < 0 || gz < 0 || gx > N - 1 || gz > N - 1) continue;
            if (Band(bands, gx, gz, clearance) is not var (lo, hi)) continue;
            vertices[v + 1] = y < lo ? lo : y > hi ? hi : y;
        }
    }

    private static (float Lo, float Hi)? Band(Bands b, double gx, double gz, double clearance)
    {
        int c0 = Math.Min((int)gx, N - 2), r0 = Math.Min((int)gz, N - 2);
        double u = gx - c0, v = gz - r0;
        int k00 = r0 * N + c0, k10 = k00 + 1, k01 = k00 + N, k11 = k01 + 1;
        bool all = !float.IsNaN(b.Lo[k00]) && !float.IsNaN(b.Lo[k10]) && !float.IsNaN(b.Lo[k01]) && !float.IsNaN(b.Lo[k11]);
        if (all)
        {
            double Bi(float[] a) => (a[k00] * (1 - u) + a[k10] * u) * (1 - v) + (a[k01] * (1 - u) + a[k11] * u) * v;
            double c = clearance * Bi(b.Clear);
            return ((float)(Bi(b.Lo) - c), (float)(Bi(b.Hi) - c));
        }
        int k = (v < 0.5 ? r0 : r0 + 1) * N + (u < 0.5 ? c0 : c0 + 1);
        if (float.IsNaN(b.Lo[k])) return null;
        double cl = clearance * b.Clear[k];
        return ((float)(b.Lo[k] - cl), (float)(b.Hi[k] - cl));
    }

    /// <summary>A copy of a leaf's roads in <paramref name="to"/>'s anchor frame (from <paramref name="from"/>'s; null: the same), shifted onto the grid.</summary>
    private static RoadTile Shifted(RoadTile tile, EnuFrame? from, EnuFrame to)
    {
        var copy = LeafRoads.Decode(LeafRoads.Encode(tile));
        LeafRecords.Map(copy, new Move(from, to));
        return copy;
    }

    /// <summary>A rigid move between two anchor frames (x east, y up, z south) and the shift onto the grid.</summary>
    private sealed class Move(EnuFrame? from, EnuFrame to) : LeafRecords.IMap
    {
        public (float X, float Y, float Z) Point(float x, float y, float z)
        {
            if (from is not { } f) return ((float)(x + Offset), y, (float)(z + Offset));
            var (lx, ly, lz) = to.ToLocal(f.ToEcef(x, y, z));
            return ((float)(lx + Offset), (float)ly, (float)(lz + Offset));
        }

        public float Godot(float heading, float x, float y, float z)
        {
            var (dx, dz) = Direction(-System.Math.Sin(heading), -System.Math.Cos(heading));
            return (float)System.Math.Atan2(-dx, -dz);
        }

        public float Math(float angle, float x, float y, float z)
        {
            var (dx, dz) = Direction(System.Math.Cos(angle), -System.Math.Sin(angle));
            return (float)System.Math.Atan2(-dz, dx);
        }

        /// <summary>A horizontal direction (x east, z south) from the one frame to the other, laid flat.</summary>
        private (double Dx, double Dz) Direction(double dx, double dz)
        {
            if (from is not { } f) return (dx, dz);
            var d = new EcefPos(f.East.X * dx - f.North.X * dz, f.East.Y * dx - f.North.Y * dz, f.East.Z * dx - f.North.Z * dz);
            var (lx, _, lz) = to.DirectionToLocal(d);
            return (lx, lz);
        }
    }
}
