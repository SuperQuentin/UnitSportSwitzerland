using System.Text.Json.Nodes;
using UnitSport.Adventure;
using UnitSport.Occasions.Festivals;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The festival framework's rules (#1269 part 1): the definition, site resolution and relocation, the Batzen caps and their
/// conversion, the party board's keys and milestones, the economy (src/Occasions/Festivals/*, linked in).
/// </summary>
public class FestivalFrameworkTests
{
    private static FestivalDef Test() => FestivalCatalog.Test(2600000, 1200000);

    // ---- the definition --------------------------------------------------------------------------

    [Fact]
    public void The_test_festival_is_sound()
    {
        var def = Test();
        Assert.Empty(def.Problems());
        Assert.Equal(5, def.Length);
        Assert.Equal(FestivalDef.DefaultTokenCap, def.TokenCap);
    }

    [Fact]
    public void Every_registered_festival_is_sound()
    {
        foreach (var def in FestivalCatalog.All) Assert.Empty(def.Problems());
    }

    [Fact]
    public void Games_open_by_day_and_stay_open()
    {
        var def = Test();
        Assert.True(def.IsOpen("bell", 1));
        Assert.False(def.IsOpen("gong", 1));
        Assert.False(def.IsOpen("gong", 2));
        Assert.True(def.IsOpen("gong", 3));
        Assert.True(def.IsOpen("gong", 5));   // a late player meets every game at once
        Assert.False(def.IsOpen("nonsense", 5));
        Assert.Equal(["bell", "alphorn"], def.OpenGames(2).Select(g => g.Id));   // the alphorn game (#1269 part 4) opens with the bell
        Assert.Equal(["bell", "gong", "alphorn"], def.OpenGames(4).Select(g => g.Id));
        Assert.Equal(3, def.NextOpening(1));
        Assert.Equal(5, def.NextOpening(3));   // the stone put (--steincheck) opens on the last day
        Assert.Equal(0, def.NextOpening(5));
        Assert.Equal(1, def.DayRow(2)!.From);
        Assert.Equal(3, def.DayRow(4)!.From);
    }

    [Fact]
    public void A_broken_definition_says_what_is_wrong()
    {
        var def = Test() with
        {
            Days = [new FestivalDay(2, ["bell"], ""), new FestivalDay(2, ["ghost"], "")],
            Milestones = [new FestivalMilestone(500, 400, "a"), new FestivalMilestone(100, 5, "b")],
        };
        var problems = def.Problems();
        Assert.Contains(problems, p => p.Contains("'ghost'"));
        Assert.Contains(problems, p => p.Contains("not in order"));
        Assert.Contains(problems, p => p.Contains("day 1"));
        Assert.Contains(problems, p => p.Contains("quarter"));
    }

    // ---- sites and relocation -------------------------------------------------------------------

    private static readonly FestivalDef Alp = new("alp", "Alplerfest",
        [new FestivalSite("Interlaken", 2628000, 1170000, 300), new FestivalSite("Schwaegalp", 2742000, 1237000, 300)],
        [new FestivalGame("a", "A")], [new FestivalDay(1, ["a"], "")], ClosingShow.Fireworks,
        [("08-14", "08-24")], []);

    private static readonly TownRef[] Towns =
    [
        new("Bulle", 2570000, 1163000, 900),
        new("Charmey", 2578750, 1163750, 300),
        new("Gruyeres", 2572000, 1160000, 120),   // too small to hold a festival
        new("Aaville", 2560000, 1160000, 900),    // ties Bulle on buildings: the name decides
    ];

    [Fact]
    public void The_real_site_is_used_when_the_region_holds_it()
    {
        var site = FestivalSites.Resolve(Alp, (e, n) => e > 2600000, Towns)!;
        Assert.False(site.Relocated);
        Assert.Equal("Interlaken", site.Name);
        Assert.Equal("Alplerfest at Interlaken", site.Text);
    }

    [Fact]
    public void A_fallback_site_is_used_before_a_relocation()
    {
        var site = FestivalSites.Resolve(Alp, (e, n) => e > 2700000, Towns)!;
        Assert.False(site.Relocated);
        Assert.Equal("Schwaegalp", site.Name);
    }

