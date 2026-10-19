using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.BattleRoyale;

/// <summary>
/// <c>--brbotcheck</c> with <c>--connect</c> (driven by <c>tools/brbotcheck.sh</c>, #837): one player against
/// bots in a whole match, on a server started with <c>--admin-password brbots --brpace 0.13</c>. The player
/// opens a match topped up to 3 (<c>/br bots 3</c>), alone in the lobby: the countdown starts anyway, and
/// GO adds two bots (entrants with negative ids, marked "[bot]", on the ground in the first circle,
/// their bodies here). The player jumps, then goes to each bot and knifes it: the server eliminates it,
/// its death box appears, the feed marks it, and the kills read "1 kill (1 bot)". With the second bot
/// out (by its hand, or the zone's) the player wins with "n kills (n bots)", in the record and on the board; after the results the
/// bots are gone. Scratch inventory.
/// </summary>
public partial class BrBotProbe : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--brbotcheck") || CmdArgs.Has("--brbotwatch") || CmdArgs.Has("--brsquadcheck");

    public BrBotProbe(ItemController items) : base(items, "brbots", "BB", "brbots_") { }
    public BrBotProbe() : this(null!) { }

    protected override FootPlayer? Me => GetParent().GetNodeOrNull<FootPlayer>("Players/" + Multiplayer.GetUniqueId());
    private BrManager? Br => BrManager.Instance;

    protected override void Fail(string why)
    {
        _failures++;
        base.Fail(why);
    }

    public override async void _Ready()
    {
        if (!await Joined(150, () => Br != null)) return;
        await Seconds(2.0);
        if (Watch) await RunWatch(); else if (SquadCheck) await RunSquad(); else await Run();
        await Finish(1.0);
    }

    /// <summary><c>--brbotwatch [field]</c>: a match topped up to the field (9: 8 bots) that the bots play, watched to the end.</summary>
    private static bool Watch => CmdArgs.Has("--brbotwatch");

    private async Task RunWatch()
    {
        var br = Br!;
        Chat!.Send("/login brbots");
        if (!await Until(() => Permissions.IsAdmin, 10)) { Fail("not logged in as admin"); return; }
        // --brwatchspeed n: the whole simulation n times faster (/speed; the match follows it since #856), so a
        // full-pace match, in proportion (no zone outrunning the runners), takes a fraction of the wall clock
        if (CmdArgs.Double("--brwatchspeed") is double speed && speed > 0)
            Chat.Send(FormattableString.Invariant($"/speed {speed}"));
        Chat.Send($"/br bots {CmdArgs.Int("--brbotwatch") ?? 9} normal");
        await Seconds(0.5);
        // --brwatchmode duos|trios|squads (#837 phase 4): bots in teams
        Chat.Send($"/br open 5 short {CmdArgs.Value("--brwatchmode") ?? "solo"}");
        if (!await Until(() => br.State.Phase == BrPhase.Lobby, 20)) { Fail("no lobby"); return; }
        Chat.Send("/br join");
        await Until(() => br.State.Phase == BrPhase.Countdown, 10);
        Chat.Send("/br start");
        if (!await Until(() => br.InMatch && br.State.Phase == BrPhase.Playing, 60)) { Fail("never dropped"); return; }
        if (br.State.Flight is { } flight)
        {
            await Until(() => flight.DoorsOpen(ClockSync.SimNow), 90);
            br.JumpOut();
            await Seconds(0.5);
            Me!.Leap(Me.GlobalPosition, Vector3.Zero, RideKind.OnFoot);
        }
        // every 20 s, how many orphan nodes the client holds (a leak grew this client to 128 GB)
        bool ended = false, named = false;
        for (int i = 0; i < 110 && !ended; i++)
        {
            ended = await Until(() => br.State.Phase == BrPhase.Ended, 20);
            GD.Print($"{Log} {OrphanCensus.Line()}");
            // named once, early, when they pile up: the end may never come (a leak took this client to 128 GB)
            if (!named && Node.GetOrphanNodeIds().Count >= 3000) { named = true; Node.PrintOrphanNodes(); }
        }
        // a leak shows as thousands (the replay buffer's light copies were 75,000 in a 100-player match); a few
        // hundred is normal. Named only when it fails: the list is a line a node
        int orphans = Node.GetOrphanNodeIds().Count;
        if (orphans >= 5000 && !named) Node.PrintOrphanNodes();
        var s = br.State;
        var bots = s.Entrants.Where(e => e.Bot).ToList();
        GD.Print($"{Log} watch: {(ended ? "ended" : "still running")}, winner {s.Find(s.Winner)?.Shown ?? "-"}; "
            + string.Join(", ", bots.OrderBy(b => b.Place).Select(b => $"#{b.Place} {b.Name} {b.Kills}k {b.Damage:F0}dmg")));
        Expect(ended, "the match ended");
        Expect(bots.Sum(b => b.Kills) > 0, $"bots killed ({bots.Sum(b => b.Kills)} kills by bots)");
        Expect(bots.Any(b => b.Damage > 0), "bots dealt damage");
        Expect(orphans < 5000, $"no node leak on this client ({orphans} orphan nodes)");
    }

    private FootPlayer? BodyOf(long id) => GetParent().GetNodeOrNull<FootPlayer>("Players/" + PlayerReplication.NodeName(id));

    /// <summary>
    /// <c>--brsquadcheck</c> (#837 phase 4): a duos match of A and one bot, alone on its side (no enemy to chase).
    /// The mate jumps after A and lands near, regroups when A moves off, takes A's dogtag to a Postauto stop when
    /// A is out (in the looting phase: the stops are spread over the first circle, and once circles close the bot
    /// rightly finds them too far through the zone): A is recalled; then it regroups when A moves off, goes to A's
    /// ping and revives A when A is down.
    /// </summary>
    private static bool SquadCheck => CmdArgs.Has("--brsquadcheck");

    private async Task RunSquad()
    {
        var br = Br!;
        Chat!.Send("/login brbots");
        if (!await Until(() => Permissions.IsAdmin, 10)) { Fail("not logged in as admin"); return; }
        Chat.Send("/br bots 2 normal");
        await Seconds(0.5);
        // here: A starts in a town with roads enough for the Postauto stops (tools/brsquadcheck.sh); a random
        // region may have none
        // long: the looting phase lasts, when no circle is shown yet and any stop is worth the walk
        Chat.Send("/br open here 5 long duos");
        if (!await Until(() => br.State.Phase == BrPhase.Lobby, 20)) { Fail("no lobby"); return; }
        Chat.Send("/br join");
        await Until(() => br.State.Phase == BrPhase.Countdown, 10);
        Chat.Send("/br start");
        if (!await Until(() => br.InMatch && br.State.Phase == BrPhase.Playing, 60)) { Fail("never dropped"); return; }
        var mine = br.MyEntry;
        var mate = br.State.Entrants.FirstOrDefault(e => e.Bot && mine != null && e.Team == mine.Team);
        Expect(mine is { Team: not 0 } && mate != null, $"A and a bot in one duo (team {mine?.Team}, mate {mate?.Name})");
        if (mate == null || br.State.Flight is not { } flight) return;
        long id = mate.Peer;
        var me = Me!;
        float ToMe(FootPlayer b) => new Vector2(b.GlobalPosition.X - me.GlobalPosition.X, b.GlobalPosition.Z - me.GlobalPosition.Z).Length();

        // the jump: the mate goes after A, then lands near A
        await Until(() => flight.DoorsOpen(ClockSync.SimNow), 90);
        Expect(br.JumpOut(), "A jumped");
        Expect(await Until(() => br.State.Find(id) is { Jumped: true }, 8), "the mate jumped after A");
        await Seconds(0.5);
        me.Leap(me.GlobalPosition, Vector3.Zero, RideKind.OnFoot);
        var c0 = br.State.Zone().CentreOf(0);
        br.Teleport(br.State.AreaE + c0.X, br.State.AreaN + c0.Y, "the circle's centre");
        await Until(() => me.IsOnFloor(), 30);
        bool near = await Until(() => BodyOf(id) is { Ride: RideKind.OnFoot, Visible: true } b && ToMe(b) < 150f, 240);
        Expect(near, $"the mate flew after A and landed near ({(BodyOf(id) is { } b0 ? ToMe(b0) : -1):F0} m)");
        if (!near) return;
        await Seconds(5);   // its ride as this client sees it can say on foot a moment before the server's body lands

        // out: the mate takes A's dogtag to a Postauto stop and A is back
        if (br.State.RecallPoints is not { Length: > 0 }) { Fail("no Postauto stops in this region: the recall is not tested"); return; }
        me.TakeDamage(me.Health + 10f, 0, DamageCause.Other);
        Expect(await Until(() => me.Downed, 5), "A is down, not out (a mate stands)");
        for (int i = 0; i < 20 && br.MyEntry is { Alive: true }; i++) { me.TakeDamage(500f, 0, DamageCause.Other); await Seconds(0.5); }
        Expect(await Until(() => br.MyEntry is { Alive: false }, 30), "A is out");
        Expect(await Until(() => br.MyEntry is { Alive: true, Recalled: true }, 400), "the mate took A's dogtag to a stop: A is recalled");
        // back over the stop in the wingsuit: down on foot, near the mate again for what follows
        me.Leap(me.GlobalPosition, Vector3.Zero, RideKind.OnFoot);
        if (BodyOf(id) is { } mateBody && me.Origin is { } o)
        {
            var (me2, mn2) = o.ToLv95(mateBody.GlobalPosition);
            br.Teleport(me2 + 10, mn2, "beside the mate");
        }
        await Until(() => me.IsOnFloor(), 20);

        // A moves off: the mate comes back to within 60 m
        var (ae, an) = me.Origin!.ToLv95(me.GlobalPosition);
        br.Teleport(ae + 110, an, "110 m east");
        await Until(() => me.IsOnFloor(), 20);
        Expect(await Until(() => BodyOf(id) is { } b && ToMe(b) < 60f, 90), $"the mate regrouped ({(BodyOf(id) is { } b1 ? ToMe(b1) : -1):F0} m)");

        // a ping 70 m off: the mate goes there
        var (pe, pn) = me.Origin!.ToLv95(me.GlobalPosition);
        var ping = new Vector2((float)(pe - br.State.AreaE), (float)(pn - br.State.AreaN + 70));
        Expect(br.PingMap(ping), "A pinged a spot 70 m north");
        var pingWorld = me.Origin.ToWorld(br.State.AreaE + ping.X, br.State.AreaN + ping.Y, me.GlobalPosition.Y);
        float ToPing(FootPlayer b) => new Vector2(b.GlobalPosition.X - pingWorld.X, b.GlobalPosition.Z - pingWorld.Z).Length();
        Expect(await Until(() => BodyOf(id) is { } b && ToPing(b) < 15f, 90), $"the mate went to the ping ({(BodyOf(id) is { } b2 ? ToPing(b2) : -1):F0} m off it)");

        // down, not out (a mate stands): the mate revives A
        me.TakeDamage(me.Health + 10f, 0, DamageCause.Other);
        Expect(await Until(() => me.Downed, 5), "A is down, not out");
        Expect(await Until(() => !me.Downed && br.MyEntry is { Alive: true, Downed: false }, 90), "the mate revived A");
    }

    private async Task Run()
    {
        var br = Br!;
        Chat!.Send("/login brbots");
        if (!await Until(() => Permissions.IsAdmin, 10)) { Fail("not logged in as admin"); return; }
        Chat.Send("/br bots 3 easy");   // easy: slower to react, so the knife duels go A's way
        await Seconds(0.5);
        Chat.Send("/br open 5 short");
        if (!await Until(() => br.State.Phase == BrPhase.Lobby, 20)) { Fail("no lobby"); return; }
        Expect(br.State.Bots == 3, $"the lobby says bots fill the field to 3 ({br.State.Bots})");
        Chat.Send("/br join");
        Expect(await Until(() => br.State.Phase == BrPhase.Countdown, 10), "alone in the lobby, the countdown starts: bots will fill the field");
        Chat.Send("/br start");
        if (!await Until(() => br.InMatch && br.State.Phase == BrPhase.Playing, 60)) { Fail("never dropped"); return; }

        var s = br.State;
        var bots = s.Entrants.Where(e => e.Bot).ToList();
        Expect(bots.Count == 2 && s.Field == 3, $"GO topped the field up to 3: {bots.Count} bots, field {s.Field}");
        Expect(bots.All(b => b.Peer < 0 && !b.Jumped && b.Shown.EndsWith("[bot]")), "bots: negative ids, aboard the plane, marked [bot]");
        if (bots.Count < 2) return;

        // out of the plane as soon as the doors open, then down on foot
        if (s.Flight is not { } flight) { Fail("no flight"); return; }
        await Until(() => flight.DoorsOpen(ClockSync.SimNow), 90);
        Expect(br.JumpOut(), "jumped");
        var me = Me!;
        await Seconds(0.5);
        me.Leap(me.GlobalPosition, Vector3.Zero, RideKind.OnFoot);   // out of the wingsuit first: a teleport in one is a SPLAT
        // down at the first circle's centre, where the check lands the bots (--brbotspread)
        var c0 = br.State.Zone().CentreOf(0);
        br.Teleport(br.State.AreaE + c0.X + 30, br.State.AreaN + c0.Y, "the circle's centre");
        await Until(() => me.IsOnFloor(), 30);

        // the bots jump on their own, glide and open their canopies: wait until both stand on the ground
        bool landed = await Until(() => bots.All(b => br.State.Find(b.Peer) is { Jumped: true }
            && BodyOf(b.Peer) is { } body && body.Ride == RideKind.OnFoot && body.Visible), 240);
        Expect(landed, "both bots jumped, flew and landed on foot");
        int knife = SlotOf(ItemId.Knife);
        _items.Inventory.Select(knife);
        for (int i = 0; i < bots.Count; i++)
        {
            var bot = bots[i];
            int before = br.MyEntry?.Kills ?? 0;
            if (!await Knife(me, bot.Peer)) return;
            var mine = br.MyEntry;
            Expect(br.State.Find(bot.Peer)?.Alive == false, $"{bot.Name} is out");
            // the second one may go out another way first (the zone, a fall): a match as it happens
            bool mineKill = (mine?.Kills ?? 0) > before;
            if (i > 0 && !mineKill) { GD.Print($"{Log} {bot.Name} went out before I got to it: fine"); continue; }
            Expect(mineKill && mine!.Kills == mine.BotKills, $"my kills: {BrHud.Kills(mine?.Kills ?? 0, mine?.BotKills ?? 0)}");
            Expect(br.Feed.Any(f => f.Text.Contains($"{bot.Name} [bot]")), $"the feed marks the bot ({br.Feed.LastOrDefault().Text})");
            if (i == 0)
            {
                Expect(BrHud.Kills(mine?.Kills ?? 0, mine?.BotKills ?? 0) == "1 kill (1 bot)", "the HUD reads \"1 kill (1 bot)\"");
                Expect(await Until(() => BrCrates.Instance?.All.Any(c => c.Style == CrateStyle.DeathBox && c.Label.StartsWith(bot.Name)) == true, 8),
                    $"{bot.Name}'s death box is where it fell");
                // patch up before the next one, with the kit's bandages, as a player would
                for (int b = 0; b < 3 && me.Health < 95f && SlotOf(ItemId.Bandage) is int slot and >= 0; b++)
                {
                    if (!Inventory.IsHotbar(slot)) { _items.Inventory.SwapWithHotbar(slot, 5); slot = 5; }
                    _items.UseSlot(me, slot);
                    await Seconds(ItemController.MedicalSeconds(ItemId.Bandage) + 0.6);
                }
                GD.Print($"{Log} health before the next bot: {me.Health:F0}");
                _items.Inventory.Select(knife);
            }
        }

        if (!await Until(() => br.State.Phase == BrPhase.Ended, 20)) { Expect(false, "the match ended"); return; }
        var end = br.State;
        Expect(end.Winner == br.Me, $"I won ({end.Find(end.Winner)?.Shown})");
        int k = end.Find(br.Me)?.Kills ?? 0;
        string kills = BrStats.KillText(k, k);
        Expect(await Until(() => _heard.Any(l => l.Contains($"Your Battle Royale record: 1 match · 1 win · {kills}")), 8),
            $"my record: {kills}");
        Chat.Send("/br top");
        Expect(await Until(() => _heard.Any(l => l.Contains($"1. BRBA — 1 win, {kills}")), 8), $"/br top: {kills}");
        Expect(!_heard.Any(l => l.Contains(". " + bots[0].Name + " —")), "no bot on the board");
        if (DisplayServer.GetName() != "headless") Shot("results");

        if (!await Until(() => !br.InMatch, 40)) { Expect(false, "released after the results"); return; }
        Expect(await Until(() => bots.All(b => BodyOf(b.Peer) == null), 10), "the bots are gone with the match");
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>Next to the bot, stabbing, until the server has it out (a bot walks: stand by it again before each stab).</summary>
    private async Task<bool> Knife(FootPlayer me, long id)
    {
        var br = Br!;
        if (!await Until(() => BodyOf(id) != null, 20)) { Fail($"bot {id}'s body never showed here"); return false; }
        for (int i = 0; i < 20 && br.State.Find(id)?.Alive == true; i++)
        {
            if (BodyOf(id) is not { } body) break;
            // out of a knife's reach: beside it again (not on it: a remote body is solid, and its head is out of reach)
            if (Flat(body.GlobalPosition - me.GlobalPosition) > 1.9f || Mathf.Abs(body.GlobalPosition.Y - me.GlobalPosition.Y) > 0.8f)
            {
                var (e, n) = br.Origin!.ToLv95(body.GlobalPosition);
                br.Teleport(e + 1.6, n, "next to the bot");
                // a teleport lands a few frames later: wait until we really stand by it (it walks on meanwhile)
                await Until(() => BodyOf(id) is { } near && Flat(near.GlobalPosition - me.GlobalPosition) < 2.0f
                    && Mathf.Abs(near.GlobalPosition.Y - me.GlobalPosition.Y) < 0.8f && me.IsOnFloor(), 6);
            }
            if (BodyOf(id) is not { } b) break;
            var dir = (b.GlobalPosition + Vector3.Up * 1.1f - me.EyePosition).Normalized();
            me.LookYaw = Mathf.Atan2(-dir.X, -dir.Z);
            me.LookPitch = Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f));
            await Seconds(0.05);
            GD.Print(FormattableString.Invariant($"{Log} stab {i}: {Flat(b.GlobalPosition - me.GlobalPosition):F1} m, dy {b.GlobalPosition.Y - me.GlobalPosition.Y:F1}"));
            _items.UseSlot(me, SlotOf(ItemId.Knife));
            await Seconds(Weapons.Get(ItemId.Knife)!.Interval + 0.05);
        }
        bool out_ = await Until(() => br.State.Find(id)?.Alive == false, 5);
        if (!out_) Fail($"bot {id} never went out");
        return out_;
    }
}
