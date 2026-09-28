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
    private List<(Vector3 Road, string What)>? _points;
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
            return;
        }

        // the road-blended collision is the tail of the tile build; give it time to land
        if (_points == null)
        {
            if (_roads is not { IsCompleted: true } || !_chunks.HasCollisionAt(at) || _wait < 8) return;
            _points = PickPoints(_roads.Result, tile);
            GD.Print($"[roadcheck] {_points.Count} road/path samples in tile {tile}");
            if (_points.Count == 0) { GetTree().Quit(2); SetPhysicsProcess(false); return; }
            Next();
            return;
        }

        _settle += delta;
        if (_settle < 1.2) return;

        var (road, what) = _points[_index];
        float floor = FloorAt(road);
        float body = _player.GlobalPosition.Y;
        // no floor at all under the point is a carved hole (an underpass beneath it), not a
        // road the player stands on - reported, but not held against the result
        if (!float.IsNaN(floor))
        {
            _floorGap.Add(floor - road.Y);
            _bodyGap.Add(body - road.Y);
        }
        GD.Print($"[roadcheck] {what,-22} ribbon {road.Y,8:F3}  floor {floor - road.Y,+7:F3}  "
            + $"body {body - road.Y,+7:F3}  onFloor={_player.IsOnFloor()}");

        if (_index + 1 < _points.Count) { Next(); return; }

        if (_bodyGap.Count == 0) { GetTree().Quit(2); SetPhysicsProcess(false); return; }
        float worst = _bodyGap.Min();
        GD.Print($"[roadcheck] floor-ribbon mean {_floorGap.Average():+0.000;-0.000} min {_floorGap.Min():+0.000;-0.000}; "
            + $"body-ribbon mean {_bodyGap.Average():+0.000;-0.000} min {worst:+0.000;-0.000} "
            + $"max {_bodyGap.Max():+0.000;-0.000} over {_bodyGap.Count}");
        bool ok = worst > -Tolerance;
        GD.Print(ok ? "[roadcheck] RESULT: body stands on the ribbon"
                    : "[roadcheck] RESULT: FAILED — body rests below the ribbon");
        SetPhysicsProcess(false);
        GetTree().Quit(ok ? 0 : 1);
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
    private List<(Vector3, string)> PickPoints(RoadTile? tile, TileId id)
    {
        var result = new List<(Vector3, string)>();
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
                result.Add((p, $"{s.Class}/{s.Surface}"));
            }
        return result;
    }
}
