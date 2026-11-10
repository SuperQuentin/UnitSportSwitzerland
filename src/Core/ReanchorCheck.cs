using System.Collections.Generic;
using System.Globalization;
using Godot;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// <c>godot --path . -- --frame earth --reanchorcheck [--originshift m] [--reanchortravel m] [--at E,N]</c> (#1710, G2 D0;
/// plan <c>docs/plans/global-tiling-g2-d.md</c> section 5, "no pop at any re-anchor"): a camera driven along the ground,
/// then flown 300 m over it, back and forth across the origin's re-anchors, with things at rest near the start. At every
/// re-anchor (<see cref="ReanchorWatch"/>) the camera's Earth track runs on, the ground under it is where it was, and the
/// props stay put: four boxes asleep on the ground (rigid bodies, the shifter's teleport) and a plain marker (a node
/// moved by the shift). Windowed, every frame round a re-anchor is compared with its neighbours: no spike in the picture.
/// <para>
/// The quick tier runs it on a fixture course (its longest reach, <c>--originshift 400</c>: a re-anchor every 400 m);
/// <c>tools/reanchorcheck.sh</c> on the D: region's leaves, windowed. RESULT ok or FAILED.
/// </para>
/// </summary>
public partial class ReanchorCheck : Node
{
    public static bool Requested() => CmdArgs.Has("--reanchorcheck");

    /// <summary>The run's line (world, level): north-east by default, a fixture course's longest reach (ClientWorld).</summary>
    public Vector3 Direction { get; init; } = new Vector3(1, 0, -1).Normalized();
    /// <summary>One leg, metres: the camera turns back at each end (where the leaves are known to be).</summary>
    public float LegM { get; init; } = 3000f;
    /// <summary>How far each phase goes (drive, then fly), metres.</summary>
    public float TravelM { get; init; } = CmdArgs.Float("--reanchortravel") is float m && m > 0 ? m : 6000f;
    public float DriveSpeed { get; init; } = 30f;
    public float FlySpeed { get; init; } = 80f;
    public const float EyeM = 1.6f, FlyM = 300f;

    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly List<string> _faults = new();
    private readonly List<RigidBody3D> _boxes = new();
    private readonly List<GlobalPos> _restAt = new();
    private Node3D _rover = null!, _marker = null!;
    private GlobalPos _markerAt;
    private Camera3D _camera = null!;
    private ReanchorWatch _watch = null!;
    private Vector3 _dir, _axis;
    private int _phase, _seen;
    private float _travelled, _along, _height;
    private bool _turning, _turnOut;
    /// <summary>The turn at each end of a leg, metres: a car's U-turn, never a reversal on the spot (one tick's whole step would read as a jump).</summary>
    public const float TurnRadiusM = 100f;
    private double _phaseS, _missingS;
    private ulong _startMs;

    public ReanchorCheck(IWorldGround ground, WorldOrigin origin)
    {
        _ground = ground;
        _origin = origin;
        Name = "ReanchorCheck";
    }

    private double Now => (Time.GetTicksMsec() - _startMs) / 1000.0;
    private double TimeoutS => 180 + TravelM / DriveSpeed + TravelM / FlySpeed;

    public override void _Ready()
    {
        _startMs = Time.GetTicksMsec();
        var (e, n) = SpawnPoint.ParseTarget();
        _dir = Direction with { Y = 0 };
        _dir = _dir.Normalized();
        _axis = _dir;
        _rover = new Node3D { Name = "Rover" };
        AddChild(_rover);
        _rover.GlobalPosition = _origin.ToWorld(e, n, 0);
        _ground.AddAnchor(_rover, collision: true);
        _rover.AddChild(_camera = new Camera3D { Name = "Eye", Far = 30_000, Current = true });
        _camera.Rotation = new Vector3(-0.12f, 0, 0);
        _camera.MakeCurrent();
        _origin.Shifted += OnShifted;
        bool windowed = DisplayServer.GetName() != "headless";
        // a jump of d reads as d/2 in the watch's midpoint error: 4 cm is an 8 cm pop
        _watch = new ReanchorWatch(_origin, _ground, "reanchor") { Shots = windowed, JumpTolM = 0.04f };
        AddChild(_watch);
        Log($"start LV95 {e:F0}/{n:F0}, origin {(_origin.Frame.IsAnchor ? "an anchor frame" : "FLAT")}, {(windowed ? "windowed: the picture judged too" : "headless")}");
        if (!_origin.Frame.IsAnchor) Fault("the world is not an anchor frame: run with --frame earth");
    }

