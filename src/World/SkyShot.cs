using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// <c>godot --path . -- --frame earth --skyshot sun|moon|light,altitudeM,out.png [--at E,N] [--date D] [--time H]</c>
/// (#1798): the spectator camera at that height over the place, turned to the real sun, the real moon or where the
/// scene's light comes from (the moon's shading at night), a picture of it, and its brightness against the sky round
/// it. The rule it checks is what the eye expects at any height: a body in view (above the horizon below, above the
/// Earth's limb in space) is drawn, one behind the Earth is not, and the light's own direction shows no disc unless
/// the light is the sun. Windowed only: it reads the drawn picture.
/// </summary>
public partial class SkyShot : Node
{
    public static string[]? ParseArgs() => CmdArgs.Value("--skyshot")?.Split(',') is { Length: 3 } p ? p : null;

    /// <summary>How long the camera settles on its body before the picture, s (the globe's levels stream in).</summary>
    public const double SettleSeconds = 8;

    private readonly Camera3D _camera;
    private readonly WorldOrigin _origin;
    private readonly string _body, _out;
    private double _t;
    private bool _done, _starsOff, _saved;

    public SkyShot(Camera3D camera, WorldOrigin origin, string body, float altitude, string output)
    {
        _camera = camera;
        _origin = origin;
        _body = body;
        _out = output;
        camera.Position = new Vector3(0, altitude, 0);
        // last of the frame: the sky read this frame, then the camera turned to it
        ProcessPriority = int.MaxValue;
    }

    /// <summary>Toward the body in world axes, or null while the real sky is not up yet.</summary>
    private Vector3? Toward()
    {
        if (DayNight.Instance is not { } day) return null;
        if (_body == "light") return day.Sun is { } light && IsInstanceValid(light) ? light.GlobalBasis.Z.Normalized() : null;
        if (day.Sky is not { } sky) return null;
        return _body == "moon" ? sky.Moon : sky.Sun;
    }

