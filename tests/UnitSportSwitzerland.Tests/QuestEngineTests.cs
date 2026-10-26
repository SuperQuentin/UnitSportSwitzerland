using System.Collections.Generic;
using System.Linq;
using UnitSport.Adventure;
using UnitSport.Items;
using Xunit;
using static UnitSport.Adventure.QuestCatalog;

namespace UnitSport.Tests;

/// <summary>The quest rules (src/Adventure/Quests/QuestEngine.cs, linked in, #1223).</summary>
public class QuestEngineTests
{
    private const long Now = 1_793_000_000;

    private sealed class Source : IQuestSource
    {
        private readonly QuestDef[] _all;
        public Source(params QuestDef[] all) => _all = all;
        public string Id => "test";
        public IEnumerable<QuestDef> For(QuestContext ctx) => _all;
        public QuestDef? Resolve(string questId) => _all.FirstOrDefault(q => q.Id == questId);
    }

    private static QuestEngine Engine(params QuestDef[] quests)
    {
        var e = new QuestEngine(new Source(quests));
        foreach (string plan in QuestCatalog.KnownPlans) e.Plans.Add(plan);
        return e;
    }

    private static QuestDef One(string id, Step step, params Reward[] rewards) => Q(id, id, "", [step], rewards);

    private static readonly AdventureEvent[] OneOfEach =
    {
        new PoiDiscovered(1, "k", "pass:gotthard.1", "pass", "UR"),
        new ViewpointSynced(1, "k", "tower:x", "ZH"),
        new CollectibleTaken(1, "k", "edelweiss:1", "edelweiss", "VS"),
        new ChestOpened(1, "k", "chest:1", 2, "peak:rigi-kulm"),
        new TrialFinished(1, "k", "trial:a", 170, 2),
        new PhotoTaken(1, "k", "view:a", null),
        new TravelUnlocked(1, "k", "station:bern", "station"),
        new ItemObtained(1, "k", ItemId.Cheese, 2, "chest:1"),
        new ItemDelivered(1, "k", ItemId.Cheese, 1, "area:market"),
        new Interacted(1, "k", "poi:dairy"),
        new QuestCompleted(1, "k", "other"),
    };

    /// <summary>Each step kind against every shared event: exactly its own matches.</summary>
    [Theory]
    [MemberData(nameof(StepKinds))]
    public void Every_step_kind_matches_its_own_event_only(string name, Step step, System.Type matching)
    {
        foreach (var e in OneOfEach)
            Assert.True(step.Matches(e) == (e.GetType() == matching), $"{name} vs {e.GetType().Name}");
    }

    public static IEnumerable<object[]> StepKinds() => new[]
    {
        new object[] { "reach poi", Reach(Poi("pass:gotthard.1")), typeof(PoiDiscovered) },
        new object[] { "discover", new DiscoverStep(Kind: "pass", Canton: "UR"), typeof(PoiDiscovered) },
        new object[] { "collect", new CollectStep(Kind: "edelweiss"), typeof(CollectibleTaken) },
        new object[] { "collect item", new CollectStep(Item: ItemId.Cheese), typeof(ItemObtained) },
        new object[] { "deliver", Deliver(ItemId.Cheese, 1, Area("market", 0, 0, 5)), typeof(ItemDelivered) },
        new object[] { "chest", new OpenChestStep(PoiId: "peak:rigi-kulm"), typeof(ChestOpened) },
        new object[] { "trial", new TrialStep("trial:a", MaxSeconds: 180), typeof(TrialFinished) },
        new object[] { "photo", Photo("view:a"), typeof(PhotoTaken) },
        new object[] { "interact", new InteractStep("poi:dairy"), typeof(Interacted) },
        new object[] { "unlock", new UnlockStep(PointKind: "station"), typeof(TravelUnlocked) },
        new object[] { "finish", new FinishStep("other"), typeof(QuestCompleted) },
    };

    [Fact]
    public void Filters_hold()
    {
        Assert.False(new DiscoverStep(Canton: "BE").Matches(OneOfEach[0]));
        Assert.False(new TrialStep("trial:a", MaxSeconds: 160).Matches(OneOfEach[4]));
        Assert.False(new TrialStep("trial:a", Medal: 3).Matches(OneOfEach[4]));
        Assert.False(new OpenChestStep(Tier: 3).Matches(OneOfEach[3]));
        Assert.True(new OpenChestStep(Tier: 1).Matches(OneOfEach[3]));
        Assert.False(new UnlockStep(PointKind: "cablecar").Matches(OneOfEach[6]));
    }

