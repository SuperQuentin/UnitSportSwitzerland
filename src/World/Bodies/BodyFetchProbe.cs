using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--bodyfetch BODY,LAT,LON</c> (#1849, <c>tools/bodystreamcheck.sh</c>): once the server's index for that body came, the
/// finest tile under that place (planetocentric, east) is read through the body's chain on a worker, so it streams from the
/// server as the near ground's would; logs where it came from and its height there, then quits. Standing on another body
/// online waits for C3b, so the stream is checked without the camera going there.
/// </summary>
public partial class BodyFetchProbe(CelestialBody body, Terrain.Leaves.PyramidSource chain) : Node
{
    private static string[]? Spec => CmdArgs.Value("--bodyfetch")?.Split(',');

    public static bool Wants(CelestialBody body) => Spec is { Length: 3 } s && s[0].Equals(body.Name, StringComparison.OrdinalIgnoreCase);

    private Task<string>? _task;

    public override void _Ready()
    {
        var s = Spec!;
        double lat = double.Parse(s[1], CultureInfo.InvariantCulture), lon = double.Parse(s[2], CultureInfo.InvariantCulture);
        int level = chain.MaxLevel;
        _task = Task.Run(() =>
        {
            var cube = body.Cube;
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
            var n = new EcefPos(Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la));
            var (face, u, v) = cube.FromNormal(n);
            var key = CubeKey.FromFace(face, u, v, level);
            bool streamed = chain.IsStreamed(key);
            var tile = chain.Heights(key);
            string got = tile == null ? "did not come"
                : string.Create(CultureInfo.InvariantCulture, $"came, {tile.Bits} bits, centre {tile.HeightAt(tile.Side / 2, tile.Side / 2):F0} m");
            return string.Create(CultureInfo.InvariantCulture,
                $"{body.Name} {lat:F2},{lon:F2}: tile {key} (level {level}, {(streamed ? "the server's" : "local or shipped")}) {got}");
        });
    }

    public override void _Process(double delta)
    {
        if (_task is not { IsCompleted: true } t) return;
        string line = t.IsCompletedSuccessfully ? t.Result : $"failed: {t.Exception?.GetBaseException().Message}";
        GD.Print($"[bodyfetch] {line}");
        GD.Print($"[bodyfetch] RESULT: {(line.Contains("came,") && line.Contains("the server's") ? "ok" : "FAILED")}");
        _task = null;
        GetTree().Quit();
    }
}
