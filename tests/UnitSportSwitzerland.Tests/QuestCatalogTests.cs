using System.Collections.Generic;
using System.Linq;
using UnitSport.Adventure;
using UnitSport.Items;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The quest catalog (src/Adventure/Quests/QuestCatalog.cs, linked in, #1223).</summary>
public class QuestCatalogTests
{
    private static IEnumerable<QuestDef> Every => QuestCatalog.All.Concat(new ProbeQuests(2600000, 1200000).For(new QuestContext(new QuestsProgress())));

    [Fact]
    public void Ids_are_unique_lower_case_and_steps_and_titles_are_there()
    {
        var ids = Every.Select(q => q.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var q in Every)
        {
            Assert.Matches("^[a-z0-9:-]+$", q.Id);
            Assert.NotEmpty(q.Title);
            Assert.NotEmpty(q.Blurb);
            Assert.NotEmpty(q.Steps);
            Assert.All(q.Steps, s => Assert.True(s.Count >= 1, $"{q.Id}: a step counts at least 1"));
            Assert.All(q.Steps, s => Assert.NotEmpty(s.Line));
        }
    }

    [Fact]
    public void Every_requirement_exists_and_none_loops()
    {
        var byId = QuestCatalog.All.ToDictionary(q => q.Id);
        foreach (var q in QuestCatalog.All)
        {
            foreach (string r in q.Requires) Assert.True(byId.ContainsKey(r), $"{q.Id} requires unknown {r}");
            var seen = new HashSet<string>();
            var todo = new Stack<string>(q.Requires);
            while (todo.Count > 0)
            {
                string r = todo.Pop();
                Assert.NotEqual(q.Id, r);
                if (seen.Add(r)) foreach (string rr in byId[r].Requires) todo.Push(rr);
            }
        }
    }

    [Fact]
    public void Rewards_are_bounded_and_every_unlock_kind_is_a_known_plans()
    {
        foreach (var q in Every)
            foreach (var r in q.Rewards)
                switch (r)
                {
                    case Cash c: Assert.InRange(c.Chf, 1, 5000); break;
                    case Item i: Assert.InRange(i.Count, 1, 10); Assert.NotEqual(ItemId.None, i.Id); break;
                    case Title t: Assert.False(string.IsNullOrWhiteSpace(t.Name)); break;
                    case Unlock u: Assert.Contains(u.Kind, QuestCatalog.UnlockKinds); Assert.NotEmpty(u.Arg); break;
                    default: Assert.Fail($"{q.Id}: unknown reward {r}"); break;
                }
    }

    [Fact]
    public void Every_step_names_a_known_plan()
    {
        foreach (var q in Every)
            foreach (var s in q.Steps)
                Assert.True(s.Plan == null || QuestCatalog.KnownPlans.Contains(s.Plan), $"{q.Id}: plan {s.Plan}");
    }

    [Fact]
    public void Probe_quests_are_playable_now_and_never_in_the_catalog()
    {
        var engine = new QuestEngine(new ProbeQuests(2600000, 1200000));
        foreach (var q in engine.Sources[0].For(new QuestContext(new QuestsProgress()))) Assert.True(engine.Playable(q), q.Id);
        Assert.DoesNotContain(QuestCatalog.All, q => q.Source == "probe" || q.Id.StartsWith("probe"));
    }

    /// <summary>The starter quests can be completed by the events the other plans will raise, in order.</summary>
    [Fact]
    public void Starter_quests_are_completable_by_a_scripted_event_list()
    {
        var engine = new QuestEngine(new QuestCatalog());
        foreach (string plan in QuestCatalog.KnownPlans) engine.Plans.Add(plan);
        var p = new QuestsProgress();
        const long now = 1_793_000_000;
        var done = new List<string>();
        void Feed(AdventureEvent e) => done.AddRange(engine.OnEvent(p, e, now).Where(c => c.What == QuestChangeKind.Completed).Select(c => c.QuestId));
        void At(double e, double n) => done.AddRange(engine.OnPosition(p, e, n, now).Where(c => c.What == QuestChangeKind.Completed).Select(c => c.QuestId));

        Assert.Null(engine.Start(p, "uri-passes", now));
        Assert.Null(engine.Start(p, "rigi-chest", now));

        Feed(new PoiDiscovered(1, "k", "pass:gotthard.a1", "pass", "UR"));
        Feed(new PoiDiscovered(1, "k", "pass:furka.b2", "pass", "UR"));
        Feed(new PoiDiscovered(1, "k", "pass:susten.c3", "pass", "BE"));   // Bern: not counted
        Feed(new PoiDiscovered(1, "k", "pass:klausen.d4", "pass", "UR"));
        Assert.Contains("uri-passes", done);

        At(2679530, 1212250);   // Rigi Kulm
        Feed(new ChestOpened(1, "k", "chest:rigi.1", 1, "peak:rigi-kulm"));
        Assert.Contains("rigi-chest", done);

        Assert.Null(engine.Start(p, "aletsch-glide", now));
        Feed(new TravelUnlocked(1, "k", "cablecar:bettmerhorn", "cablecar"));
        Feed(new TrialFinished(1, "k", "trial:aletsch-rings", 200, 1));   // too slow
        Feed(new TrialFinished(1, "k", "trial:aletsch-rings", 175, 2));
        Assert.Contains("aletsch-glide", done);

        // "Top of Zurich" starts by itself on reaching the tower, its first step
        At(2679470, 1244770);
        Assert.Equal(1, p.Active["uetliberg-tower"].Step);
        Feed(new PhotoTaken(1, "k", "view:uetliberg-zurich", "tower:uetliberg"));
        Assert.Contains("uetliberg-tower", done);

        Assert.Null(engine.Start(p, "gruyere-for-bern", now));
        Feed(new Interacted(1, "k", "poi:gruyeres-dairy"));
        Feed(new ItemDelivered(1, "k", ItemId.Cheese, 1, "area:Bern's market"));
        Assert.Contains("gruyere-for-bern", done);

        Assert.Equal(QuestCatalog.All.Length, p.Done.Count);
    }

    [Fact]
    public void Without_the_other_plans_no_starter_quest_is_offered()
    {
        var engine = new QuestEngine(new QuestCatalog());
        Assert.Empty(engine.Available(new QuestsProgress()));
    }
}
