using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --truckprobe N[,seconds[,shot_prefix]] [--trailer M] [--kmh V] [--load x] [--at E,N]</c>
/// (#70): a truck or bus (<see cref="HeavyCatalog"/> index N, with trailer M) driven down the real
/// road from the spawn (<see cref="RaceRoute"/>), steered by pure pursuit on the centreline and
/// slowed for the bends. Every two seconds it prints the speed, the gear, the joints and how far
/// each section's rear axles run off the centreline — the off-tracking on real Swiss bends — and
/// what the sections' own bodies hit. A screenshot every ten seconds with a prefix. Non-zero exit if
/// the train went nowhere, came apart (a non-finite state) or left the road.
/// </summary>
public partial class TruckProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly int _index;
    private readonly double _seconds;
    private readonly string? _shots;
    private readonly float _kmh;
    private readonly float _load;
    private readonly int _trailer;

    private RaceRoute? _route;
    private bool _requested, _mounted, _done;
    private FootPlayer? _player;
    private double _t, _sinceReport, _sinceShot, _sinceTrace;
    private readonly bool _trace = CmdArgs.Has("--trace");
    private int _shotCount, _near;
    private float _worstOff, _worstJoint, _travelled, _top, _offTime;
    private readonly List<float> _worstSection = new();
    private Vector3 _last;

    public TruckProbe(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
        var parts = (CmdArgs.Value("--truckprobe") ?? "0").Split(',');
        _index = int.TryParse(parts[0], out int n) ? n : 0;
        _seconds = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 60;
        _shots = parts.Length > 2 ? parts[2] : null;
        _kmh = CmdArgs.Float("--kmh") ?? 45f;
        _load = CmdArgs.Float("--load") ?? 1f;
        _trailer = CmdArgs.Int("--trailer") ?? -1;
        if (!CmdArgs.Has("--profile")) GameSettings.Current.RideProfile = RideProfile.Sim;
    }

    public static bool Requested => CmdArgs.Has("--truckprobe");

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _t += delta;
        if (_t > _seconds + 120) { GD.Print("[truckprobe] TIMEOUT"); Finish(1); return; }

        if (!_requested)
        {
            if (_chunks.Source == null) return;
            _requested = true;
            var (e, n) = SpawnPoint.ParseTarget();
            var at = _origin.ToWorld(e, n, 0);
            var source = _chunks.Source!;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var route = await RaceRoute.BuildAsync(source, _origin, at, default,
                    CmdArgs.Has("--minor") ? Terrain.Format.RoadClass.Minor : Terrain.Format.RoadClass.Road);
                Callable.From(() =>
                {
                    if (route == null) { GD.Print("[truckprobe] no road near the spawn"); Finish(1); return; }
                    _route = route;
                    GD.Print($"[truckprobe] road: {route.Class}, {route.Arc[^1]:F0} m, {route.Width.Average():F1} m wide on average");
                    RoadReport(route);
                }).CallDeferred();
            });
            return;
        }
        if (_route == null) return;

        if (_player == null)
        {
            var start = _route.Centre[Mathf.Min(8, _route.Centre.Count - 1)];
            if (!_chunks.TryGetHeight(start, out float g) || !_chunks.HasCollisionAt(start)) return;
            var ahead = _route.Centre[Mathf.Min(14, _route.Centre.Count - 1)] - start;
            _player = new FootPlayer { Name = "TruckProbe", Terrain = _chunks };
            // a node faces −Z: the yaw that turns −Z onto the road
            _player.Rotation = new Vector3(0, Mathf.Atan2(-ahead.X, -ahead.Z), 0);
            AddChild(_player);
            _player.Announced += (text, _) => GD.Print($"[truckprobe] announced: {text}");
            _player.GlobalPosition = start with { Y = g + 1.5f };
            _last = _player.GlobalPosition;
            return;
        }

        if (!_mounted)
        {
            if (!_player.IsOnFloor()) return;
            _player.NextLoad = _load;
            _mounted = _player.SetRide((RideKind)(HeavyCatalog.First + _index));
            if (_mounted && _trailer >= 0)
                GD.Print(_player.SpawnTrailer(_trailer, _load) ? $"[truckprobe] coupled {TrailerCatalog.All[_trailer].Label}" : "[truckprobe] TRAILER REFUSED");
            GD.Print(_mounted ? $"[truckprobe] driving {HeavyCatalog.All[_index].Label}" : "[truckprobe] MOUNT REFUSED");
            if (!_mounted) { Finish(1); return; }
            _player.RideControls = Drive;
            _t = 0;
            return;
        }

        if (_player.Heavy is not { } truck)
        {
            GD.Print($"[truckprobe] out of the vehicle after {_t:F1} s ({_travelled:F0} m)");
            GD.Print("[truckprobe] RESULT: FAILED");
            Finish(1);
            return;
        }
        var pos = _player.GlobalPosition;
        _travelled += ((pos - _last) with { Y = 0 }).Length();
        _last = pos;
        _top = Mathf.Max(_top, _player.RideSpeed);

        // how far each section's rear axles run from the centreline, beyond the road's half width
        while (_worstSection.Count < truck.SectionCount) _worstSection.Add(0f);
        float past = 0f;
        for (int k = 0; k < truck.SectionCount; k++)
        {
            var b = truck.Train.Bodies[k];
            var local = truck.NodeLocal(k) * new Vector3(0, 0, -(b.CgAt - HeavyTrain.RearGroupAt(b.Spec)));
            var axles = _player.GlobalTransform * local;
            float off = Off(axles, out float width);
            _worstSection[k] = Mathf.Max(_worstSection[k], off);
            _worstOff = Mathf.Max(_worstOff, off - width * 0.5f);
            past = Mathf.Max(past, off - width * 0.5f);
        }
        // a moment past the edge is a verge; seconds of it is off the road
        if (past > 1.5f) _offTime += (float)delta;
        for (int j = 0; j < truck.SectionCount - 1; j++) _worstJoint = Mathf.Max(_worstJoint, Mathf.Abs(truck.Articulation[j]));

        _sinceTrace += delta;
        if (_trace && _sinceTrace >= 0.25)
        {
            _sinceTrace = 0;
            GD.Print($"[truckprobe]   {_t,5:F2} v {_player.RideSpeed * 3.6f:F1} steer {_player.LastRideInput.Steer:F2} δ {Mathf.RadToDeg(truck.SteerAngle):F1}° "
                + string.Join("  ", truck.Train.Bodies.Select((b, k) => $"#{k} ψ' {b.W:F2} lat {b.RollLat:F2}/{b.Srt:F2}g"))
                + $"  joints {string.Join(" ", truck.Articulation.Take(truck.SectionCount - 1).Select(j => $"{Mathf.RadToDeg(j):F0}"))}");
        }
        _sinceReport += delta;
        if (_sinceReport >= 2.0)
        {
            _sinceReport = 0;
            GD.Print($"[truckprobe] t={_t,5:F1}s  {_player.RideSpeed * 3.6f,5:F1} km/h  {truck.GearLabel} {truck.Rpm:F0} rpm"
                + $"  joints {string.Join(" ", truck.Articulation.Take(truck.SectionCount - 1).Select(j => $"{Mathf.RadToDeg(j):F0}°"))}"
                + $"  off-centre {string.Join(" ", Enumerable.Range(0, truck.SectionCount).Select(k => { var b = truck.Train.Bodies[k]; return $"{Off(_player.GlobalTransform * (truck.NodeLocal(k) * new Vector3(0, 0, -(b.CgAt - HeavyTrain.RearGroupAt(b.Spec)))), out _):F1}"; }))} m"
                + $"  roll {truck.Train.WorstRoll:F2}  section hits {_player.SectionHits}  on {Audio.Surfaces.At(_chunks, pos, false)}"
                + $"  mem {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1e6:F0} MB  objects {Performance.GetMonitor(Performance.Monitor.ObjectCount):F0}  nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):F0}");
        }
        _sinceShot += delta;
        if (_shots != null && _sinceShot >= 10.0)
        {
            _sinceShot = 0;
            string file = $"{_shots}_{_shotCount++}.png";
            GD.Print(GetViewport().GetTexture().GetImage().SavePng(file) == Error.Ok ? $"[truckprobe] wrote {file}" : $"[truckprobe] FAILED to write {file}");
        }

        if (_t < _seconds && _near < _route.Centre.Count - 20) return;
        bool finite = float.IsFinite(_player.RideSpeed) && truck.Articulation.All(float.IsFinite);
        GD.Print($"[truckprobe] {_travelled:F0} m in {_t:F0} s, top {_top * 3.6f:F0} km/h, joints at most {Mathf.RadToDeg(_worstJoint):F0}°, "
            + $"rear axles at most {string.Join(" / ", _worstSection.Select(w => w.ToString("F1", CultureInfo.InvariantCulture)))} m off the centreline, "
            + $"{_worstOff:F1} m past the edge at worst, {_offTime:F1} s more than 1.5 m past it; {_player.SectionHits} section hits");
        bool ok = finite && _travelled > 100f && _offTime < 4f;
        GD.Print(ok ? "[truckprobe] RESULT: ok" : "[truckprobe] RESULT: FAILED");
        Finish(ok ? 0 : 1);
    }

    /// <summary>Distance of a point from the centreline near where the tractor is, and the road's width there.</summary>
    private float Off(Vector3 p, out float width)
    {
        var c = _route!.Centre;
        float best = float.MaxValue;
        int bi = _near;
        for (int i = Mathf.Max(0, _near - 40); i < Mathf.Min(c.Count - 1, _near + 40); i++)
        {
            var a = c[i];
            var ab = (c[i + 1] - a) with { Y = 0 };
            var ap = (p - a) with { Y = 0 };
            float t = Mathf.Clamp(ap.Dot(ab) / Mathf.Max(ab.LengthSquared(), 1e-4f), 0f, 1f);
            float d = (ap - ab * t).Length();
            if (d < best) { best = d; bi = i; }
        }
        width = _route.Width[bi];
        return best;
    }

    /// <summary>Pure pursuit on the centreline, and a speed the next bends allow.</summary>
    private RideInput Drive()
    {
        var c = _route!.Centre;
        var pos = _player!.GlobalPosition;
        float bestD = float.MaxValue;
        for (int i = Mathf.Max(0, _near - 5); i < Mathf.Min(c.Count, _near + 60); i++)
        {
            float d = ((c[i] - pos) with { Y = 0 }).LengthSquared();
            if (d < bestD) { bestD = d; _near = i; }
        }
        float v = _player.RideSpeed;
        float look = Mathf.Max(9f, v * 1.3f);
        int target = _near;
        float arc = _route.Arc[_near];
        while (target < c.Count - 1 && _route.Arc[target] - arc < look) target++;
        var fwd = -_player.GlobalTransform.Basis.Z with { Y = 0 };
        var to = (c[target] - pos) with { Y = 0 };
        float angle = Mathf.Atan2(-fwd.Cross(to).Y, fwd.Dot(to));   // + to the right
        float steer = Mathf.Clamp(angle * 2.2f * (1f + v / 12f), -1f, 1f);
        // a driver does not yank a loaded train round: no more lock than 0.15 g at this speed
        if (_player.Heavy is { } heavy && v > 3f)
        {
            float wheelbase = HeavyTrain.Wheelbase(heavy.Train.Bodies[0]);
            float lockScale = 1f / (1f + v / 9f);
            float most = Mathf.Atan(0.15f * 9.81f * wheelbase / (v * v)) / (heavy.Spec.MaxSteer * lockScale);
            steer = Mathf.Clamp(steer, -most, most);
        }

        // the tightest bend in the next 60 m sets the speed: ~0.2 g of side force for a heavy train
        float tight = float.MaxValue;
        for (int i = _near; i < c.Count - 2 && _route.Arc[i] - arc < 60f; i += 2)
        {
            var a = c[i]; var b = c[Mathf.Min(i + 4, c.Count - 1)]; var d = c[Mathf.Min(i + 8, c.Count - 1)];
            float r = Radius(a, b, d);
            tight = Mathf.Min(tight, r);
        }
        // and the arc pure pursuit itself asks for: 2·sin(angle)/look is its curvature
        float asked = look / Mathf.Max(2f * Mathf.Abs(Mathf.Sin(angle)), 1e-3f);
        float want = Mathf.Min(_kmh / 3.6f, Mathf.Sqrt(0.15f * 9.81f * Mathf.Min(tight, asked)));
        want = Mathf.Max(want, 2.5f);
        float err = want - v;
        return new RideInput(Mathf.Clamp(err * 0.5f, 0f, 1f), Mathf.Clamp(-err * 0.35f, 0f, 1f), steer, false);
    }

    /// <summary>
    /// Which bends of this road the train fits through (#70, the road-width check): at each bend,
    /// the width it sweeps (<see cref="HeavyTrain.SweptWidth"/>, its outer front corner on the
    /// outside edge) against the road's TLM width.
    /// </summary>
    private void RoadReport(RaceRoute route)
    {
        var truck = new Truck(HeavyCatalog.All[_index], _trailer >= 0 ? TrailerCatalog.Code(_trailer, _load) : 0, _load);
        var sections = truck.Train.Bodies.Select(b => b.Spec).ToList();
        int tight = 0;
        float worstR = float.MaxValue, worstNeed = 0f, worstWidth = 0f, worstAt = 0f;
        for (int i = 4; i < route.Centre.Count - 4; i += 2)
        {
            float r = Radius(route.Centre[i - 4], route.Centre[i], route.Centre[i + 4]);
            if (r > 150f) continue;
            float width = route.Width[i];
            float need = HeavyTrain.SweptWidth(sections, r + width * 0.5f);
            if (need > width + 0.5f) tight++;
            if (r < worstR) { worstR = r; worstNeed = need; worstWidth = width; worstAt = route.Arc[i]; }
        }
        GD.Print(worstR < float.MaxValue
            ? $"[truckprobe] road check: tightest bend R {worstR:F0} m at {worstAt:F0} m, the train sweeps {worstNeed:F1} m of a {worstWidth:F1} m road; {tight} spots where it needs more than the road and half a metre of verge"
            : "[truckprobe] road check: no bend tighter than 150 m");
    }

    private static float Radius(Vector3 a, Vector3 b, Vector3 c)
    {
        var p = new Vector2(a.X, a.Z); var q = new Vector2(b.X, b.Z); var r = new Vector2(c.X, c.Z);
        float area2 = Mathf.Abs((q - p).Cross(r - p));
        if (area2 < 1e-3f) return float.MaxValue;
        return (q - p).Length() * (r - q).Length() * (r - p).Length() / (2f * area2);
    }

    private void Finish(int code)
    {
        _done = true;
        GetTree().Quit(code);
    }
}
