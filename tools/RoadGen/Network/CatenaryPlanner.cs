namespace UnitSport.Tools.RoadGen.Network;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// Overhead lines over electrified rail (#1052, <c>docs/plans/catenary.md</c>): supports along every written rail piece and a
/// contact wire over each of its tracks. Supports stand at along-line metres on a grid of the base spacing (a tram's 30 m, a main
/// line's 60 m) halved as often as the local curve needs (the wire runs straight between supports: its chord's L²/8R stays within
/// ±0.30 m of the track, L ≤ √(4.8·R)), so neighbouring tiles agree on them. On a street (track in the carriageway or in town
/// paving) a span across it, anchored on the first facade within <see cref="SearchReach"/> each side, else on a mast at the first
/// spot off every carriageway (the user's rule); elsewhere a cantilever mast <see cref="MastOut"/> out from each track, on the
/// side away from a neighbouring track. Pure: buildings and carriageways come in as predicates.
/// </summary>
public static class CatenaryPlanner
{
    /// <summary>Base spacing (m): Bern's tram spans at most 32 m; main lines about twice that.</summary>
    public const double TramSpacing = 30, MainSpacing = 60;
    /// <summary>The chord's allowance from the track, both ways of the zigzag (m): L ≤ √(8·R·this).</summary>
    public const double ChordAllowance = 0.6;
    private const int Halvings = 3;
    /// <summary>Contact wire over the rail: tram 5.80 m (Bern), main line 5.50 m (BLS: 5.30-5.75 m); a ballasted rail's top over its line.</summary>
    public const float TramWire = 5.8f, MainWire = 5.5f, RailTop = 0.18f, GrooveTop = 0.02f;
    /// <summary>A cantilever mast stands this far out from its track; a span's anchors are looked for this far out, in steps; a mast keeps this clear of any track.</summary>
    public const double MastOut = 3.0, SearchReach = 15, SearchStep = 0.25, TrackClear = 2.0;
    /// <summary>A contact wire's points stand this far apart along its track (m).</summary>
    public const double WireStep = 4.0;

    /// <summary>One written rail piece: its plan (LV95) and heights, flags, whether it lies in a street, its along-line metre at its start, its tile.</summary>
    public sealed record Rail(IReadOnlyList<Vec2> Plan, float[] Height, RoadFlags Flags, bool Street, bool Embedded, double FromM, TileId Tile);

    public sealed class Stats
    {
        public int Pieces, Cantilevers, Spans, SpansShared, WallEnds, MastEnds, Skipped;
        public double WireKm, Ms;

        public string Format() => string.Create(CultureInfo.InvariantCulture,
            $"    overhead lines (#1052): {Pieces:N0} electrified rail pieces, {WireKm:F2} km of contact wire, {Cantilevers:N0} cantilever masts, {Spans:N0} spans ({WallEnds:N0} ends on facades, {MastEnds:N0} on masts; {SpansShared:N0} times a track's wire hung from another track's span), {Skipped:N0} supports with no room; planning {Ms:N0} ms");
    }

    /// <summary>Electrified: rail at grade or on a bridge, neither a funicular nor disused (TLM has no electrification; nearly all Swiss rail is).</summary>
    public static bool Electrified(RoadSegment s) =>
        s.Class == RoadClass.Railway && (s.Flags & (RoadFlags.Funicular | RoadFlags.Disused | RoadFlags.Tunnel)) == 0;

    /// <summary>The spacing a support needs at a curve of radius <paramref name="radius"/> (m), halved from <paramref name="base"/> as often as needed.</summary>
    public static double SpacingAt(double @base, double radius)
    {
        double step = @base, most = Math.Sqrt(8 * radius * ChordAllowance);
        for (int k = 0; k < Halvings && step > most; k++) step *= 0.5;
        return step;
    }

