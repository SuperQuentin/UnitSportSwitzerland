using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// <c>godot --path . -- --trafficcheck[,out.png] [--at E,N] [--time h]</c>: hovers a camera over
/// the nearest motorway (else the busiest road), lets the traffic run for 40 s, prints cars,
/// trains and speeds every 5 s, and fails if nothing ever moved.
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

    public TrafficProbe(Traffic traffic, Camera3D camera, string? shot)
    {
        _traffic = traffic;
        _camera = camera;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--trafficcheck"))
            {
                var p = a.Split(',');
                return (true, p.Length > 1 ? p[1] : null);
            }
        return (false, null);
    }

    public override void _Process(double delta)
    {
        _t += delta;
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
        if (_t > 20 && _traffic.Watch(_t > 32) is var (pos, dir))
        {
            float back = _t > 32 ? 35f : 11f, up = _t > 32 ? 28f : 4f;
            var eye = pos - dir * back + Vector3.Up * up + new Vector3(-dir.Z, 0, dir.X) * (_t > 32 ? 30f : 0f);
            _camera.GlobalTransform = new Transform3D(
                Player.Flyer.Orient(pos + dir * 6f - eye, Vector3.Up, Vector3.Forward), eye);
            if (_shot != null && Math.Abs(_t - 31) < delta)
                GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", "_car.png"));
        }

        _maxCars = Math.Max(_maxCars, _traffic.CarCount);
        _maxTrains = Math.Max(_maxTrains, _traffic.TrainCount);
        _maxSpeed = Math.Max(_maxSpeed, _traffic.AverageCarSpeed);
        if ((int)(_t / 5) != (int)((_t - delta) / 5))
            GD.Print($"[trafficcheck] t={_t:F0}s cars {_traffic.CarCount} (avg {_traffic.AverageCarSpeed * 3.6f:F0} km/h), "
                + $"trains {_traffic.TrainCount} (avg {_traffic.AverageTrainSpeed * 3.6f:F0} km/h)");

        if (_t < 40) return;
        if (_shot != null && GetViewport().GetTexture().GetImage().SavePng(_shot) == Error.Ok)
            GD.Print($"[trafficcheck] wrote {_shot}");
        bool ok = _maxCars > 0 && _maxSpeed > 2f;
        GD.Print(ok ? $"[trafficcheck] RESULT: ok (peak {_maxCars} cars, {_maxTrains} trains)"
                    : "[trafficcheck] RESULT: FAILED — no moving traffic");
        GetTree().Quit(ok ? 0 : 1);
        SetProcess(false);
    }
}
