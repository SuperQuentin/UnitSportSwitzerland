using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

namespace UnitSport.Tools.RoadGen.Rewrite;

/// <summary>
/// Car parks (#499): the network-stage half. Reads the <see cref="RawParking"/> rings of a block,
/// hands each to <see cref="ParkingPlanner"/> with the block's own roads, walls and ground, and
/// turns what comes back into tile records — <c>APRP</c> pad, islands and walks, <c>PNT2</c> bay
/// lines and arrows, <c>PPRP</c> boom, kiosk, shelter and sign, and the <c>PARK</c> bay list.
/// Rules and values: docs/notes/tools/parking-lots.md.
/// </summary>
public static partial class TileRewriter
{
    public sealed class ParkingStats
    {
        public int Lots, Planned, Bays, Pads, Islands, Walks, Fixtures, Marks;
        public int RejectedSmall, RejectedGround, RejectedOther;
        public int AxisFromRoad, Paid, SeamSplit;

        public string Format() =>
            $"  car parks: {Planned}/{Lots} laid out, {Bays:N0} bays ({Paid} paid, {AxisFromRoad} square to a road), "
            + $"{Pads} pads, {Islands} islands, {Walks} walks, {Fixtures} fixtures, {Marks:N0} markings; "
            + $"rejected: small {RejectedSmall}, ground {RejectedGround}, other {RejectedOther}"
            + (SeamSplit > 0 ? $"; {SeamSplit} pieces split at a tile seam" : "");
    }

    /// <summary>Clearance kept off a carriageway edge, as <c>CoverStage</c> keeps off a corridor.</summary>
    private const double ParkingRoadClearance = 2.0;

    /// <summary>A quad is subdivided until its corners share a tile or it is this short, so a piece never overhangs far.</summary>
    private const double ParkingPieceMin = 4.0;

    /// <summary>
    /// Plans every car park whose home tile is in <paramref name="block"/> and files the pieces by
    /// the tile each falls in. A lot is planned once, from its whole polygon, even where it crosses
    /// a seam — which is the reason the layout is baked here rather than built per tile at runtime.
    /// </summary>
    internal static void PlanParking(
        Func<TileId, List<RawParking.Lot>>? lotsOf,
        IReadOnlyCollection<TileId> block,
        HashSet<TileId> wanted,
        Dictionary<TileId, List<RoadSegment>> roads,
        Facades facades,
        Dictionary<TileId, ChunkGrid>? grids,
        Dictionary<TileId, List<RoadAreaProp>> areas,
        Dictionary<TileId, List<RoadPaint>> paint,
        Dictionary<TileId, List<RoadPointProp>> points,
        Dictionary<TileId, List<ParkingBay>> bays,
        ParkingStats stats)
    {
        if (grids is null || lotsOf is null) return;   // no .terr: nothing to stand a lot on; no lots

        // every segment of the block and its halo once, for the borders and the corridor test
        var allRoads = roads.SelectMany(kv => kv.Value.Select(s => (Tile: kv.Key, Segment: s))).ToList();

        foreach (var home in block)
        {
            foreach (var lot in lotsOf(home))
            {
                stats.Lots++;
                var ring = lot.Points();
                var ground = new ParkingGround(grids, facades, allRoads, ring);
                var borders = Borders(allRoads, ring);
                var frontage = Frontage(facades, grids, ring);

                var plan = ParkingPlanner.Plan(ring, borders, ground, frontage);
                if (plan.Rejected is { } why)
                {
                    if (why.Contains("m2") || why.Contains("bays fit") || why.Contains("too small")) stats.RejectedSmall++;
                    else if (why.Contains("ground")) stats.RejectedGround++;
                    else stats.RejectedOther++;
                    continue;
                }

                stats.Planned++;
                if (plan.AxisFromRoad) stats.AxisFromRoad++;
                if (plan.Paid) stats.Paid++;
                Emit(plan, wanted, areas, paint, points, bays, stats);
            }
        }
    }

    // ---- what the planner is told ------------------------------------------------------------

