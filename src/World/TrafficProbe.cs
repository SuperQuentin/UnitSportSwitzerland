using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// <c>godot --path . -- --trafficcheck[,out.png] [--at E,N] [--time h]</c>: hovers a camera over
/// the nearest motorway (else the busiest road), lets the traffic run for 40 s, prints cars,
/// trains and speeds every 5 s, and fails if nothing ever moved. With <c>--at</c> the camera stays over that point and the
/// traffic lives around it (<c>--dense</c>: more of it near there); at the end it prints, per approach (#353), what the lights and
/// lanes did, and fails on a red run. <c>--seconds N</c> runs longer.
/// With <c>--crossing</c> (#124) it watches the rail at <c>--at</c> instead (a level crossing):
/// spawns a train there and fails unless its units roll over it with their wheels on the road
/// surface (the groove paint), within 5 cm.
/// </summary>
public partial class TrafficProbe : Node
{
    private readonly Traffic _traffic;
    private readonly Camera3D _camera;
    private readonly string? _shot;
    private double _t;
    private bool _placed;
    private int _maxCars, _maxTrains;
    private float _maxSpeed;
    private int _wrongWay;
    /// <summary>Most cars giving way at a side road's Wartelinie at once (#121); reported, not judged.</summary>
    private int _givingWay;
    /// <summary>Most cars waiting at a red light at once, and signalised approaches seen (#353).</summary>
    private int _atRed, _signalApproaches;
    private readonly bool _crossing = CmdArgs.Has("--crossing"), _stay = CmdArgs.Has("--at"), _dense = CmdArgs.Has("--at") && CmdArgs.Has("--dense");
    /// <summary>How long the traffic runs (40 s; <c>--seconds N</c> for more cars through the junctions, #353).</summary>
    private readonly double _seconds = CmdArgs.Double("--seconds") ?? 40;
    private Vector3 _at;
    private bool _spawned;
    private int _over;
    private float _worst;

    /// <summary>For <c>--crossing</c>: where the .road tile under the crossing is.</summary>
    public WorldOrigin? Origin { get; init; }

    /// <summary>Wheel bottom below a train unit's origin (lift 0.2 over a raised rail head at 0.18).</summary>
    private const float WheelDrop = 0.02f;

    /// <summary>Groove paint vertices around the crossing, world space, as RoadPaintBuilder lifts them.</summary>
    private List<Vector3>? _grooves;

