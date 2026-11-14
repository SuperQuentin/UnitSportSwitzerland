using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;
using UnitSport.World.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// <c>--moonsites [id,id,...]</c> (#1939): visits every Moon site (or the ones named) in daylight. For each it moves the
/// bodies' clock to a morning with the sun ~30° up there (the sky's own moment, shifted by whole lunar hours), puts the
/// anchor and the spectator camera on the site, waits for the near ground and the site to be built, checks the ground
/// under every part (slope at most <see cref="MaxSlopeDeg"/>, no part floating or buried) and, windowed, saves
/// <c>test_output/moon/site_&lt;id&gt;.png</c>. RESULT PASS or FAIL. Pass <c>--frame earth --bodies DIR --sun real</c>.
/// </summary>
public partial class MoonSitesCheck : Node
{
    public static bool Requested => CmdArgs.Has("--moonsites");

    /// <summary>The steepest ground a lander or a rover may stand on, degrees.</summary>
    public const double MaxSlopeDeg = 12;

    private readonly WorldOrigin _origin;
    private readonly Func<BodyGround?> _ground;
    private readonly List<MoonSite> _sites;
    private readonly List<string> _fails = new();
    private int _index = -1, _builtAt, _frames;
    private double _t, _days;
    private bool _headless;
    private Func<SkyMoment>? _baseSky;

    private readonly Func<Player.FootPlayer?> _local;

    public MoonSitesCheck(WorldOrigin origin, Func<BodyGround?> ground, Func<Player.FootPlayer?> local)
    {
        _origin = origin;
        _ground = ground;
        _local = local;
        Name = "MoonSitesCheck";
        var only = CmdArgs.Value("--moonsites");
        // the named ones in the order given (the film dances at the last), or every site
        var ids = string.IsNullOrEmpty(only) || only.StartsWith("--") ? null : only.Split(',').Select(s => s.Trim()).ToList();
        _sites = ids == null ? MoonSites.All.ToList() : ids.Select(MoonSites.Find).OfType<MoonSite>().ToList();
    }

    public override void _Ready()
    {
        _headless = DisplayServer.GetName() == "headless";
        _baseSky = SkyClock.Live;
        // the pictures at 1280x720 (the test profile's window is 640x360)
        if (!_headless) DisplayServer.WindowSetSize(new Vector2I(1280, 720));
        GD.Print($"[moonsites] {_sites.Count} site(s) to visit{(_headless ? " (headless: no pictures)" : "")}");
    }

    public override void _Process(double delta)
    {
        _t += delta;
        if (_dance > 0)
        {
            DanceStep(delta);
            return;
        }
        if (_index < 0)
        {
            if (_t < 8) return;
            if (_ground() == null) { Finish("no Moon ground (--frame earth --bodies DIR)"); return; }
            Next();
            return;
        }
        var site = _sites[_index];
        var ground = _ground()!;
        bool placed = MoonSurface.Active?.Sites.TryGetValue(site.Id, out var p) == true && p!.TracesIn;
        if (ground.Built > _builtAt && placed && ground.MediumBuilt > 0) _frames++;
        // a second of frames on the built ground, then the picture and the next site
        if (_frames > 60 && _t > 3)
        {
            Judge(site, ground);
            Next();
        }
        else if (_t > 40)
        {
            _fails.Add($"{site.Id}: the ground or the site was not built in 40 s (squares {ground.Built}, placed {placed})");
            Next();
        }
    }

