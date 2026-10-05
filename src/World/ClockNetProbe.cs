using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.World;

/// <summary>
/// <c>--clocknet A|B</c> with <c>--connect</c> (driven by <c>tools/clocknetcheck.sh</c>, #452): every
/// screen shows the server's time of day.
/// <list type="bullet">
/// <item>Each side waits for the world clock and its sky to settle on it (B joins later with its own
/// clock stopped at another hour, <c>--time 3</c>).</item>
/// <item>Each sends the hour its sky shows at an instant of the server clock; the other computes its
/// own hour for that same instant: they must agree.</item>
/// <item>A logs in as admin, <c>/time set 22</c>, then <c>/time speed 2</c>: both skies follow, and
/// still agree afterwards.</item>
/// </list>
/// </summary>
public partial class ClockNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--clocknet");

    public ClockNetProbe(ItemController items) : base(items, "clocknet", "CK") { }
    public ClockNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    /// <summary>Agreement between two peers, in game hours: 0.02 h is about a game minute.</summary>
    private const double Tolerance = 0.02;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (!await Settled(20)) { await Finish(1); return; }
        if (_role == "A") await RunA(); else await RunB();
        await Finish(2.0);
    }

    /// <summary>The world clock is in hand and this screen's sky has eased onto it.</summary>
    private async Task<bool> Settled(double seconds)
    {
        bool ok = await Until(() => WorldClock.Active && Net.ClockSync.Synced && DayNight.Instance is { } d
            && System.Math.Abs(TimeCommand.ShortWay(d.Hour, WorldClock.Hour)) < 0.005, seconds);
        Expect(ok, $"the sky follows the world clock ({(DayNight.Instance?.Clock ?? "no sky")}, world {TimeCommand.Format(WorldClock.Hour)})");
        return ok;
    }

    private async Task RunA()
    {
        if (!await Handshake("hello", "ready")) return;
        await Compare("1");

        Chat?.Send("/login test");
        if (!await Until(() => Permissions.IsAdmin, 10)) { Fail("no admin rights"); return; }
        Chat?.Send("/time set 22");
        Expect(await Until(() => Near(22.0, 0.1), 10), $"A's sky went to 22:00 ({DayNight.Instance?.Clock})");
        Say("set");
        Expect(await Heard("B", "saw set", 15), "B's sky went to 22:00");

        Chat?.Send("/time speed 2");
        Expect(await Until(() => System.Math.Abs(WorldClock.MinutesPerDay - 2f) < 1e-3, 10), "A's day is 2 min long");
        Say("speed");
        Expect(await Heard("B", "saw speed", 15), "B's day is 2 min long");
        await Seconds(3);
        await Compare("2");
    }

    private async Task RunB()
    {
        if (!await Handshake("ready", "hello")) return;
        await Compare("1");

        if (!await Heard("A", "set", 30)) { Fail("A never set the time"); return; }
        if (await Until(() => Near(22.0, 0.1), 10)) Say("saw set");
        else Fail($"B's sky is at {DayNight.Instance?.Clock}, not 22:00");

        if (!await Heard("A", "speed", 30)) { Fail("A never set the speed"); return; }
        if (await Until(() => System.Math.Abs(WorldClock.MinutesPerDay - 2f) < 1e-3, 10)) Say("saw speed");
        else Fail($"B's day is {WorldClock.MinutesPerDay} min");
        await Compare("2");
    }

    private static bool Near(double hour, double within) =>
        DayNight.Instance is { } d && System.Math.Abs(TimeCommand.ShortWay(d.Hour, hour)) < within;

    /// <summary>A says <paramref name="mine"/> until it hears <paramref name="theirs"/>; B answers once.</summary>
    private async Task<bool> Handshake(string mine, string theirs)
    {
        string other = _role == "A" ? "B" : "A";
        if (_role == "B")
        {
            if (!await Heard(other, theirs, 90)) { Fail($"{other} never said {theirs}"); return false; }
            Say(mine);
            return true;
        }
        for (int i = 0; i < 40; i++)
        {
            Say(mine);
            if (await Heard(other, theirs, 3)) return true;
        }
        Fail($"{other} never said {theirs}");
        return false;
    }

    /// <summary>
    /// Sends this sky's hour at an instant of the server clock, and checks the other side's: the
    /// hour this peer's world clock gives for that same instant.
    /// </summary>
    private async Task Compare(string round)
    {
        string other = _role == "A" ? "B" : "A";
        double at = Net.ClockSync.ServerNow;
        double hour = DayNight.Instance?.Hour ?? WorldClock.Hour;
        Say(string.Format(CultureInfo.InvariantCulture, "at{0} {1:F4} {2:F5}", round, at, hour));

        string key = $"CK {other} at{round} ";
        if (!await Until(() => _heard.Exists(l => l.Contains(key)), 30)) { Fail($"{other} never sent round {round}"); return; }
        string line = _heard.Find(l => l.Contains(key))!;
        string[] parts = line[(line.IndexOf(key) + key.Length)..].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        double theirAt = double.Parse(parts[0], CultureInfo.InvariantCulture);
        double theirHour = double.Parse(parts[1], CultureInfo.InvariantCulture);
        double mineThen = WorldClock.HourAt(theirAt);
        double gap = System.Math.Abs(TimeCommand.ShortWay(theirHour, mineThen));
        Expect(gap < Tolerance, string.Format(CultureInfo.InvariantCulture,
            "round {0}: {1} showed {2} at server {3:F2} s, here the clock gives {4} (gap {5:F4} h)",
            round, other, TimeCommand.Format(theirHour), theirAt, TimeCommand.Format(mineThen), gap));
    }
}
