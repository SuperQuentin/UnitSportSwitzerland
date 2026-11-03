using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Escalade;

/// <summary>
/// <c>--escaladecheck --escaladefast --festival test --date 2027-12-11 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c>
/// (quick tier, #1351): offline, this client plays the server's part, on its own scratch folder (<c>test_output/escaladecheck</c>, never the real adventure
/// or festival saves). The Escalade (held at the fixture's start under <c>--festival test</c>) is on its day 1 of 2. A lone player is both the youngest and the
/// oldest of the party, so the check walks all three games through the real game: away from them there is no prompt and a forged use is refused; at the
/// Marmite E smashes it (Batzen, points, chocolate, the saying) once a day; at the pot E ladles a bowl once a day; at the start line E begins the Course,
/// teleports through the six gates and the line pay by the time (the sprint limit lifted by <c>--escaladefast</c>; with it back a teleport is no run);
/// a bot is counted and never paid; the next day pays again; the festival opens on the second Saturday of December for two days. Windowed it saves
/// <c>test_output/escalade_*.png</c>.
/// </summary>
public partial class EscaladeCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--escaladecheck");

    private readonly Func<FootPlayer?> _me;

    public EscaladeCheck(ItemController items, Func<FootPlayer?> me) : base(items, "escaladecheck", "", "escalade_") => _me = me;
    public EscaladeCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static FestivalService Svc => FestivalService.Instance!;

    private void Teleport((double E, double N) at, double dn = 0)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(at.E, at.N + dn, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    private FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("escalade-2027") ?? new FestivalInstance();

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && EscaladeGame.Instance != null && Me is { } me && me.IsOnFloor()
                && FestivalService.Instance.RunningOf(EscaladeRules.FestivalId) != null, 90))
        {
            Fail($"no Escalade (service {FestivalService.Instance != null}, game {EscaladeGame.Instance != null}, body {Me?.Global})");
            return;
        }
        var game = EscaladeGame.Instance!;
        const string Id = EscaladeRules.FestivalId;
        var run = Svc.RunningOf(Id)!;
        var site = run.Site!;
        var local = AdventureService.LocalPeer;
        var told = new List<(FestivalService.EscaladeEvent What, int A, int Ms, string Text)>();
        Svc.EscaladeTold += (w, a, ms, t) => told.Add((w, a, ms, t));
        string? refused = null;
        Svc.Refused += why => refused = why;

        Expect(run.Day == 1 && run.Length == 2, $"day 1 of 2 of the Escalade ({run.Day}/{run.Length})");
        Expect(site is { Relocated: false }, $"held at the fixture's start ({site})");
        Expect(FestivalOccasion.SiteOf(Id) != null, "the occasion dresses the site (Marmite, pot, arch and torches)");
        Expect(FestivalCatalog.Get(Id)!.Problems().Count == 0, "the festival definition is sound");
        Expect(Mine.Batzen == 0, "a fresh player holds no Batzen");
        var marmite = EscaladeRules.MarmiteAt(site);
        var pot = EscaladeRules.PotAt(site);
        var line = EscaladeRules.StartAt(site);

        // ---- away from everything: nothing offered, forged uses refused -----------------------------------
        Teleport(marmite, 70);
        await Seconds(0.4);
        Expect(EscaladeGame.InteractionFor(Me!) == null && !EscaladeGame.TryInteract(Me!), "no prompt away from the ground, E does nothing");
        foreach (int target in new[] { FestivalService.EscaladeMarmite, FestivalService.EscaladePot, FestivalService.EscaladeStart })
        {
            refused = null;
            Expect(Svc.ServeEscalade(local, target) != null && refused?.Contains("Go to the") == true, $"target {target} from afar is refused ({refused})");
        }
        Expect(Svc.ServeEscalade(local, 9) != null, "a target that does not exist is refused");
        Expect(Mine.Batzen == 0, "nothing was paid");

        // ---- the Marmite: a lone player is the pair -----------------------------------------------------------
        Teleport(marmite, 2.5);
        Expect(await Until(() => game.NearTarget == FestivalService.EscaladeMarmite, 5), $"the Marmite is at hand ({game.NearTarget})");
        Expect(EscaladeGame.InteractionFor(Me!) is { } smash && smash.Text.Contains("Smash the Marmite"), $"the smash is offered ({EscaladeGame.InteractionFor(Me!)?.Text})");
        Expect(EscaladeGame.TryInteract(Me!), "E uses the Marmite");
        int pay = EscaladeRules.SmashBatzen + EscaladeRules.SmashPairBatzen;
        Expect(await Until(() => Mine.Batzen == pay, 5), $"the smash paid {pay} Batzen, the pair's share ({Mine.Batzen})");
        Expect(Mine.Points == EscaladeRules.SmashPoints && Svc.CrewPoints(local, Id) == EscaladeRules.SmashPoints, $"and the party points ({Mine.Points} / crew {Svc.CrewPoints(local, Id)})");
        Expect(await Until(() => Count(ItemId.MarmiteChocolate) == EscaladeRules.ChocolatePieces, 5), "and the chocolate is in the pack");
        Expect(told.Any(t => t.What == FestivalService.EscaladeEvent.Smashed && t.Text.Contains(EscaladeRules.Saying)), "she is told the saying");
        refused = null;
        Expect(Svc.ServeEscalade(local, FestivalService.EscaladeMarmite) != null && refused?.Contains("today") == true, $"only once a day ({refused})");
        Expect(Svc.EscaladeSmashes == 1, "one smash on the server");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(1.0);
            GD.Print($"[escaladecheck] shot {Shot("marmite")}");
        }

        // ---- the soup pot -----------------------------------------------------------------------------------------
        Teleport(pot, 2.0);
        Expect(await Until(() => game.NearTarget == FestivalService.EscaladePot, 5), "the pot is at hand");
        Expect(EscaladeGame.InteractionFor(Me!) is { } ladle && ladle.Text.Contains("Ladle the soupe"), $"the ladle is offered ({EscaladeGame.InteractionFor(Me!)?.Text})");
        int before = Mine.Batzen;
        Expect(EscaladeGame.TryInteract(Me!), "E ladles");
        Expect(await Until(() => Mine.Batzen == before + EscaladeRules.SoupeBatzen, 5), $"a bowl paid {EscaladeRules.SoupeBatzen} Batzen ({Mine.Batzen})");
        Expect(await Until(() => Count(ItemId.SoupeEscalade) == 1, 5), "and is in the pack");
        refused = null;
        Expect(Svc.ServeEscalade(local, FestivalService.EscaladePot) != null && refused?.Contains("eaten") == true, $"a second bowl is refused ({refused})");
        var soup = ItemDefs.Get(ItemId.SoupeEscalade)!;
        Expect(soup is { Buff: StaminaBuff.Warm } && (int)ItemId.SoupeEscalade is >= 500 and < 540 && (int)ItemId.MarmiteChocolate is >= 500 and < 540, "the soup is warm and both items are in the festivals' id block");

        // ---- the Course -----------------------------------------------------------------------------------------------
        var path = EscaladeRules.Path(site);
        double par = EscaladeRules.ParSeconds(site);
        Teleport(line, 1.5);
        Expect(await Until(() => game.NearTarget == FestivalService.EscaladeStart, 5), "the start line is at hand");
        Expect(EscaladeGame.InteractionFor(Me!) is { } start && start.Text.Contains("Start the Course"), $"the start is offered ({EscaladeGame.InteractionFor(Me!)?.Text})");
        // the line alone finishes nothing: the gates come first
        Expect(EscaladeGame.TryInteract(Me!) && await Until(() => game.Running, 5), "E starts the Course");
        await Seconds(0.8);
        Expect(game.Running && game.NextGate == 0 && Svc.EscaladeFinished == 0, "standing at the line is not a finish: gate 0 comes first");
        for (int k = 0; k < EscaladeRules.Gates; k++)
        {
            Teleport(path[k]);
            Expect(await Until(() => game.NextGate == k + 1, 8), $"gate {k + 1} is passed ({game.NextGate})");
        }
        Teleport(path[EscaladeRules.Gates]);
        before = Mine.Batzen;
        int earned = Mine.Earned;
        Expect(await Until(() => told.Any(t => t.What == FestivalService.EscaladeEvent.CourseDone), 8), "the line finishes the Course");
        Expect(!game.Running && Svc.EscaladeFinished == 1, "the run is over");
        var done = told.Last(t => t.What == FestivalService.EscaladeEvent.CourseDone);
        int expected = EscaladeRules.CoursePay(done.Ms / 1000.0, par);
        Expect(expected == EscaladeRules.CourseBatzen + EscaladeRules.SpeedBatzen, $"a teleporting run is the fastest the pay knows ({expected})");
        // the run's points (180) take the party past the first milestone (250), which pays its 10 Batzen on top, outside the cap
        Expect(await Until(() => Mine.Earned == earned + expected, 5), $"the run earned {expected} Batzen ({Mine.Earned - earned})");
        Expect(Mine.Batzen == before + expected + 10 && Mine.Milestones.SequenceEqual([0]), $"and the first milestone paid its 10 on top ({Mine.Batzen - before})");
        Expect(Mine.Paid.GetValueOrDefault("course:1") == expected, "inside the game's day cap");
        Expect(done.Text.Contains("Course de l'Escalade"), $"the finish is told ({done.Text})");

        // a run that is too fast for a body is no run
        FestivalService.EscaladeSpeedLimit = EscaladeRules.SprintSpeed;
        Teleport(line, 1.5);
        await Until(() => game.NearTarget == FestivalService.EscaladeStart, 5);
        EscaladeGame.TryInteract(Me!);
        await Until(() => game.Running, 5);
        for (int k = 0; k <= EscaladeRules.Gates; k++)
        {
            Teleport(path[k]);
            await Until(() => game.NextGate == k + 1 || !game.Running, 8);
            await Seconds(0.5);
        }
        before = Mine.Batzen;
        Expect(await Until(() => told.Any(t => t.What == FestivalService.EscaladeEvent.CourseLost && t.Text.Contains("not a run")), 8), $"a teleport at sprint limit is told it was not a run ({string.Join(", ", told.TakeLast(4).Select(t => t.What + ":" + t.A))}; gate {game.NextGate}, running {game.Running})");
        Expect(Mine.Batzen == before && Svc.EscaladeFinished == 1, "and pays nothing");
        FestivalService.EscaladeSpeedLimit = 1e9;

        // ---- a bot is counted, never paid -------------------------------------------------------------------------------
        const long Bot = -9;
        int bots = Svc.EscaladeBotActs, granted = Svc.GrantedBatzen;
        Expect(Svc.ServeEscalade(Bot, FestivalService.EscaladeMarmite) == null && Svc.ServeEscalade(Bot, FestivalService.EscaladePot) == null, "a bot smashes and ladles");
        Expect(Svc.EscaladeBotActs == bots + 2 && Svc.GrantedBatzen == granted && Svc.BatzenOf(Bot, Id) == 0, "counted, no Batzen granted");

        // ---- a new day pays again; the dates -------------------------------------------------------------------------
        OccasionManager.Instance!.OverrideDate(new DateOnly(2027, 12, 12));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 2, 15), "12 December is day 2");
        Teleport(marmite, 2.5);
        await Until(() => game.NearTarget == FestivalService.EscaladeMarmite, 5);
        before = Mine.Batzen;
        Expect(Svc.ServeEscalade(local, FestivalService.EscaladeMarmite) == null && await Until(() => Mine.Batzen == before + pay, 5), $"the Marmite pays again on day 2 ({Mine.Batzen - before})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 12, 13));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "the Escalade is over on 13 December");
        refused = null;
        Expect(Svc.ServeEscalade(local, FestivalService.EscaladePot) != null && refused?.Contains("not on") == true, $"nothing to ladle then ({refused})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 12, 10));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "and not yet open on the Friday");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 12, 11));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 1, 15), "day 1 is the second Saturday, 11 December");
        FestivalService.EscaladeSpeedLimit = EscaladeRules.SprintSpeed;
        await Finish(0);
    }
}
