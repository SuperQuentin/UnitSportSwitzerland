using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Earth;

/// <summary>
/// <c>godot --headless --path . -- --frame earth --earthcheck --leaves &lt;dir&gt; --at E,N</c> (#1510, G2 C1 PR 1): the
/// earth world on a region's real leaves. Waits for the ground under the start, then:
/// <list type="number">
/// <item>collision and the height query agree: over a 21 × 21 grid 20 m apart, a ray down onto the patches lands
/// within 5 cm of <see cref="IWorldGround.TryGetHeight"/> or on a feature over it (a roof, a bridge), under it at
/// most 1 % of the rays (road holes), and at least 100 reach the ground;</item>
/// <item>a ball dropped 3 m stands on the ground 3 s later (no fall through a patch);</item>
/// <item>the ground streams ahead of a body at 30 m/s: moving 4.5 km along a line (the camera with it, so the origin
/// re-anchors past 3 km), the ground under it is known within 2 s of its arrival, nowhere missing longer;</item>
/// <item>the re-anchor moves nothing: a point's height asked before and after (by its Earth position) is the same
/// within 1 cm, and the shift turned nothing.</item>
/// </list>
/// RESULT ok or FAILED.
/// </summary>
public partial class EarthCheck : Node3D
{
    public static bool Requested() => CmdArgs.Has("--earthcheck");

    private const double TimeoutS = 240;
    private const int Half = 10;
    private const float Step = 20f;
    private const float Speed = 30f, TravelM = 4500f;

    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly List<string> _faults = new();
    private readonly List<(GlobalPos At, float Height)> _marks = new();
    private Camera3D? _camera;
    private RigidBody3D? _ball;
    private ulong _startMs;
    private double _phaseS, _missingS, _worstMissingS;
    private int _phase, _shifts, _rays, _hits, _above, _below;
    private float _travelled;
    private Vector3 _dir;
    private double _worstRay;

    public EarthCheck(IWorldGround ground, WorldOrigin origin)
    {
        _ground = ground;
        _origin = origin;
        Name = "EarthCheck";
    }

    public override void _Ready()
    {
        var (e, n) = SpawnPoint.ParseTarget();
        Position = _origin.ToWorld(e, n, 0);
        _ground.AddAnchor(this, collision: true);
        _origin.Shifted += OnShifted;
        AddChild(_camera = new Camera3D { Current = true, Far = 20_000 });
        _camera.MakeCurrent();
        _startMs = Time.GetTicksMsec();
        Log($"start LV95 {e:F0}/{n:F0}, origin {(_origin.Frame.IsAnchor ? "an anchor frame" : "FLAT")}");
        if (!_origin.Frame.IsAnchor) _faults.Add("the world is not an anchor frame: run with --frame earth");
    }

    public override void _ExitTree()
    {
        _origin.Shifted -= OnShifted;
        _ground.RemoveAnchor(this);
    }

    private void OnShifted(OriginShift shift)
    {
        _shifts++;
        if (shift.Turns) _faults.Add($"shift {_shifts} turned the world: the earth frame moves without turning until C3");
    }

    // the wall clock: leaves load and patches build on threads, which --fixed-fps would outrun
    private double Now => (Time.GetTicksMsec() - _startMs) / 1000.0;

    public override void _PhysicsProcess(double delta)
    {
        if (Now > TimeoutS)
        {
            _faults.Add($"timed out in phase {_phase}");
            Finish();
            return;
        }
        switch (_phase)
        {
            case 0: WaitForGround(); break;
            case 1: Rays(); break;
            case 2: Ball(); break;
            case 3: Travel((float)delta); break;
            case 4: Marks(); break;
        }
    }

    private void WaitForGround()
    {
        if (!_ground.HasCollisionAt(Position) || !_ground.TryGetHeight(Position, out float h)) return;
        Position = Position with { Y = h + 2f };
        Log($"ground under the start at {Now:F1} s: {h:F2} m, tile {_ground.TileAt(Position)}");
        _phase = 1;
        _phaseS = Now;
    }