    [Fact]
    public void A_festival_outside_the_region_moves_to_the_largest_town()
    {
        var site = FestivalSites.Resolve(Alp, (e, n) => e < 2600000, Towns)!;
        Assert.True(site.Relocated);
        Assert.Equal("Aaville", site.Name);                       // 900 buildings, first by name
        Assert.Equal("Alplerfest, held this year in Aaville", site.Text);
        Assert.Equal(300, site.RadiusM);
        Assert.Null(site.PoiId);
    }

    [Fact]
    public void Relocation_only_picks_a_town_the_region_holds_with_enough_buildings()
    {
        // the region holds only the east of Bulle: Aaville is out; Gruyeres is in but too small
        var site = FestivalSites.Resolve(Alp, (e, n) => e >= 2570000 && e < 2600000, Towns)!;
        Assert.Equal("Bulle", site.Name);
        Assert.Null(FestivalSites.Resolve(Alp, (e, n) => e is >= 2571000 and < 2573000, Towns));   // only Gruyeres: not held here
        Assert.Null(FestivalSites.Resolve(Alp, (e, n) => false, Towns));
    }

    [Fact]
    public void The_minimum_is_ten_times_the_occasion_towns()
    {
        Assert.Equal(200, FestivalSites.MinBuildings);
        var onlyTwoHundred = new[] { new TownRef("Small", 1, 1, 199), new TownRef("Edge", 2, 2, 200) };
        Assert.Equal("Edge", FestivalSites.Resolve(Alp, (e, n) => e < 10, onlyTwoHundred)!.Name);
    }

    // ---- Batzen: caps, conversion ---------------------------------------------------------------

    [Fact]
    public void A_game_pays_up_to_its_daily_cap()
    {
        var def = Test();
        var inst = new FestivalInstance { Festival = "test" };
        Assert.Equal(40, FestivalRules.Earn(def, inst, "bell", 1, 40, 100));
        Assert.Equal(40, FestivalRules.Earn(def, inst, "bell", 1, 40, 100));
        Assert.Equal(20, FestivalRules.Earn(def, inst, "bell", 1, 40, 100));   // the 100 of the day, no more
        Assert.Equal(0, FestivalRules.Earn(def, inst, "bell", 1, 40, 100));
        Assert.Equal(100, inst.Batzen);
        Assert.Equal(100, inst.Paid["bell:1"]);
        Assert.Equal(4, inst.Played["bell:1"]);
        Assert.Equal(400, inst.Points);          // the points are never cut
        Assert.Equal(100, inst.Best["bell"]);
        Assert.Equal(40, FestivalRules.Earn(def, inst, "bell", 2, 40, 100));   // tomorrow the cap is fresh
    }

    [Fact]
    public void The_festival_cap_counts_what_games_earned_and_not_what_was_spent()
    {
        var def = Test() with { TokenCap = 120 };
        var inst = new FestivalInstance { Festival = "test" };
        Assert.Equal(100, FestivalRules.Earn(def, inst, "bell", 1, 100, 10));
        inst.Batzen -= 80;   // spent at a stall (a later part)
        Assert.Equal(20, FestivalRules.Earn(def, inst, "gong", 3, 100, 10));
        Assert.Equal(0, FestivalRules.Earn(def, inst, "gong", 3, 100, 10));
        Assert.Equal(120, inst.Earned);
        Assert.Equal(40, inst.Batzen);
    }

    [Fact]
    public void A_negative_award_pays_nothing()
    {
        var inst = new FestivalInstance();
        Assert.Equal(0, FestivalRules.Earn(Test(), inst, "bell", 1, -50, 0));
        Assert.Equal(0, inst.Batzen);
    }

    [Fact]
    public void Games_alone_can_reach_the_cap_and_no_further()
    {
        var def = Test();
        var inst = new FestivalInstance { Festival = "test" };
        for (int day = 1; day <= def.Length; day++)
            foreach (var g in def.OpenGames(day))
                for (int i = 0; i < 10; i++) FestivalRules.Earn(def, inst, g.Id, day, 60, 100);
        Assert.Equal(def.TokenCap, inst.Batzen);
    }

    [Fact]
    public void A_milestone_pays_a_member_once_outside_the_cap()
    {
        var def = Test() with { TokenCap = 60 };
        var inst = new FestivalInstance { Festival = "test" };
        FestivalRules.Earn(def, inst, "bell", 1, 60, 0);
        Assert.True(FestivalRules.PayMilestone(inst, 0, 20));
        Assert.False(FestivalRules.PayMilestone(inst, 0, 20));
        Assert.Equal(80, inst.Batzen);
        Assert.Equal(60, inst.Earned);   // the cap did not move
    }