    public override void _ExitTree()
    {
        _origin.Shifted -= OnShifted;
        _ground.RemoveAnchor(_rover);
        foreach (var b in _boxes) _ground.RemoveAnchor(b);
    }

    // the heading is a world direction: it turns with the world, kept level
    private void OnShifted(OriginShift shift)
    {
        // --reanchorinject <m>: a pop on purpose at the 3rd re-anchor (the marker and the camera put that far aside), to
        // show the check catches one (#1710); never in a map row
        if (++_seen == 3 && CmdArgs.Float("--reanchorinject") is float pop && _marker != null)
        {
            var aside = _dir.Cross(Vector3.Up) * pop;
            _marker.GlobalPosition += aside;
            _rover.GlobalPosition += aside;
            Log($"injected a {pop:F2} m pop at re-anchor 3");
        }
        _dir = shift.Direction(_dir) with { Y = 0 };
        if (_dir != Vector3.Zero) _dir = _dir.Normalized();
        _axis = shift.Direction(_axis) with { Y = 0 };
        if (_axis != Vector3.Zero) _axis = _axis.Normalized();
    }

    // on the physics tick, as a vehicle moves (ReanchorWatch samples there)
    public override void _PhysicsProcess(double delta)
    {
        if (!_camera.Current) _camera.MakeCurrent();
        if (Now > TimeoutS) { Fault($"timed out in phase {_phase}"); Finish(); return; }
        switch (_phase)
        {
            case 0: WaitForGround(); break;
            case 1: Settle(); break;
            case 2: Move((float)delta, fly: false); break;
            case 3: Move((float)delta, fly: true); break;
        }
    }

