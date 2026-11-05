using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Splits a map-space polyline at the 1 km tile grid so each piece belongs to exactly one
/// tile. Without this, a road loaded with one tile would disappear when that tile unloads
/// even though it visibly extends into a tile that is still on screen.
/// </summary>
public static class PolylineClipper
{
    public const double TileSize = ChunkFormat.TileSizeM;

    /// <summary>The LV95 instance of the road stage's tile loop (<see cref="Lv95RoadSpace"/>).</summary>
    public static List<RoadPiece<TileId>> SplitByTile(GeoPackageReader.Polyline line)
    {
        var pieces = new List<RoadPiece<TileId>>();
        if (line.Count < 2) return pieces;

        RoadPiece<TileId>? current = null;

        void Start(TileId tile, (double, double, double) p)
        {
            current = new RoadPiece<TileId>(tile, new List<(double, double, double)> { p });
            pieces.Add(current);
        }

        var first = (line.E[0], line.N[0], line.Z[0]);
        Start(TileOf(first.Item1, first.Item2), first);

        for (int i = 1; i < line.Count; i++)
        {
            var a = (E: line.E[i - 1], N: line.N[i - 1], Z: line.Z[i - 1]);
            var b = (E: line.E[i], N: line.N[i], Z: line.Z[i]);

            // walk crossings of the tile grid between a and b, in order along the segment
            foreach (double t in Crossings(a.E, a.N, b.E, b.N))
            {
                double ce = a.E + (b.E - a.E) * t;
                double cn = a.N + (b.N - a.N) * t;
                double cz = a.Z + (b.Z - a.Z) * t;

                current!.Points.Add((ce, cn, cz));
                // nudge past the boundary to decide which tile the next piece belongs to
                double eps = 1e-6;
                double ne = a.E + (b.E - a.E) * Math.Min(t + eps, 1.0);
                double nn = a.N + (b.N - a.N) * Math.Min(t + eps, 1.0);
                Start(TileOf(ne, nn), (ce, cn, cz));
            }

            current!.Points.Add((b.E, b.N, b.Z));
        }

        pieces.RemoveAll(p => p.Points.Count < 2);
        if (pieces.Count > 0)
        {
            pieces[0].AtLineStart = true;
            pieces[^1].AtLineEnd = true;
        }
        return pieces;
    }

    private static TileId TileOf(double e, double n) =>
        new((int)Math.Floor(e / TileSize), (int)Math.Floor(n / TileSize));

    /// <summary>Parameters t in (0,1) where the segment crosses a tile boundary, ascending.</summary>
    private static List<double> Crossings(double e0, double n0, double e1, double n1)
    {
        var ts = new List<double>();
        AddAxisCrossings(ts, e0, e1);
        AddAxisCrossings(ts, n0, n1);
        ts.Sort();
        // drop duplicates (exact corner hits) to avoid zero-length pieces
        var unique = new List<double>(ts.Count);
        foreach (double t in ts)
            if (unique.Count == 0 || t - unique[^1] > 1e-12)
                unique.Add(t);
        return unique;
    }

    private static void AddAxisCrossings(List<double> ts, double a, double b)
    {
        if (a == b) return;
        int ia = (int)Math.Floor(a / TileSize);
        int ib = (int)Math.Floor(b / TileSize);
        if (ia == ib) return;

        int step = b > a ? 1 : -1;
        for (int k = ia; k != ib; k += step)
        {
            double boundary = (step > 0 ? k + 1 : k) * TileSize;
            double t = (boundary - a) / (b - a);
            if (t > 0 && t < 1) ts.Add(t);
        }
    }
}
