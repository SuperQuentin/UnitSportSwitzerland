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
/// <c>later:H</c> (#1992) then moves the game's clock H hours on and saves a second picture (<c>..._later.png</c>): the
/// local day's sky moved (the log gives the subsolar point and the Earth's direction in the body's axes both times).
/// <c>fly:V</c> (#2035) then flies the camera toward the body's centre at V m/s for 6 s (as the spectator's top speed
/// does, the origin following it), saves <c>..._fly.png</c> in flight, stops, and saves <c>..._stopped.png</c> 3 s later
/// (with <c>--starwatch</c>: the stars counted all along).
/// HEIGHT_KM ending in <c>g</c> (#2016, <c>0.002g</c>) is over the ground there (its heights: data, craters, relief), not the
/// reference: a walker's eye on Mars' ground kilometres under its datum; the picture then also waits for the ground's squares.
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
        // first in the frame, as the spectator's own input: what places itself by the camera (the globes, the air) follows
        ProcessPriority = -1000;
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
                // the ground's squares too (#2016): a picture from the ground waits for its near, fine and medium squares
                int settled = drawn + (_globe()?.Ground is { } ground ? ground.Settled : 0);
                _quiet = settled == _last ? _quiet + 1 : 0;
                _last = settled;
                if ((_quiet > 120 && _t > 4) || _t > 15)
                {
                    Save(drawn, quit: _laterHours == 0 && _flySpeed == 0);
                    _stage = 2;
                    _t = 0;
                    if (_laterHours != 0)
                    {
                        // online the world's clock; offline the sky's own (a check's profile holds it still)
                        if (WorldClock.Active) WorldClock.AddHours(_laterHours);
                        else if (DayNight.Instance is { } dn) dn.Hour = TimeCommand.Wrap(dn.Hour + _laterHours);
                        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[bodyshot] the clock {_laterHours} h on"));
                    }
                }
                break;
            case 2 when _laterHours != 0 && _t > 3:
                _file = _file.Replace(".png", "_later.png");
                Save(_globe()?.DrawnTiles ?? 0, quit: true);
                _stage = 3;
                break;
            case 2 when _flySpeed != 0:
                Fly(delta);
                if (_t > 5.5 && !_flySaved)
                {
                    _flySaved = true;
                    var file = _file;
                    _file = file.Replace(".png", "_fly.png");
                    Save(_globe()?.DrawnTiles ?? 0, quit: false);
                    _file = file;
                }
                if (_t > 6) { _stage = 3; _t = 0; GD.Print("[bodyshot] stopped"); }
                break;
            case 3 when _flySpeed != 0 && _t > 3:
                _file = _file.Replace(".png", "_stopped.png");
                Save(_globe()?.DrawnTiles ?? 0, quit: true);
                _stage = 4;
                break;
        }
    }

    private int _last = -1;
    private double _laterHours, _flySpeed;
    private bool _flySaved;

    /// <summary><c>fly:V</c>: the camera toward the body's centre at V m/s, never nearer than two of its radii.</summary>
    private void Fly(double delta)
    {
        if (GetViewport()?.GetCamera3D() is not { } cam || _origin.Frame.Axes is not { } axes) return;
        var (x, y, z) = axes.FromEcef(new EcefPos(0, 0, 0));
        var to = new Vector3((float)(x - cam.GlobalPosition.X), (float)(y - cam.GlobalPosition.Y), (float)(z - cam.GlobalPosition.Z));
        double left = to.Length() - 2 * CelestialBody.Of(_body).Shape.A;
        if (left <= 0) return;
        cam.GlobalPosition += to.Normalized() * (float)Math.Min(_flySpeed * delta, left);
    }
    private BodyId _body;

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
        bool overGround = parts[3].Trim().EndsWith('g');
        double km = double.Parse(parts[3].Trim().TrimEnd('g'), CultureInfo.InvariantCulture);
        bool earth = parts.Contains("earth");
        if (parts.FirstOrDefault(p => p.StartsWith("later:")) is { } later)
            _laterHours = double.Parse(later[6..], CultureInfo.InvariantCulture);
        if (parts.FirstOrDefault(p => p.StartsWith("fly:")) is { } fly)
            _flySpeed = double.Parse(fly[4..], CultureInfo.InvariantCulture);
        _body = body.Id;
        var numbers = parts.Skip(4).Where(p => p != "earth" && !p.StartsWith("later:") && !p.StartsWith("fly:")).Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        double pitch = numbers.Length > 0 ? numbers[0] : 20, yaw = numbers.Length > 1 ? numbers[1] : 0;
        _origin.Rebase(body.Id, sky);
        double h = km * 1000;
        if (overGround && _globe()?.Ground is { } g)
            h += g.Heights.At(CubeSphere.NormalAt(lat, lon));
        var at = body.Shape.ToEcef(new Geodetic(lat, lon, h));
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
        string tag = string.Create(CultureInfo.InvariantCulture, $"{name}_{lat:0.#}_{lon:0.#}_{km:0.###}km{(overGround ? "g" : "")}{(earth ? "_earth" : "")}");
        _file = $"res://test_output/bodies/{tag}.png";
        GD.Print($"[bodyshot] on {body.Name} at {lat}, {lon}, {km} km up{(earth ? ", toward the Earth" : $", {pitch}° down toward {yaw}°")}");
    }

    private void Save(int drawn, bool quit = true)
    {
        // where the sun stands overhead and where the Earth is, in the body's own axes (#1992: the local day moves the one)
        var sky = SkyClock.Now;
        static (double Lat, double Lon) LatLon(DVec3 v) =>
            (Math.Atan2(v.Z, Math.Sqrt(v.X * v.X + v.Y * v.Y)) * 180 / Math.PI, Math.Atan2(v.Y, v.X) * 180 / Math.PI);
        var (sLat, sLon) = LatLon(Frames.Convert(BodyId.Sun, DVec3.Zero, _body, sky));
        var (eLat, eLon) = _body == BodyId.Earth ? (0.0, 0.0) : LatLon(Frames.Convert(BodyId.Earth, DVec3.Zero, _body, sky));
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[bodyshot] at {WorldClock.CurrentHour:F2} h: the sun over {sLat:F2}, {sLon:F2}; the Earth over {eLat:F2}, {eLon:F2}; local turn {sky.LocalTurn * 180 / Math.PI:F2}°"));
        var path = ProjectSettings.GlobalizePath(_file);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var image = GetViewport().GetTexture().GetImage();
        image.SavePng(path);
        var sun = _globe()?.Sun ?? Vector3.Zero;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[bodyshot] the sun {Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(sun.Y, -1f, 1f))):F1}° over the camera's horizon"));
        GD.Print($"[bodyshot] RESULT: ok — {drawn} tiles drawn, saved {path}");
        if (quit) GetTree().Quit();
    }

    private void Fail(string why)
    {
        GD.PrintErr($"[bodyshot] RESULT: FAILED — {why}");
        GetTree().Quit(1);
    }
}