    private void Rays()
    {
        // a frame for the bodies to enter the physics space
        if (Now - _phaseS < 0.5) return;
        var space = GetWorld3D().DirectSpaceState;
        for (int j = -Half; j <= Half; j++)
            for (int i = -Half; i <= Half; i++)
            {
                var p = Position + new Vector3(i * Step, 0, j * Step);
                if (!_ground.HasCollisionAt(p) || !_ground.TryGetHeight(p, out float h)) continue;
                _rays++;
                var q = PhysicsRayQueryParameters3D.Create(p with { Y = h + 50 }, p with { Y = h - 50 });
                var hit = space.IntersectRay(q);
                if (hit.Count == 0) continue;
                _hits++;
                // over the ground: a roof, a bridge, a wall (the patches carry the leaf's features); under it: a hole
                double off = ((Vector3)hit["position"]).Y - h;
                if (off > 0.05) { _above++; continue; }
                if (off < -0.05)
                {
                    _below++;
                    if (_below <= 5) Log($"  under the query at {i * Step:F0},{j * Step:F0} m: {off:F2} m");
                }
                _worstRay = System.Math.Max(_worstRay, System.Math.Abs(off));
            }
        int ground = _hits - _above;
        Log($"rays: {_hits} of {_rays} hit; {_above} on a feature over the ground, {_below} under the query; on the ground worst {_worstRay * 100:F1} cm");
        if (_rays < 100) _faults.Add($"only {_rays} grid points had ground and collision");
        if (_hits < _rays * 0.99) _faults.Add($"rays missed the patches: {_rays - _hits} of {_rays}");
        // in a city centre half the grid is roofs: enough rays on the ground to judge, whatever stands on it
        if (ground < 100) _faults.Add($"only {ground} of {_rays} rays reached the ground (the rest stopped on features; bar 100)");
        if (_below > _rays / 100) _faults.Add($"{_below} of {_rays} rays went under the height query by over 5 cm (bar 1 %)");

        _ball = new RigidBody3D { Name = "Ball" };
        _ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.5f } });
        GetParent().AddChild(_ball);
        _ball.GlobalPosition = Position + Vector3.Up * 3;
        _phase = 2;
        _phaseS = Now;
    }

    private void Ball()
    {
        if (Now - _phaseS < 3) return;
        var at = _ball!.GlobalPosition;
        float rest = _ground.TryGetHeight(at, out float h) ? at.Y - 0.5f - h : float.NaN;
        Log($"the ball after 3 s: {rest * 100:F1} cm over the ground");
        if (!(Mathf.Abs(rest) < 0.15f)) _faults.Add($"the ball is {rest:F2} m off the ground after 3 s (fell through, or hangs)");
        _ball.QueueFree();

        // a mark to ask again after the re-anchor, by its Earth position
        for (int k = 0; k < 5; k++)
        {
            var p = Position + new Vector3(k * 37, 0, -k * 23);
            if (_ground.TryGetHeight(p, out float y)) _marks.Add((_origin.ToGlobal(p with { Y = y }), y));
        }
        _dir = new Vector3(1, 0, -1).Normalized();
        _phase = 3;
        _phaseS = Now;
    }

    private void Travel(float delta)
    {
        var step = _dir * Speed * delta;
        Position += step;
        _travelled += step.Length();
        if (_ground.TryGetHeight(Position, out float h) && _ground.HasCollisionAt(Position))
        {
            Position = Position with { Y = h + 2f };
            _missingS = 0;
        }
        else
        {
            _missingS += delta;
            _worstMissingS = System.Math.Max(_worstMissingS, _missingS);
        }
        if (_travelled < TravelM) return;
        Log($"travelled {_travelled:F0} m at {Speed:F0} m/s: {_shifts} re-anchors, the ground missing at most {_worstMissingS:F1} s at once");
        if (_shifts == 0) _faults.Add("no re-anchor in 4.5 km");
        if (_worstMissingS > 2) _faults.Add($"the ground was missing {_worstMissingS:F1} s at once under a body at {Speed} m/s (bar 2 s)");
        _phase = 4;
        _phaseS = Now;
        // back to the marks, so their leaves load again
        Position = _origin.ToWorld(_marks.Count > 0 ? _marks[0].At : _origin.ToGlobal(Position));
    }

    private void Marks()
    {
        int known = 0;
        double worst = 0;
        foreach (var (at, before) in _marks)
        {
            var p = _origin.ToWorld(at);
            if (!_ground.TryGetHeight(p, out float now)) continue;
            known++;
            // the same Earth point: its world height then is its height now, moved as the frame moved
            worst = System.Math.Max(worst, Mathf.Abs(now - p.Y));
        }
        if (known < _marks.Count && Now - _phaseS < 30) return;
        Log($"marks after {_shifts} re-anchors: {known} of {_marks.Count} known again, worst {worst * 100:F2} cm");
        if (known < _marks.Count) _faults.Add($"only {known} of {_marks.Count} marks had ground again");
        if (worst > 0.01) _faults.Add($"a height moved {worst * 100:F2} cm across the re-anchors (bar 1 cm)");
        Finish();
    }

    private void Finish()
    {
        _phase = -1;
        foreach (var f in _faults) Log("FAIL " + f);
        Log(_faults.Count == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_faults.Count})");
        GetTree().Quit(_faults.Count == 0 ? 0 : 1);
    }

    private static void Log(string s) => GD.Print($"[earthcheck] {s}");
}