    /// <summary>
    /// The roads running past a lot: the nearest point of each carriageway within reach, one entry
    /// per segment, so the planner can pick the most important one for the entrance.
    /// </summary>
    private static List<ParkingPlanner.Border> Borders(
        List<(TileId Tile, RoadSegment Segment)> roads, List<(double E, double N)> ring)
    {
        const double reach = 40.0;
        var (cE, cN) = (ring.Average(p => p.E), ring.Average(p => p.N));
        var borders = new List<ParkingPlanner.Border>();

        foreach (var (tile, seg) in roads)
        {
            if (seg.PointCount < 2) continue;
            if (!PriorityPlanner.IsCarRoad(seg.Class)) continue;
            if ((seg.Flags & (RoadFlags.Tunnel | RoadFlags.Bridge)) != 0) continue;

            double best = double.MaxValue;
            double bE = 0, bN = 0, heading = 0;
            for (int i = 0; i < seg.PointCount - 1; i++)
            {
                var (e0, n0) = seg.Lv95(tile, i);
                var (e1, n1) = seg.Lv95(tile, i + 1);
                double dx = e1 - e0, dy = n1 - n0;
                double len2 = dx * dx + dy * dy;
                double t = len2 < 1e-9 ? 0 : Math.Clamp(((cE - e0) * dx + (cN - n0) * dy) / len2, 0, 1);
                double pe = e0 + dx * t, pn = n0 + dy * t;
                double d = (pe - cE) * (pe - cE) + (pn - cN) * (pn - cN);
                if (d < best) { best = d; bE = pe; bN = pn; heading = Math.Atan2(dy, dx); }
            }
            if (best > reach * reach) continue;
            borders.Add(new ParkingPlanner.Border(bE, bN, heading,
                seg.Attributes.Priority >> 4, seg.Width * 0.5, seg.Attributes.OneWay != 0));
        }
        return borders;
    }

    /// <summary>
    /// The building the lot serves, when one stands within 50 m of it: the nearest wall point stands
    /// in for the door (the <c>.bldg</c> walls are all the stage has), and the footprint's coarse
    /// extent for its size. A big retail shed is what earns a trolley shelter.
    /// </summary>
    private static ParkingPlanner.Frontage? Frontage(
        Facades facades, Dictionary<TileId, ChunkGrid>? grids, List<(double E, double N)> ring)
    {
        var (cE, cN) = (ring.Average(p => p.E), ring.Average(p => p.N));
        double bestD = double.MaxValue, dE = 0, dN = 0;

        // a coarse ring out from the centroid: the planner only needs a point to aim the walk at
        for (double r = 10; r <= 50; r += 2.5)
            for (int a = 0; a < 48; a++)
            {
                double th = a * Math.Tau / 48;
                double e = cE + Math.Cos(th) * r, n = cN + Math.Sin(th) * r;
                if (!facades.Occupied(e, n)) continue;
                double d = r;
                if (d < bestD) { bestD = d; dE = e; dN = n; }
            }
        if (bestD == double.MaxValue) return null;

        // how much wall stands within 60 m of that point, as a stand-in for floor area: a shed has
        // long unbroken walls, a house a few metres of them
        int hits = 0;
        for (double e = dE - 60; e <= dE + 60; e += 2.0)
            for (double n = dN - 60; n <= dN + 60; n += 2.0)
                if (facades.Occupied(e, n)) hits++;
        double areaish = hits * 4.0;

        return new ParkingPlanner.Frontage(dE, dN, areaish, Retail: areaish >= 600);
    }

    /// <summary>The ground and the obstacles a lot is laid out over: the block's terrain, its walls and its roads.</summary>
    private sealed class ParkingGround : IParkingGround
    {
        private readonly Dictionary<TileId, ChunkGrid> _grids;
        private readonly Facades _facades;
        private readonly List<(TileId Tile, RoadSegment Segment)> _near;

        /// <summary>Only the segments whose extent comes near this lot: a corridor test per sample point otherwise walks the block.</summary>
        public ParkingGround(Dictionary<TileId, ChunkGrid> grids, Facades facades,
            List<(TileId Tile, RoadSegment Segment)> roads, List<(double E, double N)> ring)
        {
            _grids = grids;
            _facades = facades;
            double minE = ring.Min(p => p.E) - 40, maxE = ring.Max(p => p.E) + 40;
            double minN = ring.Min(p => p.N) - 40, maxN = ring.Max(p => p.N) + 40;

            _near = new List<(TileId, RoadSegment)>();
            foreach (var (tile, seg) in roads)
            {
                for (int i = 0; i < seg.PointCount; i++)
                {
                    var (e, n) = seg.Lv95(tile, i);
                    if (e < minE || e > maxE || n < minN || n > maxN) continue;
                    _near.Add((tile, seg));
                    break;
                }
            }
        }

        public double Ground(double e, double n) => SampleGround(_grids, e, n);

