using System;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--moonlookcheck</c> (#2047, windowed: the moon is placed just before a frame is drawn; <c>--frame earth --chunks
/// fixture:straight</c>): the Moon is enlarged and haloed only from the Earth's ground and near-Earth space
/// (<see cref="MoonDisc.NearEarth"/>), its true size and brightness everywhere else, a point by its magnitude as the planets
/// are. The fly camera looks at the Moon from four places and reads what <see cref="MoonDisc"/> drew:
/// <list type="bullet">
/// <item>the Earth's ground (the run's start): the near-Earth look whole, its size the old rule's (true size, at least
/// <see cref="MoonDisc.ScreenShare"/> of the view);</item>
/// <item>Mars (100,000 km up), Saturn (1 million km up) and 1 AU out from the Earth: none of it, the true angular size, no
/// halo, a magnitude that fits the distance and a point only when it is brighter than the 7th; the Earth beside it a point
/// by the same rule (<see cref="SkyBodies"/>; it was not drawn at all); Jupiter's magnitude from Saturn logged, the sky's
/// own reading from the camera.</item>
/// </list>
/// Pictures in <c>test_output/bodies/moonlook_*.png</c>.
/// </summary>
public partial class MoonLookCheck : Node
{
    public static bool Requested => CmdArgs.Has("--moonlookcheck");

    private readonly WorldOrigin _origin;
    private int _failed;

    public MoonLookCheck(WorldOrigin origin)
    {
        _origin = origin;
        Name = "MoonLookCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[moonlook] {what}"));

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

    private async Task Run()
    {
        await Wait(4);
        if (GetTree().Root.FindChild(ChatManager.NodeName, recursive: true, owned: false) is not ChatManager chat) { Finish("no chat"); return; }
        if (GetTree().Root.FindChild("MoonDisc", recursive: true, owned: false) is not MoonDisc moon) { Finish("no moon disc"); return; }

        // the Earth's ground: as before
        var ground = await LookAt(moon, "ground");
        float fov = Mathf.DegToRad(CameraLens.FovOf(GetViewport().GetCamera3D()));
        if (ground is { } g)
        {
            Expect(g.Shown.Near == 1f, $"ground: the near-Earth look whole ({g.Shown.Near:F3})");
            float old = Mathf.Max(g.True, fov * MoonDisc.ScreenShare);
            Expect(Mathf.Abs(g.Shown.Angular - old) <= old * 1e-5f, $"ground: drawn as before ({Mathf.RadToDeg(g.Shown.Angular):F4}° radius, the old rule {Mathf.RadToDeg(old):F4}°)");
            Expect(g.Shown.Point == 0f, "ground: no point (the halo stands for its brightness)");
        }

        await Far(chat, moon, "mars", "/goto mars 0 0 100000", 0);
        await Far(chat, moon, "saturn", "/goto saturn 0 0 1000000", 0);
        if (SkyBodies.Instance?.MagnitudeOf(SkyBody.Jupiter) is { } jupiter)
            Log(string.Create(CultureInfo.InvariantCulture, $"Jupiter from Saturn: magnitude {jupiter:F2} (−2.9 at best from the Earth)"));
        // 1 AU out from the Earth, along its up from a place at the equator
        await Far(chat, moon, "1au", "/goto earth 0 0 1000000", Ephemeris.AuMetres - 1e9);
        Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
    }

    private sealed record Look(MoonDisc.ShownMoon Shown, float True);

    private async Task Far(ChatManager chat, MoonDisc moon, string name, string go, double outM)
    {
        chat.Send(go);
        await Wait(1.5);
        if (outM > 0 && GetViewport()?.GetCamera3D() is { } cam)
        {
            cam.GlobalPosition += _origin.UpAt(cam.GlobalPosition) * (float)outM;
            await Wait(1.5);
        }
        if (await LookAt(moon, name) is not { } l) return;
        // from its distance: full (no phase term) at the bright end, the thinnest crescent's term (9.4) at the faint end,
        // the Sun 0.98 to 1.02 AU from it
        double delta = MoonDisc.RadiusMetres / l.True / Ephemeris.AuMetres;
        var magnitude = (Lo: 0.21 + 5 * Math.Log10(0.98 * delta) - 0.05, Hi: 0.21 + 5 * Math.Log10(1.02 * delta) + 9.5);
        Expect(l.Shown.Near == 0f, $"{name}: none of the near-Earth look ({l.Shown.Near:F3})");
        Expect(Mathf.Abs(l.Shown.Angular - l.True) <= l.True * 1e-4f, string.Create(CultureInfo.InvariantCulture,
            $"{name}: its true size ({Mathf.RadToDeg(l.Shown.Angular) * 3600:F2}\" radius, true {Mathf.RadToDeg(l.True) * 3600:F2}\")"));
        Expect(l.Shown.Halo == 0f, $"{name}: no halo ({l.Shown.Halo:F3})");
        // a point by eye only brighter than the 7th magnitude, as the sky's planets
        Expect(l.Shown.Magnitude >= magnitude.Lo && l.Shown.Magnitude <= magnitude.Hi && (l.Shown.Point > 0f) == (l.Shown.Magnitude < 7f),
            string.Create(CultureInfo.InvariantCulture,
                $"{name}: magnitude {l.Shown.Magnitude:F2} ({delta:F2} AU off: {magnitude.Lo:F2} full to {magnitude.Hi:F2} new), a point {(l.Shown.Point > 0f ? "shown" : "not shown")} (alpha {l.Shown.Point:F2})"));

        // the Earth beside it: a point as the planets' (it had none: not drawn at all from Mars)
        if (GetViewport()?.GetCamera3D() is { } c && BodyLook.Seen(BodyId.Earth, c.GlobalPosition, out _, out double earthM, out _, out _)
            && SkyBodies.Instance is { } skyBodies)
        {
            double earthAu = earthM / Ephemeris.AuMetres;
            // its phase law from full (−0.2 at its least) to new (+6.5 at 180°)
            var earth = (Lo: -3.99 + 5 * Math.Log10(0.98 * earthAu) - 0.2, Hi: -3.99 + 5 * Math.Log10(1.02 * earthAu) + 6.6);
            float em = skyBodies.EarthMagnitude;
            Expect(em >= earth.Lo && em <= earth.Hi && (skyBodies.EarthPointAlpha > 0f) == (em < 7f), string.Create(CultureInfo.InvariantCulture,
                $"{name}: the Earth's magnitude {em:F2} ({earthAu:F2} AU off: {earth.Lo:F2} to {earth.Hi:F2}), its point {(skyBodies.EarthPointAlpha > 0f ? "shown" : "not shown")} (alpha {skyBodies.EarthPointAlpha:F2})"));
        }
    }

    /// <summary>The camera turned to the Moon; what the disc drew there, and its true angular radius.</summary>
    private async Task<Look?> LookAt(MoonDisc moon, string name)
    {
        if (GetViewport()?.GetCamera3D() is not { } cam) { Expect(false, $"{name}: no camera"); return null; }
        if (!BodyLook.Seen(BodyId.Moon, cam.GlobalPosition, out var dir, out double distance, out _, out _)
            && DayNight.Instance is { Sky: { } sky })
            (dir, distance) = (sky.Moon, sky.MoonDistance);
        var hint = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.95f ? Vector3.Forward : Vector3.Up;
        cam.GlobalBasis = Basis.LookingAt(dir, hint);
        await Wait(0.5);
        var shown = moon.Shown;
        float trueAngular = (float)(MoonDisc.RadiusMetres / distance);
        Log(string.Create(CultureInfo.InvariantCulture,
            $"{name}: the anchor on {_origin.Body}, the Moon {distance / 1000:F0} km off; drawn {Mathf.RadToDeg(shown.Angular) * 3600:F1}\" radius (true {Mathf.RadToDeg(trueAngular) * 3600:F1}\"), halo {shown.Halo:F3}, point {shown.Point:F2} at magnitude {shown.Magnitude:F2}, near-Earth {shown.Near:F2}"));
        Shot($"moonlook_{name}");
        return new Look(shown, trueAngular);
    }

    private void Shot(string name)
    {
        string dir = ProjectSettings.GlobalizePath("res://test_output/bodies");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, name + ".png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"picture {path}");
    }

    private void Finish(string? fail)
    {
        if (fail == null) GD.Print("[moonlook] RESULT: ok");
        else GD.PrintErr($"[moonlook] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