    [Fact]
    public void An_ended_instance_converts_once_at_one_franc_a_batzen()
    {
        var book = new FestivalBook();
        book.Of("test-2026", "test").Batzen = 120;
        book.Of("test-2027", "test").Batzen = 45;
        Func<string, bool> ended = k => k == "test-2026";
        var first = FestivalRules.Convert(book, ended);
        Assert.Equal([("test-2026", "test", 120)], first);
        Assert.True(book.Instances["test-2026"].Converted);
        Assert.Equal(0, book.Instances["test-2026"].Batzen);
        Assert.Equal(120, book.Instances["test-2026"].ConvertedChf);
        Assert.False(book.Instances["test-2027"].Converted);                 // still running: kept
        Assert.Empty(FestivalRules.Convert(book, ended));                     // a second load pays nothing
        Assert.Equal(45, FestivalRules.Waiting(book));
        var later = FestivalRules.Convert(book, _ => true);                   // a player away for a year
        Assert.Equal([("test-2027", "test", 45)], later);
        Assert.Equal(0, FestivalRules.Waiting(book));
    }

    [Fact]
    public void A_milestone_paid_after_the_conversion_converts_again_and_the_record_adds_up()
    {
        var book = new FestivalBook();
        var inst = book.Of("test-2027", "test");
        inst.Batzen = 100;
        Assert.Single(FestivalRules.Convert(book, _ => true));
        // owed to a member who was away (the service puts it back as unconverted)
        inst.Batzen += 40;
        inst.Converted = false;
        Assert.Equal([("test-2027", "test", 40)], FestivalRules.Convert(book, _ => true));
        Assert.Equal(140, inst.ConvertedChf);
        Assert.Empty(FestivalRules.Convert(book, _ => true));
    }

    [Fact]
    public void The_book_round_trips_through_the_progress_store()
    {
        var p = new PlayerProgress("k");
        var book = p.Get(FestivalBook.Section);
        var inst = book.Of("test-2027", "test");
        FestivalRules.Earn(Test(), inst, "bell", 1, 40, 100);
        FestivalRules.PayMilestone(inst, 0, 20);
        p.Touch(FestivalBook.Section);
        var back = AdventureBook.FromJson(AdventureBook.ToNode(p).ToJsonString()).Get(FestivalBook.Section).Instances["test-2027"];
        Assert.Equal(60, back.Batzen);
        Assert.Equal(100, back.Points);
        Assert.Equal(100, back.Best["bell"]);
        Assert.Equal([0], back.Milestones);
        Assert.Equal("festivals", FestivalBook.Section.Id);
    }

    // ---- the party board ------------------------------------------------------------------------

    [Fact]
    public void A_crew_is_its_sorted_keys()
    {
        Assert.Equal("a+b+c", FestivalBoard.CrewKey(["c", "a", "b", "a"]));
        Assert.Equal("a", FestivalBoard.CrewKey(["a"]));
        Assert.Equal("a+b", FestivalBoard.CrewKey(["b", "", "a"]));
    }

    [Fact]
    public void Both_members_points_land_on_one_entry_whatever_the_order()
    {
        var def = Test();
        var board = new FestivalBoard { Instance = "test-2027", Festival = "test" };
        board.Add(def, ["alice", "bob"], "alice", "Alice", 100);
        board.Add(def, ["bob", "alice"], "bob", "Bob", 30);
        Assert.Single(board.Crews);
        var crew = board.Find(["bob", "alice"])!;
        Assert.Equal(130, crew.Points);
        Assert.Equal(100, crew.Contributed["alice"]);
        Assert.Equal(30, crew.Contributed["bob"]);
        Assert.Equal("Bob", crew.Names["bob"]);
    }

    [Fact]
    public void A_party_that_re_forms_finds_its_score_and_another_one_does_not()
    {
        var def = Test();
        var board = new FestivalBoard();
        board.Add(def, ["alice", "bob"], "alice", "Alice", 90);
        Assert.Equal(90, board.Find(["bob", "alice"])!.Points);
        Assert.Null(board.Find(["alice", "carol"]));
        Assert.Null(board.Find(["alice"]));   // a solo run is a crew of one, with its own score
        board.Add(def, ["alice"], "alice", "Alice", 10);
        Assert.Equal(10, board.Find(["alice"])!.Points);
        Assert.Equal(2, board.Crews.Count);
    }