    /// <param name="tracks">Every rail line of the block (halo included), to keep masts off them.</param>
    /// <param name="building">A point inside a building.</param>
    /// <param name="clear">A point off every carriageway.</param>
    public static void Plan(IReadOnlyList<Rail> rails, IReadOnlyList<IReadOnlyList<Vec2>> tracks, Func<Vec2, bool> building, Func<Vec2, bool> clear,
        Dictionary<TileId, List<CatenarySupport>> supports, Dictionary<TileId, List<CatenaryWire>> wires, Stats stats)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var near = new TrackGrid(tracks);
        // the spans placed so far: a track mapped as a line of its own beside another (Geneva) hangs its wire from theirs
        var spans = new List<(Vec2 A, Vec2 B, CatenarySupport Support, TileId Tile)>();
        foreach (var rail in rails)
        {
            if (rail.Plan.Count < 2) continue;
            stats.Pieces++;
            bool tram = (rail.Flags & RoadFlags.Tramway) != 0;
            var line = tram ? CatenaryLine.Tram : CatenaryLine.Main;
            double @base = tram ? TramSpacing : MainSpacing;
            float wire = (tram ? TramWire : MainWire) + (rail.Embedded ? GrooveTop : RailTop);
            float o = RoadFormat.TrackOffset(rail.Flags);
            float[] offsets = o > 0 ? [-o, o] : [0f];
            var arc = Polyline.ArcLengths(rail.Plan);
            double total = arc[^1];
            var tile = rail.Tile;

            // a contact wire over each track, following it, a point every WireStep metres (the lines are densified to centimetres)
            int samples = Math.Max(1, (int)Math.Ceiling(total / WireStep));
            foreach (float off in offsets)
            {
                var pts = new float[(samples + 1) * 3];
                for (int i = 0; i <= samples; i++)
                {
                    var (at, dir, height) = At(rail.Plan, arc, rail.Height, total * i / samples);
                    var p = at + new Vec2(dir.Y, -dir.X) * off;
                    pts[i * 3] = (float)(p.X - tile.MinE);
                    pts[i * 3 + 1] = height + wire;
                    pts[i * 3 + 2] = (float)(tile.MaxN - p.Y);
                }
                Get(wires, tile).Add(new CatenaryWire { Line = line, Points = pts });
                stats.WireKm += total / 1000;
            }

            // supports on the grid, each in exactly one piece (half-open)
            double fine = @base / (1 << Halvings);
            long first = (long)Math.Ceiling(rail.FromM / fine), last = (long)Math.Ceiling((rail.FromM + total) / fine) - 1;
            for (long m = first; m <= last; m++)
            {
                double u = m * fine - rail.FromM;
                if (u < 0 || u >= total) continue;
                double step = SpacingAt(@base, Radius(rail.Plan, arc, u, Math.Min(@base * 0.5, 15)));
                long every = Math.Max(1, (long)Math.Round(step / fine));
                if (m % every != 0) continue;
                var (at, dir, height) = At(rail.Plan, arc, rail.Height, u);
                var n = new Vec2(dir.Y, -dir.X);   // right of the line's drawing direction
                float y = height, wireY = height + wire;
                if (rail.Street) PlaceSpan(at, n, offsets, y, wireY, line, tile, step * 0.5, spans, building, clear, supports, stats);
                else
                {
                    bool placed = false;
                    foreach (float off in offsets)
                    {
                        // a double track's masts stand outside it; a single track's on the right unless a track is near there
                        double[] sides = off > 0 ? [1] : off < 0 ? [-1] : [1, -1];
                        foreach (double side in sides)
                        {
                            var mast = at + n * (off + side * MastOut);
                            if (!clear(mast) || building(mast) || near.Within(mast, TrackClear)) continue;
                            var over = at + n * off;
                            Get(supports, tile).Add(new CatenarySupport
                            {
                                Kind = CatenarySupportKind.Cantilever, Line = line,
                                AX = (float)(mast.X - tile.MinE), AY = y, AZ = (float)(tile.MaxN - mast.Y),
                                BX = (float)(over.X - tile.MinE), BY = y, BZ = (float)(tile.MaxN - over.Y),
                                WireY = wireY, Over = [(float)(over.X - tile.MinE), (float)(tile.MaxN - over.Y)],
                            });
                            stats.Cantilevers++;
                            placed = true;
                            break;
                        }
                    }
                    if (!placed) stats.Skipped++;
                }
            }
        }
        stats.Ms += clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// A span across a street at <paramref name="at"/>: to the first facade within reach each side, else to the first spot off the
    /// road; or, where a span already crosses this track within <paramref name="share"/> metres along it (another track of the
    /// street, mapped as a line of its own), this track's wire hung from that span instead.
    /// </summary>
    private static void PlaceSpan(Vec2 at, Vec2 n, float[] offsets, float y, float wireY, CatenaryLine line, TileId tile, double share,
        List<(Vec2 A, Vec2 B, CatenarySupport Support, TileId Tile)> spans,
        Func<Vec2, bool> building, Func<Vec2, bool> clear, Dictionary<TileId, List<CatenarySupport>> supports, Stats stats)
    {
        var dir = new Vec2(-n.Y, n.X);
        foreach (var (sa, sb, support, home) in spans)
        {
            // where the span crosses this track's line: at + dir t = sa + (sb - sa) w
            var e = sb - sa;
            double det = dir.X * -e.Y - dir.Y * -e.X;
            if (Math.Abs(det) < 1e-6) continue;
            var d = sa - at;
            double t = (d.X * -e.Y - d.Y * -e.X) / det, w = (dir.X * d.Y - dir.Y * d.X) / det;
            if (Math.Abs(t) > share || w < 0 || w > 1) continue;
            var cross = at + dir * t;
            var hung = support.Over.ToList();
            foreach (float off in offsets)
            {
                var q = cross + n * off;
                float qx = (float)(q.X - home.MinE), qz = (float)(home.MaxN - q.Y);
                bool known = false;
                for (int i = 0; i + 1 < hung.Count; i += 2)
                    if ((hung[i] - qx) * (hung[i] - qx) + (hung[i + 1] - qz) * (hung[i + 1] - qz) < 1f) known = true;
                if (!known) { hung.Add(qx); hung.Add(qz); }
            }
            support.Over = [.. hung];
            stats.SpansShared++;
            return;
        }
        double outer = offsets.Max(Math.Abs);
        (Vec2 P, bool Wall)? End(double side)
        {
            for (double d = outer + 1.0; d <= SearchReach; d += SearchStep)
                if (building(at + n * (side * d))) return (at + n * (side * (d - SearchStep)), true);
            for (double d = outer + 2.5; d <= SearchReach; d += SearchStep)
                if (clear(at + n * (side * d))) return (at + n * (side * (d + 0.3)), false);
            return null;
        }
        if (End(1) is not var (a, aWall) || End(-1) is not var (b, bWall)) { stats.Skipped++; return; }
        var over = new float[offsets.Length * 2];
        for (int i = 0; i < offsets.Length; i++)
        {
            var p = at + n * offsets[i];
            over[i * 2] = (float)(p.X - tile.MinE);
            over[i * 2 + 1] = (float)(tile.MaxN - p.Y);
        }
        var span = new CatenarySupport
        {
            Kind = CatenarySupportKind.Span, Line = line,
            AX = (float)(a.X - tile.MinE), AY = y, AZ = (float)(tile.MaxN - a.Y), AWall = aWall,
            BX = (float)(b.X - tile.MinE), BY = y, BZ = (float)(tile.MaxN - b.Y), BWall = bWall,
            WireY = wireY, Over = over,
        };
        Get(supports, tile).Add(span);
        spans.Add((a, b, span, tile));
        stats.Spans++;
        stats.WallEnds += (aWall ? 1 : 0) + (bWall ? 1 : 0);
        stats.MastEnds += (aWall ? 0 : 1) + (bWall ? 0 : 1);
    }

