using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Items;

namespace UnitSport.Adventure;

// Plain C#, no Godot: linked into the unit tests (QuestCatalogTests; docs/notes/adventure/quests.md).

/// <summary>
/// The quests that ship with the build (#1223), the <c>Crafting/Recipes.All</c> idiom: records, small
/// builders, one static array, checked by <c>QuestCatalogTests</c>. The ids it references (items, POIs)
/// are checked at compile or test time.
/// <para>
/// The starter quests of <c>docs/plans/adventure-objectives.md</c> §6 lean on the other Adventure plans
/// (POIs, chests, trials, photos, travel points): each is offered only once the plans of its steps run
/// (<see cref="QuestEngine.Playable"/>). Coordinates are approximate LV95 areas until the places
/// catalogue's ids exist. "All aboard" and "Grand tour" wait for the navigation plan's half-fare and GA
/// items (<c>ItemId.HalfFare</c>, <c>ItemId.GaCard</c>), which do not exist yet.
/// </para>
/// </summary>
public sealed class QuestCatalog : IQuestSource
{
    /// <summary>The reward kinds another plan registers (<c>AdventureService.RegisterReward</c>).</summary>
    public static readonly string[] UnlockKinds = { "reveal", "travel", "stamina" };

    /// <summary>The plans a step may need (<see cref="Step.Plan"/>).</summary>
    public static readonly string[] KnownPlans = { "places", "rewards", "navigation", "stamina" };

    public static readonly QuestDef[] All =
    {
        Q("uetliberg-tower", "Top of Zurich", "Climb the Uetliberg lookout tower and photograph the city.",
            [Reach(Area("the Uetliberg tower", 2679480, 1244760, 40)), Photo("view:uetliberg-zurich")],
            [new Cash(60), new Unlock("reveal", "tower:uetliberg")]) with { Canton = "ZH", Start = QuestStart.OnEvent },

        Q("uri-passes", "Passes of Uri", "Find three mountain passes in Uri.",
            [new DiscoverStep(Kind: "pass", Canton: "UR") { Count = 3 }],
            [new Cash(150), new Title("Pass hunter"), new Unlock("travel", "busstop:andermatt")]) with { Canton = "UR" },

        Q("gruyere-for-bern", "Cheese for the capital", "Fetch a Gruyère in Gruyères and sell it at Bern's market.",
            [new InteractStep("poi:gruyeres-dairy"), Deliver(ItemId.Cheese, 1, Area("Bern's market", 2600420, 1199550, 40))],
            [new Cash(120), new Item(ItemId.Fondue)]) with { Canton = "BE", Requires = ["uetliberg-tower"] },

        Q("rigi-chest", "Queen of the mountains", "Reach Rigi Kulm and open the chest near the summit.",
            [Reach(Area("Rigi Kulm", 2679520, 1212220, 60)), new OpenChestStep(PoiId: "peak:rigi-kulm")],
            [new Cash(80), new Unlock("stamina", "+1")]) with { Canton = "SZ" },

        Q("aletsch-glide", "Over the ice", "Unlock Bettmerhorn's cable car, then glide the Aletsch rings under 3 minutes.",
            [new UnlockStep(PointId: "cablecar:bettmerhorn"), new TrialStep("trial:aletsch-rings", MaxSeconds: 180)],
            [new Title("Glacier glider"), new Item(ItemId.HotChocolate, 2)]) with { Canton = "VS", Requires = ["uri-passes"] },
    };

    private static readonly Dictionary<string, QuestDef> ById = All.ToDictionary(q => q.Id);

    public string Id => "catalog";
    public IEnumerable<QuestDef> For(QuestContext ctx) => All;
    public QuestDef? Resolve(string questId) => ById.GetValueOrDefault(questId);

    // ---- builders ----------------------------------------------------------------------------------

    public static QuestDef Q(string id, string title, string blurb, Step[] steps, Reward[] rewards) => new(id, title, blurb, steps, rewards);
    public static AreaTarget Area(string name, double e, double n, float radiusM) => new(name, e, n, radiusM);
    public static PoiTarget Poi(string id) => new(id);
    public static ReachStep Reach(TargetRef at) => new(at);
    public static PhotoStep Photo(string targetId) => new(targetId);
    public static DeliverStep Deliver(ItemId item, int count, TargetRef at) => new(item, at) { Count = count };
}

/// <summary>
/// The checks' quests (<c>--adventure-test</c>, <c>--adventurecheck</c>, <c>tools/adventurenetcheck.sh</c>):
/// areas a few tens of metres from a point (the spawn), an errand with a hand-over, and a walk.
/// Never offered to players: only a server or offline client started with the flag adds this source.
/// </summary>
public sealed class ProbeQuests : IQuestSource
{
    public const string Errand = "probe-errand", Walk = "probe-walk";

    private readonly QuestDef[] _all;

    public ProbeQuests(double e, double n)
    {
        _all = new[]
        {
            QuestCatalog.Q(Errand, "Probe errand", "Walk to the first marker, then hand an apple over at the second.",
                [QuestCatalog.Reach(PointA(e, n)), QuestCatalog.Deliver(ItemId.Apple, 1, PointB(e, n))],
                [new Cash(25), new Item(ItemId.Chocolate), new Title("Errand runner")]) with { Source = Id_ },
            QuestCatalog.Q(Walk, "Probe walk", "Walk to the first marker.",
                [QuestCatalog.Reach(PointA(e, n))], [new Cash(5)]) with { Source = Id_, Repeatable = true },
        };
    }

    public static AreaTarget PointA(double e, double n) => QuestCatalog.Area("probe A", e + 30, n, 6);
    public static AreaTarget PointB(double e, double n) => QuestCatalog.Area("probe B", e + 30, n + 30, 6);

    private const string Id_ = "probe";
    public string Id => Id_;
    public IEnumerable<QuestDef> For(QuestContext ctx) => _all;
    public QuestDef? Resolve(string questId) => Array.Find(_all, q => q.Id == questId);
}