    private void Next()
    {
        _index++;
        _t = 0;
        _frames = 0;
        if (_index >= _sites.Count)
        {
            if (Dance && _fails.Count == 0 && _sites.Count > 0) { _index = _sites.Count - 1; _dance = 1; return; }
            Finish(_fails.Count == 0 ? null : string.Join("; ", _fails));
            return;
        }
        var s = _sites[_index];
        var ground = _ground()!;
        _builtAt = ground.Built;
        // a morning there: whole hours of the bodies' clock until the sun stands ~30° up, rising
        _days = MorningDays(s);
        var baseSky = _baseSky;
        double days = _days;
        SkyClock.Live = () =>
        {
            var m = baseSky?.Invoke() ?? SkyMoment.At(DateTime.UtcNow);
            return m with { Utc = m.Utc.AddDays(days) };
        };
        var sky = SkyClock.Now;
        _origin.Rebase(BodyId.Moon, sky);
        var n = s.Normal;
        var g = ground.Heights.Ground(n);
        OriginShifter.Instance?.ShiftTo(0, 0, exact: true, axes: FrameAxes.Enu(g, BodyId.Moon));
        if (GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false) is not Camera3D cam)
        {
            Finish("no spectator camera");
            return;
        }
        cam.MakeCurrent();
        if (!_headless)
            foreach (var node in GetTree().Root.FindChildren("*", "CanvasLayer", true, false))
                ((CanvasLayer)node).Visible = false;
        // the view: from the sun's side and 40° round, far enough to hold the site's main part, looking at it
        var sun = SunLocal(s, sky);
        double sunAz = Math.Atan2(sun.East, sun.North);
        double az = sunAz + 0.7;
        // an impact: the crater's own point, the debris on its rim in view
        var main = s.Kind == MoonSiteKind.Impact ? new SitePart(MoonPart.SivbDebris, 0, 0, 0) : s.Parts[0];
        double dist = main.Part switch
        {
            MoonPart.DescentStage => 20, MoonPart.RocketWreck => 48, MoonPart.SivbDebris => 55, MoonPart.ChangeLander or MoonPart.LunaStage
                or MoonPart.LunaRampStage => 11, MoonPart.Flag => 5.5, _ => 7,
        };
        double up = Math.Max(1.8, dist * 0.32);
        var eye = new Vector3((float)(main.East + Math.Sin(az) * dist), (float)up, (float)-(main.North + Math.Cos(az) * dist));
        var look = new Vector3((float)main.East, (float)(dist * 0.06), (float)-main.North);
        // the frame is the site's tangent at its ground point: lift the eye over the ground under it
        var eyeN = MoonSites.Offset(s, eye.X, -eye.Z);
        var axes = _origin.Frame.Axes!;
        var (_, gy, _) = axes.FromEcef(ground.Heights.Ground(eyeN));
        eye.Y += (float)gy;
        var (_, ly, _) = axes.FromEcef(ground.Heights.Ground(MoonSites.Offset(s, main.East, main.North)));
        look.Y += (float)ly;
        cam.GlobalPosition = eye;
        cam.LookAt(look, Vector3.Up);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[moonsites] {s.Id} ({s.Name}) at {s.Lat:F4}, {s.Lon:F4}: the bodies' clock +{_days:F2} d, the sun {Math.Asin(sun.Up) * 180 / Math.PI:F0}° up"));
    }

    // ---- --moondance: a film of the walker dancing in front of the last site (dev, the clip recorder) ------------

    /// <summary><c>--moondance</c>: after the sites, the walker steps in before the last one, twerks, and a camera films it (<c>test_output/clips/moon-twerk</c>).</summary>
    public static bool Dance => CmdArgs.Has("--moondance");

    private int _dance;
    private double _danceT;
    private Camera3D? _film;
    private Vector3 _walkerAt, _lookAt;

    private static void Key(Key key, bool down) => Input.ParseInputEvent(new InputEventKey
    {
        Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = key, Keycode = key, KeyLabel = key, Pressed = down,
    });

    private void DanceStep(double delta)
    {
        _danceT += delta;
        var s = _sites[_index];
        var ground = _ground()!;
        var axes = _origin.Frame.Axes!;
        Vector3 World(double e, double n, double up)
        {
            var (x, y, z) = axes.FromEcef(ground.Heights.Ground(MoonSites.Offset(s, e, n)));
            return new Vector3((float)x, (float)(y + up), (float)z);
        }
        switch (_dance)
        {
            case 1:
            {
                // the spectator 9 m out on the sun's side of the main part, looking at it: T drops the walker there, facing it
                var sun = SunLocal(s, SkyClock.Now);
                double az = Math.Atan2(sun.East, sun.North) + 0.35;
                var main = s.Parts[0];
                double we = main.East + Math.Sin(az) * 9, wn = main.North + Math.Cos(az) * 9;
                _walkerAt = World(we, wn, 0);
                _lookAt = World(main.East, main.North, 1.5);
                var cam = (Camera3D)GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false)!;
                cam.GlobalPosition = _walkerAt + Vector3.Up * 1.7f;
                cam.LookAt(_lookAt, Vector3.Up);
                Key(Godot.Key.T, true);
                _dance = 2;
                _danceT = 0;
                break;
            }
            case 2:
                Key(Godot.Key.T, false);
                _dance = 3;
                break;
            case 3:
                if (_local() is { } me && me.Camera.Current && me.IsOnFloor() && _danceT > 1.5)
                {
                    int twerk = -1;
                    for (int i = 0; i < Avatar.HumanMeshBuilder.EmoteCount; i++)
                        if (Avatar.HumanMeshBuilder.EmoteName(i) == "Twerk") twerk = i;
                    me.DanceId = Player.FootPlayer.EmoteDanceBase + twerk;
                    _walkerAt = me.GlobalPosition;
                    _film = new Camera3D { Name = "DanceFilm", Fov = 60 };
                    AddChild(_film);
                    _film.MakeCurrent();
                    var why = ClipRecorder.Film(GetTree(), "moon-twerk");
                    GD.Print($"[moonsites] the walker twerks at {_walkerAt}{(why == null ? ", filming" : $"; no film: {why}")}");
                    _dance = 4;
                    _danceT = 0;
                }
                else if (_danceT > 20) Finish("the walker did not stand within 20 s of T");
                break;
            case 4:
            {
                // a slow arc behind the walker, the lander over its shoulder
                var back = (_walkerAt - _lookAt) with { Y = 0 };
                back = back.Normalized();
                float a = Mathf.DegToRad(-45 + 90 * (float)Math.Clamp(_danceT / 9, 0, 1));
                var dir = back.Rotated(Vector3.Up, a);
                _film!.GlobalPosition = _walkerAt + dir * 3.4f + Vector3.Up * 1.1f;
                _film.LookAt(_walkerAt + Vector3.Up * 1.0f + ((_lookAt - _walkerAt) with { Y = 0 }).Normalized() * 2.5f, Vector3.Up);
                if (_danceT > 9)
                {
                    ClipRecorder.Cut();
                    _dance = 5;
                    _danceT = 0;
                }
                break;
            }
            case 5:
                if (!ClipRecorder.Busy || _danceT > 60) Finish(null);
                break;
        }
    }

    /// <summary>The unit direction to the sun at a site, in its east, north, up.</summary>
    private static (double East, double North, double Up) SunLocal(MoonSite s, SkyMoment sky)
    {
        var sun = Frames.Convert(BodyId.Sun, DVec3.Zero, BodyId.Moon, sky);
        double l = Math.Sqrt(sun.X * sun.X + sun.Y * sun.Y + sun.Z * sun.Z);
        var f = new EnuFrame(new Geodetic(s.Lat, s.Lon, 0), Ellipsoid.Moon);
        var (e, u, south) = f.DirectionToLocal(new EcefPos(sun.X / l, sun.Y / l, sun.Z / l));
        return (e, -south, u);
    }

    /// <summary>Days to add to the bodies' clock for a morning with the sun ~30° up at a site (a lunar day is 29.5 days).</summary>
    private double MorningDays(MoonSite s)
    {
        var baseNow = _baseSky?.Invoke() ?? SkyMoment.At(DateTime.UtcNow);
        double best = 0, bestErr = double.MaxValue;
        double prev = double.NaN;
        for (double d = 0; d < 29.6; d += 0.05)
        {
            var sun = SunLocal(s, baseNow with { Utc = baseNow.Utc.AddDays(d) });
            double el = Math.Asin(Math.Clamp(sun.Up, -1, 1)) * 180 / Math.PI;
            // rising, and the nearest to 30° (the poles' sun never climbs: the highest morning there)
            if (!double.IsNaN(prev) && el > prev && Math.Abs(el - 30) < bestErr)
            {
                bestErr = Math.Abs(el - 30);
                best = d;
            }
            prev = el;
        }
        return best;
    }

    /// <summary>The ground under each part: its slope, and the part standing on it.</summary>
    private void Judge(MoonSite s, BodyGround ground)
    {
        var h = ground.Heights;
        foreach (var part in s.Parts)
        {
            if (part.Part is MoonPart.SivbDebris or MoonPart.WreckDebris or MoonPart.RocketWreck or MoonPart.Alsep) continue;
            double worst = 0;
            double c = h.At(MoonSites.Offset(s, part.East, part.North));
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                double hk = h.At(MoonSites.Offset(s, part.East + Math.Sin(a) * 2, part.North + Math.Cos(a) * 2));
                worst = Math.Max(worst, Math.Atan2(Math.Abs(hk - c), 2) * 180 / Math.PI);
            }
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[moonsites]   {part.Part}: ground slope {worst:F1}°"));
            if (worst > MaxSlopeDeg) _fails.Add($"{s.Id}: {part.Part} on a {worst:F1}° slope");
        }
        if (MoonSurface.Active?.Sites.TryGetValue(s.Id, out var placed) == true
            && GetViewport().GetCamera3D() is { } cam)
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[moonsites]   the site at {placed.Node.GlobalPosition}, the camera at {cam.GlobalPosition}, {cam.GlobalPosition.DistanceTo(placed.Node.GlobalPosition):F1} m"));
        if (_headless) return;
        // the picture without the HUD
        foreach (var node in GetTree().Root.FindChildren("*", "CanvasLayer", true, false))
            ((CanvasLayer)node).Visible = false;
        var path = ProjectSettings.GlobalizePath($"res://test_output/moon/site_{s.Id}.png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[moonsites]   saved {path}");
    }

    private void Finish(string? why)
    {
        SkyClock.Live = _baseSky;
        if (why == null) GD.Print($"[moonsites] RESULT: PASS — {_sites.Count} site(s)");
        else GD.PrintErr($"[moonsites] RESULT: FAIL — {why}");
        GetTree().Quit(why == null ? 0 : 1);
    }
}
