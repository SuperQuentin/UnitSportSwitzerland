using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// <c>--festivalnet a|b|b2|a2</c> with <c>--connect --festival test</c> (driven by <c>tools/festivalnetcheck.sh</c>, #1269): the
/// festival framework over a dedicated server whose calendar reads 11 March 2027 (day 2 of the check-only festival), every
/// assertion about what reached the REMOTE peer.
/// <list type="bullet">
/// <item><c>a</c> (Lea) and <c>b</c> (Tom) hear the running list (day 2, the fixture start as the site), form a party, and play the
/// bell: A twice, then B three times. Both members' points land on one board that each sees (A reads B's contribution and the
/// reverse); the Warm-up milestone (150) paid only A, who had contributed alone; the Crowd milestone (400) paid both, and each
/// sees its own paid in its section. B leaves with its Batzen. A moves the server's calendar past the end: A, online, is paid its
/// Batzen as francs at once.</item>
/// <item><c>b2</c> (Tom again, the same <c>user://</c>) joins after the end: its Batzen are converted when its progress loads.</item>
/// <item><c>a2</c> (Lea again) joins: nothing is converted twice. The script counts the server's bank lines.</item>
/// </list>
/// </summary>
public partial class FestivalNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--festivalnet");

    private const string Instance = "test-2027";

    public FestivalNetProbe(ItemController items) : base(items, "festivalnet", "FS", "festivalnet_") { }
    public FestivalNetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault(Instance) ?? new FestivalInstance();

    private int _rings;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null)) return;
        // this player's progress is loaded on the server once its first section arrives
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached it");
        Svc.Rang += (_, count, _) => _rings++;
        switch (_role)
        {
            case "A": await A(); break;
            case "B": await B(); break;
            case "B2": await B2(); break;
            default: await A2(); break;
        }
        await Finish(3.0);
    }

    private async Task<bool> Running(int day, double seconds = 30) =>
        await Until(() => Svc.RunningOf(FestivalCatalog.TestId) is { } r && r.Instance == Instance && r.Day == day, seconds);

    /// <summary>Three rings of a game, each waiting for the server's echo: one play.</summary>
    private async Task<bool> Play(string game)
    {
        for (int i = 0; i < FestivalCatalog.Rings; i++)
        {
            int before = _rings;
            Svc.AskRing(game);
            if (!await Until(() => _rings > before, 10)) return false;
        }
        return true;
    }

    private static bool Board(int points, params int[] taken) =>
        Svc.BoardOf(FestivalCatalog.TestId) is { } v && v.Points == points && v.Taken.OrderBy(x => x).SequenceEqual(taken);

    private async Task A()
    {
        long balance = Bank.Instance?.Balance ?? 0;
        Expect(await Running(2), "the server runs the test festival on day 2");
        var run = Svc.RunningOf(FestivalCatalog.TestId)!;
        Expect(run.Site is { Relocated: false } && run.Site.Text == "Test festival at the test field", $"held at the real site ({run.Site?.Text})");
        Expect(run.Length == 5 && run.End == new DateOnly(2027, 3, 14), $"five days to 14 March ({run.Length}, {run.End})");
        Say("ready");
        Expect(await Heard("B", "ready", 120), "B is on");

        // a party: the crew is its two members
        Chat!.Send("/party invite Tom");
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "A's party holds A and B");
        Say("party");

        // the gong is closed on day 2, on the server's word
        string? refused = null;
        Svc.Refused += why => refused = why;
        Svc.AskRing("gong");
        Expect(await Until(() => refused != null, 10) && refused!.Contains("opens on day 3"), $"the gong is closed ({refused})");

        Expect(await Play("bell"), "the first play");
        Expect(await Until(() => Mine.Batzen == 40 && Mine.Points == 100, 10), $"A: 40 Batzen, 100 points ({Mine.Batzen}/{Mine.Points})");
        Expect(await Play("bell"), "the second play");
        Expect(await Until(() => Mine.Batzen == 100 && Mine.Milestones.SequenceEqual([0]), 10), $"A: 80 earned + the Warm-up milestone's 20 ({Mine.Batzen})");
        Expect(await Until(() => Board(200, 0), 10), "A's board: 200 points, the first milestone reached");
        Say("played");

        // B plays on the same board: A reads B's points from the server
        Expect(await Heard("B", "done", 120), "B finished its plays");
        Expect(await Until(() => Board(500, 0, 1), 15), $"A's board holds B's points too: 500 and both milestones ({Svc.BoardOf(FestivalCatalog.TestId)?.Points})");
        var view = Svc.BoardOf(FestivalCatalog.TestId)!;
        Expect(view.Members.Length == 2 && view.Members.Any(m => m.Name == "Tom" && m.Points == 300) && view.Members.Any(m => m.Name == "Lea" && m.Points == 200),
            $"it names both: {string.Join(", ", view.Members.Select(m => $"{m.Name} {m.Points}"))}");
        Expect(await Until(() => Mine.Batzen == 140 && Mine.Milestones.SequenceEqual([0, 1]) && Mine.Earned == 80, 10),
            $"the Crowd milestone paid A too, from B's points: 140 held ({Mine.Batzen}, earned {Mine.Earned})");

        // B goes away holding its Batzen: the festival must end while it is offline
        Expect(await Heard("B", "finished", 60), "B is leaving");
        Expect(await Until(() => Parties.Client is not { Members.Count: 2 }, 20), "B has left (its party is gone)");
        await Seconds(1.0);

        // the end, online: the Batzen become francs at once
        var converted = new List<(string, int, int)>();
        Svc.Converted += (t, b, c) => converted.Add((t, b, c));
        Svc.AskTestDate(new DateOnly(2027, 3, 15));
        Expect(await Until(() => Svc.Running.Count == 0, 20), "the festival is over for the client too");
        Expect(await Until(() => converted.Count == 1, 15) && converted[0] == ("Test festival", 140, 140), $"A is told: 140 Batzen as CHF 140 ({string.Join(",", converted)})");
        Expect(await Until(() => Bank.Instance?.Balance == balance + 140, 10), $"CHF 140 in A's account ({balance} -> {Bank.Instance?.Balance})");
        Expect(await Until(() => Mine is { Converted: true, Batzen: 0, ConvertedChf: 140 }, 10), "A's record is marked paid, no Batzen left");
        Say("ended");
    }

    private async Task B()
    {
        Expect(await Running(2), "the server runs the test festival on day 2");
        Say("ready");
        Expect(await Heard("A", "ready", 120), "A is on");
        Expect(await Until(() => Invites().Incoming.Any(v => v.Kind == Parties.Kind), 40), "B is invited to A's party");
        if (Invites().Incoming.FirstOrDefault(v => v.Kind == Parties.Kind) is { } invite) Invites().Accept(invite.Id);
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "B's party holds A and B");
        Expect(await Heard("A", "played", 120), "A played twice");
        // A's two plays are on B's board already: the crew score is the party's, not B's
        Expect(await Until(() => Board(200, 0), 15), $"B sees A's 200 points and the first milestone on the shared board ({Svc.BoardOf(FestivalCatalog.TestId)?.Points})");
        Expect(Mine.Batzen == 0 && Mine.Milestones.Count == 0, "but B holds nothing: the Warm-up milestone paid only who had contributed");

        Expect(await Play("bell"), "B's first play");
        Expect(await Until(() => Mine.Batzen == 40 && Board(300, 0), 10), $"B: 40 Batzen; the board 300 ({Mine.Batzen})");
        Expect(await Play("bell"), "B's second play: the crew reaches 400");
        Expect(await Until(() => Mine.Batzen == 120 && Mine.Milestones.SequenceEqual([1]) && Board(400, 0, 1), 10),
            $"the Crowd milestone paid B its 40 on top of 80 earned ({Mine.Batzen})");
        Expect(await Play("bell"), "B's third play fills the bell's daily cap");
        Expect(await Until(() => Mine.Batzen == 140 && Mine.Earned == 100 && Board(500, 0, 1), 10), $"B: 140 held, 100 earned ({Mine.Batzen}/{Mine.Earned})");
        var view = Svc.BoardOf(FestivalCatalog.TestId)!;
        Expect(view.Members.Any(m => m.Name == "Lea" && m.Points == 200) && view.Members.Any(m => m.Name == "Tom" && m.Points == 300), "B's board names both members");
        Say("done");
        GD.Print($"{Log} leaving with {Mine.Batzen} Batzen");
    }

    private async Task B2()
    {
        // the calendar is past the end: nothing runs, and this player's Batzen were converted as its progress loaded
        Expect(await Until(() => Mine is { Converted: true }, 30), $"B's Batzen were converted on load ({Mine.Converted}, {Mine.Batzen})");
        Expect(Mine is { Batzen: 0, ConvertedChf: 140 }, $"CHF 140, nothing left ({Mine.ConvertedChf}/{Mine.Batzen})");
        Expect(await Until(() => Bank.Instance?.Balance >= 140, 10), $"CHF 140 in B's account ({Bank.Instance?.Balance})");
        Expect(Svc.Running.Count == 0, "no festival runs");
        long balance = Bank.Instance?.Balance ?? 0;
        await Until(() => false, 2.0);
        Expect(Bank.Instance?.Balance == balance && Mine.ConvertedChf == 140, "and nothing more is paid");
    }

    private async Task A2()
    {
        // A was paid online at the end; this load pays nothing again
        Expect(await Until(() => Mine is { Converted: true }, 30), "A's record is marked paid");
        long balance = Bank.Instance?.Balance ?? 0;
        Expect(balance >= 140, $"A's account holds the CHF 140 ({balance})");
        await Until(() => false, 2.5);
        Expect(Mine is { Batzen: 0, ConvertedChf: 140 } && Bank.Instance?.Balance == balance, $"loading again converted nothing ({Mine.ConvertedChf}, {Bank.Instance?.Balance})");
    }

    private static InviteService Invites() => InviteService.Client!;
}
