using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// A polyline in a source's coordinates cut where it crosses a leaf's edge (#1509, G2 B5), as
/// <c>FixtureChunkSource.SplitAtTiles</c> cuts at LV95 km lines: the crossing point ends one piece and starts the next,
/// so pieces in two leaves share an end (the lane graph links ends). A leaf's edges are not straight lines in a source,
/// so a crossing is found by bisection on the leaf a point lies in, to 10⁻¹² of the segment (a leaf is convex: a
/// straight piece leaves it once).
/// </summary>
public static class LeafSplit
{
    public static List<(CubeKey Leaf, List<(double A, double B, double Z)> Piece)> AtLeaves(
        ISourceProjection source, IReadOnlyList<(double A, double B, double Z)> line)
    {
        var pieces = new List<(CubeKey Leaf, List<(double A, double B, double Z)> Piece)>();
        if (line.Count == 0) return pieces;
        var leaf = Of(line[0]);
        pieces.Add((leaf, [line[0]]));
        for (int k = 1; k < line.Count; k++)
        {
            var (p, q) = (line[k - 1], line[k]);
            double from = 0;
            // walk the segment: each time the leaf changes before q, cut at the crossing
            while (Of(q) != leaf)
            {
                double lo = from, hi = 1;
                for (int it = 0; it < 60 && hi - lo > 1e-12; it++)
                {
                    double mid = (lo + hi) / 2;
                    if (Of(At(mid)) == leaf) lo = mid; else hi = mid;
                }
                var cut = At(hi);
                pieces[^1].Piece.Add(cut);
                leaf = Of(cut);
                pieces.Add((leaf, [cut]));
                from = hi;
            }
            pieces[^1].Piece.Add(q);

            (double A, double B, double Z) At(double t) => (p.A + (q.A - p.A) * t, p.B + (q.B - p.B) * t, p.Z + (q.Z - p.Z) * t);
        }
        // a cut exactly on a vertex leaves a one-point piece behind it
        pieces.RemoveAll(x => x.Piece.Count < 2);
        return pieces;

        CubeKey Of((double A, double B, double Z) point) => SourceOnLeaf.LeafOf(source, point.A, point.B);
    }
}