    [Fact]
    public void Distinct_pois_count_once()
    {
        var engine = Engine(One("passes", new DiscoverStep(Kind: "pass") { Count = 3 }));
        var p = new QuestsProgress();
        Assert.Null(engine.Start(p, "passes", Now));
        engine.OnEvent(p, new PoiDiscovered(1, "k", "pass:a", "pass", "UR"), Now);
        engine.OnEvent(p, new PoiDiscovered(1, "k", "pass:a", "pass", "UR"), Now);
        Assert.Equal(1, p.Active["passes"].Count);
        engine.OnEvent(p, new PoiDiscovered(1, "k", "pass:b", "pass", "UR"), Now);
        var last = engine.OnEvent(p, new PoiDiscovered(1, "k", "pass:c", "pass", "UR"), Now + 9);
        Assert.Equal(QuestChangeKind.Completed, Assert.Single(last).What);
        Assert.Equal(Now + 9, p.Done["passes"]);
        Assert.Empty(p.Active);
    }

    [Fact]
    public void Steps_run_in_order()
    {
        var engine = Engine(Q("two", "Two", "", [Photo("a"), Photo("b")], [new Cash(1)]));
        var p = new QuestsProgress();
        engine.Start(p, "two", Now);
        Assert.Empty(engine.OnEvent(p, new PhotoTaken(1, "k", "b", null), Now));   // not yet
        Assert.Equal(QuestChangeKind.StepDone, Assert.Single(engine.OnEvent(p, new PhotoTaken(1, "k", "a", null), Now)).What);
        Assert.Equal(1, p.Active["two"].Step);
        Assert.Equal(QuestChangeKind.Completed, Assert.Single(engine.OnEvent(p, new PhotoTaken(1, "k", "b", null), Now)).What);
    }

    [Fact]
    public void Item_steps_count_the_items()
    {
        var engine = Engine(One("cheese", Deliver(ItemId.Cheese, 3, Area("market", 0, 0, 5))));
        var p = new QuestsProgress();
        engine.Start(p, "cheese", Now);
        Assert.Equal(QuestChangeKind.Progressed, Assert.Single(engine.OnEvent(p, new ItemDelivered(1, "k", ItemId.Cheese, 2, "area:market"), Now)).What);
        Assert.Equal(QuestChangeKind.Completed, Assert.Single(engine.OnEvent(p, new ItemDelivered(1, "k", ItemId.Cheese, 5, "area:market"), Now)).What);
    }

    [Fact]
    public void Requires_gates_the_start_and_the_log()
    {
        var engine = Engine(One("a", Photo("x")), One("b", Photo("y")) with { Requires = ["a"] });
        var p = new QuestsProgress();
        Assert.Equal(new[] { "a" }, engine.Available(p).Select(q => q.Id));
        Assert.NotNull(engine.Start(p, "b", Now));
        engine.Start(p, "a", Now);
        engine.OnEvent(p, new PhotoTaken(1, "k", "x", null), Now);
        Assert.Equal(new[] { "b" }, engine.Available(p).Select(q => q.Id));
        Assert.Null(engine.Start(p, "b", Now));
    }

    [Fact]
    public void Completion_is_reported_once_so_rewards_apply_once()
    {
        var engine = Engine(One("a", Photo("x"), new Cash(10)));
        var p = new QuestsProgress();
        engine.Start(p, "a", Now);
        int completed = 0;
        for (int i = 0; i < 3; i++)
            completed += engine.OnEvent(p, new PhotoTaken(1, "k", "x", null), Now).Count(c => c.What == QuestChangeKind.Completed);
        Assert.Equal(1, completed);
        Assert.NotNull(engine.Start(p, "a", Now));   // done, not repeatable
    }

    [Fact]
    public void Abandon_then_restart_begins_again_and_repeatable_runs_again()
    {
        var engine = Engine(Q("a", "A", "", [Photo("x"), Photo("y")], []), One("r", Photo("z")) with { Repeatable = true });
        var p = new QuestsProgress();
        engine.Start(p, "a", Now);
        engine.OnEvent(p, new PhotoTaken(1, "k", "x", null), Now);
        Assert.Equal(1, p.Active["a"].Step);
        Assert.True(engine.Abandon(p, "a"));
        Assert.Null(p.Tracked);
        Assert.Null(engine.Start(p, "a", Now));
        Assert.Equal(0, p.Active["a"].Step);

        engine.Start(p, "r", Now);
        engine.OnEvent(p, new PhotoTaken(1, "k", "z", null), Now);
        Assert.True(p.Done.ContainsKey("r"));
        Assert.Null(engine.Start(p, "r", Now + 1));
    }

    [Fact]
    public void Tracking_follows_starts_and_ends()
    {
        var engine = Engine(One("a", Photo("x")), One("b", Photo("y")));
        var p = new QuestsProgress();
        engine.Start(p, "a", Now);
        engine.Start(p, "b", Now);
        Assert.Equal("a", p.Tracked);
        Assert.True(engine.Track(p, "b"));
        Assert.False(engine.Track(p, "nope"));
        engine.OnEvent(p, new PhotoTaken(1, "k", "y", null), Now);
        Assert.Equal("a", p.Tracked);   // the tracked one done: the next running one
    }