    public override void _Process(double delta)
    {
        if (_done) return;
        _t += delta;
        if (Walk)
        {
            // --skyshot-walk: the walking player's own camera on the ground at --at, turned by its look (the moon went
            // when looked at head-on there, #1835 follow-up)
            if (GetParentOrNull<Core.ClientWorld>() is not { } world) return;
            if (world.StudioPlayer is not { } walker)
            {
                if (!_toggled) { _toggled = true; world.ToggleMode(); }
                _t = 0;
                return;
            }
            // first person, as the eye sees it (third person stands the camera behind the body)
            if (!walker.IsFirstPerson) walker.ViewForCheck(thirdPerson: false);
            if (Toward() is not { } toward) return;
            walker.LookYaw = Mathf.Atan2(-toward.X, -toward.Z) + (Sweep && _t > SweepFrom ? Mathf.DegToRad(25f) * Mathf.Sin((float)(_t - SweepFrom) * 2.2f) : 0f);
            if (Sweep && _body == "moon" && _t > SweepFrom && Swept()) return;
            if (Sweep) return;   // settling: nothing measured, nothing hidden
            walker.LookPitch = Mathf.Asin(Mathf.Clamp(toward.Y, -1f, 1f));
        }
        else
        {
            _camera.Current = true;
            if (Toward() is not { } aim) return;
            _camera.LookAt(_camera.GlobalPosition + aim, Mathf.Abs(aim.Y) > 0.99f ? Vector3.Forward : Vector3.Up);
        }
        if (Toward() is not { } dir) return;
        var at = GetViewport().GetCamera3D()?.GlobalPosition ?? _camera.GlobalPosition;
        if (Slats && _body == "sun" && Slatted(dir)) return;
        // the picture as the eye sees it, then the stars, planets and satellites out of the measure (not what it
        // checks; a star at the centre read as a body)
        if (_t >= SettleSeconds - 1.5 && !_saved)
        {
            _saved = true;
            GetViewport().GetTexture().GetImage().SavePng(_out);
        }
        if (_t >= SettleSeconds - 1 && !_starsOff)
        {
            _starsOff = true;
            foreach (var name in new[] { "StarField", "SkyBodies", "Satellites" })
                if (GetTree().Root.FindChild(name, true, false) is Node3D n) n.Visible = false;
        }
        if (_t < SettleSeconds) return;
        _done = true;

        var image = GetViewport().GetTexture().GetImage();
        // the body is at the centre: the brightest pixel within 12 px (a sun's disc, a crescent's sliver off the
        // moon's centre) and the mean over a 31 px box (a glow, a blob), against the sky's mean on a ring 70 px out
        int cx = image.GetWidth() / 2, cy = image.GetHeight() / 2;
        float peak = 0, wide = 0;
        for (int y = -15; y <= 15; y++)
            for (int x = -15; x <= 15; x++)
            {
                float l = Lum(image.GetPixel(cx + x, cy + y));
                wide += l;
                if (Math.Abs(x) <= 12 && Math.Abs(y) <= 12) peak = Mathf.Max(peak, l);
            }
        wide /= 31 * 31;
        float ring = 0;
        for (int i = 0; i < 32; i++)
        {
            float a = i * Mathf.Tau / 32;
            ring += Lum(image.GetPixel(cx + (int)(70 * Mathf.Cos(a)), cy + (int)(70 * Mathf.Sin(a))));
        }
        ring /= 32;

        // the sun (#1479): its brightest pixels round the ephemeris direction (the centre) and no wider than its disc.
        // Before, the Cartoon sky's sun was a 20° glow along the light (lifted 3° at sunrise): no disc, off the sun low down.
        float spot = 0, offPx = 0;
        if (_body == "sun" && GetViewport().GetCamera3D() is { } shotCam)
        {
            float top = 0;
            for (int y = -40; y <= 40; y++)
                for (int x = -40; x <= 40; x++)
                    top = Mathf.Max(top, Lum(image.GetPixel(cx + x, cy + y)));
            float sx = 0, sy = 0, n = 0;
            for (int y = -40; y <= 40; y++)
                for (int x = -40; x <= 40; x++)
                    if (Lum(image.GetPixel(cx + x, cy + y)) >= top - 0.02f) { sx += x; sy += y; n++; }
            offPx = n > 0 ? new Vector2(sx / n, sy / n).Length() : 99;
            float perRad = image.GetHeight() * 0.5f / Mathf.Tan(Mathf.DegToRad(Core.CameraLens.FovOf(shotCam)) * 0.5f);
            float disc = Mathf.Max(SunDisc.TrueRadius, Mathf.DegToRad(Core.CameraLens.FovOf(shotCam)) * MoonDisc.ScreenShare) * perRad;
            spot = Mathf.Sqrt(n / Mathf.Pi) / Mathf.Max(disc, 1f);
        }

        // in view: the ray toward it misses the Earth's ball under the camera (below the horizon from the ground)
        var axes = Globe.Axes ?? _origin.EcefAxes;
        var eye = axes.ToEcef(at.X, at.Y, at.Z);
        var g = Ellipsoid.Wgs84.ToGeodetic(eye);
        var nadir = CubeSphere.OnEllipsoid(CubeSphere.NormalAt(g.LatDeg, g.LonDeg), 0);
        double r = Math.Sqrt(FrameAxes.Dot(nadir, nadir));
        var d = new EcefPos(axes.X.X * dir.X + axes.Y.X * dir.Y + axes.Z.X * dir.Z,
            axes.X.Y * dir.X + axes.Y.Y * dir.Y + axes.Z.Y * dir.Z,
            axes.X.Z * dir.X + axes.Y.Z * dir.Y + axes.Z.Z * dir.Z);
        double b = FrameAxes.Dot(eye, d) / d.Length, c = FrameAxes.Dot(eye, eye) - r * r;
        bool hidden = b < 0 && b * b - c > 0;
        double altitude = _origin.AltitudeOf(at);
        bool lightIsSun = (DayNight.Instance?.SunElevationDeg ?? 90) > 3f;

        string expect;
        bool ok;
        if (_body == "light" && !lightIsSun)
        {
            expect = "no disc where the moon's shading comes from";
            ok = wide - ring < 0.03f;
        }
        else if (hidden)
        {
            expect = "hidden behind the Earth";
            ok = wide - ring < 0.03f;
        }
        else
        {
            // a crescent fills a share of the box: its lit sliver and the earthshine disc
            expect = _body == "moon" ? "the moon drawn" : "the sun drawn";
            ok = _body == "moon" ? peak - ring > 0.03f : peak > 0.7f && offPx < 3f && spot < 1.5f;
            if (_body == "sun") expect += $", a disc on its place ({offPx:F1} px off, brightest {spot:F2}× its disc)";
        }
        // the globe's sun (Atmosphere, Globe.Axes) against the sky's (DayNight, the world's frame): one sun, one place
        if (DayNight.Instance?.Sky is { } skyNow && Atmosphere.TrueSunWritten.LengthSquared() > 0.5f && Atmosphere.TrueSunWritten is var trueSun)
            GD.Print($"[skyshot] the globe's sun is {Mathf.RadToDeg(trueSun.Normalized().AngleTo(skyNow.Sun.Normalized())):F3}° off the sky's");
        GD.Print($"[skyshot] {_body} from {altitude / 1000:F1} km, {(hidden ? "behind the Earth" : "in view")}, the sun "
            + $"{DayNight.Instance?.SunElevationDeg:F1}° up below: centre {peak:F3}, round it {wide:F3}, sky {ring:F3}; wrote {_out}");
        GD.Print($"RESULT {(ok ? "PASS" : "FAIL")} skyshot {_body}: {expect} (centre {peak:F3}, round it {wide:F3}, sky {ring:F3})");
        GetTree().Quit(ok ? 0 : 1);
    }

