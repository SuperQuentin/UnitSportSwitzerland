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
/// <item>the ground streams ahead of a body at 30 m/s: moving 4.5 km (<c>--earthtravel &lt;m&gt;</c>) along a line (the camera with it, so the origin
/// re-anchors past 3 km), the ground under it is known within 2 s of its arrival, nowhere missing longer. On a fixture
/// course (C1 PR 3: <c>--chunks fixture:straight</c>, the quick tier) along the course's longest reach from the start and
/// 300 m into its margin, at 60 m/s;</item>
/// <item>the re-anchors turn the world with the Earth (#1512, G2 C3) and move nothing: at every re-anchor the
/// ground under the body, asked again by its Earth position, is where it was within 1 cm (no pop); the world's up at
/// the body is the ellipsoid's within 0.03°; each turn is the arc moved over R within 10 %; and the marks asked
/// after the whole run are the same within 1 cm.</item>
/// <item>(PR 2) the leaves round the start are furnished: roads, buildings, trees and water drawn in their nodes;
/// the doors in the door index stand on the ground (the sill within 1.5 m of the height query, the median within
/// 0.3 m); a door's building plans an interior through its stand-in tile, whose entrance is at that door (1 m).</item>
/// </list>
/// RESULT ok or FAILED.
/// </summary>
public partial class EarthCheck : Node3D
{
    public static bool Requested() => CmdArgs.Has("--earthcheck");

    private double TimeoutS => 240 + TravelM / Speed;
    private const int Half = 10;
    private const float Step = 20f;
    /// <summary>The run's line: north-east by default; a fixture course's longest reach (see ClientWorld).</summary>
    public Vector3 Direction { get; init; } = new Vector3(1, 0, -1).Normalized();
    public float Speed { get; init; } = 30f;
    /// <summary>One leg, metres: the body turns back at each end of it (the leaves of a sparse region lie along it).</summary>
    public float LegM { get; init; } = 4500f;
    /// <summary>
    /// How far the body travels, metres: 4.5 km (one re-anchor), or <c>--earthtravel &lt;m&gt;</c> for a long drive across
    /// several: back and forth along the same leg (<see cref="LegM"/>), where the region's leaves are known to be.
    /// </summary>
    public float TravelM { get; init; } = CmdArgs.Float("--earthtravel") is float m && m > 0 ? m : 4500f;

    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly List<string> _faults = new();
    private readonly List<(GlobalPos At, float Height)> _marks = new();
    private Camera3D? _camera;
    private RigidBody3D? _ball;
    private ulong _startMs;
    private double _phaseS, _missingS, _worstMissingS;
    private int _phase, _shifts, _rays, _hits, _above, _below, _missed;
    private List<(int I, int J)>? _points;
    private double _ballS;
    private float _travelled, _legEnd;
    private Vector3 _dir;
    private double _worstRay;
    // the ground under the body just before a re-anchor, by its Earth position, and the worst seen at the re-anchors
    private GlobalPos? _under;
    private double _worstPop, _worstTilt, _worstTurnError, _turnSum;
    private int _turns, _popsAsked;

    /// <summary>The earth world (its furniture) and the chunk source that answers its stand-in tiles; the furniture phase is skipped without.</summary>
    public EarthWorld? Earth { get; init; }
    public IChunkSource? Source { get; init; }
    /// <summary>
    /// Whether the start has a town round it: buildings, trees, water and 20 doors within 600 m, one to plan an interior
    /// behind (Geneva's default). Off on a fixture course (roads alone) and with <c>--earthbare</c>, for a start in the
    /// mountains or at the region's border (C1 PR 3); the leaves must still be furnished with roads.
    /// </summary>
    public bool ExpectTown { get; init; } = !CmdArgs.Has("--earthbare");

