using System;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--bodyviewcheck</c> (#1998, windowed: a headless run draws nothing; <c>--frame earth --bodies DIR --sun real --date
/// ...</c>): the Moon drawn one way by its globe and by its sphere in the sky, so nothing changes where one hands it to the
/// other (<see cref="BodyLook"/>). The fly camera on the line from the Moon to the Sun (its lit face; on the Earth's side the
/// face is dark half the month), looking at the Moon, two pairs of pictures, each pair at one place:
/// <list type="bullet">
/// <item>the handover, 68,000 km from the Moon's centre (inside the band where either body may count): the anchor on the
/// Moon, then relabelled onto the Earth (<see cref="WorldOrigin.Rebase"/>), as a flight home does;</item>
/// <item>the globe's size edge, 142,000 km out (the Moon's radius about 1 % of the view's height): the globe forced on,
/// then off (<see cref="BodyLook.ForceGlobe"/>).</item>
/// </list>
/// Each pair's mean difference over the Moon's box must stay under <see cref="DiffMax"/> of full scale, and its mean
/// brightness within <see cref="BrightRatio"/>. Pictures in <c>test_output/bodies/bodyview_*.png</c>.
/// </summary>
public partial class BodyViewCheck : Node
{
    public static bool Requested => CmdArgs.Has("--bodyviewcheck");

    /// <summary>
    /// A pair's mean absolute difference inside the Moon's disc, of full scale: loose enough for a pixel's shift on a disc
    /// 7 to 22 px across (the two are drawn by different meshes), tight enough for a glow against a lit disc (~0.4).
    /// </summary>
    public const double DiffMax = 0.1;

    /// <summary>A pair's mean brightness over the Moon's box may differ by this ratio at most.</summary>
    public const double BrightRatio = 0.15;

    private readonly WorldOrigin _origin;
    private int _failed;

    public BodyViewCheck(WorldOrigin origin)
    {
        _origin = origin;
        Name = "BodyViewCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[bodyview] {what}"));

    private void Expect(bool ok, string what)
    {
        Log((ok ? "ok   " : "FAIL ") + what);
        if (!ok) _failed++;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private Camera3D? Camera() => GetViewport()?.GetCamera3D();

    private async Task<bool> Until(Func<bool> condition, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            if (condition()) return true;
            await Frames(1);
        }
        return condition();
    }

    private Vector3 MoonCentre() => _origin.ToWorld(GlobalPos.On(BodyId.Moon, new EcefPos(0, 0, 0)));

    private async Task Run()
    {
        await Frames(240);
        if (DisplayServer.GetName() == "headless") { Finish("windowed only: a headless run draws nothing"); return; }
        if (GetTree().Root.FindChild(ChatManager.NodeName, recursive: true, owned: false) is not ChatManager chat) { Finish("no chat"); return; }

        // the handover: the Moon's anchor, then the Earth's, at one place
        await Place(chat, 68_000_000);
        if (_origin.Body != BodyId.Moon) { Finish($"the anchor is on {_origin.Body}, not the Moon, at 68,000 km"); return; }
        var moonSide = await Picture("handover_moon");
        _origin.Rebase(BodyId.Earth, SkyClock.Now);
        await Frames(20);
        Aim();
        await Frames(30);
        if (_origin.Body != BodyId.Earth) { Finish("the relabelling onto the Earth did not hold"); return; }
        var earthSide = await Picture("handover_earth");
        Compare("the handover (the Moon's anchor, then the Earth's)", moonSide, earthSide);

        // the globe's size edge: forced on, then off, at one place
        await Place(chat, 142_000_000);
        BodyLook.ForceGlobe = true;
        if (!await Until(() => MoonDisc.HiddenByGlobe, 900)) { Finish("the Moon's globe never drew at 142,000 km"); return; }
        await Frames(30);
        var globe = await Picture("edge_globe");
        BodyLook.ForceGlobe = false;
        if (!await Until(() => !MoonDisc.HiddenByGlobe, 120)) { Finish("the Moon's globe never stepped aside"); return; }
        await Frames(10);
        var sky = await Picture("edge_sky");
        BodyLook.ForceGlobe = null;
        Compare("the globe's size edge (the globe, then the sky's sphere)", globe, sky);
        Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
    }

    /// <summary>The fly camera <paramref name="fromCentre"/> metres from the Moon's centre toward the Sun, looking at the Moon.</summary>
    private async Task Place(ChatManager chat, double fromCentre)
    {
        var toSun = Core.Frames.Convert(BodyId.Sun, new DVec3(0, 0, 0), BodyId.Moon, SkyClock.Now);
        var g = CelestialBody.Moon.Shape.ToGeodetic(new EcefPos(toSun.X, toSun.Y, toSun.Z));
        double km = (fromCentre - CelestialBody.Moon.Shape.A) / 1000;
        chat.Send(string.Create(CultureInfo.InvariantCulture, $"/goto moon {g.LatDeg:F4} {g.LonDeg:F4} {km:F0}"));
        await Frames(30);
        Aim();
        // the globe's tiles and the sky settle
        await Frames(240);
        Aim();
        await Frames(10);
    }

    private void Aim()
    {
        if (Camera() is not { } cam) return;
        var look = (MoonCentre() - cam.GlobalPosition).Normalized();
        cam.GlobalBasis = Basis.LookingAt(look, Mathf.Abs(look.Dot(Vector3.Up)) > 0.9f ? Vector3.Forward : Vector3.Up);
    }

    private sealed record Shot(Image Image, Rect2I Box, Vector2 Centre, float Radius);

    /// <summary>The view saved, with the Moon's box on it (its projected centre and radius, a few pixels round).</summary>
    private async Task<Shot> Picture(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        string dir = ProjectSettings.GlobalizePath("res://test_output/bodies");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"bodyview_{name}.png");
        image.SavePng(path);
        var cam = Camera()!;
        // the viewport's own coordinates (the project's base size) into the picture's (the window's)
        var view = GetViewport().GetVisibleRect().Size;
        var centre = cam.UnprojectPosition(MoonCentre()) * new Vector2(image.GetWidth() / view.X, image.GetHeight() / view.Y);
        double distance = (MoonCentre() - cam.GlobalPosition).Length();
        float fov = Mathf.DegToRad(Core.CameraLens.FovOf(cam));
        float disc = (float)(CelestialBody.Moon.Shape.A / distance / fov * image.GetHeight());
        float r = disc + 4;
        var box = new Rect2I((int)(centre.X - r), (int)(centre.Y - r), (int)(2 * r), (int)(2 * r))
            .Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
        // where the brightest pixel near the middle is, against the Moon's projected centre (a check of the check)
        Vector2I best = default;
        float bestL = -1;
        for (int y = Math.Max(0, image.GetHeight() / 2 - 60); y < Math.Min(image.GetHeight(), image.GetHeight() / 2 + 60); y++)
            for (int x = Math.Max(0, image.GetWidth() / 2 - 60); x < Math.Min(image.GetWidth(), image.GetWidth() / 2 + 60); x++)
            {
                var c = image.GetPixel(x, y);
                float l = c.R + c.G + c.B;
                if (l > bestL) { bestL = l; best = new Vector2I(x, y); }
            }
        Log(string.Create(CultureInfo.InvariantCulture,
            $"{name}: anchor {_origin.Body}, globe drawn {MoonDisc.HiddenByGlobe}, in space {BodyLook.InSpace:F2}, the Moon {2 * disc:F1} px across at {distance / 1e6:F1} Mm, centre {centre.X:F1},{centre.Y:F1} of {image.GetWidth()}x{image.GetHeight()}, brightest near the middle {best.X},{best.Y}, {path}"));
        return new Shot(image, box, centre, disc);
    }

    private void Compare(string what, Shot a, Shot b)
    {
        var box = a.Box.Intersection(b.Box);
        if (box.Size.X <= 0 || box.Size.Y <= 0) { Expect(false, $"{what}: the Moon is off the picture"); return; }
        double diff = 0, ma = 0, mb = 0;
        int n = 0;
        for (int y = box.Position.Y; y < box.End.Y; y++)
            for (int x = box.Position.X; x < box.End.X; x++)
            {
                // inside the disc only: the stars round it are the sky's, not the Moon's
                if (new Vector2(x + 0.5f, y + 0.5f).DistanceTo(a.Centre) > a.Radius - 1f) continue;
                var ca = a.Image.GetPixel(x, y);
                var cb = b.Image.GetPixel(x, y);
                diff += (Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B)) / 3;
                ma += (ca.R + ca.G + ca.B) / 3;
                mb += (cb.R + cb.G + cb.B) / 3;
                n++;
            }
        diff /= n;
        ma /= n;
        mb /= n;
        double ratio = Math.Abs(ma - mb) / Math.Max(Math.Max(ma, mb), 1e-6);
        Log(string.Create(CultureInfo.InvariantCulture, $"{what}: over {box.Size.X}x{box.Size.Y} px the mean difference {diff:F4}, brightness {ma:F4} then {mb:F4}"));
        Expect(diff < DiffMax, string.Create(CultureInfo.InvariantCulture, $"{what}: the Moon looks the same ({diff:F4} of {DiffMax})"));
        Expect(ratio < BrightRatio, string.Create(CultureInfo.InvariantCulture, $"{what}: as bright ({ratio:P1})"));
    }

    private void Finish(string? fail)
    {
        BodyLook.ForceGlobe = null;
        if (fail == null) GD.Print("[bodyview] RESULT: ok");
        else GD.PrintErr($"[bodyview] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
