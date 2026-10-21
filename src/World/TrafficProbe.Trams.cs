using Godot;

namespace UnitSport.World;

/// <summary>
/// <c>--trafficcheck --mover tram</c>'s own part (#1057): every 5 s a line of the trams' counts (spawned, top speed, the tram graph's
/// paths, stops at their lights, lines crossed); at the end it fails on a tram over its line on red.
/// </summary>
public partial class TrafficProbe
{
    /// <summary>The picture of a tram at its red (windowed, <c>--trafficcheck,out.png --mover tram</c>): frames left before it is saved, -1 taken.</summary>
    private int _tramShot = int.MaxValue;
    private Vector3 _tramAt, _tramDir;
    private int _tramView;

    partial void TramsTick(double delta)
    {
        // windowed with a picture: once a tram stands first at its red, two shots: from above behind it (its line, the junction),
        // then from over its front looking on (its bar head, lit, beside the line)
        if (_kind == MoverKind.Tram && _shot != null && _tramShot >= 0)
        {
            if (_tramShot == int.MaxValue && _traffic.TramAtRed() is var (pos, dir))
                (_tramAt, _tramDir, _tramShot, _tramView) = (pos, dir, 30, 0);
            if (_tramShot != int.MaxValue)
            {
                var side = new Vector3(-_tramDir.Z, 0f, _tramDir.X);
                var (eye, look) = _tramView == 0
                    ? (_tramAt - _tramDir * 16f + Vector3.Up * 10f, _tramAt + _tramDir * 18f + Vector3.Up * 2f)
                    : (_tramAt + _tramDir * 1f + Vector3.Up * 4.2f, _tramAt + _tramDir * 14f + side * 3f + Vector3.Up * 3.2f);
                _camera.GlobalTransform = new Transform3D(Player.Flyer.Orient(look - eye, Vector3.Up, Vector3.Forward), eye);
                if (--_tramShot == 0)
                {
                    string path = _shot.Replace(".png", _tramView == 0 ? "_tram_red.png" : "_tram_red_head.png");
                    GetViewport().GetTexture().GetImage().SavePng(path);
                    GD.Print($"[trafficcheck] wrote {path}: a tram at its red");
                    (_tramShot, _tramView) = _tramView == 0 ? (30, 1) : (-1, 2);
                }
            }
        }
        if (_kind != MoverKind.Tram || (int)(_t / 5) == (int)((_t - delta) / 5)) return;
        GD.Print($"[trafficcheck] t={_t:F0}s trams: {_traffic.CountOf(MoverKind.Tram)} live, {_traffic.TramsSpawned} spawned, top {_traffic.TramTopSpeed * 3.6f:F0} km/h, "
            + $"{_traffic.GraphOf(MoverKind.Tram)?.TramPaths ?? 0} junction paths, {_traffic.GraphOf(MoverKind.Tram)?.TramStopCount ?? 0} stops; {_traffic.TramStopsServed} stops served; at their lights {_traffic.TramRedStops} stops, {_traffic.TramLinesCrossed} lines crossed, {_traffic.TramRedRuns} on red");
    }

    partial void TramsReport(ref string? failure)
    {
        if (_kind != MoverKind.Tram && Core.GameSettings.Current.Trams == 0) return;
        GD.Print($"[trafficcheck] trams: {_traffic.TramsSpawned} spawned, {_traffic.GraphOf(MoverKind.Tram)?.Edges.Count ?? 0} track edges, "
            + $"{_traffic.GraphOf(MoverKind.Tram)?.TramPaths ?? 0} junction paths, {_traffic.GraphOf(MoverKind.Tram)?.TramStopCount ?? 0} stops ({_traffic.TramStopsServed} served); at their lights {_traffic.TramRedStops} stops at red, "
            + $"{_traffic.TramLinesCrossed} lines crossed, {_traffic.TramRedRuns} crossed on red; let go from red ahead of their approach's cars {_traffic.TramsLedCars}, with them {_traffic.TramsWithCars}");
        if (_traffic.TramRedRuns > 0) failure ??= $"{_traffic.TramRedRuns} trams crossed their line on red";
    }
}
