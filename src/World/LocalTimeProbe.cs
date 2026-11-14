using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.World;

/// <summary>
/// <c>--localtimecheck</c> (offline, #1801): <c>/time</c> where the player stands. Typed through the
/// chat, each named hour must put the clock where <see cref="LocalHours"/> says and light the sky as
/// it says: the sun on the horizon at sunrise and sunset, up at day, highest at noon, down at night
/// and midnight. <c>12:00</c> in the player's zone, a named zone, and <c>/time query</c>'s reply.
/// At home (<c>--localtimecheck home</c>, Geneva) every named hour is the Swiss clock's as before #1801.
/// <code>--systems ui,physics,sky --sun real --date 2026-06-21 --at -4679383,7259183 --localtimecheck</code>
/// </summary>
public partial class LocalTimeProbe : Node
{
    public static bool Requested => CmdArgs.Has("--localtimecheck");

    private int _failures;
    private readonly List<string> _heard = new();

    private ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);

    public override async void _Ready()
    {
        bool home = CmdArgs.Value("--localtimecheck") == "home";
        // --at is LV95 notation, typed: the place it names on the Earth
        var target = SpawnPoint.ParseLv95("--at") is (double te, double tn) ? GlobalPos.FromLv95(te, tn, 500) : LocalHours.Bern;
        // the sky reads its place off the camera: wait until it stands where we were sent
        if (!await Until(() => Chat != null && DayNight.Instance is { } d && DayNight.RealSun
                && Near(d.Place, target), 120))
        {
            Fail($"no real sky at {Where(target)} (sky at {(DayNight.Instance is { } d ? Where(d.Place) : "-")}, real sun {DayNight.RealSun})");
            Done();
            return;
        }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        try { await Run(DayNight.Instance!, home); }
        catch (System.Exception e) { Expect(false, $"threw: {e.Message}"); }
        Done();
    }

    private void Done()
    {
        GD.Print(_failures == 0 ? "[localtimecheck] RESULT: ok" : $"[localtimecheck] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private static bool Near(GlobalPos a, GlobalPos b) => a.HorizontalDistanceTo(b) < 5000;

    /// <summary>A place as latitude / longitude, for the log.</summary>
    internal static string Where(GlobalPos at)
    {
        var (lat, lon) = LocalHours.LatLon(at);
        return string.Format(CultureInfo.InvariantCulture, "{0:F4}°, {1:F4}°", lat, lon);
    }

    private async Task Run(DayNight day, bool home)
    {
        var date = SunPath.Today();
        var place = day.Place;
        var zone = LocalHours.ZoneAt(place, realSun: true);
        GD.Print($"[localtimecheck] at {Where(place)} on {date:yyyy-MM-dd}, home {LocalHours.IsHome(place)}, zone {zone.Label}");
        Expect(home == LocalHours.IsHome(place), home ? "Geneva is home" : "the place is far from home");

        var elevations = new Dictionary<string, float>();
        foreach (var (name, fixedHour) in TimeCommand.Named)
        {
            if (!LocalHours.TryNamed(name, place, date, true, out double want, out string why))
            {
                // the polar day or night: the chat must say so and leave the clock
                Fail($"{name}: {why}");
                continue;
            }
            if (home)
            {
                double before = name switch
                {
                    "sunrise" => SunPath.RiseOrSet(date, rise: true),
                    "sunset" => SunPath.RiseOrSet(date, rise: false),
                    _ => fixedHour,
                };
                Expect(System.Math.Abs(TimeCommand.ShortWay(want, before)) < 1e-9,
                    $"{name} at home is {TimeCommand.Format(want)}, as before #1801 ({TimeCommand.Format(before)})");
            }
            Chat!.Send($"/time set {name}");
            bool there = await Until(() => System.Math.Abs(TimeCommand.ShortWay(day.Hour, want)) < 0.005, 5);
            await Frames(3);
            float el = day.SunElevationDeg;
            elevations[name] = el;
            GD.Print(string.Format(CultureInfo.InvariantCulture,
                "[localtimecheck] {0,-8} -> {1} Swiss, sun {2:F2}°", name, TimeCommand.Format(day.Hour), el));
            Expect(there, $"/time set {name} put the clock at {TimeCommand.Format(want)} ({day.Clock})");
        }

        if (!home)
        {
            Expect(Within(elevations, "sunrise", -2.0f, 0.5f), $"the sun on the horizon at sunrise ({Of(elevations, "sunrise")}°)");
            Expect(Within(elevations, "sunset", -2.0f, 0.5f), $"the sun on the horizon at sunset ({Of(elevations, "sunset")}°)");
            Expect(Of(elevations, "day") > 5, $"day is daylight ({Of(elevations, "day")}°)");
            Expect(Of(elevations, "noon") > Of(elevations, "day"), "the sun is higher at noon than at day");
            double noon = LocalHours.SolarNoon(date, place);
            float before = SunPath.ElevationAt(date, noon - 0.25, place), after = SunPath.ElevationAt(date, noon + 0.25, place);
            Expect(Of(elevations, "noon") > before && Of(elevations, "noon") > after,
                $"noon is the sun's highest ({Of(elevations, "noon")}° against {before:F2}° and {after:F2}° a quarter-hour either side)");
            Expect(Of(elevations, "night") < -6, $"night is dark ({Of(elevations, "night")}°)");
            Expect(Of(elevations, "midnight") < 0, $"midnight is dark ({Of(elevations, "midnight")}°)");
        }

        // a typed hour reads in the player's zone, or in the one named
        await SetAndCheck("/time set 12:00", zone.ToSwiss(12, date), $"12:00 {zone.Label}");
        await SetAndCheck("/time set 12:00 utc", new ClockZone(0).ToSwiss(12, date), "12:00 UTC");
        await SetAndCheck("/time 21:30 swiss", 21.5, "21:30 Swiss time");

        _heard.Clear();
        Chat!.Send("/time query");
        string expect = zone.IsSwiss ? "It is 21:30" : $"{TimeCommand.Format(zone.FromSwiss(21.5, date))} {zone.Label} (21:30 Swiss time)";
        Expect(await Until(() => _heard.Exists(l => l.Contains(expect)), 3), $"/time query says '{expect}' ({string.Join(" | ", _heard)})");
    }

    private async Task SetAndCheck(string line, double swissHour, string what)
    {
        var day = DayNight.Instance!;
        Chat!.Send(line);
        Expect(await Until(() => System.Math.Abs(TimeCommand.ShortWay(day.Hour, swissHour)) < 0.005, 5),
            $"'{line}' is {what}, {TimeCommand.Format(swissHour)} Swiss ({day.Clock})");
    }

    private static float Of(Dictionary<string, float> e, string name) => e.TryGetValue(name, out float v) ? v : float.NaN;

    private static bool Within(Dictionary<string, float> e, string name, float lo, float hi) => Of(e, name) is var v && v >= lo && v <= hi;

    private void Expect(bool ok, string what)
    {
        if (ok) GD.Print($"[localtimecheck] ok — {what}");
        else Fail(what);
    }

    private void Fail(string what)
    {
        _failures++;
        GD.Print($"[localtimecheck] FAIL — {what}");
    }

    private async Task<bool> Until(System.Func<bool> condition, double seconds)
    {
        double end = RealClock.Now + seconds;
        while (!condition())
        {
            if (RealClock.Now > end) return false;
            await Frames(1);
        }
        return true;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
