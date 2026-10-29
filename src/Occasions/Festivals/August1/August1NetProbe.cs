using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.August1;

/// <summary>
/// <c>--august1net a|b</c> with <c>--connect --festival test --august1fast</c> (driven by <c>tools/august1netcheck.sh</c>, #1355): the 1 August over a dedicated server
/// whose calendar reads 31 July 2027 (day 1 of 2, held at the fixture start) and whose sky reads 22:30 (<c>--august1hour</c>). Everything is asserted on the REMOTE
/// peers. Both clients see the server's lake show on the books and play it from the server's seed, and watching it pays them both. Lea (A) and Tom (B) form a party,
/// Tom is refused the stand from 150 m, both take a lampion and meet at the post. Lea starts the walk: two people and a village child. At the first stop Tom is
/// left at the post and the server makes the party wait (Tom is told); when he catches up they walk all ten stops together and both are paid for two walkers. Tom
/// buys a box of fireworks and lights it: Lea, who pressed nothing, plays his volley from the seed the server chose.
/// </summary>
public partial class August1NetProbe : ChatProbe
{
    public static string? Role => RoleArg("--august1net");

    public August1NetProbe(ItemController items) : base(items, "august1net", "AN", "august1net_") { }
    public August1NetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("august1-2027") ?? new FestivalInstance();
    private static InviteService Invites() => InviteService.Client!;