    private void WaitForGround()
    {
        var p = _rover.GlobalPosition;
        if (!_ground.HasCollisionAt(p) || !_ground.TryGetHeight(p, out float h)) return;
        _rover.GlobalPosition = p with { Y = h + EyeM };
        Log($"ground under the start at {Now:F1} s: {h:F2} m");
        // the props: four boxes dropped beside the line within 60 m of the start, a marker on the ground at it
        var side = _dir.Cross(Vector3.Up);
        for (int k = 0; k < 4; k++)
        {
            var at = p + _dir * (15f + 15f * k) + side * (k % 2 == 0 ? 6f : -6f);
            if (!_ground.TryGetHeight(at, out float g)) continue;
            var box = new RigidBody3D { Name = $"Box{k}", Mass = 20f, CanSleep = true };
            box.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
            box.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One } });
            AddChild(box);
            box.GlobalPosition = at with { Y = g + 0.6f };
            _ground.AddAnchor(box, collision: true);
            _boxes.Add(box);
        }
        _marker = new Node3D { Name = "Marker" };
        _marker.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 3f } });
        AddChild(_marker);
        _marker.GlobalPosition = p with { Y = h + 1.5f };
        _phase = 1;
        _phaseS = Now;
    }

    private void Settle()
    {
        // asleep, or 8 s on: a box on a slope may creep, and then it is judged by its own noise
        bool still = _boxes.TrueForAll(b => b.Sleeping || b.LinearVelocity.Length() < 0.02f);
        if (!still && Now - _phaseS < 8) return;
        _restAt.Clear();
        foreach (var b in _boxes) _restAt.Add(_origin.ToGlobal(b.GlobalPosition));
        _markerAt = _origin.ToGlobal(_marker.GlobalPosition);
        _watch.Watch("camera", () => _camera);
        _watch.Watch("marker", () => _marker);
        for (int k = 0; k < _boxes.Count; k++) { var b = _boxes[k]; _watch.Watch($"box{k}", () => b); }
        _watch.WatchGround(() => _rover.GlobalPosition);
        Log($"{_boxes.Count} boxes at rest ({(still ? "asleep" : "still moving after 8 s")}); driving {TravelM:F0} m at {DriveSpeed:F0} m/s, legs of {LegM:F0} m");
        _phase = 2;
        _phaseS = Now;
        _travelled = 0;
    }

    private void Move(float dt, bool fly)
    {
        float speed = fly ? FlySpeed : DriveSpeed;
        float step = speed * dt;
        // back at each end of the leg: the leaves along it are known to be there
        // an oval: out along the leg, a left U-turn of TurnRadiusM, back beside it, a left U-turn, out again
        float out_ = _dir.Dot(_axis);
        if (!_turning && ((_along > LegM && out_ > 0) || (_along < 0 && out_ < 0)))
        {
            _turning = true;
            _turnOut = out_ > 0;
        }
        if (_turning)
        {
            // the way back, fixed when the turn began (the axis turns with a re-anchor)
            var target = _turnOut ? -_axis : _axis;
            float turn = step / TurnRadiusM;
            if (_dir.AngleTo(target) <= turn) { _dir = target; _turning = false; }
            else _dir = _dir.Rotated(Vector3.Up, turn).Normalized();
        }
        _along += step * _dir.Dot(_axis);
        var next = _rover.GlobalPosition + _dir * step;
        if (_ground.TryGetHeight(next, out float h))
        {
            _missingS = 0;
            float want = fly ? FlyM : EyeM;
            // a climb into the flight over 10 s, then level
            _height = fly ? Mathf.MoveToward(_height, want, FlyM / 10f * dt) : want;
            next.Y = h + _height;
        }
        else if ((_missingS += dt) > 2.0 && _faults.Count < 20)
        {
            Fault($"no ground under the camera for 2 s at {_travelled:F0} m ({(fly ? "flying" : "driving")})");
            _missingS = double.NegativeInfinity;
        }
        _rover.GlobalPosition = next;
        var look = _dir + Vector3.Down * (fly ? 0.35f : 0.05f);
        if (look.LengthSquared() > 1e-4f) _rover.LookAt(_rover.GlobalPosition + look, Vector3.Up);
        _travelled += step;
        if (_travelled < TravelM) return;
        if (!fly)
        {
            Log($"drove {_travelled:F0} m in {Now - _phaseS:F0} s, {_watch.Shifts} re-anchors so far; flying {TravelM:F0} m at {FlySpeed:F0} m/s, {FlyM:F0} m up");
            _phase = 3;
            _phaseS = Now;
            _travelled = 0;
            _height = EyeM;
            return;
        }
        Finish();
    }


    private void Finish()
    {
        _phase = -1;
        // the props at rest: where they were, by their Earth positions, after every re-anchor
        for (int k = 0; k < _boxes.Count && k < _restAt.Count; k++)
        {
            double moved = _origin.ToGlobal(_boxes[k].GlobalPosition).DistanceTo(_restAt[k]);
            Log($"box{k}: {moved * 100:F1} cm from where it came to rest, {_boxes[k].LinearVelocity.Length():F2} m/s");
            if (moved > 0.05) Fault($"box{k} moved {moved * 100:F0} cm over the run (at rest, across {_watch.Shifts} re-anchors)");
        }
        if (_marker != null)
        {
            double m = _origin.ToGlobal(_marker.GlobalPosition).DistanceTo(_markerAt);
            Log($"marker: {m * 100:F2} cm from where it stood");
            if (m > 0.01) Fault($"the marker moved {m * 100:F1} cm over the run");
        }
        _watch.Judge();
        Log(_watch.Summary().Replace("[reanchor] ", ""));
        foreach (var f in _watch.Faults) Fault(f);
        if (_watch.Shifts < 3) Fault($"only {_watch.Shifts} re-anchors: the run must cross several (--originshift)");
        Log(_faults.Count == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_faults.Count}: {_faults[0]})");
        GetTree().Quit(_faults.Count == 0 ? 0 : 1);
    }

    // logged as it happens: a run stopped by the RAM floor or a timeout still says what it found
    private void Fault(string f)
    {
        _faults.Add(f);
        Log($"FAULT: {f}");
    }

    private static void Log(string s) => GD.Print("[reanchorcheck] " + s);
}
