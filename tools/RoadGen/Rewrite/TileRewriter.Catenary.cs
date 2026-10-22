namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

public static partial class TileRewriter
{
    /// <summary>
    /// Whether a point lies on a separated bike path (#1052, the user: an overhead line's mast never in the middle of one), from the
    /// block's written street pieces: each side's path band out from the carriageway edge past its verge (<see cref="RoadSide"/>,
    /// as <c>RoadStreetSection</c> lays it), with <paramref name="margin"/> either side. Edges in 10 m cells.
    /// </summary>
    private static Func<Vec2, bool> BikePaths(Dictionary<TileId, List<RoadSegment>> output, Dictionary<RoadSegment, List<RoadSegment>> streetPieces,
        double margin)
    {
        const double Cell = 10;
        var cells = new Dictionary<(long, long), List<(Vec2 A, Vec2 B, double From, double To, bool Right)>>();
        static long Key(double v) => (long)Math.Floor(v / Cell);
        foreach (var (tile, list) in output)
            foreach (var written in list)
                foreach (var seg in streetPieces.TryGetValue(written, out var pieces) ? pieces : [written])
                {
                    var a = seg.Attributes;
                    if (!a.Left.HasTrack && !a.Right.HasTrack) continue;
                    for (int i = 1; i < seg.PointCount; i++)
                    {
                        var p = new Vec2(tile.MinE + seg.Points[(i - 1) * 3], tile.MaxN - seg.Points[(i - 1) * 3 + 2]);
                        var q = new Vec2(tile.MinE + seg.Points[i * 3], tile.MaxN - seg.Points[i * 3 + 2]);
                        foreach (var (side, right) in new[] { (a.Left, false), (a.Right, true) })
                        {
                            if (!side.HasTrack) continue;
                            double from = seg.Width * 0.5 + side.ShiftAt(0.5) + side.VergeDm / 10.0, to = from + side.BikeDm / 10.0;
                            double reach = to + margin;
                            for (long x = Key(Math.Min(p.X, q.X) - reach); x <= Key(Math.Max(p.X, q.X) + reach); x++)
                                for (long y = Key(Math.Min(p.Y, q.Y) - reach); y <= Key(Math.Max(p.Y, q.Y) + reach); y++)
                                {
                                    if (!cells.TryGetValue((x, y), out var l)) cells[(x, y)] = l = new();
                                    l.Add((p, q, from - margin, to + margin, right));
                                }
                        }
                    }
                }
        return at =>
        {
            if (!cells.TryGetValue((Key(at.X), Key(at.Y)), out var l)) return false;
            foreach (var (a, b, from, to, right) in l)
            {
                var e = b - a;
                double len2 = e.Dot(e);
                if (len2 < 1e-9) continue;
                double t = (at - a).Dot(e) / len2;
                if (t < 0 || t > 1) continue;
                // across the edge: left of its drawing direction positive
                double across = (e.X * (at - a).Y - e.Y * (at - a).X) / Math.Sqrt(len2);
                double d = right ? -across : across;
                if (d >= from && d <= to) return true;
            }
            return false;
        };
    }
}
