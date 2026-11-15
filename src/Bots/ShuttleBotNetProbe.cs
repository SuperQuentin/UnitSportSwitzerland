using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Vehicles;

namespace UnitSport.Bots;

/// <summary>
/// <c>--shuttlebotnetcheck</c> with <c>--connect</c> (driven by <c>tools/shuttlebotnetcheck.sh</c>, #1498): a bot on a
/// dedicated server flies the shuttle, seen from a client. On the fixture airport (<c>--chunks fixture:airport</c>) the
/// client, an admin, asks <c>/bot shuttle 200</c>. It sees the bot's body walk to the parked shuttle at C3 and take it
/// (its copy's ride the shuttle), then the shuttle leave the ground and climb away: seen above 60 km, or out of this
/// client's sight (its interest range) above 5 km. The script then reads the server's log for the stage it reached (in
/// Space, the cruise drive on). That is as far as today's main lets a server body go: above 60 km it stays in the main
/// region's flat LV95 frame, wrong far from Switzerland (round the Earth the body came out under that frame's ground on
/// the far side, and the void rescue put the bot back at the spawn on foot): the whole mission online waits for G2 (#1443),
/// the trip between bodies for C3b's regions. <c>--checkspeed n</c> runs it n times faster.
/// </summary>
public partial class ShuttleBotNetProbe : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--shuttlebotnetcheck");
    private const string Password = "shuttlebot";

    public ShuttleBotNetProbe(ItemController items) : base(items, "shuttlebotnet") { }
    public ShuttleBotNetProbe() : this(null!) { }

    public override async void _Ready()
    {
        if (!await Joined(150)) return;
        await Seconds(2.0);
        try { await Run(); }
        catch (Exception e) { Fail($"threw: {e.Message}"); }
        await Finish(2.0);
    }

    private Node? Players => GetParent().GetNodeOrNull("Players");

    private FootPlayer? Bot() => Players?.GetChildren().OfType<FootPlayer>().FirstOrDefault(p => p.Name.ToString().StartsWith("npc_"));

    private async Task Run()
    {
        if (!await LogIn(Password)) return;
        if (!await Until(() => VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().Any(v => v.Kind == RideKind.Shuttle) == true, 120))
        {
            Fail("no parked shuttle streamed here");
            return;
        }
        Chat!.Send("/bot shuttle 200");
        FootPlayer? bot = null;
        Expect(await Until(() => (bot = Bot()) != null, 30), "the server's bot is here");
        if (bot == null) return;
        Expect(await Until(() => IsInstanceValid(bot) && bot.Ride == RideKind.Shuttle, 240), $"the bot took the shuttle (its copy rides {bot.Ride})");
        if (!await SpeedUp(Password)) return;
        double Alt() => IsInstanceValid(bot) && Me?.Origin is { } o ? o.AltitudeOf(bot.GlobalPosition) - o.AltitudeOf(Me.GlobalPosition) : double.NaN;
        Expect(await Until(() => Alt() > 200, 900), System.FormattableString.Invariant($"it took off ({Alt():F0} m over the airfield)"));
        // seen climbing until it leaves this client's interest range: its copy goes with it
        double last = 0;
        bool away = await Until(() =>
        {
            if (!IsInstanceValid(bot) || bot.IsQueuedForDeletion()) return last > 5_000;
            last = Math.Max(last, Alt());
            return last > 60_000;
        }, 3_000);
        Expect(away, System.FormattableString.Invariant($"it climbed away: seen up to {last / 1000:F1} km{(IsInstanceValid(bot) ? "" : ", then out of sight")}"));
        // the server's own stages on (the script reads its log): in Space and the drive on, a few minutes of its climb later
        await Seconds(360);
        await SpeedBack();
    }
}
