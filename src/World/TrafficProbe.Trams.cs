using Godot;

namespace UnitSport.World;

/// <summary>
/// <c>--trafficcheck --mover tram</c>'s own part (#1057): every 5 s a line of the trams' counts (spawned, top speed, the tram graph's
/// paths, stops at their lights, lines crossed); at the end it fails on a tram over its line on red.
/// </summary>
public partial class TrafficProbe
{
    partial void TramsTick(double delta)
    {
        if (_kind != MoverKind.Tram || (int)(_t / 5) == (int)((_t - delta) / 5)) return;
        GD.Print($"[trafficcheck] t={_t:F0}s trams: {_traffic.CountOf(MoverKind.Tram)} live, {_traffic.TramsSpawned} spawned, top {_traffic.TramTopSpeed * 3.6f:F0} km/h, "
            + $"{_traffic.GraphOf(MoverKind.Tram)?.TramPaths ?? 0} junction paths; at their lights {_traffic.TramRedStops} stops, {_traffic.TramLinesCrossed} lines crossed, {_traffic.TramRedRuns} on red");
    }

    partial void TramsReport(ref string? failure)
    {
        if (_kind != MoverKind.Tram && Core.GameSettings.Current.Trams == 0) return;
        GD.Print($"[trafficcheck] trams: {_traffic.TramsSpawned} spawned, {_traffic.GraphOf(MoverKind.Tram)?.Edges.Count ?? 0} track edges, "
            + $"{_traffic.GraphOf(MoverKind.Tram)?.TramPaths ?? 0} junction paths; at their lights {_traffic.TramRedStops} stops at red, "
            + $"{_traffic.TramLinesCrossed} lines crossed, {_traffic.TramRedRuns} crossed on red");
        if (_traffic.TramRedRuns > 0) failure ??= $"{_traffic.TramRedRuns} trams crossed their line on red";
    }
}
