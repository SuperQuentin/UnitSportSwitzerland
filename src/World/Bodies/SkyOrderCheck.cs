using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--skyordercheck [all|earth|jupiter|sun|bands]</c> (#2051, windowed: it compares drawn pictures; <c>--frame earth
/// --chunks fixture:straight --bodies DIR</c>): what stands in for a far body is hidden by the ground in front of it and
/// covers the stars behind it, at every distance. Each view is drawn several times with one part hidden, and the pictures'
/// differences give where each part is drawn:
/// <list type="bullet">
/// <item><c>earth</c>: the Earth from the Moon, low over places 89.5° from the point under the Earth (4 bearings, 0.2,
/// 1.5 and 4.458 km up: the user's height, the far plane the view distance under 5 km) — never drawn on the Moon's
/// ground;</item>
/// <item><c>jupiter</c>: Jupiter from Io the same way (when Io's data is there);</item>
/// <item><c>sun</c>: the Sun from the Moon near its horizon — its core never on the ground;</item>
/// <item>in all of them the stars never on the ground and never inside a body's drawn disc;</item>
/// <item><c>bands</c>: the Earth (from 20,000 to 1.5 million km) and the Moon (20,000 to 600,000 km) from their night
/// side: no star inside the disc, whatever draws it (the anchor's own globe, another's shrunk, the sky's disc).</item>
/// </list>
/// Pictures of each view in <c>test_output/bodies/skyorder_*.png</c>.
/// </summary>
public partial class SkyOrderCheck : Node
{
    public static bool Requested => CmdArgs.Has("--skyordercheck");

    /// <summary>A pixel differs between two pictures by more than this (0 to 1, any channel).</summary>
    public const float DiffMin = 0.04f;

    /// <summary>The view's height for the distance bands, degrees: the far discs wide enough to see stars through them.</summary>
    public const float BandFovDeg = 6f;

    private readonly WorldOrigin _origin;
    private int _failed;

    public SkyOrderCheck(WorldOrigin origin)
    {
        _origin = origin;
        Name = "SkyOrderCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[skyorder] {what}"));

    private void Expect(bool ok, string what)
    {
        Log((ok ? "ok   " : "FAIL ") + what);
        if (!ok) _failed++;
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Wait(double seconds)
    {
        double until = GameClock.Now + seconds;
        while (GameClock.Now < until) await Frame();
    }

    private ChatManager? _chat;

    private async Task Run()
    {
        await Wait(4);
        _chat = GetTree().Root.FindChild(ChatManager.NodeName, recursive: true, owned: false) as ChatManager;
        if (_chat == null || BodyViews.Instance is not { } views) { Finish("no chat or no bodies"); return; }
        string which = CmdArgs.Value("--skyordercheck") is { Length: > 0 } v && !v.StartsWith("--", StringComparison.Ordinal) ? v : "all";
        try
        {
            if (which is "all" or "earth") await Behind(views, BodyId.Moon, BodyId.Earth, [0.2, 1.5, 4.458]);
            if (which is "all" or "jupiter")
            {
                if (views.GlobeOf(BodyId.Io) == null) Log("no Io globe (its data is not under --bodies): Jupiter from Io left out");
                else await Behind(views, BodyId.Io, BodyId.Jupiter, [0.2, 1.5]);
            }
            if (which is "all" or "sun") await Behind(views, BodyId.Moon, BodyId.Sun, [0.2, 1.5]);
            if (which is "all" or "bands")
            {
                foreach (double km in new[] { 20_000.0, 100_000, 400_000, 1_500_000 }) await Band(views, BodyId.Earth, km);
                foreach (double km in new[] { 20_000.0, 150_000, 600_000 }) await Band(views, BodyId.Moon, km);
            }
        }
        finally
        {
            Show(views, all: true);
        }
        Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
    }

    // ---- the parts hidden for a comparison

    private void HideFar(BodyViews views, BodyId far, bool hide)
    {
        if (far == BodyId.Sun) { SunDisc.CheckHide = hide; return; }
        views.GlobeOf(far)?.SetShown(!hide);
        if (far == BodyId.Earth) SkyBodies.CheckHideEarth = hide;
        if (far == BodyId.Moon) MoonDisc.CheckHide = hide;
        if (CelestialBody.Of(far).Sky is { } sky)
        {
            if (hide) SkyBodies.HiddenByGlobe.Add(sky);
            else SkyBodies.HiddenByGlobe.Remove(sky);
        }
    }

    private static void HideGround(BodyViews views, BodyId near, bool hide)
    {
        if (views.GlobeOf(near) is { } g)
        {
            g.SetShown(!hide);
            g.Ground?.SetShown(!hide);
        }
    }

    private void Show(BodyViews views, bool all)
    {
        StarField.CheckHide = false;
        SunDisc.CheckHide = false;
        MoonDisc.CheckHide = false;
        SkyBodies.CheckHideEarth = false;
        foreach (var b in new[] { BodyId.Earth, BodyId.Moon, BodyId.Io, BodyId.Jupiter })
        {
            views.GlobeOf(b)?.SetShown(true);
            views.GlobeOf(b)?.Ground?.SetShown(true);
        }
        SkyBodies.HiddenByGlobe.Remove(SkyBody.Jupiter);
    }

    private async Task<Image> Capture()
    {
        for (int i = 0; i < 3; i++) await Frame();
        var image = GetViewport().GetTexture().GetImage();
        image.Convert(Image.Format.Rgba8);
        return image;
    }

    /// <summary>Where two pictures differ.</summary>
    private static bool[] Diff(Image a, Image b)
    {
        var x = a.GetData();
        var y = b.GetData();
        var mask = new bool[x.Length / 4];
        int limit = (int)(DiffMin * 255);
        for (int i = 0; i < mask.Length; i++)
        {
            int k = i * 4;
            mask[i] = Math.Abs(x[k] - y[k]) > limit || Math.Abs(x[k + 1] - y[k + 1]) > limit || Math.Abs(x[k + 2] - y[k + 2]) > limit;
        }
        return mask;
    }

    /// <summary>
    /// A star's own point lifts its pixels by at least this much (0 to 1, the mean of the channels); its glow (the
    /// screen's bloom, spilling past an edge) by less.
    /// </summary>
    public const float StarMin = 0.12f;

    /// <summary>Where the first picture is brighter than the second (what a part adds: the stars' points).</summary>
    private static bool[] Brighter(Image a, Image b)
    {
        var x = a.GetData();
        var y = b.GetData();
        var mask = new bool[x.Length / 4];
        int limit = (int)(StarMin * 255);
        for (int i = 0; i < mask.Length; i++)
        {
            int k = i * 4;
            mask[i] = x[k] + x[k + 1] + x[k + 2] - y[k] - y[k + 1] - y[k + 2] > 3 * limit;
        }
        return mask;
    }

    /// <summary>The mask shrunk by <paramref name="px"/> at its edges (a ridge's edge pixels belong to both sides).</summary>
    private static bool[] Erode(bool[] m, int w, int px)
    {
        int h = m.Length / w;
        var o = new bool[m.Length];
        for (int y = px; y < h - px; y++)
            for (int x = px; x < w - px; x++)
            {
                bool all = true;
                for (int dy = -px; dy <= px && all; dy++)
                    for (int dx = -px; dx <= px && all; dx++)
                        all = m[(y + dy) * w + x + dx];
                o[y * w + x] = all;
            }
        return o;
    }

    /// <summary>The mask without what changes between two pictures of the same view (the chat's fading lines, the clock).</summary>
    private static bool[] Without(bool[] m, bool[] noise)
    {
        var o = new bool[m.Length];
        for (int i = 0; i < m.Length; i++) o[i] = m[i] && !noise[i];
        return o;
    }

    private static int Count(bool[] m)
    {
        int n = 0;
        foreach (bool b in m) if (b) n++;
        return n;
    }

    private static int Both(bool[] a, bool[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] && b[i]) n++;
        return n;
    }

    private static bool[] BothMask(bool[] a, bool[] b)
    {
        var o = new bool[a.Length];
        for (int i = 0; i < a.Length; i++) o[i] = a[i] && b[i];
        return o;
    }

    /// <summary>The pixels of an image inside the body's disc (its centre and least angular radius projected), shrunk to a share of it.</summary>
    private bool[] Disc(Image image, Camera3D cam, BodyId body, float share, out float radiusPx)
    {
        int w = image.GetWidth(), h = image.GetHeight();
        var mask = new bool[w * h];
        var centre = _origin.ToWorld(GlobalPos.On(body, new EcefPos(0, 0, 0)));
        double d = (centre - cam.GlobalPosition).Length();
        var view = GetViewport().GetVisibleRect().Size;
        float k = h / view.Y;
        float focal = view.Y * 0.5f / Mathf.Tan(Mathf.DegToRad(CameraLens.FovOf(cam)) * 0.5f) * k;
        // its least radius (the polar one): within it is the body whatever its flattening (Jupiter's is 6.5 %)
        radiusPx = (float)Math.Asin(Math.Min(1, CelestialBody.Of(body).Shape.B / d)) * focal;
        if (cam.IsPositionBehind(centre)) return mask;
        var c = cam.UnprojectPosition(centre) * k;
        float r = radiusPx * share;
        for (int y = Math.Max(0, (int)(c.Y - r)); y < Math.Min(h, (int)(c.Y + r) + 1); y++)
            for (int x = Math.Max(0, (int)(c.X - r)); x < Math.Min(w, (int)(c.X + r) + 1); x++)
                if ((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y) <= r * r) mask[y * w + x] = true;
        return mask;
    }

    /// <summary>The bright core of what a body draws: pixels of the picture brighter than this, among its own.</summary>
    private static bool[] Core(Image image, bool[] of, float bright)
    {
        var data = image.GetData();
        var mask = new bool[of.Length];
        for (int i = 0; i < of.Length; i++)
            if (of[i]) mask[i] = (data[i * 4] + data[i * 4 + 1] + data[i * 4 + 2]) / (3f * 255f) > bright;
        return mask;
    }

    // ---- places

    /// <summary>The point under <paramref name="target"/> on <paramref name="body"/> now: latitude and longitude, degrees.</summary>
    private static (double Lat, double Lon) Under(BodyId body, BodyId target)
    {
        var p = Frames.Convert(target, new DVec3(0, 0, 0), body, SkyClock.Now);
        var g = CelestialBody.Of(body).Shape.ToGeodetic(new EcefPos(p.X, p.Y, p.Z));
        return (g.LatDeg, g.LonDeg);
    }

    /// <summary>The place <paramref name="angleDeg"/> along a great circle from a point, toward a bearing (degrees from north).</summary>
    private static (double Lat, double Lon) Along(double latDeg, double lonDeg, double angleDeg, double bearingDeg)
    {
        double f = Mathf.DegToRad((float)latDeg), l = Mathf.DegToRad((float)lonDeg);
        double d = angleDeg * Math.PI / 180, b = bearingDeg * Math.PI / 180;
        double f2 = Math.Asin(Math.Sin(f) * Math.Cos(d) + Math.Cos(f) * Math.Sin(d) * Math.Cos(b));
        double l2 = l + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(f), Math.Cos(d) - Math.Sin(f) * Math.Sin(f2));
        double lon = l2 * 180 / Math.PI;
        while (lon > 180) lon -= 360;
        while (lon < -180) lon += 360;
        return (f2 * 180 / Math.PI, lon);
    }

    private async Task<Camera3D?> GoAndLook(BodyId body, double lat, double lon, double km, BodyId lookAt, double outM = 0)
    {
        _chat!.Send(string.Create(CultureInfo.InvariantCulture, $"/goto {CelestialBody.Of(body).Name.ToLowerInvariant()} {lat:F4} {lon:F4} {Math.Min(km, 1_000_000):F3}"));
        await Wait(1.5);
        if (GetViewport()?.GetCamera3D() is not Player.SpectatorCamera cam) { Expect(false, $"no fly camera at {body}"); return null; }
        if (outM > 0)
        {
            cam.GlobalPosition += _origin.UpAt(cam.GlobalPosition) * (float)outM;
            await Wait(1);
        }
        cam.Speed = Player.SpectatorSpeed.MinSpeed;
        var to = (_origin.ToWorld(GlobalPos.On(lookAt, new EcefPos(0, 0, 0))) - cam.GlobalPosition).Normalized();
        var up = _origin.UpAt(cam.GlobalPosition);
        if (Mathf.Abs(to.Dot(up)) > 0.99f) up = Vector3.Forward;
        cam.GlobalBasis = Basis.LookingAt(to, up);
        // the near ground and its globe's tiles round the camera
        await Wait(km < 10 ? 3 : 1.5);
        return cam;
    }

    // ---- far bodies behind the ground

    private async Task Behind(BodyViews views, BodyId near, BodyId far, double[] heightsKm)
    {
        var (lat0, lon0) = Under(near, far);
        string name = far == BodyId.Sun ? "sun" : CelestialBody.Of(far).Name.ToLowerInvariant();
        Log(string.Create(CultureInfo.InvariantCulture, $"{name} from {near}: the point under it {lat0:F2}, {lon0:F2}"));
        int views_ = 0, partial = 0, seen = 0, onGround = 0, starsOnGround = 0, starsInBody = 0;
        foreach (double km in heightsKm)
            foreach (double bearing in new[] { 0.0, 90, 180, 270 })
            {
                var (lat, lon) = Along(lat0, lon0, 89.5, bearing);
                if (await GoAndLook(near, lat, lon, km, far) is not { } cam) return;
                var all = await Capture();
                var again = await Capture();
                HideFar(views, far, true);
                var noFar = await Capture();
                StarField.CheckHide = true;
                var noFarStars = await Capture();
                HideGround(views, near, true);
                var bare = await Capture();
                Show(views, all: true);
                StarField.CheckHide = true;
                var noStars = await Capture();
                StarField.CheckHide = false;

                int w = all.GetWidth();
                var noise = Diff(all, again);
                var drawn = Without(Diff(all, noFar), noise);                         // the far body
                if (far == BodyId.Sun) drawn = Core(all, drawn, 0.8f);
                var ground = Without(Erode(Diff(noFarStars, bare), w, 2), noise);     // the ground (no stars, no far body behind it)
                var stars = Without(Brighter(all, noStars), noise);                   // the stars' light as drawn
                var disc = Disc(all, cam, far, 0.9f, out float r);
                // within its disc: hiding it also takes its glow off what is round it
                if (far != BodyId.Sun) drawn = BothMask(drawn, Disc(all, cam, far, 1.05f, out _));
                int violations = Both(drawn, ground);
                int starsGround = Both(stars, ground);
                // inside its disc, a pixel and a half in from its edge (a far globe is a coarse polygon a little inside its circle)
                int starsBody = far == BodyId.Sun ? 0 : Both(BothMask(stars, drawn), Disc(all, cam, far, Mathf.Max(0f, (r - 1.5f) / Mathf.Max(r, 1e-3f)), out _));
                views_++;
                if (Count(drawn) > 0) seen++;
                if (Count(drawn) > 0 && Both(disc, ground) > 0) partial++;
                onGround += violations;
                starsOnGround += starsGround;
                starsInBody += starsBody;
                string tag = string.Create(CultureInfo.InvariantCulture, $"{name}_{km:F1}km_{bearing:F0}");
                all.SavePng(ProjectSettings.GlobalizePath($"res://test_output/bodies/skyorder_{tag}.png"));
                Log(string.Create(CultureInfo.InvariantCulture,
                    $"{tag}: at {lat:F2}, {lon:F2}; {name} drawn on {Count(drawn)} px (disc {r:F1} px, {Both(disc, ground)} of its px behind ground), the ground {Count(ground)} px, stars {Count(stars)} px; on the ground: {name} {violations}, stars {starsGround}; stars in {name}: {starsBody}"));
            }
        Expect(onGround == 0, $"{name} from {near}: never drawn on the ground ({onGround} px over {views_} views, {seen} with it in view, {partial} with the ground across its disc)");
        Expect(starsOnGround == 0, $"{name} from {near}: no star on the ground ({starsOnGround} px)");
        if (far != BodyId.Sun) Expect(starsInBody == 0, $"{name} from {near}: no star through it ({starsInBody} px)");
        // the sun's disc is a pixel or two: its core is in view or not
        Expect(seen > 0 && (far == BodyId.Sun || partial > 0), $"{name} from {near}: in view in {seen} views, partly behind the ground in {partial}");
    }

    // ---- solid at every distance

    private async Task Band(BodyViews views, BodyId body, double km)
    {
        // over the body's midnight: its night side whole in view
        var (lat0, lon0) = Under(body, BodyId.Sun);
        double lat = -lat0, lon = lon0 + 180;
        if (lon > 180) lon -= 360;
        double outM = km > 1_000_000 ? (km - 1_000_000) * 1000 : 0;
        if (await GoAndLook(body, lat, lon, km, body, outM) is not { } cam) return;
        // a narrow view, so a far disc is wide enough to see through (1 px at 640 by 360 from 1.5 million km)
        float fovWas = cam.Fov;
        cam.Fov = BandFovDeg;
        var all = await Capture();
        var again = await Capture();
        StarField.CheckHide = true;
        var noStars = await Capture();
        StarField.CheckHide = false;
        var stars = Without(Brighter(all, noStars), Diff(all, again));
        var disc = Disc(all, cam, body, 1f, out float r);
        disc = Disc(all, cam, body, Mathf.Max(0f, (r - 1.5f) / Mathf.Max(r, 1e-3f)), out _);
        int inside = Both(stars, disc);
        string name = CelestialBody.Of(body).Name.ToLowerInvariant();
        string tag = string.Create(CultureInfo.InvariantCulture, $"{name}_band_{km:F0}km");
        all.SavePng(ProjectSettings.GlobalizePath($"res://test_output/bodies/skyorder_{tag}.png"));
        string by = views.GlobeOf(body)?.Drawn == true ? "its globe" : body == BodyId.Moon && !MoonDisc.HiddenByGlobe ? "the moon's disc" : "the Earth's own globe";
        Log(string.Create(CultureInfo.InvariantCulture,
            $"{tag}: the anchor on {_origin.Body}, the disc {r:F1} px drawn by {by}; stars {Count(stars)} px, inside the disc {inside}"));
        cam.Fov = fovWas;
        // stars round it, unless it fills the view
        if (r < all.GetHeight() * 0.4f) Expect(Count(stars) > 50, $"{tag}: stars in view ({Count(stars)} px)");
        Expect(r >= 2 && inside == 0, $"{tag}: solid, no star through it ({inside} px in a {r:F1} px disc)");
    }

    private void Finish(string? fail)
    {
        if (fail == null) GD.Print("[skyorder] RESULT: ok");
        else GD.PrintErr($"[skyorder] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