    private void Teleport(double e, double n)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(e, n, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null && August1Game.Instance != null && Fireworks.Instance != null)) return;
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached the server");
        const string Id = August1Rules.FestivalId;
        var game = August1Game.Instance!;
        var fw = Fireworks.Instance!;
        Svc.Hour = () => 22.5;   // this client's sky is not running: the server's is set by --august1hour
        Expect(await Until(() => Svc.RunningOf(Id) is { Day: 1, Site: not null }, 40), "the server runs the 1 August on day 1 and this client knows it");
        Expect(await Until(() => Me is { } me && me.IsOnFloor(), 30), "the player stands on the ground");
        var run = Svc.RunningOf(Id)!;
        var site = run.Site!;
        var stand = August1Rules.LampionStandAt(site);
        var boxStand = August1Rules.FireworkStandAt(site);
        var post = August1Rules.PostAt(site);
        var path = August1Rules.Path(site);
        string? refused = null;
        Svc.Refused += why => refused = why;
        var told = new List<(FestivalService.August1Event What, int A, int Ms, string Text)>();
        Svc.August1Told += (w, a, ms, t) => told.Add((w, a, ms, t));

        // ---- the lake show is on the server's books and plays here, and watching it pays --------------------------------------
        Expect(await Until(() => Svc.RunningOf(Id)!.ShowStart > 0, 60), "the server's 22:00 put a lake show on the books, and its start reached this client");
        run = Svc.RunningOf(Id)!;
        Expect(run.ShowSeed == FestivalShows.SeedOf(run.Instance, 1), $"its seed is the instance's first ({run.ShowSeed})");
        Expect(await Until(() => fw.ShowSeed == run.ShowSeed, 20), "the client plans the same show");
        Expect(await Until(() => fw.ShowTime >= 0 && Math.Abs(fw.ShowTime - (ClockSync.SimNow - run.ShowStart)) < 2, 30), "and plays it on the server's clock");
        Expect(await Until(() => Mine.Earned == August1Rules.ShowBatzen, 90), $"standing at the site, this client watched 20 s of it and is paid in its own section ({Mine.Earned})");
        Say("watched");

        if (_role == "A")
        {
            Expect(await Heard("B", "ready", 120), "B is on");
            Chat!.Send("/party invite Tom");
            Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "A's party holds A and B");
            Say("party");
            Expect(await Heard("B", "refused", 150), "B was refused from afar");
            Teleport(stand.E, stand.N + 2.0);
            Expect(await Until(() => game.NearTarget == FestivalService.August1LampionStand, 10), "A stands at the lampion stand");
            await Seconds(1.0);
            Expect(August1Game.TryInteract(Me!) && await Until(() => Count(ItemId.Lampion) == 1, 10), "A takes a lampion");
            Say("lamp");
            Expect(await Heard("B", "lamp", 60), "B has its lampion");
            Teleport(post.E, post.N + 1.5);
            Expect(await Until(() => game.NearTarget == FestivalService.August1Post, 10), "A stands at the post");
            await Seconds(1.0);
            Say("atpost");
            Expect(await Heard("B", "atpost", 60), "B stands at the post too");
            Expect(August1Game.TryInteract(Me!) && await Until(() => game.Walking, 10), "A starts the walk");
            Expect(told.Any(t => t.What == FestivalService.August1Event.WalkStart && t.A == 2 && t.Ms == 1) && game.Walkers == 3, $"two people and one village child walk ({game.Walkers})");
            // the first stop with B left behind: the server waits
            Teleport(path[0].E, path[0].N);
            Expect(await Until(() => told.Any(t => t.What == FestivalService.August1Event.Waiting), 15), "the server makes A wait for B");
            Expect(game.NextStop == 0, "no stop was passed without B");
            Say("waiting");
            Expect(await Until(() => game.NextStop == 1, 30), "with B beside A the first stop is passed");
            for (int k = 1; k < August1Rules.Waypoints; k++)
            {
                Teleport(path[k].E, path[k].N);
                Expect(await Until(() => game.NextStop == k + 1, 20), $"stop {k + 1} is passed with B ({game.NextStop})");
            }
            Teleport(path[August1Rules.Waypoints].E, path[August1Rules.Waypoints].N);
            Expect(await Until(() => told.Any(t => t.What == FestivalService.August1Event.WalkDone), 20), "the post finishes the walk");
            int pay = August1Rules.WalkPay(2);
            Expect(await Until(() => Mine.Earned == August1Rules.ShowBatzen + pay, 10), $"A is paid for two walkers ({Mine.Earned})");
            Say("walked");
            // B's volley is played here: A pressed nothing
            Expect(await Heard("B", "lit", 150), "B lit its box");
            Expect(await Until(() => fw.PlayingOwn, 30), "A plays B's volley");
            Expect(fw.ShowSeed == August1Rules.OwnSeed(run.Instance, 1), $"from the seed the server chose ({fw.ShowSeed})");
            Expect(await Until(() => fw.BurstsFired >= 1, 30), $"its first shell bursts here ({fw.BurstsFired})");
            int points = 2 * pay * August1Rules.PointsPerBatzen;   // the show was watched before the party formed: its points are no party's
            Expect(await Until(() => Svc.BoardOf(Id)?.Points == points, 15), $"A's board holds the party's {points} points ({Svc.BoardOf(Id)?.Points})");
            Say("done");
        }
        else
        {
            Say("ready");
            Expect(await Until(() => Invites().Incoming.Any(v => v.Kind == Parties.Kind), 60), "B is invited to A's party");
            if (Invites().Incoming.FirstOrDefault(v => v.Kind == Parties.Kind) is { } invite) Invites().Accept(invite.Id);
            Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "B's party holds A and B");
            Expect(await Heard("A", "party", 60), "A has its party");
            // forged: from 150 m away
            Teleport(stand.E + 150, stand.N + 150);
            await Seconds(1.0);
            Svc.AskAugust1(FestivalService.August1LampionStand);
            Expect(await Until(() => refused?.Contains("Go to the lampion stand") == true, 10), $"the stand from 150 m away is refused ({refused})");
            refused = null;
            Svc.AskAugust1(FestivalService.August1Post);
            Expect(await Until(() => refused?.Contains("Go to the lampion post") == true, 10), $"and so is the post ({refused})");
            Expect(Mine.Earned == August1Rules.ShowBatzen && Count(ItemId.Lampion) == 0, "nothing was given");
            Say("refused");
            Expect(await Heard("A", "lamp", 60), "A has its lampion");
            Teleport(stand.E + 1.0, stand.N + 2.0);
            Expect(await Until(() => game.NearTarget == FestivalService.August1LampionStand, 10), "B stands at the lampion stand");
            await Seconds(1.0);
            Expect(August1Game.TryInteract(Me!) && await Until(() => Count(ItemId.Lampion) == 1, 10), "B takes a lampion");
            Say("lamp");
            Expect(await Heard("A", "atpost", 60), "A is at the post");
            Teleport(post.E + 1.0, post.N + 2.5);
            await Seconds(1.0);
            Say("atpost");
            Expect(await Until(() => game.Walking, 20), $"A started the walk and B is told ({refused})");
            Expect(told.Any(t => t.What == FestivalService.August1Event.WalkStart && t.A == 2 && t.Ms == 1), "B is told two people and a child walk");
            // B stays at the post: A is 50 m away at the first stop
            Expect(await Until(() => told.Any(t => t.What == FestivalService.August1Event.Waiting), 30), "B is told to catch up");
            Expect(await Heard("A", "waiting", 60), "A waits");
            Teleport(path[0].E + 2, path[0].N);
            Expect(await Until(() => game.NextStop == 1, 20), "with B beside A the first stop is passed");
            for (int k = 1; k < August1Rules.Waypoints; k++)
            {
                Teleport(path[k].E + 2, path[k].N);
                Expect(await Until(() => game.NextStop == k + 1, 20), $"stop {k + 1} is passed with A ({game.NextStop})");
            }
            Teleport(path[August1Rules.Waypoints].E + 2, path[August1Rules.Waypoints].N);
            Expect(await Until(() => told.Any(t => t.What == FestivalService.August1Event.WalkDone), 20), "the post finishes the walk");
            int pay = August1Rules.WalkPay(2);
            Expect(await Until(() => Mine.Earned == August1Rules.ShowBatzen + pay, 10), $"B is paid for two walkers in its own section ({Mine.Earned})");
            Expect(await Heard("A", "walked", 60), "A finished too");
            // the fireworks stand: a box for Batzen, lit at night
            Teleport(boxStand.E, boxStand.N + 2.0);
            Expect(await Until(() => game.NearTarget == FestivalService.August1FireworkStand, 10), "B stands at the fireworks stand");
            await Seconds(1.0);
            int held = Mine.Batzen;
            Expect(August1Game.TryInteract(Me!) && await Until(() => Count(ItemId.FireworkBox) == 1, 10), "B buys a box");
            Expect(await Until(() => Mine.Batzen == held - August1Rules.BoxPrice, 10), $"for {August1Rules.BoxPrice} Batzen ({held - Mine.Batzen})");
            Expect(August1Game.TryLight(Me!, out string? why), $"B lights it ({why})");
            Expect(await Until(() => told.Any(t => t.What == FestivalService.August1Event.Lit), 10), "the server says the fireworks are lit");
            Expect(await Until(() => fw.PlayingOwn, 30), "B plays its own volley");
            Say("lit");
            Expect(await Heard("A", "done", 120), "A saw it");
        }
        await Finish(2.0);
    }
}
