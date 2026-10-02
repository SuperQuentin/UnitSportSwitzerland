using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// Measures whether a player actually stands ON the road ribbon rather than in it.
///
/// <para>
/// <c>godot --path . -- --roadcheck [--at E,N]</c>. Spawns a body, then for real road and path
/// vertices of the tile under it compares three heights: the vertex (which is exactly where the
/// ribbon is drawn), the collision floor under it (a physics ray), and where a dropped
/// <see cref="FootPlayer"/> comes to rest. "Sinks into the path" is a claim about centimetres
/// that no screenshot can settle; this prints them, and exits non-zero if the body rests more
/// than <see cref="Tolerance"/> below the ribbon anywhere.
/// </para>
/// </summary>
public partial class RoadStandProbe : Node
{
    private const float Tolerance = 0.05f;
    private const int Samples = 16;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private Task<RoadTile?>? _roads;
    private Task<ChunkGrid?>? _grid;
    private List<(Vector3 Road, string What, Kind Kind)>? _points;
    private int _floorFails, _fallFails, _falls;
    private int _index = -1;
    private double _settle;
    private double _wait;
    private readonly List<float> _floorGap = new(), _bodyGap = new();

    public RoadStandProbe(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
    }

    public static bool Requested() => CmdArgs.Has("--roadcheck");

    /// <summary>
    /// <c>--bridges</c>: test bridge decks instead. They are not in the terrain heightfield at all
    /// (a heightfield cannot hold a deck over a gorge) but on their own collision body, drawn
    /// <see cref="BridgeLift"/> above the stored deck line.
    /// </summary>
    private static readonly bool Bridges = CmdArgs.Has("--bridges");

    /// <summary>
    /// <c>--embankments</c> (#125): add the tile's retaining walls and road slopes. A body must
    /// rest on a wall's cap, a body dropped just off its face must fall past it, and the
    /// collision floor at the wall foot and on cut/fill slopes must be the blend's own height
    /// (<c>TerrainMeshBuilder.ComputeRoadBlend</c>) to 5 cm, sampled at lattice vertices so the
    /// triangulation between them does not matter. Slopes are probed with a ray only: a body
    /// slides on a 1:1 cut.
    /// </summary>
    private static readonly bool Embankments = CmdArgs.Has("--embankments");

    /// <summary>
    /// <c>--sidewalks</c> (#119): only sidewalk samples. A body rests on a kerbed sidewalk's top
    /// (middle, and just past the chamfer at the kerb) and on the carriageway beside the kerb, and
    /// the ground just past a sidewalk's outer edge is the blend's own height.
    /// </summary>
    private static readonly bool Sidewalks = CmdArgs.Has("--sidewalks");

