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
        _camera.Current = true;
        _t += delta;
        if (Toward() is not { } dir) return;
        var at = _camera.GlobalPosition;
        _camera.LookAt(at + dir, Mathf.Abs(dir.Y) > 0.99f ? Vector3.Forward : Vector3.Up);
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
            ok = _body == "moon" ? peak - ring > 0.03f : peak > 0.7f && peak - ring > 0.3f;
        }
        GD.Print($"[skyshot] {_body} from {altitude / 1000:F1} km, {(hidden ? "behind the Earth" : "in view")}, the sun "
            + $"{DayNight.Instance?.SunElevationDeg:F1}° up below: centre {peak:F3}, round it {wide:F3}, sky {ring:F3}; wrote {_out}");
        GD.Print($"RESULT {(ok ? "PASS" : "FAIL")} skyshot {_body}: {expect} (centre {peak:F3}, round it {wide:F3}, sky {ring:F3})");
        GetTree().Quit(ok ? 0 : 1);
    }

    private static float Lum(Color c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
}
