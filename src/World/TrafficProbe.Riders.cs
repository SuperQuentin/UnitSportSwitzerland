using Godot;
using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// <c>--trafficcheck</c>'s riders (#1056): with riders on, what they did at the lights (stops at red, red runs, amber clears, green
/// crossings, rights on red under signal 5.18, crossings on the bike group's lead green, lefts on a bike group), how many cars waited
/// at a permissive turn for a rider, where they rode (bike lane, path, car lane, way, junction paths) and how far a settled rider
/// strayed from its line. Fails on a rider's red run, or one more than 0.5 m past its street's outer edge (sides included).
/// </summary>
public partial class TrafficProbe
{
    private bool RidersOn => _kind == MoverKind.Rider || GameSettings.Current.TrafficRiders > 0;
    private int _riderShot = int.MaxValue;
    private Vector3 _riderAt, _riderDir;
    private Transform3D _riderView;

    partial void RidersTick(double delta)
    {
        if (!RidersOn) return;
        _traffic.RiderProbeTick();
        // --dense: riders come into the junction too, twice a second (and those far away make room)
        if (_dense && _placed && (int)(_t * 2) != (int)((_t - delta) * 2)) _traffic.SpawnRiderNear(_camera.GlobalPosition, 120f);
        // with a picture: the traced rider turning left from the car lane (#1192) at its red, from behind it, then the camera back
        if (_shot != null && _riderShot >= 0)
        {
            if (_placed) _traffic.CarLeftNear ??= _spot;
            if (_riderShot == int.MaxValue && _traffic.CarLeftAtRed() is var (pos, dir))
                (_riderAt, _riderDir, _riderShot, _riderView) = (pos, dir, 30, _camera.GlobalTransform);
            if (_riderShot != int.MaxValue)
            {
                var side = new Vector3(-_riderDir.Z, 0f, _riderDir.X);
                var eye = _riderAt - _riderDir * 9f + side * 2.5f + Vector3.Up * 2.6f;
                _camera.GlobalTransform = new Transform3D(Player.Flyer.Orient(_riderAt + _riderDir * 14f + Vector3.Up * 2f - eye, Vector3.Up, Vector3.Forward), eye);
                if (--_riderShot == 0)
                {
                    string path = _shot.Replace(".png", "_rider_left.png");
                    GetViewport().GetTexture().GetImage().SavePng(path);
                    GD.Print($"[trafficcheck] wrote {path}: a rider turning left from the car lane at its red (#1192)");
                    _camera.GlobalTransform = _riderView;
                    _riderShot = -1;
                }
            }
        }
    }

    partial void RidersReport(ref string? failure)
    {
        if (!RidersOn) return;
        var r = _traffic.Riders;
        var (lanes, tracks, roads, ways, bikePaths, carPaths) = _traffic.RideGraphMakeUp();
        long all = Math.Max(1, r.On.Sum());
        var ror = _traffic.RightOnRedArms();
        string Share(int i) => $"{100.0 * r.On[i] / all:F0}%";
        GD.Print($"[trafficcheck] riders (#1056): graph {lanes} lane pieces, {tracks} path pieces, {roads} streets with neither, {ways} ways, "
            + $"{bikePaths} riders' junction paths, {carPaths} cars'; rode on lanes {Share(1)}, paths {Share(2)}, car lanes {Share(0)}, ways {Share(3)}, "
            + $"riders' junction paths {Share(4)}, cars' {Share(5)}");
        GD.Print($"[trafficcheck] riders at the lights: {r.RedStops} stops at red, {r.RedRuns} red runs, {r.AmberClears} amber clears, "
            + $"{r.GreenCrossings} green crossings ({r.Leads} on the bike lead green, {r.BikeLefts} lefts on a bike group), {r.RightOnRed} rights on red (5.18, at {ror.Count} approaches{(ror.First is { } at ? $", first {at}" : "")}); "
            + $"cars waiting for a rider at a permissive turn {r.CarsWaited}; past their street's edge most {Math.Max(0f, r.OffWay):F2} m; settled off their line most {r.LateralOff:F2} m ({r.LateralOver} ticks over 1 m)");
        if (_traffic.CarLeft is { Found: true } cl)
            GD.Print(System.FormattableString.Invariant($"[trafficcheck] a rider turning left from its bike lane by the car lane (#1192): its path seen {cl.SeenAt:F0} m before it, lane at {cl.LaneLateral:F1} m, path from {cl.PathOffset:F1} m; ")
                + System.FormattableString.Invariant($"55 m before {cl.At55:F1}, 30 m {cl.At30:F1}, 10 m {cl.At10:F1}; stopped at red {cl.StoppedAtRed}; crossed on {cl.Crossed ?? "-"}"));
        if (r.RedRuns > 0) failure ??= $"{r.RedRuns} riders ran a red";
        else if (r.OffWay > 0.5f) failure ??= $"a rider rode {r.OffWay:F1} m past its street's edge";
    }
}
