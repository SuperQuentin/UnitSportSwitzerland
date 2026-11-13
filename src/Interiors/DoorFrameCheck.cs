using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --path . -- --frame earth --doorframecheck[,out_dir] --at E,N</c> (#1799): the doors round the start, opened
/// through the real <see cref="InteriorManager"/> (offline), against the building they are in. Far from Switzerland an
/// earth leaf's stand-in tile is turned a lot against the world (LV95's grid north is not the world's there), and a
/// door's link took the facade's outward direction straight from the tile frame: the doorway, its leaves and the
/// interior seen through the portal stood turned against the walls. For each door (up to <c>--doorframecount</c>, 4):
/// <list type="bullet">
/// <item>the link's facade doorway faces the way the door index's door does (the building mesh's frame), within 0.5°,
/// and is up the tile's up within 0.5°;</item>
/// <item>its sill is the door's, 3 cm in, within 5 cm;</item>
/// <item>the interior node's up is the building's (the tile's) within 0.5°, and the interior doorway faces out of the
/// plan the way the door does within 6° (the plan box's own small turn);</item>
/// <item>the built leaf stands up its interior doorway's up within 0.5°.</item>
/// </list>
/// With an out dir (windowed), a picture of each door from 7 m out in front, open. RESULT ok or FAILED.
/// </summary>
public partial class DoorFrameCheck : Node3D
{
    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--doorframecheck");

    private const float AngleBar = 0.5f, PlanTurnBar = 6f, SillBar = 0.05f;
    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly string? _dir;
    private readonly int _count = CmdArgs.Float("--doorframecount") is float c && c > 0 ? (int)c : 4;
    private readonly List<string> _faults = new();
    private readonly List<DoorIndex.Entry> _todo = new();
    private Camera3D? _cam;
    private double _t, _phaseT;
    private int _phase, _checked;
    private string? _door;

    public DoorFrameCheck(IWorldGround ground, WorldOrigin origin, string? shot)
    {
        _ground = ground;
        _origin = origin;
        _dir = string.IsNullOrEmpty(shot) ? null : shot;
        Name = "DoorFrameCheck";
    }

    public override void _Ready()
    {
        var (e, n) = SpawnPoint.ParseTarget();
        Position = _origin.ToWorld(e, n, 0);
        _ground.AddAnchor(this, collision: true);
        _origin.Shifted += OnShifted;
        AddChild(_cam = new Camera3D { Fov = 70, Far = 3000 });
        _cam.MakeCurrent();
        if (_dir != null) System.IO.Directory.CreateDirectory(_dir);
        Log($"start LV95 {e:F0}/{n:F0}, origin {(_origin.Frame.IsAnchor ? "an anchor frame" : "FLAT")}");
        if (_to != null) { _phase = 5; Log($"then on to LV95 {_to.Value.E:F0}/{_to.Value.N:F0}"); }
        else if (_toGeo != null) { _phase = 5; Log($"then on to lat {_toGeo.Value.E:F3}, lon {_toGeo.Value.N:F3}"); }
    }

    /// <summary>
    /// <c>--doorframeto E,N</c>: start at <c>--at</c>, then fly there as the spectator does, along the great circle 200 km
    /// up, a step a frame, down to 3 km over it. The world's axes are carried along by every re-anchor (the least
    /// rotation): far from the start they are turned against the place's own north, which is how a player meets a turned
    /// leaf. Started at a far place, the world's axes are that place's own, and nothing shows.
    /// </summary>
    private readonly (double E, double N)? _to = SpawnPoint.ParseLv95("--doorframeto");
    /// <summary><c>--doorframetogeo lat,lon</c>: the same, to a place named by WGS84 degrees (where LV95 means nothing).</summary>
    private readonly (double E, double N)? _toGeo = SpawnPoint.ParseLv95("--doorframetogeo");
    private int _shifts, _step = -1, _steps;
    private Geodetic _from, _dest;

    private static (double X, double Y, double Z) Unit(Geodetic g)
    {
        double la = g.LatDeg * Math.PI / 180, lo = g.LonDeg * Math.PI / 180;
        return (Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la));
    }

    private void Travel()
    {
        if (_step < 0)
        {
            if (!_ground.TryGetHeight(Position, out float g) || _phaseT < 5) return;
            _from = _origin.ToGlobal(Position).Geodetic;
            _dest = _to is { } to ? GlobalPos.FromLv95(to.E, to.N, 3000).Geodetic : new Geodetic(_toGeo!.Value.E, _toGeo.Value.N, 3000);
            var (ax, ay, az) = Unit(_from);
            var (bx, by, bz) = Unit(_dest);
            double arc = Math.Acos(Math.Clamp(ax * bx + ay * by + az * bz, -1, 1)) * 6_371_000;
            _steps = (int)Math.Ceiling(arc / 8000) + 1;   // 8 km a frame
            _step = 0;
            _shifts = 0;
            Log($"flying {arc / 1000:F0} km in {_steps} steps");
            return;
        }
        if (_step <= _steps)
        {
            double t = (double)_step / _steps;
            var (ax, ay, az) = Unit(_from);
            var (bx, by, bz) = Unit(_dest);
            double om = Math.Acos(Math.Clamp(ax * bx + ay * by + az * bz, -1, 1));
            double wa = om < 1e-9 ? 1 - t : Math.Sin((1 - t) * om) / Math.Sin(om), wb = om < 1e-9 ? t : Math.Sin(t * om) / Math.Sin(om);
            double x = wa * ax + wb * bx, y = wa * ay + wb * by, z = wa * az + wb * bz;
            double lat = Math.Atan2(z, Math.Sqrt(x * x + y * y)) * 180 / Math.PI, lon = Math.Atan2(y, x) * 180 / Math.PI;
            // up to 200 km over the first 5 %, down over the last 5 %
            double h = 3000 + 197_000 * Math.Min(1, Math.Min(t, 1 - t) / 0.05);
            Position = _origin.ToWorld(GlobalPos.FromEcef(Ellipsoid.Wgs84.ToEcef(new Geodetic(lat, lon, h))));
            _cam!.Position = Vector3.Zero;
            _step++;
            _phaseT = 0;
            return;
        }
        if (!_ground.TryGetHeight(Position, out _)) return;
        var (e, n) = _origin.ToLv95(Position);
        var geo = _origin.ToGlobal(Position).Geodetic;
        Log($"arrived over LV95 {e:F0}/{n:F0} (lat {geo.LatDeg:F2}, lon {geo.LonDeg:F2}) after {_shifts} re-anchors");
        _phase = 0;
        _phaseT = 0;
    }

    public override void _ExitTree()
    {
        _origin.Shifted -= OnShifted;
        _ground.RemoveAnchor(this);
    }

    private void OnShifted(OriginShift shift)
    {
        _shifts++;
        // this node is moved with the world by the shifter itself; the door entries kept here are copies
        for (int i = 0; i < _todo.Count; i++)
            _todo[i] = _todo[i] with { World = shift.Point(_todo[i].World), Outward = shift.Direction(_todo[i].Outward) };
    }

    public override void _Process(double delta)
    {
        _t += delta;
        _phaseT += delta;
        if (_t > 1000) { _faults.Add($"timed out in phase {_phase}"); Finish(); return; }
        switch (_phase)
        {
            case 0: Pick(); break;
            case 5: Travel(); break;
            case 1: Open(); break;
            case 2: Measure(); break;
            case 3: Shoot(); break;
        }
    }

    /// <summary>The nearest street doors round the start, of different buildings, once they are drawn.</summary>
    private double _said, _asked;
    private bool _moved;

    private void Pick()
    {
        bool known = _ground.TryGetHeight(Position, out float g);
        if (_t - _said > 15)
        {
            _said = _t;
            var all = DoorIndex.All().ToList();
            Log($"waiting: ground {(known ? $"at {g:F0}" : "unknown")}, {all.Count} doors drawn, nearest "
                + (all.Count == 0 ? "none" : $"{all.Min(d => d.World.DistanceTo(Position)):F0} m"));
            if (all.Count > 0)
            {
                var near = all.MinBy(d => d.World.DistanceTo(Position));
                var (ne, nn) = _origin.ToLv95(near.World);
                var (pe, pn) = _origin.ToLv95(Position);
                Log($"  nearest {near.Key} at world {near.World} LV95 {ne:F0},{nn:F0}; the probe at {Position} LV95 {pe:F0},{pn:F0}; "
                    + $"its tile's transform says {_origin.TileTransform(near.Key.Tile).Origin}");
            }
        }
        if (!known) return;
        // the start on the ground: an altitude of 0 is the height of the anchor plane, metres under it
        if (Mathf.Abs(Position.Y - g) > 1) Position = Position with { Y = g };
        if (_cam!.GlobalPosition.DistanceTo(Position) > 50) _cam.GlobalPosition = Position with { Y = g + 30 };
        var doors = DoorIndex.All().Where(d => d.World.DistanceTo(Position) < 500 && d.Level == 0
                && d.Kind != Terrain.Format.BuildingKind.UnderConstruction)
            .OrderBy(d => d.World.DistanceTo(Position)).ToList();
        // let the leaves round the start be furnished: a few seconds after the first doors show
        // no village here: after a while, on to the nearest door drawn within 3 km (a far place picked by degrees)
        if (doors.Count == 0 && _phaseT > 40 && !_moved && DoorIndex.All().Where(d => d.World.DistanceTo(Position) < 3000)
                .OrderBy(d => d.World.DistanceTo(Position)).FirstOrDefault() is { Width: > 0 } village)
        {
            _moved = true;
            Log($"no door within 500 m: on to {village.Key}, {village.World.DistanceTo(Position):F0} m");
            Position = village.World;
            _phaseT = 0;
            return;
        }
        if (doors.Count == 0 || _phaseT < 20) { if (doors.Count == 0 && _moved) _phaseT = 0; return; }
        var seen = new HashSet<string>();
        // a vehicle door (garage, barn) among them when there is one: its leaves stand on the facade
        if (doors.FirstOrDefault(d => d.Vehicle) is { Width: > 0 } v) { _todo.Add(v); seen.Add(v.Key.Building.ToString()); }
        foreach (var d in doors)
        {
            if (_todo.Count >= _count) break;
            if (seen.Add(d.Key.Building.ToString())) _todo.Add(d);
        }
        Log($"{doors.Count} doors within 500 m; checking {_todo.Count}: {string.Join(", ", _todo.Select(d => $"{d.Key} {d.Kind}{(d.Vehicle ? " vehicle" : "")}"))}");
        Next();
    }

    private void Next()
    {
        if (_checked >= _todo.Count) { Finish(); return; }
        var d = _todo[_checked];
        // the free camera stands in front of the door, so the manager builds it (offline, the camera's doors): no
        // player, whose body may be at the start, thousands of km back
        if (InteriorManager.Instance is { } im) im.LocalPlayer = () => null;
        var (eye, at) = Pose(d, 0);
        _cam!.GlobalPosition = eye;
        _cam.LookAt(at, Up(d));
        _door = InteriorManager.Instance?.OpenDoorForCamera(d.Key.ToString());
        if (_door == null) { _faults.Add($"{d.Key}: the interior manager would not open it"); _checked++; Next(); return; }
        _phase = 1;
        _phaseT = 0;
    }

    private Vector3 Up(DoorIndex.Entry d) => _origin.TileTransform(d.Key.Tile).Basis.Y.Normalized();

    private void Open()
    {
        var link = InteriorManager.Instance!.BuiltLink(_door!);
        if (link == null || link.Swing < 0.999f)
        {
            // the ask expires after 3 s while the plan is still being made, and an open door nobody stands at shuts
            // after a minute: ask again while it is shut
            if (_t - _asked > 10 && !InteriorManager.Instance!.IsOpen(_door!))
            {
                _asked = _t;
                InteriorManager.Instance.OpenDoorForCamera(_door!);
            }
            if (_t - _said > 15)
            {
                _said = _t;
                var im = InteriorManager.Instance!;
                var plan = im.GetOrCreate(_door!);
                Log($"  {_door}: open {im.IsOpen(_door!)}, plan {(plan.IsCompleted ? (plan.Result == null ? "none" : "ready") : "pending")}, "
                    + $"link {(link == null ? "none" : $"swing {link.Swing:F2}")}, built node {(link != null && im.BuiltNode(link.Plan) != null)}, "
                    + $"{System.Threading.ThreadPool.PendingWorkItemCount} work items queued, camera {GetViewport().GetCamera3D()?.Name} at {GetViewport().GetCamera3D()?.GlobalPosition}");
            }
            // after a long flight the workers still have the leaves along the way queued: plans wait behind them
            if (_phaseT > 240) { _faults.Add($"{_door}: not built and open after 240 s"); _checked++; Next(); }
            return;
        }
        _phase = 2;
    }

    private static float Deg(Vector3 a, Vector3 b) => Mathf.RadToDeg(a.Normalized().AngleTo(b.Normalized()));

    private void Measure()
    {
        var d = _todo[_checked];
        var im = InteriorManager.Instance!;
        var link = im.BuiltLink(_door!)!;
        var up = Up(d);
        var bad = new List<string>();
        float face = Deg(link.Outside.Basis.Z, d.Outward);
        float tilt = Deg(link.Outside.Basis.Y, up);
        float sill = link.Outside.Origin.DistanceTo(d.World - d.Outward * 0.03f);
        if (!(face < AngleBar)) bad.Add($"the facade doorway faces {face:F1}° off its wall");
        if (!(tilt < AngleBar)) bad.Add($"the facade doorway's up is {tilt:F1}° off the building's");
        if (!(sill < SillBar)) bad.Add($"the facade doorway's sill is {sill * 100:F0} cm off the door's");
        float inTilt = float.NaN, planTurn = float.NaN, leafTilt = float.NaN;
        if (im.BuiltNode(link.Plan) is { } node)
        {
            inTilt = Deg(node.GlobalTransform.Basis.Y, up);
            if (!(inTilt < AngleBar)) bad.Add($"the interior's up is {inTilt:F1}° off the building's");
        }
        else bad.Add("no interior node");
        // the interior doorway's Z is out of the plan box, placed by the plan's own transform (not through the link):
        // under the building in the same world axes, it faces the door's way but for the plan box's small turn
        planTurn = Deg(link.Inside.Basis.Z, d.Outward);
        if (!(planTurn < PlanTurnBar)) bad.Add($"the interior doorway faces {planTurn:F1}° off the door (plan box turn bar {PlanTurnBar}°)");
        if (im.BuiltNode(link.Plan)?.Leaf(_door!) is { } leaf)
        {
            leafTilt = Deg(leaf.GlobalTransform.Basis.Y, link.Inside.Basis.Y);
            if (!(leafTilt < AngleBar)) bad.Add($"the leaf's up is {leafTilt:F1}° off its doorway's");
        }
        Log($"{d.Key} {d.Kind}{(d.Vehicle ? " vehicle" : "")} hang {d.Hang}: facade doorway {face:F2}° off the wall, up {tilt:F2}°, sill {sill * 100:F1} cm; "
            + $"interior up {inTilt:F2}°, its doorway {planTurn:F2}° off the door; leaf up {leafTilt:F2}° -> {(bad.Count == 0 ? "ok" : string.Join("; ", bad))}");
        foreach (var b in bad) _faults.Add($"{d.Key}: {b}");
        _phase = 3;
        _phaseT = 0;
    }

    private int _pose;

    /// <summary>
    /// The pictures of a door: from 7 m out in front, open (its leaves, the doorway and the rooms through it); from
    /// 2.5 m out and 4 m along the wall, looking along it at the windows beside the door (the fake rooms behind the
    /// glass, #1799); and from 1.5 m out under the eave looking up at it (the roof seen from below).
    /// </summary>
    private (Vector3 Eye, Vector3 At) Pose(DoorIndex.Entry d, int pose)
    {
        var up = Up(d);
        var along = up.Cross(d.Outward).Normalized();
        return pose switch
        {
            0 => (d.World + d.Outward * 7f + up * 1.7f, d.World + up * 1.2f),
            1 => (d.World + d.Outward * 2.5f - along * 4f + up * 1.6f, d.World + along * 3f + up * 2.6f),
            _ => (d.World + d.Outward * 1.5f - along * 2f + up * 1.5f, d.World + along * 1f - d.Outward * 1f + up * 7f),
        };
    }

    private static readonly string[] PoseNames = ["front", "along", "up"];

    private void Shoot()
    {
        if (_dir == null || DisplayServer.GetName() == "headless") { _checked++; Next(); return; }
        var d = _todo[_checked];
        if (_phaseT < 1.5) return;
        string path = System.IO.Path.Combine(_dir, $"door_{_checked}_{d.Key}_{PoseNames[_pose]}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"shot {path}");
        if (++_pose < PoseNames.Length)
        {
            var (eye, at) = Pose(d, _pose);
            _cam!.GlobalPosition = eye;
            _cam.LookAt(at, Up(d));
            _phaseT = 0;
            return;
        }
        _pose = 0;
        _checked++;
        Next();
    }

    private void Finish()
    {
        _phase = -1;
        SetProcess(false);
        if (_todo.Count == 0) _faults.Add("no door checked");
        foreach (var f in _faults) Log("FAIL " + f);
        Log(_faults.Count == 0 ? $"RESULT: ok ({_todo.Count} doors)" : $"RESULT: FAILED ({_faults.Count})");
        GetTree().Quit(_faults.Count == 0 ? 0 : 1);
    }

    private static void Log(string s) => GD.Print($"[doorframecheck] {s}");
}
