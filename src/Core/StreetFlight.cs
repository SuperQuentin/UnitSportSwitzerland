using System.Globalization;
using Godot;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// <c>godot --path . -- --flystreet speed,seconds --at E,N --to E,N</c> (#553): the street-level twin
/// of <see cref="FlightProbe"/>. The spectator camera at eye height along the road from the spawn
/// toward a point (<see cref="RaceRoute"/>, the route <c>--drivecheck --to</c> races), looking down
/// the street, at a steady speed, and the same frame-time report. A camera between buildings is what
/// occlusion culling and model LODs are for, and a flight at 440 m never shows them; a driven car
/// gets stuck and is reset, so its frames differ from run to run, where this one is the same path
/// every time.
/// </summary>
public partial class StreetFlight : Node
{
    /// <summary>Metres above the road: a driver's eyes.</summary>
    private const float EyeHeight = 1.8f;

    private readonly SpectatorCamera _camera;
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly float _speed;
    private readonly double _seconds;
    private readonly List<double> _frames = new();
    private RaceRoute? _route;
    private bool _requested, _done;
    private double _elapsed, _warm;
    private float _s;

    public StreetFlight(SpectatorCamera camera, ChunkManager chunks, WorldOrigin origin, float speed, double seconds)
    {
        Name = "StreetFlight";
        _camera = camera;
        _chunks = chunks;
        _origin = origin;
        _speed = speed;
        _seconds = seconds;
    }

    public static (float Speed, double Seconds)? ParseArgs() =>
        CmdArgs.Value("--flystreet")?.Split(',') is [var v, var s]
        && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float speed)
        && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? (speed, seconds) : null;

    public override void _Process(double delta)
    {
        if (_done) return;
        _camera.Current = true;
        if (!_requested) { Request(); return; }
        if (_route == null) return;
        _route.Follow(_origin.Frame);

        // let the first arrival settle before moving, as FlightProbe does
        _warm += delta;
        if (_warm < 4) { Pose(0); return; }

        _elapsed += delta;
        _s += _speed * (float)delta;
        Pose(_s);
        _frames.Add(delta * 1000);
        if (_elapsed < _seconds && _s < _route.Arc[^1] - 20f) return;

        _done = true;
        _frames.Sort();
        int n = _frames.Count;
        int over20 = _frames.Count(f => f > 20), over33 = _frames.Count(f => f > 33);
        GD.Print($"[fly] street: {n} frames over {_elapsed:F0} s and {_s:F0} m at {_speed:F0} m/s: "
            + $"p50 {_frames[n / 2]:F1} ms, p95 {_frames[(int)(n * 0.95)]:F1}, p99 {_frames[(int)(n * 0.99)]:F1}, "
            + $"max {_frames[^1]:F1}; >20 ms: {over20}, >33 ms: {over33}");
        GD.Print($"[fly] mem={Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0:F0}MB "
            + $"prims={Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)}");
        GetTree().Quit(0);
    }

    private void Request()
    {
        if (_chunks.Source is not { } source) return;
        _requested = true;
        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);
        Vector3? to = CmdArgs.Value("--to")?.Split(',') is [var te, var tn]
            && double.TryParse(te, NumberStyles.Float, CultureInfo.InvariantCulture, out double toE)
            && double.TryParse(tn, NumberStyles.Float, CultureInfo.InvariantCulture, out double toN) ? _origin.ToWorld(toE, toN, 0) : null;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var route = await RaceRoute.BuildAsync(source, _origin, at, toward: to);
            Callable.From(() =>
            {
                if (route == null) { GD.Print("[fly] street: no road near the spawn"); GetTree().Quit(1); return; }
                _route = route;
                GD.Print($"[fly] street: {route.Class}, {route.Arc[^1]:F0} m of road");
            }).CallDeferred();
        });
    }

    /// <summary>The camera at <paramref name="s"/> metres along the road, looking 15 m ahead along it.</summary>
    private void Pose(float s)
    {
        var eye = At(s) + Vector3.Up * EyeHeight;
        var ahead = At(s + 15f) + Vector3.Up * (EyeHeight - 0.3f);
        if ((ahead - eye).LengthSquared() < 0.01f) return;
        _camera.GlobalTransform = new Transform3D(Basis.LookingAt(ahead - eye, Vector3.Up), eye);
    }

    private Vector3 At(float s)
    {
        var arc = _route!.Arc;
        var c = _route.Centre;
        if (s <= 0) return c[0];
        if (s >= arc[^1]) return c[^1];
        int i = arc.BinarySearch(s);
        if (i < 0) i = ~i;
        i = Math.Clamp(i, 1, arc.Count - 1);
        float t = (s - arc[i - 1]) / Math.Max(arc[i] - arc[i - 1], 1e-3f);
        return c[i - 1].Lerp(c[i], t);
    }
}
