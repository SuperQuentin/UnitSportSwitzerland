using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Sechselaeuten;

/// <summary>
/// <c>--sechscheck --sechsefast --festival test --date 2027-04-19 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c>
/// (quick tier, #1358): offline, this client plays the server's part, on its own scratch folder (<c>test_output/sechsecheck</c>, never the real adventure
/// or festival saves). The Sechseläuten (held at the fixture's start under <c>--festival test</c>) is on its one day. Through the real game: away from the
/// pyre and the Weibel there is no prompt and a forged use is refused; a bet with an empty purse is refused; the Weibel's purse pays once a day; at the pyre
/// Use steps the minute and E bets it (one bet, ten Batzen), bots bet for the crowd; the sky crossing 18:00 lights the pyre and closes the bets, and
/// <b>nothing told before the bang carries the burn time</b> (every message on the wire has <c>Minutes -1</c>); the head bursts, the pool comes back by
/// closeness with the play's pay, the board names every bet, the stage hides the head and shows the embers; a Cervelat is grilled once; the next round
/// opens its bets and burns for a different time; a round still open when the day ends is void and the stake comes back; the dates. Windowed it saves
/// <c>test_output/sechse_*.png</c>.
/// </summary>
public partial class SechselaeutenCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--sechscheck");

    private readonly Func<FootPlayer?> _me;

    public SechselaeutenCheck(ItemController items, Func<FootPlayer?> me) : base(items, "sechscheck", "", "sechse_") => _me = me;
    public SechselaeutenCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static FestivalService Svc => FestivalService.Instance!;

    private void Teleport((double E, double N) at, double dn = 0)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(at.E, at.N + dn, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    private FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("sechselaeuten-2027") ?? new FestivalInstance();

    private double _hour = 12;

    /// <summary>The sky crosses 18:00 now (from 17:00, one look of the tick between).</summary>
    private async Task Evening()
    {
        _hour = 17.0;
        await Seconds(0.8);
        _hour = 18.05;
    }

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && SechseGame.Instance != null && Me is { } me && me.IsOnFloor()
                && FestivalService.Instance.RunningOf(SechselaeutenRules.FestivalId) != null, 90))
        {
            Fail($"no Sechseläuten (service {FestivalService.Instance != null}, game {SechseGame.Instance != null}, body {Me?.Global})");
            return;
        }
        Svc.Hour = () => _hour;
        FestivalService.SechseDraw = _ => 14.37;
        var game = SechseGame.Instance!;
        const string Id = SechselaeutenRules.FestivalId;
        var run = Svc.RunningOf(Id)!;
        var site = run.Site!;
        var local = AdventureService.LocalPeer;
        var raw = new List<string>();
        Svc.SechseRaw += json => raw.Add(json);
        string? refused = null;
        Svc.Refused += why => refused = why;

        Expect(run.Day == 1 && run.Length == 1, $"day 1 of 1 of the Sechseläuten ({run.Day}/{run.Length})");
        Expect(site is { Relocated: false }, $"held at the fixture's start ({site})");
        Expect(FestivalOccasion.SiteOf(Id) != null, "the occasion dresses the site (the Weibel and the banners)");
        Expect(FestivalCatalog.Get(Id)!.Problems().Count == 0, "the festival definition is sound");
        Expect(Mine.Batzen == 0, "a fresh player holds no Batzen");
        Expect(await Until(() => BooggStage.Instance is { Built: true } && Svc.SechseView is { Round: 1, Phase: 0 }, 20), "the stage stands and the first round is open for bets");
        var stage = BooggStage.Instance!;
        Expect(stage.HeadOn && !stage.EmbersShown && stage.Ramp == 0, "the Böögg stands with his head on, the pyre cold");
        var pyre = SechselaeutenRules.PyreAt(site);
        var weibel = SechselaeutenRules.WeibelAt(site);

        // ---- away from everything: nothing offered, forged uses refused ---------------------------------------
        Teleport(pyre, 90);
        await Seconds(0.4);
        Expect(SechseGame.InteractionFor(Me!) == null && !SechseGame.TryInteract(Me!) && !SechseGame.TakesUse, "no prompt away from the ground, E does nothing, the items keep Use");
        foreach (var (kind, what) in new[] { (FestivalService.SechseBet, "Go to the pyre"), (FestivalService.SechseGrill, "Go to the embers"), (FestivalService.SechsePurse, "Go to the Zunft") })
        {
            refused = null;
            Expect(Svc.ServeSechse(local, kind, 3) != null && refused?.Contains(what) == true, $"kind {kind} from afar is refused ({refused})");
        }
        Expect(Svc.ServeSechse(local, 9, 3) != null, "a kind that does not exist is refused");
        Expect(Mine.Batzen == 0 && Svc.SechseView!.Bets == 0, "nothing was paid, nothing was bet");

        // ---- an empty purse cannot bet; the Weibel's purse pays once a day --------------------------------------
        Teleport(pyre, 6);
        Expect(await Until(() => game.NearTarget == 0, 5), $"the pyre is at hand ({game.NearTarget})");
        refused = null;
        Expect(Svc.ServeSechse(local, FestivalService.SechseBet, 3) != null && refused?.Contains("you hold 0") == true, $"a bet with no Batzen is refused ({refused})");
        Teleport(weibel, 1.5);
        Expect(await Until(() => game.NearTarget == FestivalService.SechsePurse, 5), "the Weibel is at hand");
        Expect(SechseGame.InteractionFor(Me!) is { } take && take.Text.Contains("Take the Zunft's purse"), $"the purse is offered ({SechseGame.InteractionFor(Me!)?.Text})");
        Expect(SechseGame.TryInteract(Me!), "E takes the purse");
        Expect(await Until(() => Mine.Batzen == SechselaeutenRules.PurseBatzen && Mine.Earned == SechselaeutenRules.PurseBatzen, 5), $"the purse paid {SechselaeutenRules.PurseBatzen} ({Mine.Batzen})");
        Expect(Mine.Points == SechselaeutenRules.PursePoints, $"and its points ({Mine.Points})");
        Expect(await Until(() => SechseGame.InteractionFor(Me!) == null, 5), "the purse is no longer offered today");
        refused = null;
        Expect(Svc.ServeSechse(local, FestivalService.SechsePurse, 0) != null && refused?.Contains("today") == true, $"a second purse is refused ({refused})");

        // ---- the bet: Use steps the minute, E places it ---------------------------------------------------------------
        Teleport(pyre, 6);
        Expect(await Until(() => game.NearTarget == 0 && game.Betting && SechseGame.TakesUse, 5), "at the pyre with bets open Use belongs to the picker");
        Expect(game.Bucket == SechselaeutenRules.DefaultBucket, $"the picker starts at the median bucket ({game.Bucket})");
        Expect(SechseGame.InteractionFor(Me!) is { } offer && offer.Text.Contains("Bet 10 Batzen on 10-15 min"), $"the bet is offered ({SechseGame.InteractionFor(Me!)?.Text})");
        bool use = false;
        game.UseHeld = () => use;
        use = true;
        await Seconds(0.3);
        use = false;
        await Seconds(0.3);
        Expect(game.Bucket == 4 && SechseGame.InteractionFor(Me!)!.Value.Text.Contains("15-20 min"), $"Use stepped the minute ({game.Bucket})");
        Expect(game.Lines.Any(l => l.Contains("15-20 min")), "and the panel shows it");
        Expect(SechseGame.TryInteract(Me!), "E places the bet");
        Expect(await Until(() => Mine.Batzen == SechselaeutenRules.PurseBatzen - SechselaeutenRules.Stake, 5), $"the stake left the purse ({Mine.Batzen})");
        Expect(await Until(() => Svc.SechseView is { Mine: 4, Bets: 1, Pool: 10 }, 5), $"the round knows the bet ({Svc.SechseView})");
        Expect(Mine.Earned == SechselaeutenRules.PurseBatzen, "a stake is spent, not earned: the cap still counts the purse only");
        Expect(!game.Betting && !SechseGame.TakesUse && SechseGame.InteractionFor(Me!) == null, "with a bet placed the picker lets go of Use and nothing is offered");
        refused = null;
        Expect(Svc.ServeSechse(local, FestivalService.SechseBet, 5) != null && refused?.Contains("You have bet") == true, $"a second bet is refused ({refused})");
        Expect(Svc.ServeSechse(-5, FestivalService.SechseBet, 3) == null && Svc.ServeSechse(-6, FestivalService.SechseBet, 9) == null, "two bots bet for the crowd");
        Expect(Svc.ServeSechse(-5, FestivalService.SechseBet, 4) != null && Svc.ServeSechse(-7, FestivalService.SechseGrill, 0) != null, "a bot bets once and only bets");
        Expect(await Until(() => Svc.SechseView is { Bets: 3, Pool: 10 }, 5) && Svc.SechseBotBets == 2, "the bots are in the crowd but not in the pool");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(1.0);
            GD.Print($"[sechscheck] shot {Shot("pyre_bets")}");
        }

        // ---- before the light: no secret anywhere --------------------------------------------------------------
        Expect(Svc.SechseView!.Minutes == -1 && Svc.SechseView.DoneAt == 0 && Svc.SechseLights == 0, "bets open: no time, no light");
        _hour = 17.0;
        await Seconds(1.0);
        Expect(Svc.SechseLights == 0 && Svc.SechseView.Phase == 0, "17:00 lights nothing");

        // ---- the light ----------------------------------------------------------------------------------------------------
        _hour = 18.05;
        Expect(await Until(() => Svc.SechseView is { Phase: 1 }, 10), "the sky crossing 18:00 lights the pyre");
        Expect(Svc.SechseLights == 1 && Svc.SechseView!.LitAt > 0, "lit once, the light's time is public");
        refused = null;
        Expect(Svc.ServeSechse(-8, FestivalService.SechseBet, 2) != null, "bets are closed for a bot");
        Teleport(pyre, 40);
        Teleport(pyre, 6);
        Expect(await Until(() => game.NearTarget == 0, 5), "back at the pyre");
        Expect(Svc.ServeSechse(local, FestivalService.SechseGrill, 0) != null, "the fire is too hot to grill");
        Expect(Svc.SechseView!.Minutes == -1 && Svc.SechseView.Board.Length == 0, "burning: the view holds no minutes and no board");
        Expect(raw.Count > 0 && raw.All(j => j.Contains("\"minutes\":-1") && j.Contains("\"doneAt\":0")), $"every message of the round so far has minutes -1 ({raw.Count})");
        Expect(raw.All(j => !j.Contains("14.37")), "and none of them says the drawn time");
        Expect(await Until(() => stage.Ramp > 0.2f && stage.LightsPlayed == 1, 10), $"the flames rise and the light is heard ({stage.Ramp})");
        Expect(stage.HeadOn, "the head is still on while it burns");
        Expect(game.Lines.Any(l => l.Contains("Bets are closed")), "the panel says the bets are closed");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(2.0);
            GD.Print($"[sechscheck] shot {Shot("pyre_burning")}");
        }

        // ---- the bang ---------------------------------------------------------------------------------------------------------
        int before = Mine.Batzen, earned = Mine.Earned;
        Expect(await Until(() => Svc.SechseView is { Phase: 2 }, 40), "the head bursts");
        var done = Svc.SechseView!;
        Expect(done.Minutes == 14.37 && done.DoneAt > 0, $"now the burn time is told ({done.Minutes})");
        Expect(done.Won == SechselaeutenRules.Stake, $"the lone human bet gets the pool back ({done.Won})");
        Expect(await Until(() => Mine.Batzen == before + SechselaeutenRules.Stake + SechselaeutenRules.PlayBatzen, 5), $"the share and the play's pay arrived ({Mine.Batzen - before})");
        Expect(Mine.Earned == earned + SechselaeutenRules.PlayBatzen, $"only the play's pay counts as earned ({Mine.Earned - earned})");
        Expect(Mine.Points == SechselaeutenRules.PursePoints + SechselaeutenRules.PlayPoints + done.Won, $"points: the play's and the share's ({Mine.Points})");
        Expect(done.Board.Length == 3 && done.Board.Count(r => r.Bot) == 2 && done.Board.Where(r => r.Bot).All(r => r.Won == 0), "the board names the three bets, the bots win nothing");
        Expect(done.Msg.Contains("burst after 14.4 minutes") && done.Msg.Contains("+10"), $"she is told ({done.Msg})");
        Expect(await Until(() => !stage.HeadOn && stage.EmbersShown && stage.BlastsPlayed == 1, 5), $"the head is gone, the embers lie, the blast was played ({stage.BlastsPlayed})");
        Expect(Svc.SechseBangs == 1, "one bang on the server");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(0.15);
            GD.Print($"[sechscheck] shot {Shot("pyre_bang")}");
        }

        // ---- the embers: a Cervelat -----------------------------------------------------------------------------------------
        Expect(await Until(() => SechseGame.InteractionFor(Me!) is { } grill && grill.Text.Contains("Grill a Cervelat"), 5), "the embers offer a Cervelat");
        Expect(SechseGame.TryInteract(Me!), "E grills");
        Expect(await Until(() => CountOf(ItemId.BooggCervelat) == 1, 5), "the Cervelat is in the pack");
        refused = null;
        Expect(Svc.ServeSechse(local, FestivalService.SechseGrill, 0) != null && refused?.Contains("this round") == true, $"one a round ({refused})");

        // ---- the next round ----------------------------------------------------------------------------------------------------
        FestivalService.SechseDraw = _ => 3.0;
        Expect(await Until(() => Svc.SechseView is { Round: 2, Phase: 0, Bets: 0 }, 120), "a minute later the next round opens its bets");
        Expect(await Until(() => stage.HeadOn && !stage.EmbersShown, 5), "a new Böögg stands on a new pyre");
        Teleport(pyre, 6);
        await Until(() => game.NearTarget == 0, 5);
        before = Mine.Batzen;
        Expect(Svc.ServeSechse(local, FestivalService.SechseBet, 1) == null && await Until(() => Mine.Batzen == before - SechselaeutenRules.Stake, 5), "a bet on 0-5 min in round 2");
        await Evening();
        Expect(await Until(() => Svc.SechseView is { Round: 2, Phase: 1 }, 10), "round 2 is lit");
        Expect(await Until(() => Svc.SechseView is { Round: 2, Phase: 2 }, 40), "and its head bursts, sooner");
        Expect(Svc.SechseView!.Minutes == 3.0 && Svc.SechseView.Won == SechselaeutenRules.Stake, $"after 3 minutes the lone bet on the first bucket is back ({Svc.SechseView.Minutes}/{Svc.SechseView.Won})");
        Expect(Svc.SechseBangs == 2, "two bangs");

        // ---- a round still open when the day ends is void ---------------------------------------------------------------------
        FestivalService.SechseDraw = _ => 14.37;
        Expect(await Until(() => Svc.SechseView is { Round: 3, Phase: 0 }, 120), "round 3 opens");
        await Until(() => Mine.Batzen >= SechselaeutenRules.Stake, 5);
        int worth = Mine.Batzen;
        Expect(Svc.ServeSechse(local, FestivalService.SechseBet, 5) == null && await Until(() => Mine.Batzen == worth - SechselaeutenRules.Stake, 5), "a bet in round 3");
        int converted = Mine.ConvertedChf;
        OccasionManager.Instance!.OverrideDate(new DateOnly(2027, 4, 20));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "the Sechseläuten is over on 20 April");
        Expect(await Until(() => Svc.SechseRound("sechselaeuten-2027") == null && Mine.ConvertedChf - converted == worth, 15), $"the open round is void and the stake is back in the francs ({Mine.ConvertedChf - converted} of {worth})");
        refused = null;
        Expect(Svc.ServeSechse(local, FestivalService.SechseBet, 3) != null && refused?.Contains("not on") == true, $"nothing to bet on then ({refused})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 4, 18));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "and not yet open on the Sunday");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 4, 19));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 1, 15), "day 1 is the third Monday, 19 April");
        FestivalService.SechseDraw = null;
        await Finish(0);
    }
}
