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
    public static bool Requested => CmdArgs.Has("--moonsites") || CmdArgs.Has("--moonsurvey");

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
        _sites = ids == null ? MoonSites.All.ToList() : ids.Select(Named).OfType<MoonSite>().ToList();
    }

    /// <summary>A site by id, or a colony (<c>artemis</c> from above, <c>artemis:close</c> among its modules) as a stand-in site for the visit.</summary>
    private static MoonSite? Named(string id)
    {
        if (MoonSites.Find(id) is { } s) return s;
        var baseId = id.Split(':')[0];
        if (MoonOutposts.Find(baseId) is { } o)
            return new MoonSite(id, o.Name, MoonSiteKind.Industry, o.Lat, o.Lon, [new SitePart(MoonPart.Flag, 0, 0, 0)]);
        if (MoonColonies.Find(baseId) is not { } c) return null;
        return new MoonSite(id, c.Name, MoonSiteKind.Colony, c.Lat, c.Lon, [new SitePart(MoonPart.Flag, 0, 0, 0)]);
    }

    private static string PlacedId(MoonSite s) => s.Id.Split(':')[0];

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
        if (_air > 0)
        {
            SavePending();
            // a picture's frames run on even after the last stage: the walk-through waits for it
            if (_pending == null) AirStep(delta);
            return;
        }
        if (_crewStage > 0)
        {
            SavePending();
            if (_pending == null) CrewStep(delta);
            return;
        }
        if (_ruin > 0)
        {
            SavePending();
            if (_pending == null || _ruin == 7) RuinStep(delta);
            return;
        }
        if (_rover > 0)
        {
            SavePending();
            RoverStep(delta);
            return;
        }
        if (_dance > 0)
        {
            DanceStep(delta);
            return;
        }
        if (_index < 0)
        {
            if (_t < 8) return;
            if (_ground() == null) { Finish("no Moon ground (--frame earth --bodies DIR)"); return; }
            if (CmdArgs.Value("--moonsurvey") is { } survey) { Survey(survey); return; }
            Next();
            return;
        }
        var site = _sites[_index];
        var ground = _ground()!;
        bool placed = MoonSurface.Active?.Sites.TryGetValue(PlacedId(site), out var p) == true && p!.TracesIn;
        // a second look at the same place (artemis, then artemis:close) may need no new square
        bool again = _index > 0 && PlacedId(_sites[_index - 1]) == PlacedId(site) && _t > 2;
        if ((ground.Built > _builtAt || again) && placed && ground.MediumBuilt > 0) _frames++;
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
            if (AirlockCheck && _fails.Count == 0 && _sites.Count > 0) { _index = _sites.Count - 1; _air = 1; return; }
            if (RoverCheck && _fails.Count == 0 && _sites.Count > 0) { _index = _sites.Count - 1; _rover = 1; return; }
            if (RuinsCheck && _fails.Count == 0 && _sites.Count > 0) { _index = _sites.Count - 1; _ruin = 1; return; }
            if (ColonistCheck && _fails.Count == 0 && _sites.Count > 0) { _index = _sites.Count - 1; _crewStage = 1; return; }
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
        // a colony from the side away from its pads (they stand behind it), a little round toward the sun
        if (s.Kind == MoonSiteKind.Colony && MoonColonies.Find(PlacedId(s)) is { } col)
            az = col.Heading * Math.PI / 180 + Math.PI + (s.Id.EndsWith(":close") ? 0.5 : 0.45);
        // an impact: the crater's own point, the debris on its rim in view
        var main = s.Kind == MoonSiteKind.Impact ? new SitePart(MoonPart.SivbDebris, 0, 0, 0) : s.Parts[0];
        double dist = main.Part switch
        {
            MoonPart.DescentStage => 20, MoonPart.RocketWreck => 48, MoonPart.SivbDebris => 55, MoonPart.ChangeLander or MoonPart.LunaStage
                or MoonPart.LunaRampStage => 11, MoonPart.Flag => 5.5, _ => 7,
        };
        bool close = s.Id.EndsWith(":close");
        // <colony>:<industry kind> (#1942): a close look at the first piece of that kind, from the sun's side
        IndustryKind? piece = null;
        if (s.Kind == MoonSiteKind.Colony && s.Id.Split(':') is { Length: 2 } idParts
            && Enum.TryParse<IndustryKind>(idParts[1], true, out var kind) && MoonColonies.Find(idParts[0]) is { } pc)
        {
            foreach (var i in ColonyPlan.For(pc).Industry)
                if (i.Kind == kind)
                {
                    main = new SitePart(MoonPart.Flag, i.X, -i.Z, 0);
                    piece = kind;
                    az = sunAz + 0.6;
                    break;
                }
        }
        if (s.Kind == MoonSiteKind.Colony) dist = close ? 34 : 170;
        if (s.Kind == MoonSiteKind.Industry) dist = close ? 40 : 110;
        // an outpost from the side, looking across it (the mass driver: from its start, along the rail)
        if (s.Kind == MoonSiteKind.Industry && MoonOutposts.Find(PlacedId(s)) is { } op)
            az = op.Heading * Math.PI / 180 + (op.Kind == OutpostKind.MassDriver ? Math.PI + 0.35 : 2.4);
        if (piece != null) { dist = piece == IndustryKind.SolarArray ? 34 : 30; close = true; }
        // <colony>:<module kind> (#1944): inside the first module of that kind, by its door, looking across it
        Vector3? inside = null, insideLook = null;
        if (piece == null && s.Kind == MoonSiteKind.Colony && s.Id.Split(':') is { Length: 2 } mParts
            && Enum.TryParse<ModuleKind>(mParts[1], true, out var mk) && MoonColonies.Find(mParts[0]) is { } mc)
            foreach (var m in ColonyPlan.For(mc).Modules)
                if (m.Kind == mk)
                {
                    var (ax2, az2) = ColonyPlan.Along(m.Yaw);
                    float reach = (float)(Math.Max(m.Length, m.Width) / 2 * 0.8);
                    // east, up, north in the site's frame
                    inside = new Vector3((float)(m.X + ax2 * reach), 3.2f, (float)-(m.Z + az2 * reach));
                    insideLook = new Vector3((float)m.X, 2.2f, (float)-m.Z);
                    break;
                }
        double up = piece != null ? 9 : s.Kind == MoonSiteKind.Colony ? (close ? 2.2 : 70) : s.Kind == MoonSiteKind.Industry ? (close ? 6 : 35) : Math.Max(1.8, dist * 0.32);
        var eye = new Vector3((float)(main.East + Math.Sin(az) * dist), (float)up, (float)-(main.North + Math.Cos(az) * dist));
        if (inside is { } ins)
        {
            // the colony's frame is the site's: east, up, south (z = -north)
            eye = new Vector3(ins.X, ins.Y, -ins.Z);
            main = new SitePart(MoonPart.Flag, insideLook!.Value.X, insideLook.Value.Z, 0);
        }
        var look = new Vector3((float)main.East, (float)(piece != null ? 3 : s.Kind == MoonSiteKind.Colony ? (close ? 4 : 0) : s.Kind == MoonSiteKind.Industry ? 6 : dist * 0.06), (float)-main.North);
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
                _lookEcef = axes.ToEcef(_lookAt.X, _lookAt.Y, _lookAt.Z);
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
                    // the dive starts with the whole Moon in view: the spectator camera (its far plane and the globe
                    // drawn round it are set up for space), straight over the walker
                    _zoomCam = (Camera3D)GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false)!;
                    _zoomCam.MakeCurrent();
                    ZoomPose(0);
                    GD.Print($"[moonsites] the walker twerks at {_walkerAt}; the globe loads round the far view");
                    _dance = 7;
                    _danceT = 0;
                }
                else if (_danceT > 20) Finish("the walker did not stand within 20 s of T");
                break;
            case 4:
            {
                // a slow arc behind the walker, the lander over its shoulder
                if (_local() is { } dancer) _walkerAt = dancer.GlobalPosition;
                var up = Up();
                var toLander = Level(Lander() - _walkerAt, up);
                float a = Mathf.DegToRad(-45 + 90 * (float)Math.Clamp(_danceT / 8, 0, 1));
                var dir = (-toLander).Rotated(up, a);
                _film!.GlobalPosition = _walkerAt + dir * 3.4f + up * 1.1f;
                _film.LookAt(_walkerAt + up * 1.0f + toLander * 2.5f, up);
                Steady(_film);
                if (_danceT > 8)
                {
                    ClipRecorder.Cut();
                    _dance = 5;
                    _danceT = 0;
                }
                break;
            }
            case 5:
                if (!ClipRecorder.Busy || _danceT > 60)
                {
                    GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"[moonsites] the film's camera turned at most {_worstTurn:F1}° in a frame (on the Moon's own axes)"));
                    Finish(_worstTurn > MaxTurnDeg ? $"the film's camera jumped {_worstTurn:F1}° in a frame" : null);
                }
                break;
            case 7:
                // a pre-roll on the far view: the globe's tiles there load before the film starts
                ZoomPose(0);
                if (_danceT < 3) break;
                {
                    var why = ClipRecorder.Film(GetTree(), "moon-twerk");
                    GD.Print(why == null ? "[moonsites] filming from orbit" : $"[moonsites] no film: {why}");
                }
                _dance = 6;
                _danceT = 0;
                break;
            case 6:
                // the dive: from 4,500 km to the walker's shoulder in ZoomSeconds, then the arc behind it
                if (_danceT < ZoomSeconds)
                {
                    ZoomPose(_danceT / ZoomSeconds);
                    break;
                }
                _film!.Far = _zoomCam!.Far;
                _film.MakeCurrent();
                _dance = 4;
                _danceT = 0;
                break;
        }
    }

    /// <summary>The dive from orbit, seconds.</summary>
    private const double ZoomSeconds = 5.0;

    /// <summary>Where the dive starts, metres over the walker: the whole Moon in a 75° view.</summary>
    private const double ZoomFromM = 4_500_000;

    private Camera3D? _zoomCam;

    private Terrain.Format.EcefPos _lookEcef;

    // The world is re-anchored 30-odd times during the dive (past ~1,000 km the origin follows the camera in 3D; at 863 km
    // it drops back to the ground): world positions are relabelled each time, so a point kept as a world Vector3 goes
    // stale. The first film's camera kept the lander as one (`_lookAt`, from before the dive) and took the picture's up
    // from the direction to it: the up slid during the dive and swung 33° when the origin came back down (0:01), and its
    // straight-down workaround switched its up reference outright at 640 m (135°, 0:03). The Moon's axes themselves never
    // turned (logged at every re-anchor). So here the lander and the vertical are kept on the Moon (ECEF) and read into
    // the world every frame, and the picture's up turns smoothly; `Steady` measures each frame's turn on the Moon's axes.

    /// <summary>The most the film's camera may turn in a frame, degrees, on the Moon's own axes: the dive's swing stays well under.</summary>
    private const float MaxTurnDeg = 8;

    private Vector3 _lastForward, _lastScreenUp;
    private float _worstTurn;

    /// <summary>
    /// How far the camera turned since the last frame, read on the Moon's own axes (the world's axes turn at a re-anchor,
    /// the Moon's never do): a jump in the picture is a jump here.
    /// </summary>
    private void Steady(Camera3D cam)
    {
        if (_origin.Frame.Axes is not { } axes) return;
        Vector3 Moon(Vector3 v) => new(
            (float)(axes.X.X * v.X + axes.Y.X * v.Y + axes.Z.X * v.Z),
            (float)(axes.X.Y * v.X + axes.Y.Y * v.Y + axes.Z.Y * v.Z),
            (float)(axes.X.Z * v.X + axes.Y.Z * v.Y + axes.Z.Z * v.Z));
        var f = Moon(-cam.GlobalBasis.Z);
        var u = Moon(cam.GlobalBasis.Y);
        if (_lastForward != default)
        {
            float turn = Mathf.RadToDeg(Mathf.Max(_lastForward.AngleTo(f), _lastScreenUp.AngleTo(u)));
            if (turn > MaxTurnDeg)
                GD.PrintErr(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[moonsites] the film's camera turned {turn:F1}° in one frame at {_danceT:F2} s (stage {_dance})"));
            _worstTurn = Mathf.Max(_worstTurn, turn);
        }
        _lastForward = f;
        _lastScreenUp = u;
    }

    /// <summary>The vertical at the walker, in the world's axes now.</summary>
    private Vector3 Up()
    {
        if (_origin.Frame.Axes is not { } axes || _local() is not { } me) return Vector3.Up;
        var p = axes.ToEcef(me.GlobalPosition.X, me.GlobalPosition.Y, me.GlobalPosition.Z);
        double l = p.Length;
        return new Vector3((float)(FrameAxes.Dot(p, axes.X) / l), (float)(FrameAxes.Dot(p, axes.Y) / l), (float)(FrameAxes.Dot(p, axes.Z) / l));
    }

    /// <summary>The lander's look point, in the world's axes now.</summary>
    private Vector3 Lander()
    {
        if (_origin.Frame.Axes is not { } axes) return _lookAt;
        var (x, y, z) = axes.FromEcef(_lookEcef);
        return new Vector3((float)x, (float)y, (float)z);
    }

    /// <summary>A direction made level (its vertical part taken out) and unit.</summary>
    private static Vector3 Level(Vector3 v, Vector3 up) => (v - up * v.Dot(up)).Normalized();

    /// <summary>The arc's first pose (where the dive lands): behind the walker, 45° round, at its shoulder.</summary>
    private (Vector3 Eye, Vector3 Look) ArcStart()
    {
        var up = Up();
        var toLander = Level(Lander() - _walkerAt, up);
        var dir = (-toLander).Rotated(up, Mathf.DegToRad(-45));
        return (_walkerAt + dir * 3.4f + up * 1.1f, _walkerAt + up * 1.0f + toLander * 2.5f);
    }

    /// <summary>
    /// The dive at <paramref name="s"/> (0..1): the distance falls geometrically (the same zoom every instant, eased in and
    /// out), straight down from orbit; the last kilometres swing round to the arc's first pose behind the walker.
    /// </summary>
    private void ZoomPose(double s)
    {
        if (_zoomCam == null || _local() is not { } me) return;
        _walkerAt = me.GlobalPosition;
        var (eye, look) = ArcStart();
        var vertical = Up();
        var head = _walkerAt + vertical * 1.0f;
        var toEnd = eye - head;
        double end = toEnd.Length();
        double e = s * s * (3 - 2 * s);
        double d = ZoomFromM * Math.Pow(end / ZoomFromM, e);
        // the swing round: none over 3 km, all of it at the end, by the log of the distance
        float w = (float)Math.Clamp((Math.Log(3000) - Math.Log(d)) / (Math.Log(3000) - Math.Log(end)), 0, 1);
        w = w * w * (3 - 2 * w);
        var dir = vertical.Lerp(toEnd / (float)end, w).Normalized();
        _zoomCam.GlobalPosition = head + dir * (float)d;
        // framed on the Moon's centre at first, on the walker after the first quarter
        var target = head.Lerp(look, w);
        if (_origin.Frame.Axes is { } axes)
        {
            var (cx, cy, cz) = axes.FromEcef(new Terrain.Format.EcefPos(0, 0, 0));
            float c = (float)Math.Clamp(1 - e / 0.25, 0, 1);
            c = c * c * (3 - 2 * c);
            target = target.Lerp(new Vector3((float)cx, (float)cy, (float)cz), c);
        }
        // the picture's up: toward the lander while the view looks straight down, turning smoothly to the vertical as
        // the view comes level (never a switch: it stays a right angle off the view all the way)
        var toLander = Level(Lander() - _walkerAt, vertical);
        var up = toLander.Slerp(vertical, w);
        _zoomCam.LookAt(target, up);
        Steady(_zoomCam);
    }

    /// <summary>
    /// <c>--moonsurvey LAT,LON,SEARCH_KM,RADIUS_M</c> (or <c>LAT,LON,FROM_KM,TO_KM,LENGTH_M,HALF_WIDTH_M</c> for a strip): the flattest discs on LOLA's heights near a place (a colony's
    /// plateau), best first, then quits.
    /// </summary>
    private void Survey(string arg)
    {
        var p = arg.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var h = _ground()!.Heights;
        // LAT,LON,FROM_KM,TO_KM,LENGTH_M,HALF_WIDTH_M: the flattest strip (a runway, #1943)
        if (p.Length == 6)
        {
            foreach (var (lat, lon, hd, rms, grade) in MoonColonies.FlattestStrip(h.Data, p[0], p[1], p[2] * 1000, p[3] * 1000, p[4], p[5]))
                GD.Print(string.Create(CultureInfo.InvariantCulture,
                    $"[moonsites] strip survey: {lat:F4}, {lon:F4} heading {hd:F0}°: rms {rms:F2} m round the line, grade {grade:F2} %"));
            Finish(null);
            return;
        }
        foreach (var (lat, lon, rms, tilt, height) in MoonColonies.Flattest(h.Data, p[0], p[1], p[2] * 1000, p[3]))
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[moonsites] survey: {lat:F4}, {lon:F4}: rms {rms:F2} m round a plane tilted {tilt:F2}°, {height:F0} m"));
        Finish(null);
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
        if (s.Kind == MoonSiteKind.Colony)
        {
            // the plateau: level to a centimetre (the sphere's own drop aside) wherever the colony stands
            var c = MoonColonies.Find(PlacedId(s))!;
            double h0 = h.At(c.Normal), worst = 0;
            for (int k = 0; k < 16; k++)
            {
                double a = k * Math.PI / 8, rr = c.PlateauM * (k % 2 == 0 ? 0.95 : 0.5);
                worst = Math.Max(worst, Math.Abs(h.At(MoonSites.Offset(s, Math.Sin(a) * rr, Math.Cos(a) * rr)) - h0));
            }
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[moonsites]   plateau: within {worst * 100:F1} cm of its centre's height"));
            if (worst > 0.02) _fails.Add($"{s.Id}: the plateau is {worst:F2} m off level");
        }
        foreach (var part in s.Parts)
        {
            if (s.Kind is MoonSiteKind.Colony or MoonSiteKind.Industry) break;
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
        if (MoonSurface.Active?.Sites.TryGetValue(PlacedId(s), out var placed) == true
            && GetViewport().GetCamera3D() is { } cam)
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[moonsites]   the site at {placed.Node.GlobalPosition}, the camera at {cam.GlobalPosition}, {cam.GlobalPosition.DistanceTo(placed.Node.GlobalPosition):F1} m"));
        if (_headless) return;
        // the picture without the HUD
        foreach (var node in GetTree().Root.FindChildren("*", "CanvasLayer", true, false))
            ((CanvasLayer)node).Visible = false;
        var path = ProjectSettings.GlobalizePath($"res://test_output/moon/site_{s.Id.Replace(':', '_')}.png");
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
