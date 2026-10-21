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

    partial void RidersTick(double delta)
    {
        if (!RidersOn) return;
        _traffic.RiderProbeTick();
        // --dense: riders come into the junction too, twice a second (and those far away make room)
        if (_dense && _placed && (int)(_t * 2) != (int)((_t - delta) * 2)) _traffic.SpawnRiderNear(_camera.GlobalPosition, 120f);
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
        if (r.RedRuns > 0) failure ??= $"{r.RedRuns} riders ran a red";
        else if (r.OffWay > 0.5f) failure ??= $"a rider rode {r.OffWay:F1} m past its street's edge";
    }
}