    private Task<Interiors.InteriorLayout?>? _plan;
    private Interiors.DoorIndex.Entry _planned;

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
        // the heading is a world direction: it turns with the world, kept level
        _dir = shift.Direction(_dir) with { Y = 0 };
        if (_dir != Vector3.Zero) _dir = _dir.Normalized();
        // the tilt of up (the least rotation has no yaw), from the sine: an acos of a float near 1 loses it
        double turn = System.Math.Asin(System.Math.Min(1.0, Vector3.Up.Cross(shift.Direction(Vector3.Up)).Length()));
        if (shift.Turns) _turns++;
        _turnSum += turn;
        // the arc moved since the last anchor (the shift's translation, horizontal) over R: the Earth's own turn
        var t = shift.Transform.Origin;
        double arc = new Vector2(t.X, t.Z).Length();
        if (arc > 100)
        {
            double expected = arc / 6_371_000.0;
            _worstTurnError = System.Math.Max(_worstTurnError, System.Math.Abs(turn - expected) / expected);
        }
        // up at the body is the ellipsoid's
        var up = _origin.Frame.UpAt(Position);
        _worstTilt = System.Math.Max(_worstTilt, Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(up.Y, -1f, 1f))));
        // no pop: the ground under the body, asked again by its Earth position in the new frame
        if (_under is { } g)
        {
            var p = _origin.ToWorld(g);
            if (_ground.TryGetHeight(p, out float h))
            {
                _popsAsked++;
                _worstPop = System.Math.Max(_worstPop, Mathf.Abs(h - p.Y));
            }
        }
    }

    // the wall clock: leaves load and patches build on threads, which --fixed-fps would outrun
    private double Now => (Time.GetTicksMsec() - _startMs) / 1000.0;

    public override void _PhysicsProcess(double delta)
    {
        // online (#1655) the player spawned after the check takes the view: the origin must follow this body, not it
        if (_camera != null && !_camera.Current) _camera.MakeCurrent();
        Phase(delta);
    }

    private void Phase(double delta)
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
            case 2: Ball(delta); break;
            case 3: Travel((float)delta); break;
            case 4: Marks(); break;
            case 5: Furniture(); break;
            case 6: Interior(); break;
        }
    }

    private void WaitForGround()
    {
        if (!_ground.HasCollisionAt(Position) || !_ground.TryGetHeight(Position, out float h)) return;
        Position = Position with { Y = h + 2f };
        Log($"ground under the start at {Now:F1} s: {h:F2} m, tile {_ground.TileAt(Position)}");
        LogStreamed("on arrival");
        // re-anchors before the ground came (a joining client's first frames, #1655) had no ground under the body to ask again
        _shifts = _turns = 0;
        _turnSum = _worstTurnError = _worstTilt = 0;
        _phase = 1;
        _phaseS = Now;
    }

    private void Rays()
    {
        // a frame for the bodies to enter the physics space
        if (Now - _phaseS < 0.5) return;
        // the points are taken a physics frame before their rays: a patch counted the frame it stood up was not in
        // the space's queries yet, and a whole patch's rays (52 of 308 at the region's border) found nothing
        if (_points is null)
        {
            _points = new();
            for (int j = -Half; j <= Half; j++)
                for (int i = -Half; i <= Half; i++)
                {
                    var p = Position + new Vector3(i * Step, 0, j * Step);
                    if (_ground.HasCollisionAt(p)) _points.Add((i, j));
                }
            return;
        }
        var space = GetWorld3D().DirectSpaceState;
        foreach (var (i, j) in _points)
        {
            var p = Position + new Vector3(i * Step, 0, j * Step);
            if (!_ground.HasCollisionAt(p) || !_ground.TryGetHeight(p, out float h)) continue;
            _rays++;
            var q = PhysicsRayQueryParameters3D.Create(p with { Y = h + 50 }, p with { Y = h - 50 });
            var hit = space.IntersectRay(q);
            if (hit.Count == 0)
            {
                if (++_missed <= 5) Log($"  no hit at {i * Step:F0},{j * Step:F0} m (tile {_ground.TileAt(p)})");
                continue;
            }
            _hits++;
            // over the ground: a roof, a bridge, a wall (the patches carry the leaf's features); under it: a hole
            double off = ((Vector3)hit["position"]).Y - h;
            if (off > 0.05)
            {
                if (++_above <= 5)
                    Log($"  on a feature {off:F2} m over the ground at {i * Step:F0},{j * Step:F0} m (tile {_ground.TileAt(p)}): "
                        + $"{(hit["collider"].AsGodotObject() as Node)?.GetPath()} shape {hit["shape"]}");
                continue;
            }
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

    private void Ball(double delta)
    {
        // three seconds of physics, not of the wall clock: on a loaded machine the ball had hardly fallen
        _ballS += delta;
        if (_ballS < 3) return;
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
        _dir = Direction;
        _legEnd = LegM;
        _phase = 3;
        _phaseS = Now;
    }

    private void Travel(float delta)
    {
        var step = _dir * Speed * delta;
        Position += step;
        _travelled += step.Length();
        // a long drive turns back at each end of the leg (the leaves of a sparse region lie along it)
        if (_travelled >= _legEnd && _travelled < TravelM)
        {
            _legEnd += LegM;
            _dir = -_dir;
        }
        if (_ground.TryGetHeight(Position, out float h) && _ground.HasCollisionAt(Position))
        {
            Position = Position with { Y = h + 2f };
            _under = _origin.ToGlobal(Position with { Y = h });
            _missingS = 0;
        }
        else
        {
            _missingS += delta;
            _worstMissingS = System.Math.Max(_worstMissingS, _missingS);
        }
        if (_travelled < TravelM) return;
        Log($"travelled {_travelled:F0} m at {Speed:F0} m/s: {_shifts} re-anchors, the ground missing at most {_worstMissingS:F1} s at once");
        if (_shifts == 0) _faults.Add($"no re-anchor in {TravelM / 1000:F1} km");
        Log($"re-anchors: {_turns} of {_shifts} turned, {Mathf.RadToDeg((float)_turnSum):F4}° in all, each within {_worstTurnError * 100:F1} % of arc / R; "
            + $"up at the body within {_worstTilt:F4}° of the ellipsoid's; the ground under the body moved {_worstPop * 100:F2} cm at most ({_popsAsked} asked)");
        if (OriginShifter.GroundMode.Rotates && _turns < _shifts) _faults.Add($"only {_turns} of {_shifts} re-anchors turned the world (C3: the ground frame turns)");
        if (_worstTurnError > 0.1) _faults.Add($"a re-anchor turned {_worstTurnError * 100:F0} % off the arc moved over R (bar 10 %)");
        if (_worstTilt > 0.03) _faults.Add($"the world's up at the body was {_worstTilt:F3}° off the ellipsoid's after a re-anchor (bar 0.03°)");
        if (_popsAsked < _shifts) _faults.Add($"the ground under the body was known again at only {_popsAsked} of {_shifts} re-anchors");
        if (_worstPop > 0.01) _faults.Add($"the ground under the body popped {_worstPop * 100:F2} cm at a re-anchor (bar 1 cm)");
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
        if (Earth == null) { Finish(); return; }
        // back at the start, for its furniture
        Position = _origin.ToWorld(_marks.Count > 0 ? _marks[0].At : _origin.ToGlobal(Position));
        _phase = 5;
        _phaseS = Now;
    }

    private void Furniture()
    {
        var earth = Earth!;
        // the leaves round the start, furnished: wait while they load
        if ((!earth.Idle || earth.FurnishedCount < 4) && Now - _phaseS < 60) return;
        if (Now - _phaseS < 2) return;
        int roads = 0, buildings = 0, trees = 0, water = 0, nodes = 0;
        foreach (var node in earth.GetChildren())
        {
            if (node is not ChunkNode c) continue;
            nodes++;
            if (c.RoadMesh != null) roads++;
            foreach (var child in c.GetChildren())
            {
                string name = child.Name;
                if (child is MultiMeshInstance3D) trees++;
                else if (name.Contains("Water")) water++;
                else if (name.StartsWith("Buildings")) buildings++;
            }
        }
        Log($"furnished {earth.FurnishedCount} leaves: {nodes} nodes, roads in {roads}, building meshes {buildings}, tree layers {trees}, water {water}");
        if (earth.FurnishedCount < 4) _faults.Add($"only {earth.FurnishedCount} leaves furnished round the start");
        if (roads == 0) _faults.Add("no roads drawn on the leaves");
        if (!ExpectTown) { Finish(); return; }
        if (buildings == 0) _faults.Add("no buildings drawn on the leaves");
        if (trees == 0) _faults.Add("no trees drawn on the leaves");
        if (water == 0) _faults.Add("no water drawn on the leaves");

        // the doors stand where the ground is
        var offs = new List<float>();
        Interiors.DoorIndex.Entry? pick = null;
        foreach (var d in Interiors.DoorIndex.All())
        {
            if (!Leaves.LeafStandIn.IsStandIn(d.Key.Tile)) continue;
            if (d.World.DistanceTo(Position) > 600 || d.Level != 0) continue;
            var front = d.World + d.Outward * 1.5f;
            if (!_ground.TryGetHeight(front, out float g)) continue;
            offs.Add(d.World.Y - g);
            if (pick == null && d.Kind != Format.BuildingKind.UnderConstruction && !d.Vehicle) pick = d;
        }
        offs.Sort();
        float median = offs.Count > 0 ? offs[offs.Count / 2] : float.NaN;
        int far = offs.Count(o => Mathf.Abs(o) > 1.5f);
        Log($"doors on the leaves within 600 m: {offs.Count}; sill over the ground in front: median {median * 100:F0} cm, {far} off by over 1.5 m");
        if (offs.Count < 20) _faults.Add($"only {offs.Count} doors on the leaves round the start");
        else
        {
            if (!(Mathf.Abs(median) < 0.3f)) _faults.Add($"the doors' median sill is {median:F2} m off the ground (bar 0.3 m)");
            if (far > offs.Count / 20) _faults.Add($"{far} of {offs.Count} doors over 1.5 m off the ground (bar 5 %)");
        }
        if (pick is { } door && Source != null)
        {
            _planned = door;
            string dir = System.IO.Path.Combine(OS.GetUserDataDir(), "earthcheck_interiors");
            _plan = Interiors.InteriorManager.LoadOrGenerate(Source, dir, door.Key.Building.ToString());
            _phase = 6;
            _phaseS = Now;
            return;
        }
        if (pick == null) _faults.Add("no street door to plan an interior behind");
        Finish();
    }

    private void Interior()
    {
        if (!_plan!.IsCompleted && Now - _phaseS < 60) return;
        var door = _planned;
        if (!_plan.IsCompletedSuccessfully || _plan.Result is not { } layout)
        {
            _faults.Add($"no interior planned behind {door.Key}: {_plan.Exception?.GetBaseException().Message ?? "none"}");
            Finish();
            return;
        }
        var e = layout.EntranceOf(door.Key.ToString());
        if (e == null)
        {
            _faults.Add($"the interior behind {door.Key} has no entrance at that door");
            Finish();
            return;
        }
        var at = _origin.TileTransform(door.Key.Tile) * new Vector3(e.DoorX, e.DoorY, e.DoorZ);
        float off = at.DistanceTo(door.World);
        Log($"interior behind {door.Key} ({door.Kind}): {layout.Floors.Sum(f => f.Rooms.Count)} rooms, its entrance {off * 100:F1} cm from the door on the leaf");
        if (off > 1f) _faults.Add($"the interior's entrance is {off:F2} m from its door on the leaf (bar 1 m)");
        Finish();
    }

    private void Finish()
    {
        _phase = -1;
        LogStreamed("in all");
        foreach (var f in _faults) Log("FAIL " + f);
        Log(_faults.Count == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_faults.Count})");
        GetTree().Quit(_faults.Count == 0 ? 0 : 1);
    }

    /// <summary>What came over the network (#1655: a client with no leaves of its own streams them): the session's bytes, the server's leaves.</summary>
    private void LogStreamed(string when)
    {
        if (Earth == null) return;
        Log($"streamed {when}: {Net.ChunkStreamer.SessionBytes / (1024.0 * 1024):F1} MB, {Earth.Store.RemoteCount} leaves from the server's index, "
            + $"{Earth.Store.LocalKeys.Count} on this copy's disk");
    }

    private static void Log(string s) => GD.Print($"[earthcheck] {s}");
}