    /// <summary><c>--floorat E,N</c>: only the four lattice vertices around one LV95 point (with <c>--at</c> on its tile).</summary>
    private static (double E, double N)? FloorAtArg()
    {
        var args = CmdArgs.All;
        int i = Array.IndexOf(args, "--floorat");
        if (i < 0 || i + 1 >= args.Length) return null;
        var p = args[i + 1].Split(',');
        return (double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>What a sample asserts: a body rests on it, the ray floor is it, or a body falls past it.</summary>
    private enum Kind { Body, Floor, Fall }

    /// <summary>Mirror of RoadMeshBuilder.BridgeLift: the deck is drawn this far above its line.</summary>
    private const float BridgeLift = 0.15f;

    public override void _PhysicsProcess(double delta)
    {
        _wait += delta;
        // loading, then 1.2 s of settling per sample
        if (_wait > 60 + 2.0 * (_points?.Count ?? 30)) { GD.Print("[roadcheck] TIMEOUT"); GetTree().Quit(2); SetPhysicsProcess(false); return; }

        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);
        var tile = TileId.FromLv95(e, n);

        if (_player == null)
        {
            if (!_chunks.TryGetHeight(at, out float g)) return;
            _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(at.X, g + 1.5f, at.Z);
            _roads = _chunks.Source!.LoadRoadsAsync(tile);
            if (Embankments || Sidewalks || FloorAtArg() != null) _grid = _chunks.Source!.LoadChunkAsync(tile);
            return;
        }

        // the road-blended collision is the tail of the tile build; give it time to land
        if (_points == null)
        {
            if (_roads is not { IsCompleted: true } || !_chunks.HasCollisionAt(at) || _wait < 8) return;
            if ((Embankments || Sidewalks || FloorAtArg() != null) && _grid is not { IsCompleted: true }) return;
            _points = Sidewalks || FloorAtArg() != null ? new() : PickPoints(_roads.Result, tile);
            if (FloorAtArg() is { } fa && _roads.Result is { } ft && _grid?.Result is { } fg)
            {
                // --floorat E,N: the blend's height at the four lattice vertices around a point
                var b = _origin.ToWorld(tile.MinE, tile.MaxN, 0);
                var ground = TerrainMeshBuilder.GroundHeights(fg, TerrainMeshBuilder.ComputeRoadBlend(ft), 0.0);
                var pt = _origin.ToWorld(fa.E, fa.N, 0) - b;
                foreach (var (dc, dr) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                {
                    int c = (int)Math.Floor(pt.X) + dc, r = (int)Math.Floor(pt.Z) + dr;
                    var q = b + new Vector3(c, ground(c, r), r);
                    _points.Add((q, $"floorat vertex {c},{r}", Kind.Floor));
                }
            }
            if (Sidewalks && _roads.Result is { } st && _grid!.Result is { } sg) _points.AddRange(PickSidewalkPoints(st, sg, tile));
            if (Embankments && _roads.Result is { } rt && _grid!.Result is { } g) _points.AddRange(PickEmbankmentPoints(rt, g, tile));
            GD.Print($"[roadcheck] {_points.Count} road/path samples in tile {tile}");
            if (_points.Count == 0) { GetTree().Quit(2); SetPhysicsProcess(false); return; }
            Next();
            return;
        }

        _settle += delta;
        if (_settle < 1.2) return;

        var (road, what, kind) = _points[_index];
        float floor = FloorAt(road);
        float body = _player.GlobalPosition.Y;
        // no floor at all under the point is a carved hole (an underpass beneath it), not a
        // road the player stands on - reported, but not held against the result
        if (!float.IsNaN(floor) && kind == Kind.Body)
        {
            _floorGap.Add(floor - road.Y);
            _bodyGap.Add(body - road.Y);
        }
        if (kind == Kind.Floor && !float.IsNaN(floor) && Math.Abs(floor - road.Y) > Tolerance) _floorFails++;
        if (kind == Kind.Fall) { _falls++; if (body > road.Y - 1f) _fallFails++; }
        GD.Print($"[roadcheck] {what,-22} {(kind == Kind.Body ? "ribbon" : kind == Kind.Floor ? "blend " : "edge  ")} {road.Y,8:F3}  floor {floor - road.Y,+7:F3}  "
            + $"body {body - road.Y,+7:F3}  onFloor={_player.IsOnFloor()}  at {Lv95(road)}");

        if (_index + 1 < _points.Count) { Next(); return; }

        if (_bodyGap.Count == 0) { GetTree().Quit(2); SetPhysicsProcess(false); return; }
        float worst = _bodyGap.Min();
        GD.Print($"[roadcheck] floor-ribbon mean {_floorGap.Average():+0.000;-0.000} min {_floorGap.Min():+0.000;-0.000}; "
            + $"body-ribbon mean {_bodyGap.Average():+0.000;-0.000} min {worst:+0.000;-0.000} "
            + $"max {_bodyGap.Max():+0.000;-0.000} over {_bodyGap.Count}");
        if (Embankments || Sidewalks)
            GD.Print($"[roadcheck] embankment floor off the blend by >{Tolerance} m: {_floorFails}; "
                + $"bodies that did not fall off a wall edge: {_fallFails}/{_falls}");
        // a sidewalk sample may not float either: a body held up by something above the slab
        bool ok = worst > -Tolerance && (!Sidewalks || _bodyGap.Max() < Tolerance) && _floorFails == 0 && _fallFails == 0;
        GD.Print(ok ? "[roadcheck] RESULT: body stands on the ribbon"
                    : "[roadcheck] RESULT: FAILED — body rests below the ribbon");
        SetPhysicsProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }

    private string Lv95(Vector3 p)
    {
        var (e, n) = _origin.ToLv95(p);
        return $"{e:F1},{n:F1}";
    }

    private void Next()
    {
        _index++;
        _settle = 0;
        var road = _points![_index].Road;
        _player!.GlobalPosition = road + Vector3.Up * 0.6f;
        _player.Velocity = Vector3.Zero;
    }

    private float FloorAt(Vector3 p)
    {
        var q = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 4, p - Vector3.Up * 4);
        q.Exclude = new Godot.Collections.Array<Rid> { _player!.GetRid() };
        var hit = _player.GetWorld3D().DirectSpaceState.IntersectRay(q);
        return hit.Count > 0 ? hit["position"].AsVector3().Y : float.NaN;
    }

    /// <summary>Interior vertices of at-grade travellable lines, spread across the tile's classes.</summary>
    private List<(Vector3, string, Kind)> PickPoints(RoadTile? tile, TileId id)
    {
        var result = new List<(Vector3, string, Kind)>();
        if (tile == null) return result;
        var basePos = _origin.ToWorld(id.MinE, id.MaxN, 0);

        var candidates = tile.Segments
            .Where(s => (Bridges
                    ? (s.Flags & RoadFlags.Bridge) != 0 && s.Class != RoadClass.Railway
                    : (s.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) == 0 && s.PointCount >= 3)
                && Gpx.RoadNetwork.IsTravellable(s.Class) && s.PointCount >= 2)
            .ToList();
        // round-robin over classes so a tile full of footpaths still tests a road, and vice versa
        var byClass = candidates.GroupBy(s => s.Class).Select(g => g.ToList()).ToList();
        for (int k = 0; result.Count < Samples && k < 64; k++)
            foreach (var group in byClass)
            {
                if (result.Count >= Samples) break;
                if (k >= group.Count) continue;
                var s = group[k];
                // a bridge's mid-span, between vertices if it has only its two ends
                int i = s.PointCount / 2, j = Math.Max(0, i - (s.PointCount % 2 == 0 ? 1 : 0));
                var a = new Vector3(s.Points[i * 3], s.Points[i * 3 + 1], s.Points[i * 3 + 2]);
                var b = new Vector3(s.Points[j * 3], s.Points[j * 3 + 1], s.Points[j * 3 + 2]);
                var p = basePos + (a + b) * 0.5f + (Bridges ? Vector3.Up * BridgeLift : Vector3.Zero);
                if (!_chunks.HasCollisionAt(p)) continue;
                result.Add((p, $"{s.Class}/{s.Surface}", Kind.Body));
            }
        if (Bridges) return result;
        // turn lane widenings (#123): a body on the strip's widest triangle, at its centroid
        foreach (var strip in tile.AreaProps.Where(a => a.Type == AreaPropType.Pavement).Take(4))
        {
            Vector3 V(int i) => new(strip.Vertices[i * 3], strip.Vertices[i * 3 + 1], strip.Vertices[i * 3 + 2]);
            var widest = Enumerable.Range(0, strip.Indices.Length / 3)
                .Select(t => (V(strip.Indices[t * 3]), V(strip.Indices[t * 3 + 1]), V(strip.Indices[t * 3 + 2])))
                .MaxBy(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length());
            var p = basePos + (widest.Item1 + widest.Item2 + widest.Item3) / 3f;
            if (_chunks.HasCollisionAt(p)) result.Add((p, "turn lane strip", Kind.Body));
        }
        return result;
    }

    /// <summary>
    /// Whether a tile-local point lies on a street's carriageway or sidewalk (#119), other than
    /// <paramref name="except"/>'s, or on a corner patch: its floor is that slab, not the blend.
    /// </summary>
    private static bool OnSidewalk(RoadTile tile, Vector3 p, RoadSegment? except = null)
    {
        var xz = new Vector2(p.X, p.Z);
        // near a seam the neighbouring tile's sidewalks are not in this tile's data
        if (p.X < 6 || p.Z < 6 || p.X > 994 || p.Z > 994) return true;
        foreach (var a in tile.AreaProps)
        {
            if (a.Type != AreaPropType.Sidewalk) continue;
            var v = a.Vertices;
            for (int t = 0; t + 2 < a.Indices.Length; t += 3)
            {
                Vector2 V(int i) => new(v[a.Indices[t + i] * 3], v[a.Indices[t + i] * 3 + 2]);
                if (Geometry2D.PointIsInsideTriangle(xz, V(0), V(1), V(2))) return true;
            }
        }
        foreach (var s in tile.Segments)
        {
            if (!RoadStreetBuilder.HasSidewalk(s) || ReferenceEquals(s, except)) continue;
            float reach = s.Width * 0.5f + Math.Max(s.Attributes.Left.SidewalkDm, s.Attributes.Right.SidewalkDm) / 10f + 0.5f;
            for (int i = 0; i + 1 < s.PointCount; i++)
            {
                var a = new Vector2(s.Points[i * 3], s.Points[i * 3 + 2]);
                var b = new Vector2(s.Points[i * 3 + 3], s.Points[i * 3 + 5]);
                var q = new Vector2(p.X, p.Z);
                var ab = b - a;
                float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp((q - a).Dot(ab) / ab.LengthSquared(), 0, 1);
                if ((a + ab * t).DistanceTo(q) < reach) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Sidewalk samples of the tile (#119), spread over its kerbed sidewalks: the slab's middle
    /// and its kerb edge (just past the chamfer) at road + kerb, the carriageway 0.4 m in from
    /// the kerb at road height, and the lattice vertex past the outer edge at the blend's height.
    /// </summary>
    private List<(Vector3, string, Kind)> PickSidewalkPoints(RoadTile tile, ChunkGrid grid, TileId id)
    {
        var result = new List<(Vector3, string, Kind)>();
        var basePos = _origin.ToWorld(id.MinE, id.MaxN, 0);
        var ground = TerrainMeshBuilder.GroundHeights(grid, TerrainMeshBuilder.ComputeRoadBlend(tile), 0.0);
        int last = ChunkFormat.GridSize - 1;
        var walls = tile.LinearProps.Where(RoadWallBuilder.IsWall).ToList();
        var kerbed = tile.Segments.Where(s => RoadStreetBuilder.HasSidewalk(s) && s.PointCount >= 3
            && (s.Attributes.Left.KerbCm > 0 || s.Attributes.Right.KerbCm > 0)).ToList();
        for (int k = 0; k < kerbed.Count && result.Count < 4 * Samples; k += Math.Max(1, kerbed.Count / Samples))
        {
            var s = kerbed[k];
            int i = s.PointCount / 2;
            var p = new Vector3(s.Points[i * 3], s.Points[i * 3 + 1], s.Points[i * 3 + 2]);
            var d = new Vector3(s.Points[i * 3 + 3] - s.Points[i * 3 - 3], 0, s.Points[i * 3 + 5] - s.Points[i * 3 - 1]).Normalized();
            var right = new Vector3(-d.Z, 0, d.X);
            float half = s.Width * 0.5f;
            foreach (bool r in (ReadOnlySpan<bool>)[false, true])
            {
                var side = r ? s.Attributes.Right : s.Attributes.Left;
                if (side.KerbCm == 0) continue;
                var across = r ? right : -right;
                float w = side.SidewalkDm / 10f, kerb = side.KerbCm / 100f;
                void Add(Vector3 at, string what, Kind kind) { var q = basePos + at; if (_chunks.HasCollisionAt(q)) result.Add((q, what, kind)); }
                Add(p + across * (half + w * 0.5f) + Vector3.Up * kerb, $"{s.Class} sidewalk {w:F1}", Kind.Body);
                if (w >= 1f) Add(p + across * (half + RoadStreetBuilder.Chamfer + 0.35f) + Vector3.Up * kerb, $"{s.Class} kerb top", Kind.Body);
                Add(p + across * (half - 0.4f), $"{s.Class} by the kerb", Kind.Body);
                // 1.2 m out: the lattice vertex it rounds to (up to 0.71 m off) stays off the slab
                var o = p + across * (half + w + 1.2f);
                int c = Math.Clamp((int)Math.Round(o.X), 0, last), rr = Math.Clamp((int)Math.Round(o.Z), 0, last);
                // a retaining wall there (#125): its cap is the floor, not the blend
                bool walled = walls.Any(wl => Enumerable.Range(0, wl.PointCount).Any(m =>
                    new Vector2(wl.Points[m * 4] - c, wl.Points[m * 4 + 2] - rr).Length() < RoadEmbankment.CoverDepth + 2f));
                var past = new Vector3(c, ground(c, rr), rr);
                if (!walled && !OnSidewalk(tile, past, except: s)) Add(past, $"{s.Class} past sidewalk", Kind.Floor);
            }
        }
        // junction corners: the middle of each patch's largest triangle, at its top. The floor is
        // asserted, not the body: a patch on a sloping junction is warped, and a capsule resting
        // on it touches the higher triangle beside the sample
        var corners = tile.AreaProps.Where(a => a.Type == AreaPropType.Sidewalk && (a.Flags & PropFlags.Solid) != 0).ToList();
        for (int k = 0; k < corners.Count && k < 3 * Samples; k += Math.Max(1, corners.Count / Samples))
        {
            var a = corners[k];
            Vector3 V(int i) => new(a.Vertices[i * 3], a.Vertices[i * 3 + 1], a.Vertices[i * 3 + 2]);
            Vector3 best = default;
            float area = 0;
            for (int t = 0; t + 2 < a.Indices.Length; t += 3)
            {
                var (p0, p1, p2) = (V(a.Indices[t]), V(a.Indices[t + 1]), V(a.Indices[t + 2]));
                float ar = (p1 - p0).Cross(p2 - p0).Length();
                if (ar > area) { area = ar; best = (p0 + p1 + p2) / 3f; }
            }
            var q = basePos + best + Vector3.Up * a.Height;
            if (area > 0.5f && _chunks.HasCollisionAt(q)) result.Add((q, "corner", Kind.Floor));
        }
        return result;
    }

    /// <summary>
    /// Wall caps, wall edges, wall feet and road slopes of the tile (#125). Expected heights come
    /// from the same blend the collision was built from, at lattice vertices.
    /// </summary>
    private List<(Vector3, string, Kind)> PickEmbankmentPoints(RoadTile tile, ChunkGrid grid, TileId id)
    {
        var result = new List<(Vector3, string, Kind)>();
        var basePos = _origin.ToWorld(id.MinE, id.MaxN, 0);
        var ground = TerrainMeshBuilder.GroundHeights(grid, TerrainMeshBuilder.ComputeRoadBlend(tile), 0.0);
        int last = ChunkFormat.GridSize - 1;
        void Lattice(float x, float z, string what)
        {
            int c = Math.Clamp((int)Math.Round(x), 0, last), r = Math.Clamp((int)Math.Round(z), 0, last);
            var p = basePos + new Vector3(c, ground(c, r), r);
            if (_chunks.HasCollisionAt(p)) result.Add((p, what, Kind.Floor));
        }

        var walls = tile.LinearProps.Where(RoadWallBuilder.IsWall).ToList();
        for (int k = 0; k < walls.Count && k < 24; k += Math.Max(1, walls.Count / 6))
        {
            var w = walls[k];
            int m = w.PointCount / 2, a = Math.Max(0, m - 1), b = Math.Min(w.PointCount - 1, m + 1);
            var face = new Vector3(w.Points[m * 4], 0, w.Points[m * 4 + 2]);
            var dir = new Vector3(w.Points[b * 4] - w.Points[a * 4], 0, w.Points[b * 4 + 2] - w.Points[a * 4 + 2]).Normalized();
            var left = new Vector3(dir.Z, 0, -dir.X);   // the solid's side, X east and Z south
            float top = w.Points[m * 4 + 1] + w.Points[m * 4 + 3];
            bool fill = w.Type == LinearPropType.RetainingWallFill;
            string name = fill ? "fill" : "cut";
            // the crown, and the cover behind it (the road over a fill wall, the backfill of a cut)
            float crown = fill ? top + RoadEmbankment.FillCrownLift : top;
            float cover = fill ? top : top - RoadEmbankment.CutCrownOver;
            var cap = basePos + face + left * (w.Thickness * 0.5f) + Vector3.Up * crown;
            // a railing on the crown (#126) leaves no room to stand there: a body rests on its top
            bool railed = tile.LinearProps.Any(r => RoadRailing.IsRailing(r) && Enumerable.Range(0, r.PointCount).Any(k =>
                new Vector2(basePos.X + r.Points[k * 4] - cap.X, basePos.Z + r.Points[k * 4 + 2] - cap.Z).Length() < 1.5f));
            if (!railed && _chunks.HasCollisionAt(cap)) result.Add((cap, $"wall {name} crown", Kind.Body));
            var back = basePos + face + left * (RoadEmbankment.CoverDepth * 0.6f) + Vector3.Up * cover;
            if (_chunks.HasCollisionAt(back)) result.Add((back, $"wall {name} cover", Kind.Body));
            if (w.Points[m * 4 + 3] >= 1.5f)
            {
                var edge = basePos + face - left * 0.4f + Vector3.Up * top;
                if (_chunks.HasCollisionAt(edge)) result.Add((edge, $"wall {name} off edge", Kind.Fall));
            }
            var foot = face - left * 2.0f;
            // a cut wall's foot lies toward its road: on a street that is the sidewalk (#119), whose
            // slab, not the blend, is the floor there
            if (!OnSidewalk(tile, foot)) Lattice(foot.X, foot.Z, $"wall {name} foot");
        }

        int slopes = 0;
        foreach (var s in tile.Segments)
        {
            if (slopes >= 8) break;
            if (!RoadEmbankment.AllowsWall(s) || s.PointCount < 3) continue;
            int i = s.PointCount / 2;
            var p = new Vector3(s.Points[i * 3], 0, s.Points[i * 3 + 2]);
            var d = new Vector3(s.Points[i * 3 + 3] - s.Points[i * 3 - 3], 0, s.Points[i * 3 + 5] - s.Points[i * 3 - 1]).Normalized();
            var right = new Vector3(-d.Z, 0, d.X);
            foreach (bool r in (ReadOnlySpan<bool>)[false, true])
            {
                var q = p + (r ? right : -right) * (float)(RoadEmbankment.EdgeOffset(s, r) + 2.0);
                // a walled side has no slope: its cap is there instead
                bool walled = walls.Any(w => Enumerable.Range(0, w.PointCount).Any(k =>
                    new Vector2(w.Points[k * 4] - q.X, w.Points[k * 4 + 2] - q.Z).Length() < RoadEmbankment.CoverDepth + 2f));
                if (!walled && !OnSidewalk(tile, q)) Lattice(q.X, q.Z, $"{s.Class} slope");
            }
            slopes++;
        }
        return result;
    }
}
