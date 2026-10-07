using System.Collections.Concurrent;
using System.Text;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>Ring maths and tile placement for the farm-field stage (#494). Rings are interleaved (east, north) doubles.</summary>
public static class FieldGeometry
{
    public const double SimplifyM = 0.5;
    public const double MinAreaM2 = 200;
    /// <summary>A hole smaller than one game cell (4 m x 4 m) cannot show.</summary>
    public const double MinHoleM2 = 16;

    public static double SignedArea(double[] ring)
    {
        double a = 0;
        int n = ring.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
            a += ring[j * 2] * ring[i * 2 + 1] - ring[i * 2] * ring[j * 2 + 1];
        return a / 2;
    }

    /// <summary>Area of the first ring minus the others (square metres).</summary>
    public static double Area(IReadOnlyList<double[]> rings)
    {
        double a = Math.Abs(SignedArea(rings[0]));
        for (int i = 1; i < rings.Count; i++) a -= Math.Abs(SignedArea(rings[i]));
        return a;
    }

    public static bool InRing(double[] ring, double e, double n)
    {
        bool inside = false;
        int c = ring.Length / 2;
        for (int i = 0, j = c - 1; i < c; j = i++)
        {
            double yi = ring[i * 2 + 1], yj = ring[j * 2 + 1];
            if ((yi > n) != (yj > n) && e < (ring[j * 2] - ring[i * 2]) * (n - yi) / (yj - yi) + ring[i * 2]) inside = !inside;
        }
        return inside;
    }

    /// <summary>Douglas-Peucker on a closed ring: split at the vertex farthest from the first, simplify both halves.</summary>
    public static double[] SimplifyRing(double[] ring, double tol)
    {
        int n = ring.Length / 2;
        if (n <= 3) return ring;
        int far = 0;
        double best = -1;
        for (int i = 1; i < n; i++)
        {
            double dx = ring[i * 2] - ring[0], dy = ring[i * 2 + 1] - ring[1], d = dx * dx + dy * dy;
            if (d > best) { best = d; far = i; }
        }
        if (best <= 0) return ring;
        var first = SimplifyOpen(ring[..((far + 1) * 2)], tol);
        var tail = new double[(n - far + 1) * 2];
        Array.Copy(ring, far * 2, tail, 0, (n - far) * 2);
        tail[^2] = ring[0]; tail[^1] = ring[1];
        var second = SimplifyOpen(tail, tol);
        // first ends on vertex `far`, second starts on it and ends on vertex 0: drop the duplicates
        var result = new double[first.Length + second.Length - 4];
        Array.Copy(first, 0, result, 0, first.Length);
        Array.Copy(second, 2, result, first.Length, second.Length - 4);
        return result;
    }

    private static double[] SimplifyOpen(double[] pts, double tol)
    {
        int n = pts.Length / 2;
        if (n <= 2) return pts;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, n - 1));
        double tol2 = tol * tol;
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double ax = pts[a * 2], ay = pts[a * 2 + 1], bx = pts[b * 2], by = pts[b * 2 + 1];
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            int idx = -1;
            double worst = tol2;
            for (int i = a + 1; i < b; i++)
            {
                double px = pts[i * 2] - ax, py = pts[i * 2 + 1] - ay;
                double t = len2 > 0 ? Math.Clamp((px * dx + py * dy) / len2, 0, 1) : 0;
                double qx = px - t * dx, qy = py - t * dy, d2 = qx * qx + qy * qy;
                if (d2 > worst) { worst = d2; idx = i; }
            }
            if (idx < 0) continue;
            keep[idx] = true;
            stack.Push((a, idx));
            stack.Push((idx, b));
        }
        var result = new List<double>(n * 2);
        for (int i = 0; i < n; i++) if (keep[i]) { result.Add(pts[i * 2]); result.Add(pts[i * 2 + 1]); }
        return result.ToArray();
    }

    /// <summary>
    /// Simplifies a polygon (first ring outer) and drops what cannot show: null when the outline
    /// degenerates or the area is under <see cref="MinAreaM2"/>; holes under <see cref="MinHoleM2"/> go.
    /// </summary>
    public static List<double[]>? Prepare(IReadOnlyList<double[]> rings)
    {
        var outer = SimplifyRing(rings[0], SimplifyM);
        if (outer.Length < 6) return null;
        var result = new List<double[]> { outer };
        for (int i = 1; i < rings.Count; i++)
        {
            var hole = SimplifyRing(rings[i], SimplifyM);
            if (hole.Length >= 6 && Math.Abs(SignedArea(hole)) >= MinHoleM2) result.Add(hole);
        }
        return Area(result) < MinAreaM2 ? null : result;
    }

    /// <summary>Stable 32-bit id (FNV-1a) of a text and a part index; top bit clear (OSM ids set it).</summary>
    public static uint LwbId(string canton, string identifier, int part)
    {
        uint h = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes($"{canton}/{identifier}#{part}")) h = (h ^ b) * 16777619;
        return h & 0x7FFFFFFF;
    }

    /// <summary>OSM way (<paramref name="relation"/> false) or relation id with a part index: top bit set.</summary>
    public static uint OsmId(bool relation, long osmId, int part)
    {
        ulong h = 14695981039346656037UL;
        foreach (long v in new[] { relation ? 1L : 0L, osmId, part }) { h = (h ^ (ulong)v) * 1099511628211UL; h ^= h >> 29; }
        return (uint)(h & 0x7FFFFFFF) | 0x80000000;
    }
}

/// <summary>Collects prepared fields per tile, a field whole into every tile its bounds touch.</summary>
public sealed class FieldSink(IReadOnlySet<TileId> region)
{
    public readonly ConcurrentDictionary<TileId, List<FieldPolygon>> Tiles = new();

    /// <summary>Adds the field to every region tile its bounds touch; returns how many.</summary>
    public int Add(uint id, CropKind crop, FieldSource source, ushort lnf, IReadOnlyList<double[]> rings)
    {
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
        var outer = rings[0];
        for (int i = 0; i < outer.Length; i += 2)
        {
            minE = Math.Min(minE, outer[i]); maxE = Math.Max(maxE, outer[i]);
            minN = Math.Min(minN, outer[i + 1]); maxN = Math.Max(maxN, outer[i + 1]);
        }
        var lo = TileId.FromLv95(minE, minN);
        var hi = TileId.FromLv95(maxE, maxN);
        int placed = 0;
        for (int te = lo.E; te <= hi.E; te++)
            for (int tn = lo.N; tn <= hi.N; tn++)
            {
                var tile = new TileId(te, tn);
                if (!region.Contains(tile)) continue;
                var local = new List<float[]>(rings.Count);
                foreach (var ring in rings)
                {
                    var f = new float[ring.Length];
                    for (int k = 0; k < ring.Length; k += 2)
                    {
                        f[k] = (float)(ring[k] - tile.MinE);
                        f[k + 1] = (float)(ring[k + 1] - tile.MinN);
                    }
                    local.Add(f);
                }
                var list = Tiles.GetOrAdd(tile, _ => []);
                lock (list) list.Add(new FieldPolygon(id, crop, source, lnf, local));
                placed++;
            }
        return placed;
    }
}