    [Fact]
    public void A_milestone_pays_every_member_who_contributed_and_only_once()
    {
        var def = Test();   // 150 points: 20 Batzen
        var board = new FestivalBoard();
        Assert.Empty(board.Add(def, ["alice", "bob", "carol"], "alice", "Alice", 100));
        Assert.Empty(board.Add(def, ["alice", "bob", "carol"], "bob", "Bob", 40));
        var hits = board.Add(def, ["alice", "bob", "carol"], "alice", "Alice", 20);
        var hit = Assert.Single(hits);
        Assert.Equal(0, hit.Index);
        Assert.Equal(20, hit.Milestone.Batzen);
        Assert.Equal(["alice", "bob"], hit.Payees);               // Carol was there but contributed nothing
        Assert.Empty(board.Add(def, ["alice", "bob", "carol"], "bob", "Bob", 10));   // not again
        // a guest who joins in later and contributes is paid from the next milestone on
        var next = board.Add(def, ["alice", "bob", "carol"], "carol", "Carol", 300);
        Assert.Equal(1, Assert.Single(next).Index);
        Assert.Equal(["alice", "bob", "carol"], next[0].Payees);
    }

    [Fact]
    public void One_big_play_can_cross_several_milestones()
    {
        var board = new FestivalBoard();
        var hits = board.Add(Test(), ["alice"], "alice", "Alice", 900);
        Assert.Equal([0, 1, 2], hits.Select(h => h.Index));
        Assert.Equal([0, 1, 2], board.Find(["alice"])!.Taken);
    }

    [Fact]
    public void Points_of_zero_or_a_stranger_change_nothing()
    {
        var board = new FestivalBoard();
        Assert.Empty(board.Add(Test(), ["alice"], "alice", "Alice", 0));
        Assert.Empty(board.Add(Test(), ["alice"], "", "", 50));
        Assert.Empty(board.Crews);
    }

    [Fact]
    public void A_member_offline_when_a_milestone_paid_is_owed_it_once()
    {
        var board = new FestivalBoard();
        board.Owe("bob", 20);
        board.Owe("bob", 40);
        board.Owe("carol", 0);
        Assert.Equal(60, board.TakeOwed("bob"));
        Assert.Equal(0, board.TakeOwed("bob"));
        Assert.Equal(0, board.TakeOwed("carol"));
    }

    [Fact]
    public void The_board_file_round_trips()
    {
        var board = new FestivalBoard { Instance = "test-2027", Festival = "test" };
        board.Add(Test(), ["alice", "bob"], "alice", "Alice", 160);
        board.Owe("bob", 20);
        var back = FestivalBoard.FromJson(board.ToJson());
        Assert.Equal("test-2027", back.Instance);
        var crew = back.Find(["bob", "alice"])!;
        Assert.Equal(160, crew.Points);
        Assert.Equal([0], crew.Taken);
        Assert.Equal("Alice", crew.Names["alice"]);
        Assert.Equal(20, back.Owed["bob"]);
        Assert.NotNull(FestivalBoard.FromJson("{}"));
    }

    // ---- the economy ----------------------------------------------------------------------------

    [Fact]
    public void A_festival_is_worth_about_six_hundred_francs_and_never_a_travelcard()
    {
        // 1 Batzen = CHF 1 (decided in #1270). Reference prices in assets/adventure/fares.json: the half-fare
        // card CHF 190 (2026), the GA CHF 3 995; the whole starter quest catalogue pays CHF 440 in cash.
        const int halfFare = 190, ga = 3995, catalogue = 440;
        var def = Test();
        int most = def.TokenCap + def.MilestoneTotal;
        Assert.Equal(600, def.TokenCap);
        Assert.InRange(most, 600, 750);
        Assert.True(most >= 3 * halfFare, "a festival pays about three half-fare cards");
        Assert.True(most * 5 < ga, "five festivals do not buy a GA");
        Assert.True(most < 2 * catalogue, "and it stays under twice the starter quests' cash");
    }
}
