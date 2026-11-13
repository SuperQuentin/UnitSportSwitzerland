using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.World;

/// <summary>
/// <c>--localtimenet A|B</c> with <c>--connect</c> (driven by <c>tools/localtimenetcheck.sh</c>, #1801):
/// an admin far from Switzerland sets the one shared clock by the sun where they stand.
/// <list type="bullet">
/// <item>A stands far away (<c>--at</c>), logs in and types <c>/time set noon</c>: the server reads A's
/// place from the position A published, so the world clock goes to A's solar noon, and A's sun is at its
/// highest. B, at home, sees the same clock.</item>
/// <item>A types <c>/time set 12:00</c>: the server reads it in A's zone (UTC + the longitude / 15°).</item>
/// </list>
/// </summary>
public partial class LocalTimeNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--localtimenet");

    public LocalTimeNetProbe(ItemController items) : base(items, "localtimenet", "LT") { }
    public LocalTimeNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (!await Until(() => WorldClock.Active && Net.ClockSync.Synced && DayNight.Instance != null, 20))
        {
            Fail("no world clock");
            await Finish(1);
            return;
        }
        if (_role == "A") await RunA(); else await RunB();
        await Finish(2.0);
    }

    private async Task RunA()
    {
        var day = DayNight.Instance!;
        var place = day.Place;
        var date = SunPath.Today();
        Expect(DayNight.RealSun && !LocalHours.IsHome(place.E, place.N),
            $"A stands far from home under the real sun ({place.E:F0},{place.N:F0}, real {DayNight.RealSun})");
        // whoever joins first says so until the other answers: a single line can go before the other listens
        bool met = false;
        for (int i = 0; i < 40 && !met; i++)
        {
            Say("hello");
            met = await Heard("B", "ready", 3);
        }
        if (!met) { Fail("B never answered"); return; }
        // the server's flood gate (a burst of 5, then 0.6 lines a second) counts commands too: let it refill
        await Seconds(FloodGap * 3);
        if (!await LogIn("test")) return;
        // a stopped clock: what each peer reads is what was set, however late it reads it
        await Seconds(FloodGap);
        Chat!.Send("/time speed 0");
        Expect(await Until(() => WorldClock.MinutesPerDay == 0, 10), "the clock stopped");

        double noon = LocalHours.SolarNoon(date, place.E, place.N);
        await Seconds(FloodGap);
        Chat.Send("/time set noon");
        Expect(await Until(() => Near(WorldClock.Hour, noon) && Near(day.Hour, noon), 10),
            $"the world clock went to A's solar noon, {TimeCommand.Format(noon)} Swiss ({TimeCommand.Format(WorldClock.Hour)}, sky {day.Clock})");
        await Seconds(0.3);
        SunPath.Toward(SkyBody.Sun, date, noon - 0.25, place.E, place.N, out float before);
        SunPath.Toward(SkyBody.Sun, date, noon + 0.25, place.E, place.N, out float after);
        float el = day.SunElevationDeg;
        Expect(el > before && el > after, string.Format(CultureInfo.InvariantCulture,
            "A's sun is at its highest ({0:F2}° against {1:F2}° and {2:F2}° a quarter-hour either side)", el, before, after));
        await Seconds(FloodGap);
        Say(string.Format(CultureInfo.InvariantCulture, "noon {0:F5}", noon));
        Expect(await Heard("B", "saw noon", 15), "B's clock went to A's noon");

        var zone = LocalHours.ZoneAt(place.E, place.N, realSun: true);
        double twelve = zone.ToSwiss(12, date);
        await Seconds(FloodGap);
        Chat.Send("/time set 12:00");
        Expect(await Until(() => Near(WorldClock.Hour, twelve), 10),
            $"/time set 12:00 is 12:00 {zone.Label}, {TimeCommand.Format(twelve)} Swiss ({TimeCommand.Format(WorldClock.Hour)})");
        await Seconds(FloodGap);
        Say(string.Format(CultureInfo.InvariantCulture, "twelve {0:F5}", twelve));
        Expect(await Heard("B", "saw twelve", 15), "B's clock went to 12:00 in A's zone");
    }

    private async Task RunB()
    {
        var day = DayNight.Instance!;
        if (!await Heard("A", "hello", 150)) { Fail("A never came"); return; }
        Say("ready");
        foreach (var word in new[] { "noon", "twelve" })
        {
            if (await HeardStarting("A", word + " ", 90) is not { } line) { Fail($"A never set {word}"); return; }
            double want = double.Parse(line[(word.Length + 1)..].Split(' ')[0], CultureInfo.InvariantCulture);
            if (await Until(() => Near(WorldClock.Hour, want) && Near(day.Hour, want), 10)) Say($"saw {word}");
            else Fail($"B's clock is at {TimeCommand.Format(WorldClock.Hour)} (sky {day.Clock}), not A's {word} {TimeCommand.Format(want)}");
        }
    }

    /// <summary>Seconds between A's lines: the flood gate's refill of one line (1 / 0.6 s), and a little.</summary>
    private const double FloodGap = 1.8;

    /// <summary>Within half a game minute (the clock is stopped).</summary>
    private static bool Near(double hour, double want) => System.Math.Abs(TimeCommand.ShortWay(hour, want)) < 0.01;
}
