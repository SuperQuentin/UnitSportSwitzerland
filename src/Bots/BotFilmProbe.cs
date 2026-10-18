using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.BattleRoyale;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Bots;

/// <summary>
/// <c>--botfilm</c> with <c>--connect</c> (driven by <c>tools/botfilm.sh</c>, #842): films bots playing a
/// Battle Royale match. The player opens a match topped up with bots that land close together
/// (<c>--brbotspread</c> on the server), leaves it at once and spectates: the camera follows the bot it
/// picks. Two clips (<c>Core/ClipRecorder</c>): the drop (a bot out of the plane, the wingsuit, the canopy,
/// the landing) and the fight (always the bot nearest another living one).
/// </summary>
public partial class BotFilmProbe : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--botfilm");

    public BotFilmProbe(ItemController items) : base(items, "botfilm", "BF", "botfilm_") { }
    public BotFilmProbe() : this(null!) { }

    protected override FootPlayer? Me => GetParent().GetNodeOrNull<FootPlayer>("Players/" + Multiplayer.GetUniqueId());
    private static BrManager? Br => BrManager.Instance;

    public override async void _Ready()
    {
        if (!await Joined(150, () => Br != null)) return;
        await Seconds(2.0);
        await Film();
        await Finish(3.0);
    }

    private FootPlayer? BodyOf(long id) => GetParent().GetNodeOrNull<FootPlayer>("Players/" + Net.PlayerReplication.NodeName(id));

    private async Task Film()
    {
        var br = Br!;
        Chat!.Send("/login botfilm");
        if (!await Until(() => Permissions.IsAdmin, 10)) { Fail("not admin"); return; }
        Chat.Send("/br bots 9 normal");
        await Seconds(0.5);
        Chat.Send("/br open 5 short");
        if (!await Until(() => br.State.Phase == BrPhase.Lobby, 20)) { Fail("no lobby"); return; }
        Chat.Send("/br join");
        await Until(() => br.State.Phase == BrPhase.Countdown, 10);
        Chat.Send("/br start");
        if (!await Until(() => br.InMatch && br.State.Phase == BrPhase.Playing, 60)) { Fail("never dropped"); return; }
        await Seconds(1.0);
        // out at once: the camera spectates the bots from here on
        Chat.Send("/br leave");
        if (!await Until(() => br.Watching != 0, 20)) { Fail("not spectating"); return; }
        if (br.State.Flight is not { } flight) { Fail("no flight"); return; }

        // the drop: follow the bot that jumps first, from the doors opening
        await Until(() => Net.ClockSync.ServerNow >= flight.OpensAt - 1, 60);
        Chat.Send("/clip start bots-drop 45 0");
        long first = 0;
        await Until(() => (first = br.State.Entrants.FirstOrDefault(e => e.Bot && e.Jumped)?.Peer ?? 0) != 0, 30);
        if (first != 0) br.Watch(first);
        GD.Print($"{Log} filming the drop of {br.State.Find(first)?.Name}");
        await Seconds(46);

        // the fight: follow whichever bot stands nearest another on the ground; once two are within a knife's
        // rush (or a kill is in), film 60 s of it
        int outAt = Out();
        for (double end = GameClock.Now + 240; GameClock.Now < end; )
        {
            long near = Hottest(out float d);
            if (near != 0 && near != br.Watching) br.Watch(near);
            if (near != 0 && d < 18f || Out() > outAt) break;
            await Seconds(1.0);
        }
        Chat.Send("/clip start bots-fight 60 0");
        for (double end = GameClock.Now + 61; GameClock.Now < end;)
        {
            long hot = Hottest(out float near);
            if (hot != 0 && hot != br.Watching && near < 30f) br.Watch(hot);
            await Seconds(3.0);
        }
        GD.Print($"{Log} filmed; {br.State.Entrants.Count(e => e.Bot && !e.Alive)} bots out so far");
        await Seconds(2.0);
    }

    private int Out() => Br!.State.Entrants.Count(e => e.Bot && !e.Alive);

    /// <summary>The living bot on its feet nearest another living one on its feet (where a fight is), and that distance.</summary>
    private long Hottest(out float distance)
    {
        distance = float.PositiveInfinity;
        long best = 0;
        var alive = Br!.State.Entrants.Where(e => e.Alive && e.Jumped).Select(e => (e, BodyOf(e.Peer)))
            .Where(x => x.Item2 is { Ride: RideKind.OnFoot }).ToList();
        foreach (var (a, ba) in alive)
        {
            if (!a.Bot) continue;
            foreach (var (b, bb) in alive)
            {
                if (a == b) continue;
                float d = ba!.GlobalPosition.DistanceTo(bb!.GlobalPosition);
                if (d < distance) { distance = d; best = a.Peer; }
            }
        }
        return best;
    }
}
