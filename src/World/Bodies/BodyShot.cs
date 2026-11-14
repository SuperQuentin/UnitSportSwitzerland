using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// A picture from another body (#1848): <c>--bodyshot BODY,LAT,LON,HEIGHT_KM[,PITCH_DEG[,YAW_DEG]][,earth]</c> puts the
/// anchor on that body (moon, mars: any whose pyramid is under <c>--bodies</c>; the crossing's relabelling, <see cref="WorldOrigin.Rebase"/>, then a shift to the tangent frame
/// there), the spectator camera HEIGHT_KM over that place looking PITCH_DEG down toward YAW_DEG (from north), or straight
/// at the Earth with <c>earth</c>; waits for the globe's tiles to settle and saves
/// <c>test_output/bodies/BODY_LAT_LON_HEIGHTkm[_earth].png</c>, then quits (it waits 8 s for the world first). Windowed (a headless run draws nothing). Pass
/// <c>--frame earth --bodies DIR --sun real --date ...</c>; the RESULT line says what it drew. <c>sun,DLON</c> in place of
/// LAT,LON is the point under the sun then, DLON degrees east of it (the log names that point either way).
/// </summary>
public partial class BodyShot : Node
{
    public static bool Requested => CmdArgs.Value("--bodyshot") != null;

    private readonly WorldOrigin _origin;
    private readonly Func<BodyGlobe?> _globe;
    private double _t;
    private int _stage, _quiet;
    private string _file = "";

    public BodyShot(WorldOrigin origin, Func<BodyGlobe?> globe)
    {
        _origin = origin;
        _globe = globe;
        Name = "BodyShot";
    }

    public override void _Process(double delta)
    {
        _t += delta;
        switch (_stage)
        {
            case 0 when _t > 8:
                Go();
                _stage = 1;
                _t = 0;
                break;
            case 1:
                // settled: no tile drawn more or fewer for two seconds, or fifteen at most
                int drawn = _globe()?.DrawnTiles ?? 0;
                _quiet = drawn == _last ? _quiet + 1 : 0;
                _last = drawn;
                if ((_quiet > 120 && _t > 4) || _t > 15) { Save(drawn); _stage = 2; }
                break;
        }
    }

    private int _last = -1;

    private void Go()
    {
        var parts = CmdArgs.Value("--bodyshot")!.Split(',');
        string name = parts[0].Trim().ToLowerInvariant();
        var body = CelestialBody.All.ToArray().First(b => b.Name.ToLowerInvariant() == name);
        var sky = SkyClock.Now;
        // where the sun stands overhead now (planetocentric): "sun,DLON" in place of LAT,LON is that point's latitude,
        // DLON degrees east of it (negative: the morning side, toward the terminator)
        var toSun = Frames.Convert(BodyId.Sun, DVec3.Zero, body.Id, sky);
        double sunLat = Math.Atan2(toSun.Z, Math.Sqrt(toSun.X * toSun.X + toSun.Y * toSun.Y)) * 180 / Math.PI;
        double sunLon = Math.Atan2(toSun.Y, toSun.X) * 180 / Math.PI;
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[bodyshot] the sun is overhead {body.Name}'s {sunLat:F1}, {sunLon:F1}"));
        bool bySun = parts[1].Trim() == "sun";
        double lat = bySun ? sunLat : double.Parse(parts[1], CultureInfo.InvariantCulture);
        double lon = bySun ? Math.IEEERemainder(sunLon + double.Parse(parts[2], CultureInfo.InvariantCulture), 360) : double.Parse(parts[2], CultureInfo.InvariantCulture);
        double km = double.Parse(parts[3], CultureInfo.InvariantCulture);
        bool earth = parts.Contains("earth");
        var numbers = parts.Skip(4).Where(p => p != "earth").Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        double pitch = numbers.Length > 0 ? numbers[0] : 20, yaw = numbers.Length > 1 ? numbers[1] : 0;
        _origin.Rebase(body.Id, sky);
        var at = body.Shape.ToEcef(new Geodetic(lat, lon, km * 1000));
        OriginShifter.Instance?.ShiftTo(0, 0, exact: true, axes: FrameAxes.Enu(at, body.Id));
        var cam = GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false) as Camera3D;
        if (cam == null) { Fail("no spectator camera"); return; }
        cam.MakeCurrent();
        cam.GlobalPosition = Vector3.Zero;
        if (earth && _origin.Frame.Axes is { } axes)
        {
            var e = Frames.Convert(BodyId.Earth, DVec3.Zero, body.Id, sky).AsEcef;
            var (x, y, z) = axes.FromEcef(e);
            cam.LookAt(new Vector3((float)x, (float)y, (float)z).Normalized() * 1000, Vector3.Up);
        }
        else cam.Rotation = new Vector3(Mathf.DegToRad(-(float)pitch), Mathf.DegToRad(-(float)yaw), 0);
        string tag = string.Create(CultureInfo.InvariantCulture, $"{name}_{lat:0.#}_{lon:0.#}_{km:0.#}km{(earth ? "_earth" : "")}");
        _file = $"res://test_output/bodies/{tag}.png";
        GD.Print($"[bodyshot] on {body.Name} at {lat}, {lon}, {km} km up{(earth ? ", toward the Earth" : $", {pitch}° down toward {yaw}°")}");
    }

    private void Save(int drawn)
    {
        var path = ProjectSettings.GlobalizePath(_file);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var image = GetViewport().GetTexture().GetImage();
        image.SavePng(path);
        var sun = _globe()?.Sun ?? Vector3.Zero;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[bodyshot] the sun {Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(sun.Y, -1f, 1f))):F1}° over the camera's horizon"));
        GD.Print($"[bodyshot] RESULT: ok — {drawn} tiles drawn, saved {path}");
        GetTree().Quit();
    }

    private void Fail(string why)
    {
        GD.PrintErr($"[bodyshot] RESULT: FAILED — {why}");
        GetTree().Quit(1);
    }
}
