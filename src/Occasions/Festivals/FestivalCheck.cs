using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// <c>--festivalcheck --festival test --date 2027-03-11 --world fixture --systems ui,physics,occasions,adventure</c> (quick tier,
/// #1269): offline, this client plays the server's part. The check-only festival runs on its day 2 at the fixture's start (the real
/// site, not relocated); the bell is open and the gong is not; three rings are a play that pays Batzen and points; the
/// daily cap cuts the fourth play, the party score crosses its first two milestones and they pay outside the cap; the next
/// day opens the gong; the board is written to disk; the Journal's Festival tab says all of it (and is saved to
/// <c>test_output/festival_journal.png</c> when windowed); the date moves past the end and the Batzen become francs in the account
/// exactly once; the next year starts clean and keeps last year as a record. Its progress lives in <c>test_output/festivalcheck</c>
/// and the bank writes nothing (<see cref="Bank.Scratch"/>).
/// </summary>
public partial class FestivalCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--festivalcheck");

    private readonly Func<FootPlayer?> _me;

    public FestivalCheck(ItemController items, Func<FootPlayer?> me) : base(items, "festivalcheck", "", "festival_") => _me = me;
    public FestivalCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static FestivalService Service => FestivalService.Instance!;
    private static FestivalInstance Inst(string instance) => Service.Mine.Instances.GetValueOrDefault(instance) ?? new FestivalInstance();

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && Me is { } me && me.IsOnFloor() && Service.Running.Count > 0, 90))
        {
            Fail($"no festival running (service {FestivalService.Instance != null}, running {FestivalService.Instance?.Running.Count}, body {Me?.Global})");
            return;
        }
        var svc = Service;
        var run = svc.RunningOf(FestivalCatalog.TestId);
        Expect(run != null, "the test festival runs");
        if (run == null) { await Finish(0); return; }
        GD.Print($"[festivalcheck] {run.Title}, day {run.Day} of {run.Length}, {run.Site?.Text}");

        // ---- day 2, at the fixture's start ---------------------------------------------------------
        Expect(run.Instance == "test-2027" && run.Day == 2 && run.Length == 5, $"day 2 of 5 of test-2027 ({run.Instance} {run.Day}/{run.Length})");
        var (startE, startN) = svc.Start();
        Expect(run.Site is { Relocated: false } s && Math.Abs(s.E - startE) < 1 && Math.Abs(s.N - startN) < 1,
            $"held at the real site, the fixture start ({run.Site})");
        Expect(run.Site?.Text == "Test festival at the test field", $"the Journal's line ({run.Site?.Text})");
        Expect(FestivalOccasion.SiteOf(FestivalCatalog.TestId) == run.Site, "the occasion knows the site (the poles stand there)");
        var occasions = OccasionManager.Instance;
        Expect(occasions?.IsActive(FestivalCatalog.TestId, OccasionFacets.Games) == true, "the Games facet runs");
        var def = FestivalCatalog.Get(FestivalCatalog.TestId)!;
        Expect(def.IsOpen("bell", run.Day) && !def.IsOpen("gong", run.Day), "the bell is open, the gong is not (day 2)");
        const string Instance = "test-2027";
        long balance = Bank.Instance?.Balance ?? 0;

        // ---- a play: three rings -------------------------------------------------------------------
        var rang = new List<(string Game, int Count, int Batzen)>();
        string? refused = null;
        svc.Rang += (g, c, b) => rang.Add((g, c, b));
        svc.Refused += why => refused = why;

        svc.AskRing("gong");
        Expect(refused?.Contains("opens on day 3") == true && Inst(Instance).Batzen == 0, $"the gong is closed on day 2 ({refused})");
        svc.AskRing("bell");
        svc.AskRing("bell");
        Expect(rang.Count == 2 && rang[1].Count == 2 && Inst(Instance).Batzen == 0, "two rings are not a play yet");
        svc.AskRing("bell");
        Expect(Inst(Instance).Batzen == 40 && Inst(Instance).Points == 100 && rang[^1] == ("bell", 0, 40), $"three rings: 40 Batzen, 100 points ({Inst(Instance).Batzen}/{Inst(Instance).Points})");
        Expect(svc.BoardOf(FestivalCatalog.TestId) is { Points: 100 }, "the board shows the crew's 100 points");

        Play("bell");   // 80 Batzen, 200 points: the first milestone (150) pays 20
        Expect(Inst(Instance).Batzen == 100 && Inst(Instance).Milestones.SequenceEqual([0]), $"a second play: 80 earned + the Warm-up milestone's 20 ({Inst(Instance).Batzen})");
        Play("bell");   // the day's cap for the bell is 100: 20 more
        Expect(Inst(Instance).Earned == 100 && Inst(Instance).Batzen == 120, $"the third play fills the bell's daily cap of 100 ({Inst(Instance).Earned}/{Inst(Instance).Batzen})");
        Play("bell");   // nothing more today, but the points count: 400 = the second milestone (+40, outside the cap)
        Expect(Inst(Instance).Earned == 100 && Inst(Instance).Batzen == 160 && Inst(Instance).Points == 400,
            $"a fourth play pays no Batzen but 100 points; the Crowd milestone pays 40 ({Inst(Instance).Earned}/{Inst(Instance).Batzen}/{Inst(Instance).Points})");
        Expect(Inst(Instance).Milestones.SequenceEqual([0, 1]), "both milestones paid");
        Expect(Service.BoardOf(FestivalCatalog.TestId) is { Points: 400 } v && v.Taken.SequenceEqual([0, 1]), "the board holds 400 points and both milestones");
        Expect(svc.CrewPoints(AdventureService.LocalPeer, FestivalCatalog.TestId) == 400, "the server's crew score is 400");

        // ---- the Journal's tab ---------------------------------------------------------------------
        var page = new FestivalPage();
        AddChild(page);
        await Seconds(0.3);
        string lines = string.Join("\n", page.Lines);
        Expect(lines.Contains("Day 2 of 5") && lines.Contains("Test festival at the test field"), "the tab names the day and the place");
        Expect(lines.Contains("Ring the bell") && lines.Contains("open") && lines.Contains("Ring the gong · tomorrow"), "the day table: the bell open, the gong tomorrow");
        Expect(lines.Contains("closing show: bonfires"), "the closing show is listed");
        Expect(lines.Contains("400 points") && lines.Contains("Warm-up") && lines.Contains("Crowd · 400 points") && lines.Contains("Whole village"), "the party score and the milestones");
        Expect(lines.Contains("160 Batzen held") && lines.Contains("worth CHF 160"), "the Batzen held and their worth");
        page.QueueFree();
        if (DisplayServer.GetName() != "headless")
        {
            AdventureLog.Open(FestivalPage.TabId);
            await Seconds(1.5);
            GD.Print($"[festivalcheck] shot {Shot("journal")}");
            GameShell.Instance?.Back();
        }

        // ---- the next day: the gong ----------------------------------------------------------------
        occasions!.OverrideDate(new DateOnly(2027, 3, 12));
        Expect(await Until(() => svc.RunningOf(FestivalCatalog.TestId)?.Day == 3, 15), "the next day opens: day 3");
        Play("gong");
        Expect(Inst(Instance).Earned == 160 && Inst(Instance).Batzen == 220 && Inst(Instance).Points == 550, $"the gong plays: +60 Batzen, +150 points ({Inst(Instance).Earned}/{Inst(Instance).Batzen}/{Inst(Instance).Points})");
        Expect(Inst(Instance).Best["gong"] == 150 && Inst(Instance).Best["bell"] == 100, "the best of each game");
        Expect(svc.BoardOf(FestivalCatalog.TestId) is { Points: 550 }, "the board: 550");

        // ---- the board on disk, as the server keeps it ----------------------------------------------
        svc.SaveBoards();
        string file = FestivalService.BoardPath(Instance);
        try
        {
            var back = FestivalBoard.FromJson(System.IO.File.ReadAllText(file));
            Expect(back.Crews.Values.Sum(c => c.Points) == 550 && back.Crews.Count == 1, $"the board file holds the crew's 550 points ({file})");
        }
        catch (Exception e) { Expect(false, $"the board file reads back ({file}: {e.Message})"); }

        // ---- the end: Batzen become francs, once -----------------------------------------------------
        var converted = new List<(string, int, int)>();
        svc.Converted += (t, b, c) => converted.Add((t, b, c));
        occasions.OverrideDate(new DateOnly(2027, 3, 15));
        Expect(await Until(() => svc.Running.Count == 0, 15), "the festival is over: nothing runs");
        Expect(await Until(() => converted.Count == 1, 10) && converted[0] == ("Test festival", 220, 220), $"220 Batzen announced as CHF 220 ({string.Join(",", converted)})");
        Expect(Bank.Instance?.Balance == balance + 220, $"CHF 220 in the account ({balance} -> {Bank.Instance?.Balance})");
        Expect(Inst(Instance) is { Converted: true, Batzen: 0, ConvertedChf: 220 }, "the instance is marked paid and holds no Batzen");
        Expect(svc.ConvertFor(AdventureService.LocalPeer) == 0 && Bank.Instance?.Balance == balance + 220, "converting again pays nothing");
        occasions.OverrideDate(new DateOnly(2027, 3, 20));
        await Seconds(0.5);
        Expect(converted.Count == 1 && Bank.Instance?.Balance == balance + 220, "a later day pays nothing either");
        Expect(svc.Award(AdventureService.LocalPeer, FestivalCatalog.TestId, 50, "late", "bell", 10) == 0, "after the end an award is refused");
        page = new FestivalPage();
        AddChild(page);
        await Seconds(0.3);
        Expect(string.Join("\n", page.Lines).Contains("Test festival 2027") && string.Join("\n", page.Lines).Contains("CHF 220"), "the Journal keeps a card for it");
        page.QueueFree();

        // ---- next year -------------------------------------------------------------------------------
        occasions.OverrideDate(new DateOnly(2028, 3, 10));
        Expect(await Until(() => svc.RunningOf(FestivalCatalog.TestId) is { Instance: "test-2028", Day: 1 }, 15), "next year is a new instance on its day 1");
        Expect(Inst("test-2028").Batzen == 0 && Inst("test-2027").ConvertedChf == 220, "it starts with nothing and last year stays a record");
        AdventureService.Instance!.SaveAll();
        SaveQueue.Flush();
        await Finish(0);
    }

    /// <summary>Three rings of a game, a play.</summary>
    private static void Play(string game)
    {
        for (int i = 0; i < FestivalCatalog.Rings; i++) Service.AskRing(game);
    }
}