    public TrafficProbe(Traffic traffic, Camera3D camera, string? shot)
    {
        _traffic = traffic;
        _camera = camera;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--trafficcheck");

    public override void _Process(double delta)
    {
        _t += delta;
        if (_crossing) { Crossing(); return; }
        // with --at (#353) the camera stays over that point, so the traffic lives around it
        if (!_placed && _stay && _traffic.Roads is { } near)
        {
            // looking down on the junction from 45 m up, 15 m south of it
            var at = _camera.GlobalPosition;
            var ground = near.Edges.SelectMany(e => e.Points).MinBy(p => new Vector2(p.X - at.X, p.Z - at.Z).LengthSquared());
            var spot = new Vector3(at.X, ground.Y, at.Z);
            var eye = spot + new Vector3(0f, 45f, 15f);
            _camera.GlobalTransform = new Transform3D(Player.Flyer.Orient(spot - eye, Vector3.Up, Vector3.Forward), eye);
            _placed = true;
        }
        if (!_placed && _traffic.Roads is { } roads)
        {
            var best = roads.Edges.OrderBy(e => (int)e.Class).ThenByDescending(e => e.Length).FirstOrDefault();
            if (best != null)
            {
                var (p, t) = best.Sample(best.Length * 0.5f);
                var side = new Vector3(-t.Z, 0, t.X);
                _camera.GlobalPosition = p + Vector3.Up * 22f - side * 30f - t * 25f;
                _camera.GlobalTransform = new Transform3D(
                    Player.Flyer.Orient(p + t * 40f - _camera.GlobalPosition, Vector3.Up, Vector3.Forward),
                    _camera.GlobalPosition);
                GD.Print($"[trafficcheck] watching a {best.Class} edge, {best.Length:F0} m");
            }
            _placed = true;
        }

        // from 20 s, chase a car (then from 32 s a train) so the picture has one in it
        if (!_stay && _t > 20 && _traffic.Watch(_t > 32) is var (pos, dir))
        {
            float back = _t > 32 ? 35f : 11f, up = _t > 32 ? 28f : 4f;
            var eye = pos - dir * back + Vector3.Up * up + new Vector3(-dir.Z, 0, dir.X) * (_t > 32 ? 30f : 0f);
            _camera.GlobalTransform = new Transform3D(
                Player.Flyer.Orient(pos + dir * 6f - eye, Vector3.Up, Vector3.Forward), eye);
            if (_shot != null && Math.Abs(_t - 31) < delta)
                GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", "_car.png"));
        }

        if (_t >= TickFrom && _t - delta < TickFrom) _traffic.ResetTickCost();
        // --dense: more of the traffic where the junction under test is (#353)
        if (_dense && (int)(_t * 4) != (int)((_t - delta) * 4)) _traffic.SpawnNear(_camera.GlobalPosition, NearSpawn);
        _maxCars = Math.Max(_maxCars, _traffic.CarCount);
        _maxTrains = Math.Max(_maxTrains, _traffic.TrainCount);
        _maxSpeed = Math.Max(_maxSpeed, _traffic.AverageCarSpeed);
        _wrongWay = Math.Max(_wrongWay, _traffic.WrongWayCars);
        _givingWay = Math.Max(_givingWay, _traffic.GivingWayCars);
        _atRed = Math.Max(_atRed, _traffic.AtRedCars);
        _signalApproaches = Math.Max(_signalApproaches, _traffic.SignalApproaches);
        if ((int)(_t / 5) != (int)((_t - delta) / 5))
            GD.Print($"[trafficcheck] t={_t:F0}s cars {_traffic.CarCount} (avg {_traffic.AverageCarSpeed * 3.6f:F0} km/h), "
                + $"trains {_traffic.TrainCount} (avg {_traffic.AverageTrainSpeed * 3.6f:F0} km/h), "
                + $"one-way edges {_traffic.OneWayEdges}, cars against one-way {_traffic.WrongWayCars}, giving way {_traffic.GivingWayCars}, "
                + $"signalised approaches {_traffic.SignalApproaches}, at red {_traffic.AtRedCars}, stops at red {_traffic.RedStops}, lines crossed {_traffic.LinesCrossed}, red runs {_traffic.RedRuns}");

        if (_t < _seconds) return;
        if (_shot != null && GetViewport().GetTexture().GetImage().SavePng(_shot) == Error.Ok)
            GD.Print($"[trafficcheck] wrote {_shot}");
        var (mean, p50, p99, ticks, cars) = _traffic.TickCost();
        GD.Print($"[trafficcheck] tick cost from {TickFrom:F0} s (perf-traffic-tick): mean {mean:F0} us, p50 {p50:F0} us, p99 {p99:F0} us over {ticks} ticks, {cars:F0} cars on average");
        Approaches();
        bool ok = _maxCars > 0 && _maxSpeed > 2f && _wrongWay == 0 && _traffic.RedRuns == 0;
        GD.Print(ok ? $"[trafficcheck] RESULT: ok (peak {_maxCars} cars, {_maxTrains} trains, none against a one-way, up to {_givingWay} giving way, "
                        + $"{_signalApproaches} signalised approaches, up to {_atRed} at red, {_traffic.RedStops} stops at red, {_traffic.LinesCrossed} stop lines crossed on green, no red run)"
                    : _wrongWay > 0 ? $"[trafficcheck] RESULT: FAILED — up to {_wrongWay} cars against a one-way"
                    : _traffic.RedRuns > 0 ? $"[trafficcheck] RESULT: FAILED — {_traffic.RedRuns} cars crossed a stop line on red"
                    : "[trafficcheck] RESULT: FAILED — no moving traffic");
        GetTree().Quit(ok ? 0 : 1);
        SetProcess(false);
    }

    /// <summary>
    /// Per approach (#353), nearest the camera first (<c>--at</c>), those any car came through:
    /// cars stopped at red, cars entering on red (must be 0; clearing a yellow is allowed),
    /// crossings on green, left turns from a pocket, permissive lefts that waited, waits for room
    /// past the junction; on an approach with several lanes, the cars per lane and how far right
    /// of their usual line they crossed. The 15 nearest, then any other a pocket or a wait was
    /// used at. Then the totals.
    /// </summary>
    private void Approaches()
    {
        if (_traffic.Roads is not { } roads) return;
        var at = _camera.GlobalPosition;
        var used = roads.Approaches
            .Where(a => a.RedStops + a.RedRuns + a.AmberClears + a.GreenCrossings + a.PermissiveWaits + a.RoomWaits > 0)
            .OrderBy(a => new Vector2(a.Stop.X - at.X, a.Stop.Z - at.Z).Length()).ToList();
        foreach (var a in used.Where((a, i) => i < 15 || a.PocketLefts + a.PermissiveWaits + a.RoomWaits + a.RedRuns > 0))
        {
            string where = Origin is { } o && o.ToLv95(a.Stop) is var (e, n) ? $"LV95 {e:F0},{n:F0}" : $"{a.Stop}";
            string lanes = string.Join(" | ", a.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).Select(l => Letters(l.Moves)));
            float heading = Mathf.RadToDeg(Mathf.Atan2(-a.Out.Z, a.Out.X));
            string perLane = !a.MultiLane ? ""
                : ", at the line " + string.Join(", ", a.Lanes.Select((l, i) => (l, i)).Where(x => x.l.Kind == ApproachLaneKind.Car)
                    .Select(x => $"{Letters(x.l.Moves)} {a.Crossings[x.i]}{(a.Crossings[x.i] > 0 ? $" at {a.OffsetSum[x.i] / a.Crossings[x.i]:+0.0;-0.0} m" : "")}"))
                  + $" (lane {string.Join("/", a.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).Select(l => $"{l.Offset:+0.0;-0.0}"))} m from the original lane), worst lag {a.LaneError:F2} m";
            GD.Print($"[trafficcheck] approach {where} arm {heading:F0} deg, {(a.Site is null ? "no lights" : "lights")}, lanes {lanes}"
                + $"{(a.Banned != 0 ? $", no {Letters(a.Banned)}" : "")}, {new Vector2(a.Stop.X - at.X, a.Stop.Z - at.Z).Length():F0} m away: "
                + $"stopped at red {a.RedStops}, entered on red {a.RedRuns} (cleared yellow {a.AmberClears}), {(a.Site is null ? "crossed" : "on green")} {a.GreenCrossings}, "
                + $"left from the pocket {a.PocketLefts}, permissive lefts that waited {a.PermissiveWaits}, waited for room past {a.RoomWaits}{perLane}");
        }
        int lit = roads.Approaches.Count(a => a.Site is not null), pockets = roads.Approaches.Count(a => a.Site is null);
        GD.Print($"[trafficcheck] approaches: {lit} with lights, {pockets} with a pocket and no lights, {used.Count} driven through; "
            + $"stopped at red {_traffic.RedStops}, entered on red {_traffic.RedRuns} (cleared yellow {_traffic.AmberClears}), on green or without lights {_traffic.LinesCrossed}, "
            + $"left from a pocket {_traffic.PocketLefts}, permissive lefts that waited {_traffic.PermissiveWaits}, waited for room past {_traffic.RoomWaits}");
    }

