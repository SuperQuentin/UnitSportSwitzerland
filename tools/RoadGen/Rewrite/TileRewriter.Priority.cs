namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Junction priority in the network stage (#121): <see cref="PriorityPlanner"/> decides each
/// junction of the block and its halo; this writes the result into the block's tiles: the ATTR
/// yield bits, the Wartelinie and the main road's carried-through centre line as paint, the
/// signs as point props after checking each one has somewhere to stand.
/// </summary>
public static partial class TileRewriter
{
    private sealed class PriorityResult
    {
        public readonly List<(Junction Junction, PriorityPlanner.Plan Plan)> Plans = new();
        public readonly Dictionary<int, RoadAttrFlags> Yield = new();
        /// <summary>The guide lines written through junctions; a turn pocket (#123) replaces the one on its side.</summary>
        public readonly HashSet<RoadPaint> Guides = new();
        /// <summary>Each guide by (junction node, side 0 = first main arm's left / 1 = its right): a bike crossing (#120) replaces it.</summary>
        public readonly Dictionary<(int Node, int Side), (TileId Tile, RoadPaint Paint)> GuideAt = new();
        /// <summary>A yielding link's Wartelinie rows, and its signs (tile, index in the tile's props): a path crossing (#120) moves them back.</summary>
        public readonly Dictionary<int, List<(TileId Tile, RoadPaint Paint)>> TeethOf = new();
        public readonly Dictionary<int, List<(TileId Tile, int Index)>> SignsOf = new();

        public RoadAttrFlags FlagsOf(int linkId) => Yield.GetValueOrDefault(linkId);
    }

    /// <summary>Dash and gap of a guide line (Führungslinie, SSV 6.16) through a junction.</summary>
    private const float GuideDash = 1.0f;

    /// <summary>A sign must not stand where the ground is this far above or below the road: an embankment or a cut, no verge.</summary>
    private const double SignMaxGroundStep = 1.5;

    private static PriorityPlanner.LinkInfo? InfoOf(RoadLink link) =>
        link.Tag is Source s && s.Segment.Class != RoadClass.Railway
            ? new PriorityPlanner.LinkInfo(s.Segment.Class, s.Segment.Surface, s.Segment.Flags,
                CrossSectionPlanner.Attributes(s.Line), s.Line.Width)
            : null;

    /// <summary>
    /// Which dead ends may be carried onto a nearby flank: ordinary roads, paths and tracks; never
    /// a divided carriageway or a motorway class (their partner lies inside their half width).
    /// </summary>
    private static bool MayJoinNearEnd(RoadLink a, RoadLink b)
    {
        static bool Ok(RoadLink l) => l.Tag is Source s && s.Segment.Class is not (RoadClass.Railway
            or RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp) && (s.Segment.Flags & RoadFlags.Divided) == 0;
        return Ok(a) && Ok(b);
    }

    private static void EmitPriority(PriorityResult priority, RoadGenResult result, HashSet<TileId> block,
        HashSet<TileId> wanted, Dictionary<TileId, ChunkGrid>? grids, Footprints buildings,
        Dictionary<TileId, List<RoadPaint>> paint, Dictionary<TileId, List<RoadPointProp>> props,
        PriorityPlanner.Stats stats)
    {
        var net = result.Network;
        PriorityPlanner.Clearance? clearance = null;

        foreach (var (junction, plan) in priority.Plans)
        {
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            bool counted = block.Contains(home) && wanted.Contains(home);
            if (counted)
            {
                stats.Kinds[(int)plan.Kind]++;
                if (plan.Kind == PriorityPlanner.Kind.Main && plan.Arms.Any(a => a.Role == PriorityPlanner.Role.Yield)) stats.MainWithSideRoad++;
                foreach (var arm in plan.Arms)
                    if (arm.Role == PriorityPlanner.Role.Yield)
                    {
                        if (arm.Approach) stats.YieldingArms++; else stats.YieldNoApproach++;
                        if (arm.Approach && net.Links[arm.LinkId].Tag is Source { Segment.Surface: not RoadSurface.Paved })
                            stats.NoTeethUnpaved++;
                    }
            }

            foreach (var row in plan.Teeth)
            {
                if (net.Links[row.LinkId].Tag is not Source source || !block.Contains(source.Tile)) continue;
                float lift = (source.Segment.Flags & RoadFlags.Bridge) != 0 ? PaintBridgeLift : 0f;
                var vertices = Local(source.Tile, [row.From, row.To], source.SampleHeight, lift);
                Get(paint, source.Tile).Add(new RoadPaint
                {
                    Shape = PaintShape.Polyline, Type = PaintType.SharkTooth, Rgba = PaintEmitter.White,
                    Width = RoadSigns.ToothHeight, Dash = RoadSigns.ToothBase, Gap = RoadSigns.ToothGap,
                    Vertices = vertices,
                });
                stats.TeethRows++;
                stats.Teeth += RoadPaintGeometry.Runs(paint[source.Tile][^1]).Count;
                Get(priority.TeethOf, row.LinkId).Add((source.Tile, paint[source.Tile][^1]));
            }

            if (plan.CentreLine is { } line && counted)
            {
                var anchors = Anchors(junction, net);
                if (anchors.Count > 0)
                {
                    float dash = 3f, gap = plan.CentreUrban ? 3f : 6f;   // PaintEmitter.Leitlinie
                    Get(paint, home).Add(new RoadPaint
                    {
                        Shape = PaintShape.Polyline, Type = PaintType.WhiteDashed, Rgba = PaintEmitter.White,
                        Width = PaintEmitter.LineWidth, Dash = dash, Gap = gap,
                        Vertices = Local(home, line, p => HeightAt(anchors, p), 0f),
                    });
                    stats.CentreLines++;

                    // the edges through the junction: dashed across a joining road's mouth
                    for (int side = 0; side < plan.Guides.Count; side++)
                    {
                        var (guide, dashed) = plan.Guides[side];
                        if (!dashed && plan.CentreUrban) continue;   // no edge lines in towns
                        var g = new RoadPaint
                        {
                            Shape = PaintShape.Polyline, Type = dashed ? PaintType.WhiteDashed : PaintType.WhiteSolid,
                            Rgba = PaintEmitter.White, Width = PaintEmitter.LineWidth,
                            Dash = dashed ? GuideDash : 0, Gap = dashed ? GuideDash : 0,
                            Vertices = Local(home, guide, p => HeightAt(anchors, p), 0f),
                        };
                        Get(paint, home).Add(g);
                        priority.Guides.Add(g);
                        priority.GuideAt[(junction.NodeId, side)] = (home, g);
                        stats.Guides++;
                    }
                }
            }

            foreach (var sign in plan.Signs)
            {
                if (net.Links[sign.LinkId].Tag is not Source source || !block.Contains(source.Tile)) continue;
                clearance ??= new PriorityPlanner.Clearance(result.Ribbons, result.Junctions);
                float road = source.SampleHeight(sign.RoadPoint);
                string? why = null;
                if ((source.Segment.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) why = "structure";
                else if (!clearance.IsClear(sign.At, RoadSigns.Side(sign.Type, sign.Variant) * 0.5)) why = "carriageway";
                else if (buildings.Contains(sign.At)) why = "building";
                else if (grids is not null && SampleGround(grids, sign.At.X, sign.At.Y) is var ground
                         && !double.IsNaN(ground) && Math.Abs(ground - road) > SignMaxGroundStep) why = "no verge";
                var list = Get(props, source.Tile);
                var local = Local(source.Tile, [sign.At], _ => road, 0f);
                if (why is null && list.Any(p => p.Type == sign.Type
                        && Math.Abs(p.X - local[0]) < 1.5f && Math.Abs(p.Z - local[2]) < 1.5f)) why = "duplicate";
                if (why is not null) { stats.Reject(sign.Type, why); continue; }

                var side = CrossSectionPlanner.Attributes(source.Line);
                float kerb = sign.OnSidewalk ? Math.Max(side.Left.KerbCm, side.Right.KerbCm) / 100f : 0f;
                float lower = sign.OnSidewalk ? RoadSigns.LowerEdgeSidewalk : RoadSigns.LowerEdge;
                // heading about +Y, 0 = facing -Z (north): the plate's front looks along Facing
                float heading = (float)Math.Atan2(-sign.Facing.X, sign.Facing.Y);
                list.Add(new RoadPointProp(sign.Type, sign.Variant, PropFlags.Solid, local[0], road + kerb, local[2],
                    heading, lower + RoadSigns.PlateHeight(sign.Type, sign.Variant)));
                Get(priority.SignsOf, sign.LinkId).Add((source.Tile, list.Count - 1));
                stats.Place(sign.Type);
            }
        }
    }

    /// <summary>Mirror of PaintEmitter's bridge lift: a deck is drawn this far above its line.</summary>
    private const float PaintBridgeLift = 0.15f;

    private static List<T> Get<TKey, T>(Dictionary<TKey, List<T>> d, TKey id) where TKey : notnull
    {
        if (!d.TryGetValue(id, out var l)) d[id] = l = new List<T>();
        return l;
    }

    private static float[] Local(TileId id, IReadOnlyList<Vec2> plan, Func<Vec2, float> height, float lift)
    {
        var v = new float[plan.Count * 3];
        for (int i = 0; i < plan.Count; i++)
        {
            v[i * 3] = (float)(plan[i].X - id.MinE);
            v[i * 3 + 1] = height(plan[i]) + lift;
            v[i * 3 + 2] = (float)(id.MaxN - plan[i].Y);
        }
        return v;
    }

    /// <summary>The junction's arm mouths and their heights, as <see cref="ToJunction"/> weights them.</summary>
    private static List<(Vec2 At, float Height)> Anchors(Junction junction, RoadNetwork net)
    {
        var anchors = new List<(Vec2 At, float Height)>();
        foreach (var arm in junction.Arms)
            if (net.Links[arm.LinkId].Tag is Source source)
            {
                var mid = (arm.Left + arm.Right) * 0.5;
                anchors.Add((mid, source.SampleHeight(mid)));
            }
        return anchors;
    }

    /// <summary>
    /// Building footprints of a block, from the <c>.bldg</c> tiles beside the roads: a sign never
    /// stands inside a building. A point is inside when a triangle of the building covers it in
    /// plan (the roof always does). Loaded lazily per tile.
    /// </summary>
    private sealed class Footprints(string chunkDir)
    {
        private readonly Dictionary<TileId, List<(double MinX, double MinY, double MaxX, double MaxY, float[] Tris)>> _tiles = new();

        public bool Contains(Vec2 p)
        {
            var at = TileId.FromLv95(p.X, p.Y);
            for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
            {
                var id = new TileId(at.E + de, at.N + dn);
                double x = p.X - id.MinE, z = id.MaxN - p.Y;
                foreach (var b in Load(id))
                {
                    if (x < b.MinX || x > b.MaxX || z < b.MinY || z > b.MaxY) continue;
                    for (int t = 0; t + 8 < b.Tris.Length; t += 9)
                        if (InTriangle(x, z, b.Tris[t], b.Tris[t + 2], b.Tris[t + 3], b.Tris[t + 5], b.Tris[t + 6], b.Tris[t + 8]))
                            return true;
                }
            }
            return false;
        }

        private List<(double MinX, double MinY, double MaxX, double MaxY, float[] Tris)> Load(TileId id)
        {
            if (_tiles.TryGetValue(id, out var list)) return list;
            _tiles[id] = list = new();
            string path = Path.Combine(chunkDir, BuildingFormat.FileName(id));
            if (!File.Exists(path)) return list;
            try
            {
                using var stream = File.OpenRead(path);
                foreach (var b in BuildingCodec.Decode(stream).Buildings)
                {
                    double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
                    for (int i = 0; i + 2 < b.Triangles.Length; i += 3)
                    {
                        minX = Math.Min(minX, b.Triangles[i]); maxX = Math.Max(maxX, b.Triangles[i]);
                        minZ = Math.Min(minZ, b.Triangles[i + 2]); maxZ = Math.Max(maxZ, b.Triangles[i + 2]);
                    }
                    list.Add((minX, minZ, maxX, maxZ, b.Triangles));
                }
            }
            catch (Exception)
            {
                // a building tile that will not decode only loses this check
            }
            return list;
        }

        private static bool InTriangle(double px, double pz, double ax, double az, double bx, double bz, double cx, double cz)
        {
            double d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
            double d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
            double d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
    }
}
