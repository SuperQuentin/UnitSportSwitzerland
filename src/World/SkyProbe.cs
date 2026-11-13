using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.World;

/// <summary>
/// <c>--skycheck</c> (offline, <c>--systems ui,physics,sky --sun real --date 2026-12-11 --occasion none</c>,
/// #1444): the stars loaded; every planet and big moon where a fresh ephemeris puts it; satellites
/// sunlit before dawn; and through a set-up telescope, the lock holding Jupiter at 04:00, Saturn at
/// 20:00 and a satellite at 06:20. Windowed it saves <c>test_output/sky_jupiter.png</c>,
/// <c>sky_saturn.png</c> and <c>sky_satellite.png</c>. The telescope it set up is gone at the end.
/// </summary>
public partial class SkyProbe : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--skycheck") >= 0;

    private int _failures;
    private long _scope;

    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public override async void _Ready()
    {
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        await Seconds(2.0);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => Me is { } m && m.IsOnFloor() && PlacedObjects.Instance != null && Telescopes.Instance != null
                && DayNight.Instance != null && SkyBodies.Instance != null && Satellites.Instance != null, 120))
        {
            Fail("no player, sky or telescopes");
            return;
        }
        await Seconds(1.0);
        try { await Run(Me!, PlacedObjects.Instance!, DayNight.Instance!); }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        TelescopeView.Current?.Close();
        await Seconds(0.3);
        if (_scope != 0) PlacedObjects.Instance?.RequestRemove(_scope);
        await Seconds(0.5);
        GD.Print(_failures == 0 ? "[skycheck] RESULT: ok" : $"[skycheck] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task Run(FootPlayer me, PlacedObjects placed, DayNight day)
    {
        var stars = GetParent().GetNodeOrNull<StarField>("StarField");
        Expect(stars is { Count: > 9000 }, $"the star catalogue ({stars?.Count} stars)");
        Expect(Satellites.Instance!.Count > 100, $"the satellites' elements ({Satellites.Instance.Count})");

        // every body where the ephemeris puts it, on the turning sky
        day.HourOverride = 4.0;
        await Seconds(1.5);
        if (day.Sky is not { } sky) { Fail("no real sky"); return; }
        foreach (var body in SkyBodies.Bodies)
        {
            var drawn = SkyBodies.Instance!.DirectionOf(body);
            var truth = SunPath.Toward(body, sky.Date, sky.Hour, sky.E, sky.N, out _);
            float off = drawn is { } d ? Mathf.RadToDeg(d.AngleTo(truth)) : 99f;
            Expect(off < 0.01f, $"{body} drawn where the ephemeris has it ({off:0.0000}°)");
        }

        // a telescope, set up beside the player through the API, looked through with E's path
        PlacedResult? set = null;
        var at = me.GlobalPosition + me.GlobalBasis.Z * -1.0f;
        placed.RequestPlace(PlacedKind.Telescope, new Transform3D(Basis.Identity, at), "", r => set = r);
        if (!await Until(() => set != null, 4) || set!.Value.Object is not { } scope) { Fail("no telescope"); return; }
        _scope = scope.Id;
        await Seconds(0.5);
        Expect(Telescopes.TryInteract(me) && await Until(() => TelescopeView.Current != null, 3), "looking through it");
        if (TelescopeView.Current is not { } view) return;

        await Track(view, day, 4.0, SkyBody.Jupiter, "jupiter");
        await Track(view, day, 20.0, SkyBody.Saturn, "saturn");
        await Track(view, day, 5.0, SkyBody.Mars, "mars");   // #1813: a disc at the longest end

        // before dawn the low satellites are sunlit over a dark sky
        day.HourOverride = 6.3;
        await Seconds(3.0);   // the worker propagates them all to the new moment
        Expect(Satellites.Instance.Shown > 0, $"satellites show before dawn ({Satellites.Instance.Shown})");
        if (Satellites.Instance.Nearest(Vector3.Up) is { } sat)
        {
            if (view.Locked) view.ToggleLock();   // still on Saturn
            while (view.Focal > 800f) view.Zoom(-1);
            view.Aim(sat.Direction);
            await Frame();
            Expect(view.ToggleLock() && view.Satellite == sat.Name, $"locked on a satellite ({view.Satellite})");
            day.HourOverride = null;   // the sky runs again: the satellite moves fast
            await Seconds(1.0);
            float off = Satellites.Instance.DirectionOf(sat.Name) is { } now ? Mathf.RadToDeg(view.Direction.AngleTo(now)) : 99f;
            Expect(!view.Locked || off < 0.05f, $"followed it ({off:0.000}° off, or it set)");
            await Shot("satellite");
        }
    }

    /// <summary>At <paramref name="hour"/>, aims at <paramref name="body"/>, zooms all the way, locks, and checks it stays in the middle.</summary>
    private async Task Track(TelescopeView view, DayNight day, double hour, SkyBody body, string name)
    {
        if (view.Locked) view.ToggleLock();
        day.HourOverride = hour;
        await Seconds(1.0);
        Expect(SkyBodies.Instance!.DirectionOf(body) is { Y: > 0.2f }, $"{body} well up at {hour:0}:00");
        if (SkyBodies.Instance.DirectionOf(body) is not { } dir) return;
        view.Aim(dir);
        while (view.Focal < TelescopeOptics.FocalMax) view.Zoom(1);
        await Frame();
        Expect(view.ToggleLock() && view.Locked && view.Target == body && view.Satellite == null, $"locked on {body} ({view.Target})");
        day.HourOverride = null;   // the sky turns again
        await Seconds(3.0);
        float off = SkyBodies.Instance.DirectionOf(body) is { } now ? Mathf.RadToDeg(view.Direction.AngleTo(now)) : 99f;
        // a tenth of the view: at the longest end (#1813) the view is 9.9″ tall
        float within = TelescopeOptics.Fov(view.Focal) * 0.1f;
        Expect(off < within, $"{body} held in the middle as the sky turns ({off * 3600f:0.00}″, within {within * 3600f:0.00}″)");
        await Shot(name);
    }

    private async Task Shot(string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        await Seconds(0.8);
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("res://test_output"));
        GetViewport().GetTexture().GetImage().SavePng($"res://test_output/sky_{name}.png");
        GD.Print($"[skycheck] wrote test_output/sky_{name}.png");
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!condition())
        {
            if (GameClock.Now > end) return false;
            await Frame();
        }
        return true;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Expect(bool ok, string what)
    {
        GD.Print($"[skycheck]   {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        _failures++;
        GD.Print($"[skycheck] FAIL {why}");
    }
}
