using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// <c>--walloff[,seconds]</c> on a connected client (#125): that long after joining (default 20 s)
/// the local player gets out of any vehicle and is put on the cap of the retaining wall nearest
/// the spawn point (<c>--at</c>), in that tile; it must
/// come to rest there, then steps 0.4 m past the face and must fall to the ground below and stand
/// on it, not hover over the drop. The player is the replicated one, so the other peers see the
/// fall. Prints <c>[walloff] RESULT</c>; never quits (the run's other probe decides when to end).
/// </summary>
public partial class WallOffProbe : Node
{
    private readonly Node3D _players;
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly double _after;
    private double _t, _stageT;
    private int _stage;
    private Task<RoadTile?>? _roads;
    private Vector3 _cap, _edge;
    private readonly double _spawnE, _spawnN;
    private float _top;

    public WallOffProbe(Node3D players, ChunkManager chunks, WorldOrigin origin, double after)
    {
        _players = players;
        _chunks = chunks;
        _origin = origin;
        _after = after;
        (_spawnE, _spawnN) = SpawnPoint.ParseTarget();
    }

    public static double? ParseArgs()
    {
        foreach (var a in CmdArgs.All)
            if (a.StartsWith("--walloff"))
            {
                var parts = a.Split(',');
                return parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 20;
            }
        return null;
    }

    public override void _PhysicsProcess(double delta)
    {
        _t += delta;
        _stageT += delta;
        if (_stage > 3 || _t < _after) return;
        var me = _players.GetNodeOrNull<FootPlayer>(Multiplayer.GetUniqueId().ToString());
        if (me == null) return;
        // a client that joins before its tile streamed goes on foot high over it and falls: if that
        // landing knocked it out, its revive puts it back on its last safe spot, mid-check
        if (me.KnockedOut) { if (_stage > 0) _stage = 1; _stageT = 0; return; }

        switch (_stage)
        {
            case 0:
                // out of whatever it rides first (a car after a race)
                if (me.Ride != RideKind.OnFoot) { me.SetRide(RideKind.OnFoot); return; }
                _roads = _chunks.Source!.LoadRoadsAsync(TileId.FromLv95(_spawnE, _spawnN));
                Next();
                break;
            case 1:
                if (_roads is not { IsCompleted: true }) return;
                if (!Pick(_roads.Result, me)) { GD.Print("[walloff] RESULT: no retaining wall in this tile"); _stage = 9; return; }
                Put(me, _cap + Vector3.Up * 1.0f);
                Next();
                break;
            case 2:
                // collision follows the player: wait for it, keep it parked on the cap meanwhile
                if (!_chunks.HasCollisionAt(_cap)) { Put(me, _cap + Vector3.Up * 1.0f); _stageT = 0; return; }
                if (_stageT < 2.5) return;
                GD.Print($"[walloff] on the cap: body {me.GlobalPosition.Y - _top:+0.000;-0.000} m from the top, onFloor={me.IsOnFloor()}");
                Put(me, _edge);
                Next();
                break;
            case 3:
                if (_stageT < 3.0) return;
                float floor = FloorBelow(me);
                float fell = _top - me.GlobalPosition.Y, above = me.GlobalPosition.Y - floor;
                // the ground under a fill wall is a steep hillside, and a capsule resting on a 45°
                // slope has its centre line 0.12 m over the contact plane: hovering is more than that
                bool ok = fell > 1.0f && Math.Abs(above) < 0.2f && me.IsOnFloor();
                GD.Print($"[walloff] stepped off: fell {fell:F2} m, body {above:+0.000;-0.000} m above the floor under it, onFloor={me.IsOnFloor()} at {Lv95(me.GlobalPosition)}");
                GD.Print(ok ? "[walloff] RESULT: fell off the wall and stands on the ground" : "[walloff] RESULT: FAILED");
                _stage = 9;
                break;
        }
    }

    private void Next() { _stage++; _stageT = 0; }

    /// <summary>A teleport: the fall the body was in is not charged when it lands on the wall.</summary>
    private static void Put(FootPlayer me, Vector3 at)
    {
        me.PlaceAt(at, me.Rotation.Y);
        me.Velocity = Vector3.Zero;
    }

    /// <summary>The drawn wall at least 2 m high nearest the spawn point (<c>--at</c>): a point on its cap and one just past its face.</summary>
    private bool Pick(RoadTile? tile, FootPlayer me)
    {
        if (tile == null) return false;
        var basePos = _origin.ToWorld(tile.Id.MinE, tile.Id.MaxN, 0);
        float best = float.MaxValue;
        foreach (var w in tile.LinearProps)
        {
            if (!RoadWallBuilder.IsWall(w)) continue;
            for (int k = 1; k < w.PointCount - 1; k++)
            {
                if (w.Points[k * 4 + 3] < 2f) continue;
                var face = basePos + new Vector3(w.Points[k * 4], 0, w.Points[k * 4 + 2]);
                var off = face - _origin.ToWorld(_spawnE, _spawnN, 0);
                float d = new Vector2(off.X, off.Z).Length();
                if (d >= best) continue;
                best = d;
                var dir = new Vector3(w.Points[k * 4 + 4] - w.Points[k * 4 - 4], 0, w.Points[k * 4 + 6] - w.Points[k * 4 - 2]).Normalized();
                var left = new Vector3(dir.Z, 0, -dir.X);   // the solid's side, X east and Z south
                _top = w.Points[k * 4 + 1] + w.Points[k * 4 + 3];
                _cap = face + left * 1.0f + Vector3.Up * _top;
                _edge = face - left * 0.4f + Vector3.Up * (_top + 0.2f);
            }
        }
        if (best == float.MaxValue) return false;
        GD.Print($"[walloff] wall {best:F0} m away, top {_top:F2} at {Lv95(_cap)}");
        return true;
    }

    private static float FloorBelow(FootPlayer me)
    {
        var p = me.GlobalPosition;
        var q = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 0.5f, p - Vector3.Up * 6f);
        q.Exclude = new Godot.Collections.Array<Rid> { me.GetRid() };
        var hit = me.GetWorld3D().DirectSpaceState.IntersectRay(q);
        return hit.Count > 0 ? hit["position"].AsVector3().Y : float.NaN;
    }

    private string Lv95(Vector3 p)
    {
        var (e, n) = _origin.ToLv95(p);
        return $"{e:F1},{n:F1}";
    }
}