    [Fact]
    public void At_most_eight_run_at_once()
    {
        var defs = Enumerable.Range(0, 9).Select(i => One($"q{i}", Photo($"t{i}"))).ToArray();
        var engine = Engine(defs);
        var p = new QuestsProgress();
        for (int i = 0; i < 8; i++) Assert.Null(engine.Start(p, $"q{i}", Now));
        Assert.NotNull(engine.Start(p, "q8", Now));
    }

    [Fact]
    public void Reach_an_area_moves_on_by_position_only_inside_it()
    {
        var engine = Engine(One("go", Reach(Area("hut", 2600000, 1200000, 20))));
        var p = new QuestsProgress();
        engine.Start(p, "go", Now);
        Assert.True(engine.WaitsOnPosition(p));
        Assert.Empty(engine.OnPosition(p, 2600000, 1200030, Now));
        Assert.Equal(QuestChangeKind.Completed, Assert.Single(engine.OnPosition(p, 2600010, 1200010, Now)).What);
        Assert.False(engine.WaitsOnPosition(p));
    }

    [Fact]
    public void Reach_a_poi_needs_the_catalogue_or_its_discovery()
    {
        var engine = Engine(One("go", Reach(Poi("tower:x"))));
        var p = new QuestsProgress();
        engine.Start(p, "go", Now);
        Assert.Empty(engine.OnPosition(p, 0, 0, Now));   // no catalogue: no position
        engine.ResolvePoi = id => id == "tower:x" ? (0, 0, 10f) : null;
        Assert.Single(engine.OnPosition(p, 3, 4, Now));

        engine.Start(p, "go", Now);   // done: a second run is refused
        var q = new QuestsProgress();
        engine.Start(q, "go", Now);
        Assert.Single(engine.OnEvent(q, new PoiDiscovered(1, "k", "tower:x", "tower", "ZH"), Now));
    }

    [Fact]
    public void With_places_running_a_poi_is_reached_only_by_finding_it()
    {
        var engine = Engine(One("go", Reach(Poi("tower:x"))));
        engine.ResolvePoi = id => id == "tower:x" ? (0, 0, 10f) : null;
        engine.PoisByDiscoveryOnly = true;
        var p = new QuestsProgress();
        engine.Start(p, "go", Now);
        Assert.Empty(engine.OnPosition(p, 3, 4, Now));   // driven there: not reached
        Assert.Equal((0d, 0d, 10f), engine.Locate(new PoiTarget("tower:x")));   // still tracked
        Assert.Single(engine.OnEvent(p, new PoiDiscovered(1, "k", "tower:x", "tower", "ZH"), Now));
    }

    [Fact]
    public void An_on_event_quest_starts_and_counts_on_its_first_steps_event()
    {
        var engine = Engine(Q("auto", "Auto", "", [Photo("x"), Photo("y")], []) with { Start = QuestStart.OnEvent });
        var p = new QuestsProgress();
        Assert.Empty(engine.Available(p));   // never offered in the log
        var changes = engine.OnEvent(p, new PhotoTaken(1, "k", "x", null), Now);
        Assert.Equal(new[] { QuestChangeKind.Started, QuestChangeKind.StepDone }, changes.Select(c => c.What));
        Assert.Equal(1, p.Active["auto"].Step);
        Assert.Equal("auto", p.Tracked);
    }

    [Fact]
    public void A_quest_needing_a_missing_plan_is_not_offered()
    {
        var engine = new QuestEngine(new Source(One("photo", Photo("x")), One("walk", Reach(Area("a", 0, 0, 5)))));
        var p = new QuestsProgress();
        Assert.Equal(new[] { "walk" }, engine.Available(p).Select(q => q.Id));
        Assert.NotNull(engine.Start(p, "photo", Now));
        engine.Plans.Add("rewards");
        Assert.Null(engine.Start(p, "photo", Now));
    }

    [Fact]
    public void Inactive_peers_events_are_dropped_by_the_bus()
    {
        var bus = new AdventureEvents { IsActive = peer => peer != 7 };
        var seen = new List<AdventureEvent>();
        bus.Raised += seen.Add;
        bus.Raise(new PhotoTaken(7, "k", "x", null));   // a Battle Royale entrant
        bus.Raise(new PhotoTaken(1, "k", "x", null));
        Assert.Single(seen);
        Assert.Equal(1, bus.Dropped);
    }

    [Fact]
    public void The_quests_section_round_trips()
    {
        var progress = new PlayerProgress("k");
        var engine = Engine(One("passes", new DiscoverStep() { Count = 2 }));
        var q = progress.Get(QuestsProgress.Section);
        engine.Start(q, "passes", Now);
        engine.OnEvent(q, new PoiDiscovered(1, "k", "pass:a", "pass", "UR"), Now);
        progress.Touch(QuestsProgress.Section);
        var back = AdventureBook.FromJson(AdventureBook.ToJson(progress)).Get(QuestsProgress.Section);
        Assert.Equal(1, back.Active["passes"].Count);
        Assert.Equal(new[] { "pass:a" }, back.Active["passes"].Seen);
        Assert.Equal("passes", back.Tracked);
        Assert.Equal(Now, back.Active["passes"].Started);
    }
}
