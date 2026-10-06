using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Items;

namespace UnitSport.Core;

/// <summary>
/// <c>--speednet A|B</c> with <c>--connect</c> (driven by <c>tools/speednetcheck.sh</c>, #579): the
/// server owns the simulation's pace and every peer runs at it.
/// <list type="bullet">
/// <item>Both sides wait for the clock sync and the server's speed to arrive.</item>
/// <item>A logs in as admin and sends <c>/speed 0.25</c>: both peers must reach
/// <see cref="Engine.TimeScale"/> 0.25 and <see cref="SimClock.Scale"/> 0.25.</item>
/// <item>Each sends the simulated time its own clock gives for an instant of the server clock; the
/// other computes its own for that same instant. They must agree — that is the whole point of
/// scheduling the change at a server instant instead of applying it on receipt.</item>
/// <item>A sends <c>/speed normal</c> and both must come back to 1x.</item>
/// </list>
/// </summary>
public partial class SpeedNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--speednet");

    public SpeedNetProbe(ItemController items) : base(items, "speednet", "SP") { }
    public SpeedNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    /// <summary>Agreement between two peers, in simulated seconds. A frame at 60 Hz is 0.017 s.</summary>
    private const double Tolerance = 0.05;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (!await Synced(20)) { await Finish(1); return; }
        if (_role == "A") await RunA(); else await RunB();
        await Finish(2.0);
    }

    /// <summary>The server's clock is in hand, so <see cref="SimClock.SimAt"/> means something here.</summary>
    private async Task<bool> Synced(double seconds)
    {
        bool ok = await Until(() => Net.ClockSync.Synced, seconds);
        Expect(ok, $"the server's clock is in hand (rtt {Net.ClockSync.Rtt * 1000:F0} ms)");
        return ok;
    }

    private async Task RunA()
    {
        if (!await Handshake("hello", "ready")) return;
        await Compare("1");

        Chat?.Send("/login test");
        if (!await Until(() => Permissions.IsAdmin, 10)) { Fail("no admin rights"); return; }

        Chat?.Send("/speed 0.25");
        Expect(await Until(() => AtScale(0.25), 10), $"A runs at x0.25 ({Describe()})");
        Say("slow");
        Expect(await Heard("B", "saw slow", 15), "B runs at x0.25");
        await EnvSlowedDown(0.25);
        await Compare("2");

        Chat?.Send("/speed normal");
        Expect(await Until(() => AtScale(1.0), 10), $"A is back to normal speed ({Describe()})");
        await ReplicationKeptFlowing();
        Say("normal");
        Expect(await Heard("B", "saw normal", 15), "B is back to normal speed");
    }

    private async Task RunB()
    {
        if (!await Handshake("ready", "hello")) return;
        await Compare("1");

        if (!await Heard("A", "slow", 30)) { Fail("A never slowed the simulation"); return; }
        if (await Until(() => AtScale(0.25), 10)) Say("saw slow");
        else Fail($"B is at {Describe()}, not x0.25");
        await Compare("2");

        if (!await Heard("A", "normal", 30)) { Fail("A never restored the speed"); return; }
        if (await Until(() => AtScale(1.0), 10)) Say("saw normal");
        else Fail($"B is at {Describe()}, not normal speed");
    }

    /// <summary>
    /// Environment time rides the simulation speed (#579 phase 3): over a stretch of the server's
    /// real clock the hour must advance at <c>DayFactor * scale</c>, not at <c>DayFactor</c>. This
    /// is the claim the whole phase exists for, and nothing else would notice if it broke.
    /// </summary>
    private async Task EnvSlowedDown(double scale)
    {
        double server0 = Net.ClockSync.ServerNow;
        double env0 = World.WorldClock.EnvNow;
        await Seconds(4);
        double elapsed = Net.ClockSync.ServerNow - server0;
        double advanced = World.WorldClock.EnvNow - env0;
        double expected = World.WorldClock.DayFactor * scale * elapsed;
        // 15%: the scale change lands mid-window and ClockSync's estimate of the server moves a little
        bool ok = expected > 0 && System.Math.Abs(advanced - expected) < expected * 0.15;
        Expect(ok, string.Format(CultureInfo.InvariantCulture,
            "env time advanced {0:F1} s over {1:F1} s of server clock at x{2}; at this day length x{2} wants {3:F1} s (full speed would be {4:F1})",
            advanced, elapsed, scale, expected, World.WorldClock.DayFactor * elapsed));
    }

    /// <summary>
    /// The scale change is scheduled, and every peer applies it at one instant on the server's
    /// clock — but <see cref="Engine.TimeScale"/> also scales the <c>_Process</c> that drives
    /// replication. So the thing to prove is that net states kept arriving across the flips: a peer
    /// whose sending stalled would have its body frozen as a wall by
    /// <c>FootPlayer.SilentSeconds</c>, which is exactly the #50 failure the silence check exists
    /// for.
    ///
    /// <para>
    /// This does <b>not</b> prove a <i>moving</i> body does not visibly jump across the flip. That
    /// needs a driven remote body and belongs in <c>Net.NetSmoothProbe</c> / <c>--synccheck</c>,
    /// which already measures replicated motion smoothness; asserting it on the stationary players
    /// here would pass whatever happened.
    /// </para>
    /// </summary>
    private async Task ReplicationKeptFlowing()
    {
        var other = Remote();
        if (other == null) { Say("no remote body to watch"); return; }
        double before = other.LastNetState;
        await Seconds(2);
        double after = other.LastNetState;
        Expect(after > before, string.Format(CultureInfo.InvariantCulture,
            "the other peer's net states kept arriving across the speed changes (last at {0:F2} s, then {1:F2} s)",
            before, after));
    }

    /// <summary>The other peer's body on this screen, whichever peer id it has.</summary>
    private Player.FootPlayer? Remote()
    {
        if (GetParent()?.GetNodeOrNull("Players") is not { } players) return null;
        foreach (var child in players.GetChildren())
            if (child is Player.FootPlayer p && p != Me) return p;
        return null;
    }

    /// <summary>The clock and the engine both have to be there: one without the other is the bug.</summary>
    private static bool AtScale(double scale) =>
        System.Math.Abs(SimClock.Scale - scale) < 1e-6 && System.Math.Abs(Engine.TimeScale - scale) < 1e-6;

    private static string Describe() => string.Format(CultureInfo.InvariantCulture,
        "clock {0:F4}, engine {1:F4}", SimClock.Scale, Engine.TimeScale);

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
    /// Sends the simulated time this peer's clock gives for an instant of the server clock, and
    /// checks the other side's against what this peer's clock gives for *their* instant. Two peers
    /// that agree on the server's clock must agree on simulated time, before and after a change.
    /// </summary>
    private async Task Compare(string round)
    {
        string other = _role == "A" ? "B" : "A";
        double at = Net.ClockSync.ServerNow;
        Say(string.Format(CultureInfo.InvariantCulture, "at{0} {1:F4} {2:F5} {3:F5}",
            round, at, SimClock.SimAt(at), World.WorldClock.EnvAt(SimClock.SimAt(at))));

        string key = $"SP {other} at{round} ";
        if (!await Until(() => _heard.Exists(l => l.Contains(key)), 30)) { Fail($"{other} never sent round {round}"); return; }
        string line = _heard.Find(l => l.Contains(key))!;
        string[] parts = line[(line.IndexOf(key) + key.Length)..].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        double theirAt = double.Parse(parts[0], CultureInfo.InvariantCulture);
        double theirSim = double.Parse(parts[1], CultureInfo.InvariantCulture);
        double theirEnv = double.Parse(parts[2], CultureInfo.InvariantCulture);
        double mineThen = SimClock.SimAt(theirAt);
        double gap = System.Math.Abs(theirSim - mineThen);
        Expect(gap < Tolerance, string.Format(CultureInfo.InvariantCulture,
            "round {0}: {1} was at sim {2:F4} s at server {3:F2} s, here the clock gives {4:F4} (gap {5:F4} s)",
            round, other, theirSim, theirAt, mineThen, gap));

        // environment time is derived from the same sim instant, so two peers that agree about
        // simulated time must agree about the hour as well (#579 phase 3)
        double myEnv = World.WorldClock.EnvAt(mineThen);
        double envGap = System.Math.Abs(theirEnv - myEnv);
        Expect(envGap < Tolerance * World.WorldClock.DayFactor + Tolerance, string.Format(CultureInfo.InvariantCulture,
            "round {0}: {1} was at env {2:F2} s ({3}), here the clock gives {4:F2} s ({5}) (gap {6:F3} s)",
            round, other, theirEnv, World.TimeCommand.Format(World.TimeCommand.HourOf(theirEnv, World.WorldClock.HourShift)),
            myEnv, World.TimeCommand.Format(World.TimeCommand.HourOf(myEnv, World.WorldClock.HourShift)), envGap));
    }
}