    private static float Lum(Color c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;

    /// <summary>
    /// <c>--skyshot-slats</c> (#1479): posts 300 m out across the sun, so its light shafts show between them. A picture
    /// with "Sun rays" on, one with them off, and the stripes the shafts draw measured on a row above the posts: the RESULT, and quits. Only with <c>sun</c>.
    /// </summary>
    private static bool Slats => CmdArgs.Has("--skyshot-slats");

    /// <summary><c>--skyshot-walk</c>: the shot from the walking player's camera, not the spectator's.</summary>
    private static bool Walk => CmdArgs.Has("--skyshot-walk");

    private bool _toggled;

    /// <summary><c>--skyshot-sweep</c> (with <c>-walk</c>, the moon): the view swings ±25° across it for 4 s, as a player turning, and every
    /// drawn frame the moon's pixel is read where it should be on screen: lit, or culled (it went near the screen's middle).</summary>
    private static bool Sweep => CmdArgs.Has("--skyshot-sweep");

    /// <summary>The sweep starts once the world has settled (the sky streams in for a few seconds).</summary>
    private const double SweepFrom = SettleSeconds + 2;

    private int _seen, _missed;
    private float _worstX;

    private bool _sampling;

    private bool Swept()
    {
        if (!_sampling)
        {
            _sampling = true;
            RenderingServer.FramePostDraw += SampleSweep;
        }
        if (_t > SweepFrom + 4.5)
        {
            if (_done) return true;
            _done = true;
            RenderingServer.FramePostDraw -= SampleSweep;
            bool ok = _seen > 20 && _missed == 0;
            GD.Print($"RESULT {(ok ? "PASS" : "FAIL")} skyshot sweep: the moon drawn in every frame on screen ({_seen} lit, {_missed} missing, worst at {_worstX:0} px from the middle)");
            GetTree().Quit(ok ? 0 : 1);
        }
        return true;
    }

    /// <summary>After a frame is drawn: the moon's pixel where that frame's camera put it.</summary>
    private void SampleSweep()
    {
        if (_done || GetTree().Root.FindChild("MoonDisc", true, false) is not Node3D moon || GetViewport().GetCamera3D() is not { } cam
            || cam.IsPositionBehind(moon.GlobalPosition)) return;
        var image = GetViewport().GetTexture().GetImage();
        // the picture is the viewport's texture, larger than its coordinates under a content scale
        var px = cam.UnprojectPosition(moon.GlobalPosition) * (new Vector2(image.GetWidth(), image.GetHeight()) / GetViewport().GetVisibleRect().Size);
        if (px.X < 4 || px.Y < 4 || px.X > image.GetWidth() - 5 || px.Y > image.GetHeight() - 5) return;
        float lit = 0;
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
                lit = Mathf.Max(lit, Lum(image.GetPixel((int)px.X + x, (int)px.Y + y)));
        if (lit > 0.45f) _seen++;
        else
        {
            _missed++;
            _worstX = Mathf.Abs(px.X - image.GetWidth() / 2f);
            if (_missed <= 10) GD.Print($"[skyshot] moon missing at {px} (lit {lit:F2})");
            if (_missed == 5)
            {
                for (int k = -6; k <= 6; k++) { image.SetPixel((int)px.X + k, (int)px.Y - 8, Colors.Red); image.SetPixel((int)px.X - 8, (int)px.Y + k, Colors.Red); }
                image.SavePng(_out.Replace(".png", "_missing.png"));
            }
        }
    }

    private Node3D? _posts;
    private Image? _raysOn;

    private bool Slatted(Vector3 dir)
    {
        if (_posts == null)
        {
            _posts = new Node3D { Name = "SkyShotSlats", TopLevel = true };
            AddChild(_posts);
            var mat = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.22f, 0.2f) };
            for (int i = -3; i <= 3; i++)
                _posts.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(14, 160, 4) }, MaterialOverride = mat, Position = new Vector3(i * 32, -40, 0) });
        }
        // square to the sun, 300 m out, its posts upright in the camera's up
        var right = dir.Cross(_camera.GlobalBasis.Y).Normalized();
        var up = right.Cross(dir).Normalized();
        _posts.GlobalTransform = new Transform3D(new Basis(right, up, -dir), _camera.GlobalPosition + dir * 300f);
        if (_t < SettleSeconds) return true;
        if (_raysOn == null)
        {
            _raysOn = GetViewport().GetTexture().GetImage();
            _raysOn.SavePng(_out.Replace(".png", "_rays.png"));
            Core.GameSettings.Current.SunRays = false;
            _t = SettleSeconds - 0.5;
            return true;
        }
        if (_done) return true;
        _done = true;
        var off = GetViewport().GetTexture().GetImage();
        off.SavePng(_out.Replace(".png", "_norays.png"));
        // on a row above the posts, where the beams fan out from the gaps: the light the shafts add, and its stripes
        int cy = off.GetHeight() / 2 - off.GetHeight() / 5, w = off.GetWidth();
        float lo = float.MaxValue, hi = 0, sum = 0;
        for (int x = w / 4; x < 3 * w / 4; x++)
        {
            float d = Lum(_raysOn.GetPixel(x, cy)) - Lum(off.GetPixel(x, cy));
            lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d); sum += d;
        }
        float mean = sum / (w / 2);
        bool ok = mean > 0.01f && hi - lo > 0.03f;
        GD.Print($"RESULT {(ok ? "PASS" : "FAIL")} skyshot slats: the sun's shafts between the posts (added {mean:F3} on average, stripes {lo:F3} to {hi:F3})");
        GetTree().Quit(ok ? 0 : 1);
        return true;
    }
}
