using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

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
    }

    public override void _ExitTree()
    {
        _origin.Shifted -= OnShifted;
        _ground.RemoveAnchor(this);
    }

    private void OnShifted(OriginShift shift)
    {
        Position = shift.Point(Position);
        for (int i = 0; i < _todo.Count; i++)
            _todo[i] = _todo[i] with { World = shift.Point(_todo[i].World), Outward = shift.Direction(_todo[i].Outward) };
    }

    public override void _Process(double delta)
    {
        _t += delta;
        _phaseT += delta;
        if (_t > 600) { _faults.Add($"timed out in phase {_phase}"); Finish(); return; }
        switch (_phase)
        {
            case 0: Pick(); break;
            case 1: Open(); break;
            case 2: Measure(); break;
            case 3: Shoot(); break;
        }
    }

    /// <summary>The nearest street doors round the start, of different buildings, once they are drawn.</summary>
    private void Pick()
    {
        if (!_ground.TryGetHeight(Position, out float g)) return;
        if (_cam!.GlobalPosition.DistanceTo(Position) > 50) _cam.GlobalPosition = Position with { Y = g + 30 };
        var doors = DoorIndex.All().Where(d => d.World.DistanceTo(Position) < 500 && d.Level == 0
                && d.Kind != Terrain.Format.BuildingKind.UnderConstruction)
            .OrderBy(d => d.World.DistanceTo(Position)).ToList();
        // let the leaves round the start be furnished: a few seconds after the first doors show
        if (doors.Count == 0 || _phaseT < 20) { if (doors.Count == 0) _phaseT = 0; return; }
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
        // the free camera stands in front of the door, so the manager builds it (offline, the camera's doors)
        _cam!.GlobalPosition = d.World + d.Outward * 7f + Up(d) * 1.7f;
        _cam.LookAt(d.World + Up(d) * 1.2f, Up(d));
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
            if (_phaseT > 90) { _faults.Add($"{_door}: not built and open after 90 s"); _checked++; Next(); }
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

    private void Shoot()
    {
        if (_dir == null || DisplayServer.GetName() == "headless") { _checked++; Next(); return; }
        if (_phaseT < 1.5) return;
        var d = _todo[_checked];
        string path = System.IO.Path.Combine(_dir, $"door_{_checked}_{d.Key}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"shot {path}");
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
