using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.World;

/// <summary>
/// <c>godot --path . -- --treecheck[,out.png] [--at E,N]</c>: waits for the trunk pool to fill
/// around a player, picks a real tree 20-40 m away on ground level enough to ride at, puts the
/// player 25 m short of it on a bike and rides straight at it. Passes when the rider never gets
/// through the trunk and an impact is registered. Prints how many trunks are live and what the pool
/// costs per physics frame.
/// </summary>
public partial class TreeCheck : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly string? _shot;
    private FootPlayer? _player;
    private double _t;
    private int _phase;
    private Vector3 _tree, _dir;
    private float _radius, _closest = float.MaxValue, _impact, _topSpeed;

    public TreeCheck(ChunkManager chunks, WorldOrigin origin, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--treecheck");

    public override void _PhysicsProcess(double delta)
    {
        _t += delta;
        if (_t > 150) { GD.Print("[treecheck] TIMEOUT"); Finish(2); return; }
        var pool = TreeColliders.Instance;

        switch (_phase)
        {
            case 0:   // a player on the ground at the spawn
            {
                var (e, n) = SpawnPoint.ParseTarget();
                var at = _origin.ToWorld(e, n, 0);
                if (!_chunks.TryGetHeight(at, out float g)) return;
                _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
                AddChild(_player);
                _player.GlobalPosition = new Vector3(at.X, g + 1.5f, at.Z);
                _player.Impacted += lost => _impact += lost;   // reported a slice per frame
                _phase = 1;
                _t = 0;
                return;
            }
            case 1:   // the pool fills in around it; pick a tree to hit
            {
                if (pool == null || _t < 3 || pool.LiveTrunks == 0 || !_player!.IsOnFloor()) return;
                var me = _player.GlobalPosition;
                for (float min = 20f; min < 40f; min += 1f)
                {
                    if (pool.NearestLive(me, min) is not { } t) break;
                    float d = MathX.Flat(t.Base - me).Length();
                    if (d > 40f) break;
                    var dir = MathX.Flat(t.Base - me).Normalized();
                    var start = t.Base - dir * 25f;
                    if (!_chunks.TryGetHeight(start, out float gs) || Mathf.Abs(gs - t.Base.Y) > 3f) continue;
                    if (Blocked(pool, start, t.Base, dir)) continue;
                    _tree = t.Base; _radius = t.Radius; _dir = dir;
                    _player.GlobalPosition = start with { Y = gs + 1.2f };
                    _player.Rotation = new Vector3(0, Mathf.Atan2(-dir.X, -dir.Z), 0);
                    _player.Velocity = Vector3.Zero;
                    GD.Print($"[treecheck] {pool.LiveTrunks} trunks live, pool {pool.LastMs:F2} ms this frame "
                        + $"(max {pool.MaxMs:F2}); riding at a trunk r={_radius:F2} m, 25 m ahead, {Mathf.Abs(gs - t.Base.Y):F1} m climb");
                    pool.MaxMs = 0;   // from here on: steady state while riding, not the first fill
                    _phase = 2;
                    _t = 0;
                    return;
                }
                if (_t > 20) { GD.Print("[treecheck] no rideable tree 20-40 m from the spawn; try another --at"); Finish(1); }
                return;
            }
            case 2:   // mount once settled
                if (_t < 0.5 || !_player!.IsOnFloor()) return;
                if (!_player.SetRide(_kind)) { GD.Print($"[treecheck] MOUNT REFUSED for {_kind}"); Finish(1); return; }
                GD.Print($"[treecheck] {_kind} at the trunk");
                // an aircraft is put in the air a few metres up and flown straight at the trunk
                if (_player.IsFlying)
                    _player.DebugLaunch(_player.GlobalPosition + Vector3.Up * 4f, _dir * (_kind == RideKind.Plane ? 32f : 14f));
                _player.RideControls = () =>
                {
                    // hold the line at the trunk
                    var to = MathX.Flat(_tree - _player.GlobalPosition);
                    var fwd = MathX.Flat(-_player.GlobalBasis.Z);
                    float err = Mathf.Atan2(fwd.Z * to.X - fwd.X * to.Z, fwd.Dot(to));
                    return new RideInput(1f, 0f, Mathf.Clamp(-err * 3f, -1f, 1f), true);
                };
                _phase = 3;
                _t = 0;
                return;
            case 3:   // ride at it
            {
                // a helicopter hovers hands-off: hold the stick forward, as a pilot would
                if (_kind == RideKind.Helicopter)
                {
                    // hovers hands-off and settles without collective: stick forward, and hold
                    // about 4 m of height with the collective (Space), as a pilot would
                    Input.ActionPress(Core.PlayerInput.MoveForward);
                    bool low = !_chunks.TryGetHeight(_player!.GlobalPosition, out float gh) || _player.GlobalPosition.Y < gh + 4f;
                    if (low) Input.ActionPress(Core.PlayerInput.Jump); else Input.ActionRelease(Core.PlayerInput.Jump);
                }
                if (CmdArgs.Has("--trace") && (int)(_t * 2) != (int)((_t - delta) * 2))
                    GD.Print($"[treecheck]   t={_t:F1} d={MathX.Flat(_player!.GlobalPosition - _tree).Length():F1} v={_player.Velocity} floor={_player.IsOnFloor()} wall={_player.IsOnWall()} ride={_player.Ride}");
                var p = _player!.GlobalPosition;
                _closest = Mathf.Min(_closest, MathX.Flat(p - _tree).Length());
                _topSpeed = Mathf.Max(_topSpeed, _player.GroundSpeed);
                if (_t < 9) return;
                float past = MathX.Flat(p - _tree).Dot(_dir);   // > 0 once beyond the trunk
                bool through = past > _radius + 0.3f;
                // a crash that ends the ride (thrown off, wreck) is the tree stopping it too
                bool crashed = _player.Ride != _kind;
                bool ok = !through && (_impact > 3f || crashed) && _closest > _radius + 0.1f;
                GD.Print($"[treecheck] top {_topSpeed * 3.6f:F0} km/h, closest {_closest:F2} m to the axis (trunk r {_radius:F2}), "
                    + $"{(through ? "WENT THROUGH" : "stopped short")}{(crashed ? " (crashed)" : "")}, impacts {_impact * 3.6f:F0} km/h in total, "
                    + $"{pool!.LiveTrunks} trunks live, pool max {pool.MaxMs:F2} ms/frame");
                GD.Print(ok ? "[treecheck] RESULT: the tree stopped the rider" : "[treecheck] RESULT: FAILED");
                if (_shot != null && GetViewport().GetTexture().GetImage().SavePng(_shot) == Error.Ok)
                    GD.Print($"[treecheck] wrote {_shot}");
                Finish(ok ? 0 : 1);
                return;
            }
        }
    }

    /// <summary>Another trunk within 1.5 m of the line would be hit first.</summary>
    private static bool Blocked(TreeColliders pool, Vector3 from, Vector3 tree, Vector3 dir)
    {
        for (float s = 2f; s < 24f; s += 2f)
            if (pool.NearestLive(from + dir * s) is { } o && MathX.Flat(o.Base - tree).Length() > 0.5f
                && MathX.Flat(o.Base - (from + dir * s)).Length() < 1.5f)
                return true;
        return false;
    }


    /// <summary><c>--ride bike|car|heli|plane</c>: what to throw at the trunk (bike by default).</summary>
    private static RideKind Kind()
    {
        var args = CmdArgs.All;
        int i = System.Array.IndexOf(args, "--ride");
        string v = i >= 0 && i + 1 < args.Length ? args[i + 1] : "bike";
        return v switch
        {
            "car" => (RideKind)Player.CarCatalog.First,
            "heli" or "helicopter" => RideKind.Helicopter,
            "plane" => RideKind.Plane,
            _ => RideKind.RoadBike,
        };
    }

    private readonly RideKind _kind = Kind();

    private void Finish(int code)
    {
        SetPhysicsProcess(false);
        GetTree().Quit(code);
    }
}