    private static string Letters(SignalMoves m) =>
        ((m & SignalMoves.Left) != 0 ? "L" : "") + ((m & SignalMoves.Through) != 0 ? "T" : "") + ((m & SignalMoves.Right) != 0 ? "R" : "");

    private void Crossing()
    {
        if (!_placed)
        {
            _at = _camera.GlobalPosition;
            _placed = true;
        }
        if (!_spawned && _t > 6 && Origin != null)
        {
            var (e, n) = Origin.ToLv95(_at);
            var id = TileId.FromLv95(e, n);
            using (var f = System.IO.File.OpenRead(System.IO.Path.Combine(TerrainPaths.FindChunkDir(), RoadFormat.FileName(id))))
                _grooves = RoadCodec.Decode(f).Paint.Where(p => p.Type == PaintType.RailGroove)
                    .SelectMany(p => Enumerable.Range(0, p.Vertices.Length / 3)
                        .Select(i => Origin.ToWorld(id.MinE + p.Vertices[i * 3], id.MaxN - p.Vertices[i * 3 + 2], p.Vertices[i * 3 + 1] + 0.02f)))
                    .ToList();
            if (GrooveBelow(_at) is not { } g0) { GD.Print("[trafficcheck] no groove paint at --at"); _t = 40; return; }
            _at.Y = g0;
            var eye = _at + new Vector3(18f, 7f, 14f);
            _camera.GlobalTransform = new Transform3D(Player.Flyer.Orient(_at - eye, Vector3.Up, Vector3.Forward), eye);
            _spawned = _traffic.SpawnTrainAt(_at);
            if (_spawned) GD.Print($"[trafficcheck] train spawned over the crossing at {_at}");
        }
        foreach (var u in _traffic.TrainUnits)
        {
            if (new Vector2(u.X - _at.X, u.Z - _at.Z).Length() > 1.0f || GrooveBelow(u) is not { } g) continue;
            float gap = u.Y - WheelDrop - g;
            _over++;
            if (Math.Abs(gap) > Math.Abs(_worst)) _worst = gap;
            if (_over % 10 == 1) GD.Print($"[trafficcheck] unit over the crossing: wheel - groove = {gap:F3} m");
        }
        if (_shot != null && _over >= 15 && !_shotTaken)
            _shotTaken = GetViewport().GetTexture().GetImage().SavePng(_shot) == Error.Ok;
        if (_t < 40) return;
        bool ok = _over > 0 && Math.Abs(_worst) < 0.05f;
        GD.Print(ok ? $"[trafficcheck] RESULT: ok (train over the crossing {_over} frames, worst wheel - groove {_worst:F3} m)"
                    : $"[trafficcheck] RESULT: FAILED (frames over the crossing {_over}, worst {_worst:F3} m)");
        GetTree().Quit(ok ? 0 : 1);
        SetProcess(false);
    }

    private bool _shotTaken;

    /// <summary>The traffic tick is timed from here on (#353): the cars have spawned.</summary>
    private const double TickFrom = 10;

    /// <summary>With <c>--at</c> and <c>--dense</c>, extra cars appear within this many metres of it (#353).</summary>
    private const float NearSpawn = 300f;

    /// <summary>Height of the groove paint nearest a point (within 2 m), as drawn.</summary>
    private float? GrooveBelow(Vector3 u)
    {
        var near = _grooves?.Where(g => new Vector2(g.X - u.X, g.Z - u.Z).Length() < 2f).ToList();
        return near is { Count: > 0 } ? near.MinBy(g => new Vector2(g.X - u.X, g.Z - u.Z).Length()).Y : null;
    }
}
