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

    public static bool Requested() => OS.GetCmdlineUserArgs().Contains("--roadcheck");

    /// <summary>
    /// <c>--bridges</c>: test bridge decks instead. They are not in the terrain heightfield at all
    /// (a heightfield cannot hold a deck over a gorge) but on their own collision body, drawn
    /// <see cref="BridgeLift"/> above the stored deck line.
    /// </summary>
    private static readonly bool Bridges = OS.GetCmdlineUserArgs().Contains("--bridges");

    /// <summary>
    /// <c>--embankments</c> (#125): add the tile's retaining walls and road slopes. A body must
    /// rest on a wall's cap, a body dropped just off its face must fall past it, and the
    /// collision floor at the wall foot and on cut/fill slopes must be the blend's own height
    /// (<c>TerrainMeshBuilder.ComputeRoadBlend</c>) to 5 cm, sampled at lattice vertices so the
    /// triangulation between them does not matter. Slopes are probed with a ray only: a body
    /// slides on a 1:1 cut.
    /// </summary>
    private static readonly bool Embankments = OS.GetCmdlineUserArgs().Contains("--embankments");

    /// <summary>What a sample asserts: a body rests on it, the ray floor is it, or a body falls past it.</summary>
    private enum Kind { Body, Floor, Fall }

    /// <summary>Mirror of RoadMeshBuilder.BridgeLift: the deck is drawn this far above its line.</summary>
    private const float BridgeLift = 0.15f;

    public override void _PhysicsProcess(double delta)
    {
        _wait += delta;
        if (_wait > 120) { GD.Print("[roadcheck] TIMEOUT"); GetTree().Quit(2); SetPhysicsProcess(false); return; }

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
            if (Embankments) _grid = _chunks.Source!.LoadChunkAsync(tile);
            return;
        }

        // the road-blended collision is the tail of the tile build; give it time to land
        if (_points == null)
        {
            if (_roads is not { IsCompleted: true } || !_chunks.HasCollisionAt(at) || _wait < 8) return;
            if (Embankments && _grid is not { IsCompleted: true }) return;
            _points = PickPoints(_roads.Result, tile);
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
        if (Embankments)
            GD.Print($"[roadcheck] embankment floor off the blend by >{Tolerance} m: {_floorFails}; "
                + $"bodies that did not fall off a wall edge: {_fallFails}/{_falls}");
        bool ok = worst > -Tolerance && _floorFails == 0 && _fallFails == 0;
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
            if (_chunks.HasCollisionAt(cap)) result.Add((cap, $"wall {name} crown", Kind.Body));
            var back = basePos + face + left * (RoadEmbankment.CoverDepth * 0.6f) + Vector3.Up * cover;
            if (_chunks.HasCollisionAt(back)) result.Add((back, $"wall {name} cover", Kind.Body));
            if (w.Points[m * 4 + 3] >= 1.5f)
            {
                var edge = basePos + face - left * 0.4f + Vector3.Up * top;
                if (_chunks.HasCollisionAt(edge)) result.Add((edge, $"wall {name} off edge", Kind.Fall));
            }
            var foot = face - left * 2.0f;
            Lattice(foot.X, foot.Z, $"wall {name} foot");
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
                if (!walled) Lattice(q.X, q.Z, $"{s.Class} slope");
            }
            slopes++;
        }
        return result;
    }
}