        public bool Occupied(double e, double n)
        {
            if (_facades.Occupied(e, n)) return true;

            foreach (var (tile, seg) in _near)
            {
                double radius = seg.Width * 0.5 + ParkingRoadClearance
                    + Math.Max(seg.Attributes.Left.Reach, seg.Attributes.Right.Reach);
                double r2 = radius * radius;
                for (int i = 0; i < seg.PointCount - 1; i++)
                {
                    var (e0, n0) = seg.Lv95(tile, i);
                    var (e1, n1) = seg.Lv95(tile, i + 1);
                    double dx = e1 - e0, dy = n1 - n0;
                    double len2 = dx * dx + dy * dy;
                    double t = len2 < 1e-9 ? 0 : Math.Clamp(((e - e0) * dx + (n - n0) * dy) / len2, 0, 1);
                    double px = e0 + dx * t - e, py = n0 + dy * t - n;
                    if (px * px + py * py <= r2) return true;
                }
            }
            return false;
        }
    }

    // ---- what comes back, as tile records ----------------------------------------------------

    private static void Emit(
        ParkingPlanner.Lot plan, HashSet<TileId> wanted,
        Dictionary<TileId, List<RoadAreaProp>> areas,
        Dictionary<TileId, List<RoadPaint>> paint,
        Dictionary<TileId, List<RoadPointProp>> points,
        Dictionary<TileId, List<ParkingBay>> bays,
        ParkingStats stats)
    {
        foreach (var area in plan.Areas)
        {
            var type = area.Kind switch
            {
                ParkingPlanner.AreaKind.Island => AreaPropType.ParkingIsland,
                ParkingPlanner.AreaKind.Walk => AreaPropType.Sidewalk,
                _ => AreaPropType.ParkingPad,
            };
            // kerbed surfaces get collision like a #119 sidewalk slab; the pad is flush and is
            // carried by the heightfield instead, as a #123 turn-lane strip is
            var flags = area.Height > 0 ? PropFlags.Solid : PropFlags.None;
            int before = Counted(areas);
            EmitQuad(area.Ring, area.Y, area.Height, type, flags, wanted, areas, stats);
            int added = Counted(areas) - before;
            if (area.Kind == ParkingPlanner.AreaKind.Island) stats.Islands += added;
            else if (area.Kind == ParkingPlanner.AreaKind.Walk) stats.Walks += added;
            else stats.Pads += added;
        }

        foreach (var mark in plan.Marks)
        {
            if (mark.Type == PaintType.Arrow)
            {
                var at = TileId.FromLv95(mark.Line[0], mark.Line[1]);
                if (!wanted.Contains(at)) continue;
                Get(paint, at).Add(PaintEmitter.Arrow(
                    mark.Line[0] - at.MinE, mark.Y, at.MaxN - mark.Line[1],
                    Math.Cos(mark.HeadingRad), -Math.Sin(mark.HeadingRad), PaintArrow.Straight, tipY: mark.Y));   // an aisle is level
                stats.Marks++;
                continue;
            }
            if (mark.Type == PaintType.DisabledBay)
            {
                var at = TileId.FromLv95(mark.Line[0], mark.Line[1]);
                if (!wanted.Contains(at)) continue;
                Get(paint, at).Add(PaintEmitter.DisabledBay(
                    mark.Line[0] - at.MinE, mark.Y, at.MaxN - mark.Line[1], mark.HeadingRad, mark.Width));
                stats.Marks++;
                continue;
            }

            // a line: the tile holding its middle takes it whole (a bay divider is 5 m long)
            double mE = (mark.Line[0] + mark.Line[^2]) * 0.5, mN = (mark.Line[1] + mark.Line[^1]) * 0.5;
            var tile = TileId.FromLv95(mE, mN);
            if (!wanted.Contains(tile)) continue;
            var v = new float[mark.Line.Length / 2 * 3];
            for (int i = 0; i < mark.Line.Length / 2; i++)
            {
                v[i * 3] = (float)(mark.Line[i * 2] - tile.MinE);
                v[i * 3 + 1] = (float)mark.Y;
                v[i * 3 + 2] = (float)(tile.MaxN - mark.Line[i * 2 + 1]);
            }
            Get(paint, tile).Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = mark.Type, Rgba = PaintEmitter.White,
                Width = (float)mark.Width, Vertices = v, Indices = [],
            });
            stats.Marks++;
        }

        foreach (var f in plan.Fixtures)
        {
            // the island trees are planted by CoverStage out of the finished APRP islands, so they
            // get the tree LOD, thinning and pooled collision instead of being props
            if (f.Kind == ParkingPlanner.FixtureKind.Tree) continue;

            var at = TileId.FromLv95(f.E, f.N);
            if (!wanted.Contains(at)) continue;
            var (type, height) = f.Kind switch
            {
                ParkingPlanner.FixtureKind.Barrier => (PointPropType.TicketBarrier, 1.1f),
                ParkingPlanner.FixtureKind.Kiosk => (PointPropType.TicketKiosk, 1.5f),
                ParkingPlanner.FixtureKind.Shelter => (PointPropType.CartShelter, 2.4f),
                // the blue P on its pole: the plate's lower edge plus the plate itself
                _ => (PointPropType.ParkingSign,
                    RoadSigns.LowerEdge + RoadSigns.PlateHeight(PointPropType.ParkingSign, RoadSigns.Normal)),
            };
            Get(points, at).Add(new RoadPointProp(type, f.Variant, PropFlags.Solid,
                (float)(f.E - at.MinE), (float)f.Y, (float)(at.MaxN - f.N),
                (float)ParkingPlanner.ToGodotHeading(f.HeadingRad), height));
            stats.Fixtures++;
        }

        foreach (var b in plan.Bays)
        {
            var at = TileId.FromLv95(b.E, b.N);
            if (!wanted.Contains(at)) continue;
            Get(bays, at).Add(new ParkingBay(
                (float)(b.E - at.MinE), (float)b.Y, (float)(at.MaxN - b.N),
                (float)ParkingPlanner.ToGodotHeading(b.HeadingRad), b.Kind, b.Flags));
            stats.Bays++;
        }
    }

    private static int Counted(Dictionary<TileId, List<RoadAreaProp>> areas) => areas.Sum(kv => kv.Value.Count);

    /// <summary>
    /// One LV95 quad as an area prop, subdivided until its corners share a tile (or it is under
    /// <see cref="ParkingPieceMin"/>), so a run that crosses a seam is split instead of overhanging
    /// its tile by half its length.
    /// </summary>
    private static void EmitQuad(
        double[] ring, double y, double height, AreaPropType type, PropFlags flags,
        HashSet<TileId> wanted, Dictionary<TileId, List<RoadAreaProp>> areas, ParkingStats stats, int depth = 0)
    {
        var a = (E: ring[0], N: ring[1]);
        var b = (E: ring[2], N: ring[3]);
        var c = (E: ring[4], N: ring[5]);
        var d = (E: ring[6], N: ring[7]);

        var t0 = TileId.FromLv95(a.E, a.N);
        bool same = TileId.FromLv95(b.E, b.N) == t0 && TileId.FromLv95(c.E, c.N) == t0 && TileId.FromLv95(d.E, d.N) == t0;
        double side = Math.Max(Dist(a, b), Dist(b, c));

        if (!same && side > ParkingPieceMin && depth < 8)
        {
            // halve along the longer pair of edges and recurse: both halves stay rectangles
            stats.SeamSplit++;
            if (Dist(a, b) >= Dist(b, c))
            {
                var ab = Mid(a, b); var dc = Mid(d, c);
                EmitQuad([a.E, a.N, ab.E, ab.N, dc.E, dc.N, d.E, d.N], y, height, type, flags, wanted, areas, stats, depth + 1);
                EmitQuad([ab.E, ab.N, b.E, b.N, c.E, c.N, dc.E, dc.N], y, height, type, flags, wanted, areas, stats, depth + 1);
            }
            else
            {
                var bc = Mid(b, c); var ad = Mid(a, d);
                EmitQuad([a.E, a.N, b.E, b.N, bc.E, bc.N, ad.E, ad.N], y, height, type, flags, wanted, areas, stats, depth + 1);
                EmitQuad([ad.E, ad.N, bc.E, bc.N, c.E, c.N, d.E, d.N], y, height, type, flags, wanted, areas, stats, depth + 1);
            }
            return;
        }

        // the tile holding the middle owns it, so a piece that still straddles a line lands beside
        // its neighbours rather than two tiles away
        var at = TileId.FromLv95((a.E + c.E) * 0.5, (a.N + c.N) * 0.5);
        if (!wanted.Contains(at)) return;

        var v = new float[12];
        foreach (var (p, i) in new[] { (a, 0), (b, 1), (c, 2), (d, 3) })
        {
            v[i * 3] = (float)(p.E - at.MinE);
            v[i * 3 + 1] = (float)y;
            v[i * 3 + 2] = (float)(at.MaxN - p.N);
        }
        Get(areas, at).Add(new RoadAreaProp
        {
            Type = type, Variant = 0, Flags = flags, Height = (float)height,
            Vertices = v, Indices = [0, 1, 2, 0, 2, 3],
        });

        static double Dist((double E, double N) p, (double E, double N) q) =>
            Math.Sqrt((p.E - q.E) * (p.E - q.E) + (p.N - q.N) * (p.N - q.N));
        static (double E, double N) Mid((double E, double N) p, (double E, double N) q) =>
            ((p.E + q.E) * 0.5, (p.N + q.N) * 0.5);
    }

    private static double Dist((double E, double N) p, (double E, double N) q) =>
        Math.Sqrt((p.E - q.E) * (p.E - q.E) + (p.N - q.N) * (p.N - q.N));
}
