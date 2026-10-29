using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Sechselaeuten;

/// <summary>
/// <c>--sechsenet a|b</c> with <c>--connect --festival test</c> (driven by <c>tools/sechsenetcheck.sh</c>, #1358): the Sechseläuten over a dedicated server whose
/// calendar reads 19 April 2027 and whose sky runs, its burn fixed at 8 minutes (<c>--sechsedraw 8</c>, 0.75 s a minute: six seconds) so the check can watch the
/// burning without racing it. Everything is asserted on the REMOTE peers. Tom (B), 150 m off, is refused the pyre and the Weibel by the server; Lea (A) and Tom take
/// the Zunft's purse and bet (Lea through the game's E on the picker's median bucket, Tom through the server's request on the 45-50 bucket): each is told
/// the pool and its own bet only. The admin Lea sets the clock to 17:59 and the server's sky crossing 18:00 lights the pyre: bets close, both clients are
/// told, <b>every message they have been sent so far says <c>minutes -1</c></b> and the stage burns on this client without knowing the burn. The head bursts: both
/// are told the time at once, the board names both bets, each is paid its share of the pool by closeness (as the rules compute it from the board) plus the play's
/// pay in its own section, the stage shows the blast and the embers, and both grill a Cervelat.
/// </summary>
public partial class SechselaeutenNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--sechsenet");

    public SechselaeutenNetProbe(ItemController items) : base(items, "sechsenet", "SN", "sechsenet_") { }
    public SechselaeutenNetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("sechselaeuten-2027") ?? new FestivalInstance();

    private void Teleport(double e, double n)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(e, n, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    /// <summary>A teleport is a crash to the body (a jump of metres in a frame): it is up again at once, where it stands, so it never revives at its last safe spot.</summary>
    public override void _PhysicsProcess(double delta)
    {
        if (Me is { } m && (m.Health <= 0 || m.Down > 0)) m.Respawn(m.GlobalPosition);
    }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null && SechseGame.Instance != null)) return;
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached the server");
        const string Id = SechselaeutenRules.FestivalId;
        var game = SechseGame.Instance!;
        Expect(await Until(() => Svc.RunningOf(Id) is { Day: 1, Site: not null }, 40), "the server runs the Sechseläuten on its day and this client knows it");
        Expect(await Until(() => Me is { } me && me.IsOnFloor(), 30), "the player stands on the ground");
        Expect(await Until(() => Svc.SechseView is { Round: 1, Phase: 0 }, 30), "the server told this client the first round is open");
        Expect(await Until(() => BooggStage.Instance is { Built: true }, 30), "the pyre stands on this client");
        var site = Svc.RunningOf(Id)!.Site!;
        var stage = BooggStage.Instance!;
        var pyre = SechselaeutenRules.PyreAt(site);
        var weibel = SechselaeutenRules.WeibelAt(site);
        string? refused = null;
        Svc.Refused += why => refused = why;
        var raw = new List<string>();
        Svc.SechseRaw += json => raw.Add(json);
        void AtPyre() => Teleport(pyre.E, pyre.N + 6);
        const int Stake = SechselaeutenRules.Stake;

        if (_role == "A")
        {
            Expect(await Heard("B", "ready", 120), "B is on");
            Expect(await Heard("B", "refused", 150), "B was refused from afar");
            Teleport(weibel.E, weibel.N + 1.5);
            Expect(await Until(() => game.NearTarget == FestivalService.SechsePurse, 10), "A stands at the Weibel");
            await Seconds(1.0);   // the server learns where the body is from the movement packets: let it
            Expect(SechseGame.TryInteract(Me!), "A takes the purse");
            Expect(await Until(() => Mine.Batzen == SechselaeutenRules.PurseBatzen && Mine.Earned == SechselaeutenRules.PurseBatzen, 10), $"A is paid in its own section ({Mine.Batzen})");
            AtPyre();
            Expect(await Until(() => game.NearTarget == 0 && game.Betting, 10), "A stands at the pyre with the picker on");
            await Seconds(1.0);
            Expect(game.Bucket == SechselaeutenRules.DefaultBucket, "the picker starts at the median bucket");
            Expect(SechseGame.TryInteract(Me!), "A places the bet");
            Expect(await Until(() => Svc.SechseView is { Mine: 3 } && Mine.Batzen == SechselaeutenRules.PurseBatzen - Stake, 10), $"A's bet is on the median bucket and its stake gone ({Mine.Batzen})");
            Say("bet");
            Expect(await Heard("B", "bet", 60), "B has bet too");
            Expect(await Until(() => Svc.SechseView is { Bets: 2, Pool: 2 * Stake }, 10), $"the pool holds both stakes ({Svc.SechseView})");
            Expect(Svc.SechseView!.Board.Length == 0 && Svc.SechseView.Minutes == -1, "bets open: A is not told the other's bucket nor any time");
            // the admin sets the clock: the crossing of 18:00 lights the pyre
            if (!await LogIn("sechw")) return;
            Chat!.Send("/time set 17:59");
            Expect(await Until(() => Svc.SechseView is { Phase: 1 }, 60), "the server's sky crossing 18:00 lit the pyre");
            Say("lit");
            await WatchBurn(stage, raw);
            Expect(await Until(() => Svc.SechseView is { Phase: 2 }, 60), "the head bursts");
            await AfterBang(stage, Mine, 3);
            Say("banged");
            Expect(await Heard("B", "banged", 60), "B saw it too");
            Expect(game.NearTarget == 0, "A is still at the pyre");
            Expect(await Until(() => SechseGame.InteractionFor(Me!) is { } g && g.Text.Contains("Grill a Cervelat"), 10), "the embers offer a Cervelat");
            Expect(SechseGame.TryInteract(Me!), "A grills");
            Expect(await Until(() => CountOf(ItemId.BooggCervelat) == 1, 10), "A has its Cervelat");
            Say("grilled");
            Expect(await Heard("B", "grilled", 60), "B grilled too");
        }
        else
        {
            Say("ready");
            // forged: from 150 m away
            Teleport(pyre.E + 150, pyre.N + 150);
            await Seconds(1.0);
            Svc.AskSechse(FestivalService.SechseBet, 3);
            Expect(await Until(() => refused?.Contains("Go to the pyre") == true, 10), $"the bet from 150 m away is refused ({refused})");
            refused = null;
            Svc.AskSechse(FestivalService.SechsePurse);
            Expect(await Until(() => refused?.Contains("Zunft's Weibel") == true, 10), $"and so is the purse ({refused})");
            Expect(Mine.Earned == 0 && Svc.SechseView is { Bets: 0 }, "nothing was paid or bet");
            Say("refused");
            Teleport(weibel.E, weibel.N + 1.5);
            await Seconds(1.0);
            Svc.AskSechse(FestivalService.SechsePurse);
            Expect(await Until(() => Mine.Batzen == SechselaeutenRules.PurseBatzen, 10), $"B is paid its purse ({Mine.Batzen})");
            AtPyre();
            await Seconds(1.0);
            // the bet through the server's request on a far bucket (the picker's way is A's)
            Svc.AskSechse(FestivalService.SechseBet, 10);
            Expect(await Until(() => Svc.SechseView is { Mine: 10 } && Mine.Batzen == SechselaeutenRules.PurseBatzen - Stake, 10), $"B's bet is on 45-50 min ({Mine.Batzen})");
            refused = null;
            Svc.AskSechse(FestivalService.SechseBet, 4);
            Expect(await Until(() => refused?.Contains("You have bet") == true, 10), $"a second bet is refused ({refused})");
            Say("bet");
            Expect(await Heard("A", "lit", 120), "A lit the pyre by the clock");
            Expect(await Until(() => Svc.SechseView is { Phase: 1 }, 20), "B was told the pyre is lit");
            refused = null;
            Svc.AskSechse(FestivalService.SechseGrill);
            Expect(await Until(() => refused != null, 10), $"the fire is too hot to grill ({refused})");
            await WatchBurn(stage, raw);
            Expect(await Until(() => Svc.SechseView is { Phase: 2 }, 60), "the head bursts");
            await AfterBang(stage, Mine, 10);
            Say("banged");
            Expect(await Heard("A", "banged", 60), "A saw it too");
            Expect(await Until(() => SechseGame.InteractionFor(Me!) is { } g && g.Text.Contains("Grill a Cervelat"), 10), "the embers offer B a Cervelat");
            Expect(SechseGame.TryInteract(Me!), "B grills");
            Expect(await Until(() => CountOf(ItemId.BooggCervelat) == 1, 10), "B has its Cervelat");
            Say("grilled");
            Expect(await Heard("A", "grilled", 60), "A grilled too");
        }
        await Finish(2.0);
    }

    /// <summary>While the pyre burns: bets closed, the flames up, the head on, and not one message of the round has said the time.</summary>
    private async Task WatchBurn(BooggStage stage, List<string> raw)
    {
        var view = Svc.SechseView!;
        Expect(view.Minutes == -1 && view.DoneAt == 0 && view.Board.Length == 0, "burning: the view holds no minutes, no bang and no board");
        Expect(raw.Count > 0 && raw.All(j => j.Contains("\"minutes\":-1") && j.Contains("\"doneAt\":0")), $"every message of the round so far says minutes -1 ({raw.Count})");
        Expect(raw.All(j => !j.Contains("\"minutes\":8")), "none of them says the drawn time");
        refused = null;
        Svc.AskSechse(FestivalService.SechseBet, 2);
        Expect(await Until(() => refused != null, 10), $"a late bet is refused ({refused})");
        Expect(await Until(() => stage.Ramp > 0.1f || stage.BlastsPlayed > 0, 10), "the flames rise on this client");
    }

    private string? refused;

    /// <summary>After the bang: the time is told, the board names both bets, each is paid its share as the rules compute it, the stage blasted.</summary>
    private async Task AfterBang(BooggStage stage, FestivalInstance mine, int bucket)
    {
        var view = Svc.SechseView!;
        Expect(view.Minutes == 8 && view.DoneAt > 0, $"now the burn time is told ({view.Minutes})");
        Expect(view.Board.Length == 2 && view.Board.Any(r => r.Bucket == 3) && view.Board.Any(r => r.Bucket == 10), "the board names both bets");
        var bets = view.Board.Select((r, i) => new SechselaeutenRules.Bet(r.Name, r.Name, r.Bucket, Stake: 10, r.Bot)).ToList();
        var expected = SechselaeutenRules.Share(bets, 8);
        var row = view.Board.First(r => r.Bucket == bucket);
        Expect(row.Won == expected[row.Name] && view.Won == row.Won, $"the share is the rules' by closeness ({view.Won}, expected {expected[row.Name]})");
        Expect(view.Board.Sum(r => r.Won) == 2 * SechselaeutenRules.Stake, "the shares add up to the pool");
        Expect(view.Board.First(r => r.Bucket == 3).Won > view.Board.First(r => r.Bucket == 10).Won, "the nearer bet won more");
        int want = SechselaeutenRules.PurseBatzen - SechselaeutenRules.Stake + view.Won + SechselaeutenRules.PlayBatzen;
        Expect(await Until(() => mine.Batzen == want, 10), $"paid in its own section: purse - stake + share + the play's pay ({mine.Batzen}, expected {want})");
        Expect(mine.Earned == SechselaeutenRules.PurseBatzen + SechselaeutenRules.PlayBatzen, $"only the purse and the play count as earned ({mine.Earned})");
        Expect(await Until(() => stage.BlastsPlayed == 1 && !stage.HeadOn && stage.EmbersShown, 10), $"the head burst on this client's stage ({stage.BlastsPlayed})");
        Expect(view.Msg.Contains("burst after 8.0 minutes"), $"the line told to this player ({view.Msg})");
    }

}
