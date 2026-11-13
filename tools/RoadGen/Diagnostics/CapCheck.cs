namespace UnitSport.Tools.RoadGen.Diagnostics;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>
/// No junction cap swallows a street (#1821): <c>RoadGen --cap-check --chunks DIR [--at E,N] [--list]</c>. Reads the built
/// tiles; a ground cap with a point of an at-grade car road or track more than <see cref="Deep"/> inside its outline paves
/// over a street that should stop at its edge (its arms are trimmed to the mouths). Geneva's quay (2501018,1117887) had a
/// merged core's fallback paving the verge between a road and a lane side by side for 70 m, a track 8.4 m inside it; seen
/// from above it read as a pit. Paths and bike paths are carried over cores on purpose and are not counted.
/// </summary>
public static class CapCheck
{
    /// <summary>
    /// How far inside a cap's outline a street's point may lie (m): the planner's own residues (a stub's end under the paving,
    /// one node's mouth cut away) reach 7.1 m in Geneva's 2499-2503 / 1116-1119 block; the swallowed streets lay 8.4 m and deeper.
    /// </summary>
    public const double Deep = 8;

    public static int Run(string chunks, string? at, bool list, Action<string> log)
    {
        var inv = CultureInfo.InvariantCulture;
        TileId? only = null;
        if (at is not null)
        {
            var en = at.Split(',');
            only = TileId.FromLv95(double.Parse(en[0], inv), double.Parse(en[1], inv));
        }
        int tiles = 0, caps = 0, bad = 0;
        foreach (var path in Directory.EnumerateFiles(chunks, "roads_*.road").Order(StringComparer.Ordinal))
        {
            RoadTile tile;
            using (var s = File.OpenRead(path)) tile = RoadCodec.Decode(s);
            if (only is { } o && tile.Id != o) continue;
            tiles++;
            foreach (var (cap, depth, cls, e, n) in Swallowed(tile))
            {
                bad++;
                if (list || bad <= 20)
                    log(string.Create(inv, $"  {tile.Id} cap {cap} at {e:F1},{n:F1}: a {cls} point {depth:F1} m inside"));
            }
            caps += tile.Junctions.Count(j => j.Layer == 0);
        }
        bool ok = tiles > 0 && bad == 0;
        log(string.Create(inv, $"RESULT {(ok ? "ok" : "FAILED")}: {tiles} tiles, {caps} ground caps, {bad} swallowing a street (a car road's or track's point over {Deep} m inside)"));
        return ok ? 0 : 1;
    }

    /// <summary>Each ground cap of the tile with a street point deeper than <see cref="Deep"/> inside it: the deepest, in LV95.</summary>
    public static IEnumerable<(int Cap, double Depth, RoadClass Class, double E, double N)> Swallowed(RoadTile tile)
    {
        for (int j = 0; j < tile.Junctions.Count; j++)
        {
            var cap = tile.Junctions[j];
            if (cap.Layer != 0 || cap.VertexCount < 3) continue;
            var v = cap.Vertices;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < cap.VertexCount; i++)
            {
                minX = Math.Min(minX, v[i * 3]); maxX = Math.Max(maxX, v[i * 3]);
                minZ = Math.Min(minZ, v[i * 3 + 2]); maxZ = Math.Max(maxZ, v[i * 3 + 2]);
            }
            double worst = 0, wx = 0, wz = 0;
            var wc = RoadClass.Road;
            foreach (var s in tile.Segments)
            {
                if ((s.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) continue;
                if (s.Class is not (RoadClass.Major or RoadClass.Road or RoadClass.Minor or RoadClass.Lane or RoadClass.Track)) continue;
                for (int i = 0; i < s.PointCount; i++)
                {
                    float x = s.Points[i * 3], z = s.Points[i * 3 + 2];
                    if (x < minX || x > maxX || z < minZ || z > maxZ) continue;
                    double d = Inside(v, cap.VertexCount, x, z);
                    if (d > worst) (worst, wx, wz, wc) = (d, x, z, s.Class);
                }
            }
            if (worst > Deep) yield return (j, worst, wc, tile.Id.MinE + wx, tile.Id.MaxN - wz);
        }
    }

    /// <summary>How far inside the ring (x, z of each vertex triple) the point lies; 0 outside.</summary>
    private static double Inside(float[] v, int n, double px, double pz)
    {
        bool inside = false;
        double near = double.MaxValue;
        for (int k = 0, l = n - 1; k < n; l = k++)
        {
            double ax = v[k * 3], az = v[k * 3 + 2], bx = v[l * 3], bz = v[l * 3 + 2];
            if ((az > pz) != (bz > pz) && px < (bx - ax) * (pz - az) / (bz - az) + ax) inside = !inside;
            double dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            double t = l2 < 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (pz - az) * dz) / l2, 0, 1);
            near = Math.Min(near, Math.Sqrt((ax + dx * t - px) * (ax + dx * t - px) + (az + dz * t - pz) * (az + dz * t - pz)));
        }
        return inside ? near : 0;
    }
}