    /// <summary>Rail lines' edges in 10 m cells: whether a point lies within a distance of any (the lines are densified to centimetres).</summary>
    private sealed class TrackGrid
    {
        private const double Cell = 10;
        private readonly Dictionary<(long, long), List<(Vec2 A, Vec2 B)>> _cells = new();

        public TrackGrid(IReadOnlyList<IReadOnlyList<Vec2>> tracks)
        {
            foreach (var t in tracks)
                for (int i = 1; i < t.Count; i++)
                {
                    var (a, b) = (t[i - 1], t[i]);
                    for (long x = Key(Math.Min(a.X, b.X)); x <= Key(Math.Max(a.X, b.X)); x++)
                        for (long y = Key(Math.Min(a.Y, b.Y)); y <= Key(Math.Max(a.Y, b.Y)); y++)
                        {
                            if (!_cells.TryGetValue((x, y), out var l)) _cells[(x, y)] = l = new();
                            l.Add((a, b));
                        }
                }
        }

        private static long Key(double v) => (long)Math.Floor(v / Cell);

        public bool Within(Vec2 p, double gap)
        {
            for (long x = Key(p.X - gap); x <= Key(p.X + gap); x++)
                for (long y = Key(p.Y - gap); y <= Key(p.Y + gap); y++)
                    if (_cells.TryGetValue((x, y), out var l))
                        foreach (var (a, b) in l)
                            if (Junctions.PriorityPlanner.DistanceToSegment(p, a, b) < gap) return true;
            return false;
        }
    }

    /// <summary>The point, unit direction and height <paramref name="u"/> metres along the line.</summary>
    private static (Vec2 At, Vec2 Dir, float Height) At(IReadOnlyList<Vec2> plan, double[] arc, float[] height, double u)
    {
        int i = 1;
        while (i < plan.Count - 1 && arc[i] < u) i++;
        double len = arc[i] - arc[i - 1], t = len < 1e-9 ? 0 : Math.Clamp((u - arc[i - 1]) / len, 0, 1);
        var d = plan[i] - plan[i - 1];
        double l = d.Length;
        return (plan[i - 1] + d * t, l < 1e-9 ? new Vec2(1, 0) : d * (1 / l), (float)(height[i - 1] + (height[i] - height[i - 1]) * t));
    }

    /// <summary>The radius of the line around <paramref name="u"/>, from the circle through the points <paramref name="w"/> metres either side (clamped to the piece); infinite where straight.</summary>
    public static double Radius(IReadOnlyList<Vec2> plan, double[] arc, double u, double w)
    {
        double total = arc[^1];
        var p0 = At(plan, arc, new float[plan.Count], Math.Max(0, u - w)).At;
        var p1 = At(plan, arc, new float[plan.Count], u).At;
        var p2 = At(plan, arc, new float[plan.Count], Math.Min(total, u + w)).At;
        double a = p0.DistanceTo(p1), b = p1.DistanceTo(p2), c = p0.DistanceTo(p2);
        double cross = Math.Abs((p1 - p0).X * (p2 - p0).Y - (p1 - p0).Y * (p2 - p0).X);
        return cross < 1e-6 ? double.PositiveInfinity : a * b * c / (2 * cross);
    }

    private static List<T> Get<T>(Dictionary<TileId, List<T>> d, TileId id)
    {
        if (!d.TryGetValue(id, out var l)) d[id] = l = new List<T>();
        return l;
    }
}
