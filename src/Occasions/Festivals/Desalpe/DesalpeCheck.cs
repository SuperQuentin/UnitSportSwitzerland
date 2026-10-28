using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Desalpe;

/// <summary>
/// <c>--desalpecheck --festival test --date 2027-09-24 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c> (quick tier,
/// #1269 part 7): offline, this client plays the server's part. The Désalpe (held at the fixture's start under <c>--festival test</c>, a 45 m
/// route) is on its day 5 of 16: the herd stands on the alp and is drawn. The check dresses cows through the real game (E for the headdress, Use for the
/// colour, the prompt and the outline at hand, none away from the herd), pays the first dressing only, refuses a dress from afar; starts the drive at the
/// alp sign with a farmhand and a bot; drives the herd (the player walks behind the Leitkuh) to the fair; checks the result, the Batzen, the party board
/// and that the bots earned nothing; looks at the ring (the vote for one's own herd is refused); sees the herd come up fresh and dressed again; and
/// closes the drive before day 3 and the vote before day 4. Windowed it saves <c>test_output/desalpe_*.png</c>. Progress lives in <c>test_output/desalpecheck</c>.
/// </summary>
public partial class DesalpeCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--desalpecheck");

    private readonly Func<FootPlayer?> _me;
    private const long Bot = -7;

    public DesalpeCheck(ItemController items, Func<FootPlayer?> me) : base(items, "desalpecheck", "", "desalpe_") => _me = me;
    public DesalpeCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private void Teleport(double e, double n)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(e, n, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && DesalpeGame.Instance != null && DesalpeHerd.Instance != null && Me is { } me && me.IsOnFloor()
                && FestivalService.Instance.RunningOf(DesalpeRules.FestivalId) != null && FestivalService.Instance.DesalpeNow != null && DesalpeHerd.Instance.Count > 0, 90))
        {
            Fail($"no Desalpe or no herd (service {FestivalService.Instance != null}, game {DesalpeGame.Instance != null}, herd {DesalpeHerd.Instance?.Count}, body {Me?.Global})");
            return;
        }
        var svc = FestivalService.Instance!;
        var game = DesalpeGame.Instance!;
        var herd = DesalpeHerd.Instance!;
        var adventure = AdventureService.Instance!;
        const string Id = DesalpeRules.FestivalId;
        var run = svc.RunningOf(Id)!;
        var site = run.Site!;
        var route = svc.DesalpeRouteNow()!;
        Expect(run.Day == 5 && run.Length == 16, $"day 5 of 16 of the Désalpe ({run.Day}/{run.Length})");
        Expect(site is { Relocated: false }, $"held at the fixture's start ({site})");
        Expect(herd.Count == DesalpeRules.Cows && herd.Phase == DesalpePhase.Grazing, $"the herd of {herd.Count} stands on the alp ({herd.Phase})");
        Expect(Math.Abs(route.Length - DesalpeRules.TestRouteMeters) < 0.5, $"a {route.Length:F0} m drive");
        Expect(FestivalOccasion.SiteOf(Id) != null, "the occasion dresses the site (sign and ring)");

        // ---- far from the herd: no prompt, a dress refused --------------------------------------------
        await Seconds(0.3);
        Expect(DesalpeGame.InteractionFor(Me!) == null && !DesalpeGame.TakesUse, "no prompt away from the herd, the items keep Use");
        string? refused = null;
        svc.Refused += why => refused = why;
        Expect(svc.ServeDesalpeDress(AdventureService.LocalPeer, 3, DesalpeRules.Dress(1, 0)) != null && refused?.Contains("Go to the cow") == true, $"a dress from afar is refused ({refused})");

        // ---- dress the cows through the game ----------------------------------------------------------------
        var (ce, cn) = herd.CowAt(3)!.Value;
        Teleport(ce + 1.6, cn);
        await Seconds(0.4);
        Expect(herd.Nearest(Me!.Global.E, Me.Global.N) is var (near, d) && d <= DesalpeGame.CowReach && near >= 0, $"a cow within reach (cow {herd.Nearest(Me.Global.E, Me.Global.N).Cow})");
        await Until(() => game.Dressing, 3);
        int cow = game.NearCow;
        Expect(game.Dressing && cow >= 0 && DesalpeGame.TakesUse, "the game takes Use near a cow");
        Expect(DesalpeGame.InteractionFor(Me) is { } prompt && prompt.Text.Contains("Dress") && prompt.Target.IsSome, $"the prompt and the outline are offered ({DesalpeGame.InteractionFor(Me)?.Text})");
        int before = svc.BatzenOf(AdventureService.LocalPeer, Id);
        Expect(DesalpeGame.TryInteract(Me), "E puts on a headdress");
        Expect(await Until(() => DesalpeRules.ShapeOf(herd.DressOf(cow)) == 1, 5), $"the first shape is on ({DesalpeModels.Describe(herd.DressOf(cow))})");
        Expect(svc.BatzenOf(AdventureService.LocalPeer, Id) == before + DesalpeRules.DressBatzen, $"dressing a bare cow paid {DesalpeRules.DressBatzen} Batzen ({svc.BatzenOf(AdventureService.LocalPeer, Id) - before})");
        // Use turns the colour (a pulse of the key)
        bool use = true;
        game.UseHeld = () => use;
        await Seconds(0.3);
        use = false;
        Expect(await Until(() => DesalpeRules.ColourOf(herd.DressOf(cow)) == 1, 5), $"Use turned the colour ({DesalpeModels.Describe(herd.DressOf(cow))})");
        DesalpeGame.TryInteract(Me);
        Expect(await Until(() => DesalpeRules.ShapeOf(herd.DressOf(cow)) == 2, 5), "E turned the shape and kept the colour");
        Expect(DesalpeRules.ColourOf(herd.DressOf(cow)) == 1 && svc.BatzenOf(AdventureService.LocalPeer, Id) == before + DesalpeRules.DressBatzen, "changing a dress again pays nothing");
        // the Leitkuh and a third cow
        foreach (int c in new[] { 0, 5 })
        {
            var (e2, n2) = herd.CowAt(c)!.Value;
            Teleport(e2 + 0.9, n2);
            Expect(await Until(() => game.Dressing && game.NearCow == c, 3), $"cow {c} within reach (near {game.NearCow})");
            DesalpeGame.TryInteract(Me);
            Expect(await Until(() => herd.DressOf(c) != 0, 5), $"cow {c} is dressed");
        }
        Expect(DesalpeRules.Decorated(svc.DesalpeRunNow!.Dress) == 3 && svc.DesalpeNow!.Dress.Count(x => x != 0) == 3, "three cows dressed on the server and in the view");
        if (DisplayServer.GetName() != "headless")
        {
            var (le, ln) = herd.CowAt(0)!.Value;
            Teleport(le + 4, ln + 1);
            await Seconds(1.2);
            GD.Print($"[desalpecheck] shot {Shot("dressed")}");
        }

        // ---- start the drive at the sign ----------------------------------------------------------------------
        var (se, sn) = DesalpeGround.Sign(site, route);
        Teleport(se, sn);
        await Seconds(0.4);
        Expect(!game.Dressing && DesalpeGame.InteractionFor(Me!) is { } start && start.Text.Contains("Start the Alpabzug"), $"the prompt at the sign ({DesalpeGame.InteractionFor(Me!)?.Text})");
        adventure.SessionFor(Bot);
        Expect(DesalpeGame.TryInteract(Me!), "E starts the drive");
        Expect(await Until(() => svc.DesalpeRunNow?.Phase == DesalpePhase.Driving, 5), "the server runs the herd");
        Expect(svc.DesalpeNow!.Hands == 1, $"one farmhand makes up the second flank ({svc.DesalpeNow.Hands})");
        Expect(svc.ServeDesalpeBot(Bot) == null && svc.DesalpeNow!.Hands == 2, "a bot joins as a second hand");
        Expect(svc.ServeDesalpeStart(AdventureService.LocalPeer) != null, "a second start is refused");
        refused = null;
        Expect(svc.ServeDesalpeDress(AdventureService.LocalPeer, 4, 1) != null, "no dressing on the way");

        // ---- drive: walk behind the Leitkuh --------------------------------------------------------------------
        var live = svc.DesalpeRunNow!;
        double lastShot = -100;
        int guard = 0;
        while (live.Phase == DesalpePhase.Driving && guard++ < 4000)
        {
            var f = live.Flock;
            var (tx, ty) = route.Tangent(f.LeaderS);
            Teleport(site.E + f.X[0] - tx * 12, site.N + f.Y[0] - ty * 12);
            await Seconds(0.4);
            if (DisplayServer.GetName() != "headless" && f.LeaderS - lastShot > 40) { lastShot = f.LeaderS; GD.Print($"[desalpecheck] shot {Shot($"drive{(int)f.LeaderS}")}"); }
        }
        Expect(live.Phase == DesalpePhase.Arrived && live.Result is { } , $"the herd came down ({live.Phase}, leader at {live.Flock.LeaderS:F0}/{route.Length:F0} m)");
        var r = live.Result!;
        GD.Print($"[desalpecheck] drive: {r}");
        Expect(r.Arrived >= 8 && r.Total == DesalpeRules.Cows, $"most of the herd arrived ({r.Arrived}/{r.Total}, {r.Lost} lost)");
        Expect(r.Dressed == 3 || r.ArrivedDressed >= 2, $"the dressed cows came too ({r.ArrivedDressed}/{r.Dressed})");
        Expect(r.Score >= r.Arrived * 50, $"the score counts the cows ({r.Score})");
        Expect(await Until(() => svc.LastDesalpe != null, 5) && svc.LastDesalpe!.Value.Paid == 1, $"one person was paid ({svc.LastDesalpe?.Paid})");
        int driveBatzen = Math.Min(DesalpeRules.BatzenFor(r.Score), 120);
        int held = svc.BatzenOf(AdventureService.LocalPeer, Id);
        int games = DesalpeRules.DressBatzen * 3 + driveBatzen, milestone = svc.CrewPoints(AdventureService.LocalPeer, Id) >= 600 ? 20 : 0;
        Expect(held == games + milestone, $"the Batzen are the dressing and the drive's, and the first milestone's ({held} vs {games} + {milestone})");
        Expect(svc.CrewPoints(AdventureService.LocalPeer, Id) == 3 * DesalpeRules.DressPoints + r.Score, $"the party board holds the points ({svc.CrewPoints(AdventureService.LocalPeer, Id)})");
        Expect(game.LastResult.Score == r.Score, $"the player was told the result ({game.LastResult.Score})");
        Expect(adventure.Progress(Bot)?.Get(FestivalBook.Section).Instances.Values.Sum(i => i.Batzen) is null or 0 && svc.GrantedBatzen == games, $"the bot and the farmhand earned nothing (granted {svc.GrantedBatzen})");
        Expect(herd.Phase == DesalpePhase.Arrived, $"the client sees the herd at the fair ({herd.Phase})");
        var (ge, gn) = herd.CowAt(0)!.Value;
        Expect(Math.Sqrt((ge - site.E) * (ge - site.E) + (gn - site.N) * (gn - site.N)) < 25, $"drawn at the fair ({ge - site.E:F0}, {gn - site.N:F0})");

        // ---- the ring ----------------------------------------------------------------------------------------------
        Teleport(site.E + 6, site.N);
        await Seconds(0.5);
        Expect(svc.DesalpeNow!.Ring.Length == 1 && svc.DesalpeNow.Ring[0].Dressed == r.ArrivedDressed, $"the herd stands in the ring ({svc.DesalpeNow.Ring.Length})");
        Expect(game.Voting && DesalpeGame.InteractionFor(Me!) is { } vote && vote.Text.Contains("Vote for Herd 1"), $"the prompt in the ring ({DesalpeGame.InteractionFor(Me!)?.Text})");
        refused = null;
        DesalpeGame.TryInteract(Me!);
        await Seconds(0.3);
        Expect(refused?.Contains("own herd") == true, $"one's own herd is refused ({refused})");
        Expect(svc.BatzenOf(AdventureService.LocalPeer, Id) == held, "nothing was paid for it");
        if (DisplayServer.GetName() != "headless") GD.Print($"[desalpecheck] shot {Shot("ring")}");

        // ---- a fresh herd goes up, dressed as before ------------------------------------------------------------
        Expect(await Until(() => svc.DesalpeRunNow?.Phase == DesalpePhase.Grazing && svc.DesalpeRunNow.Number == 2, 40), "a fresh herd is up on the alp");
        Expect(DesalpeRules.Decorated(svc.DesalpeRunNow!.Dress) == 3, "it keeps its dress");
        Expect(await Until(() => herd.Phase == DesalpePhase.Grazing, 5), "and the client sees it");

        // ---- the calendar: no drive before day 3, no vote before day 4 --------------------------------------------------
        Teleport(se, sn);
        OccasionManager.Instance!.OverrideDate(new DateOnly(2027, 9, 21));
        Expect(await Until(() => svc.RunningOf(Id)?.Day == 2, 15), "the date moves to day 2");
        refused = null;
        Expect(svc.ServeDesalpeStart(AdventureService.LocalPeer) != null && refused?.Contains("day 3") == true, $"the drive is closed on day 2 ({refused})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 9, 22));
        Expect(await Until(() => svc.RunningOf(Id)?.Day == 3, 15), "day 3");
        Expect(svc.ServeDesalpeStart(AdventureService.LocalPeer) == null, "the drive opens on day 3");
        refused = null;
        Teleport(site.E + 6, site.N);
        Expect(svc.ServeDesalpeVote(AdventureService.LocalPeer, 1) != null && refused?.Contains("day 4") == true, $"the vote is closed on day 3 ({refused})");
        await Finish(0);
    }
}
