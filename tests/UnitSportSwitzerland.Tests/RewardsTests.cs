using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Adventure;
using UnitSport.Adventure.Places;
using UnitSport.Adventure.Rewards;
using UnitSport.Items;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Shared synthetic terrain for the rewards' tests (#1227).</summary>
internal static class RewardTerrain
{
    /// <summary>A coarse grid (stride 10) of a height function.</summary>
    public static ChunkGrid Grid(TileId id, Func<double, double, double> h)
    {
        int size = (ChunkFormat.GridSize - 1) / ChunkFormat.CoarseStride + 1;
        var heights = new ushort[size * size];
        float min = float.MaxValue, max = float.MinValue;
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
            {
                double e = id.MinE + c * 10, n = id.MaxN - r * 10, z = h(e, n);
                heights[r * size + c] = ChunkFormat.Quantize(z);
                min = Math.Min(min, (float)z);
                max = Math.Max(max, (float)z);
            }
        return new ChunkGrid(id, heights, min, max, ChunkFormat.CoarseStride);
    }

    /// <summary>Alpine ridges: 1200-2000 m, slopes from flat to cliffs.</summary>
    public static double Alps(double e, double n) => 1600 + 400 * Math.Sin(e / 150) * Math.Cos(n / 170) + (n - 1100000) * 0.02;

    /// <summary>A gentle Mittelland: 480-520 m.</summary>
    public static double Lowland(double e, double n) => 500 + 12 * Math.Sin(e / 90) + 9 * Math.Cos(n / 70);

    public static readonly Poi[] NoPois = Array.Empty<Poi>();

    /// <summary>A straight carriageway across the tile, west to east at y (tile-local north offset).</summary>
    public static RoadTile Road(TileId id, double y)
    {
        var pts = new List<float>();
        for (int x = 0; x <= 1000; x += 50) pts.AddRange(new[] { (float)x, 0f, (float)(1000 - y) });
        return new RoadTile { Id = id, Segments = new List<RoadSegment> { new() { Class = RoadClass.Road, Width = 6, Points = pts.ToArray() } } };
    }
}

public class RewardPlacerTests
{
    [Fact]
    public void The_same_tile_gives_the_same_spots()
    {
        var id = new TileId(2620, 1100);
        var grid = RewardTerrain.Grid(id, RewardTerrain.Alps);
        var a = RewardPlacer.Place(id, grid, RewardTerrain.NoPois, RewardTerrain.NoPois, null);
        var b = RewardPlacer.Place(id, RewardTerrain.Grid(id, RewardTerrain.Alps), RewardTerrain.NoPois, RewardTerrain.NoPois, null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Ids_name_their_10_m_cell_and_their_tile()
    {
        int checkedIds = 0;
        for (int e = 2600; e < 2612; e++)
            foreach (var s in RewardPlacer.Place(new TileId(e, 1100), RewardTerrain.Grid(new TileId(e, 1100), RewardTerrain.Alps),
                         RewardTerrain.NoPois, RewardTerrain.NoPois, null))
            {
                Assert.Equal(RewardRules.CellId(s.Id[..2], s.E, s.N), s.Id);
                Assert.True(RewardRules.TryTile(s.Id, _ => null, out var tile));
                Assert.Equal((e, 1100), tile);
                checkedIds++;
            }
        Assert.True(checkedIds > 10, $"only {checkedIds} spots on 12 alpine tiles");
    }

    [Fact]
    public void The_budget_per_tile_holds_and_highest_tiers_stay()
    {
        for (int e = 2600; e < 2640; e++)
        {
            var id = new TileId(e, 1101);
            var spots = RewardPlacer.Place(id, RewardTerrain.Grid(id, RewardTerrain.Alps), RewardTerrain.NoPois, RewardTerrain.NoPois, null);
            Assert.True(spots.Count(s => s.Kind == SpotKind.Chest) <= RewardPlacer.MaxChests);
            Assert.True(spots.Count(s => s.Kind == SpotKind.Edelweiss) <= RewardPlacer.MaxEdelweiss);
            Assert.True(spots.Count(s => s.Kind == SpotKind.Cairn) <= RewardPlacer.MaxCairns);
            Assert.Equal(spots.Length, spots.Select(s => s.Id).Distinct().Count());
            Assert.All(spots, s => Assert.Equal((e, 1101), s.Tile));
        }
    }

    [Fact]
    public void No_chest_stands_on_a_road()
    {
        int chests = 0;
        for (int e = 2500; e < 2560; e++)
        {
            var id = new TileId(e, 1180);
            var spots = RewardPlacer.Place(id, RewardTerrain.Grid(id, RewardTerrain.Lowland), RewardTerrain.NoPois, RewardTerrain.NoPois,
                RewardTerrain.Road(id, 500));
            foreach (var c in spots.Where(s => s.Kind == SpotKind.Chest))
            {
                chests++;
                double off = Math.Abs(c.N - (id.MinN + 500));
                Assert.True(off >= RewardPlacer.RoadClear, $"{c.Id} is {off:F1} m from the road");
                if (c.Tier == ChestTier.Fine) Assert.True(off >= RewardPlacer.FineClear, $"{c.Id} (Fine) is {off:F1} m from the road");
            }
        }
        Assert.True(chests > 30, $"only {chests} chests on 60 lowland tiles");
    }

    [Fact]
    public void Edelweiss_grow_high_and_common_chests_are_never_sealed()
    {
        var all = new List<RewardSpot>();
        for (int e = 2600; e < 2700; e++)
        {
            var id = new TileId(e, 1102);
            all.AddRange(RewardPlacer.Place(id, RewardTerrain.Grid(id, RewardTerrain.Alps), RewardTerrain.NoPois, RewardTerrain.NoPois, null));
        }
        var flowers = all.Where(s => s.Kind == SpotKind.Edelweiss).ToList();
        Assert.NotEmpty(flowers);
        Assert.All(flowers, f => Assert.True(f.Alt >= RewardPlacer.EdelweissFloor, $"{f.Id} at {f.Alt} m"));
        Assert.All(all.Where(s => s.Tier == ChestTier.Common && s.Kind == SpotKind.Chest), c => Assert.Equal(SealKind.None, c.Seal));
        // per km² of walkable lowland: the plan's ~1.2 Common, ~0.35 Fine
        var low = new List<RewardSpot>();
        for (int e = 2500; e < 2600; e++)
        {
            var id = new TileId(e, 1190);
            low.AddRange(RewardPlacer.Place(id, RewardTerrain.Grid(id, RewardTerrain.Lowland), RewardTerrain.NoPois, RewardTerrain.NoPois, null));
        }
        Assert.InRange(low.Count(s => s.Kind == SpotKind.Chest && s.Tier == ChestTier.Common) / 100.0, 0.9, 1.6);
        Assert.InRange(low.Count(s => s.Kind == SpotKind.Chest && s.Tier == ChestTier.Fine) / 100.0, 0.2, 0.7);
    }

    [Fact]
    public void Places_get_their_precious_and_luxurious_chests()
    {
        var cat = PoiCatalogue.Fixture();
        var peak = cat.OfKind(PoiKind.Peak)[0];
        var castle = cat.OfKind(PoiKind.Castle)[0];
        var id = TileId.FromLv95(peak.E, peak.N);
        var pois = cat.InTile(id).ToArray();
        var spots = RewardPlacer.Place(id, RewardTerrain.Grid(id, (_, _) => 500), pois, RewardTerrain.NoPois, null);
        // the fixture peak is a tier-1 viewpoint: a Precious and a Luxurious chest, the latter always sealed
        Assert.Contains(spots, s => s.Id == RewardRules.PoiChestId(peak.Id, 0) && s.Tier == ChestTier.Precious);
        var lux = Assert.Single(spots, s => s.Id == RewardRules.PoiChestId(peak.Id, 1));
        Assert.Equal(ChestTier.Luxurious, lux.Tier);
        Assert.NotEqual(SealKind.None, lux.Seal);
        Assert.Equal(peak.Id, lux.PoiId);
        Assert.True(RewardRules.TryTile(lux.Id, p => cat.Resolve(p) is { } q ? (q.E, q.N) : null, out var t));
        Assert.Equal((id.E, id.N), t);
        // a castle's sealed chest is held by pressure plates, or one in six by a two-handle gate (#1266)
        int gates = 0;
        for (ulong h = 0; h < 1200; h++)
        {
            var seal = RewardPlacer.SealFor(ChestTier.Luxurious, h, castle, 500, 5, false);
            Assert.True(seal is SealKind.Plates or SealKind.Gate, $"{seal}");
            if (seal == SealKind.Gate) gates++;
        }
        Assert.InRange(gates, 150, 250);
        // a Luxurious chest high on a glacier needs a rope team
        Assert.Equal(SealKind.Roped, RewardPlacer.SealFor(ChestTier.Luxurious, 7, null, 3100, 10, false));
    }

    [Fact]
    public void Luxurious_chests_come_back_every_1_August()
    {
        Assert.Equal(2025, RewardRules.Edition(new DateTime(2026, 7, 31, 23, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(2026, RewardRules.Edition(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(2026, RewardRules.Edition(new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        var lux = new RewardSpot("ch:peak:x.0123456789:1", SpotKind.Chest, 0, 0, 0, ChestTier.Luxurious);
        var fine = new RewardSpot("ch:2600000_1200000", SpotKind.Chest, 0, 0, 0, ChestTier.Fine);
        Assert.Equal("ch:peak:x.0123456789:1#2026", RewardRules.OpenId(lux, 2026));
        Assert.Equal(fine.Id, RewardRules.OpenId(fine, 2026));
        Assert.Equal(lux.Id, RewardRules.SpotIdOf("ch:peak:x.0123456789:1#2026"));
    }

    [Fact]
    public void The_rewards_items_keep_to_ids_400_to_419()
    {
        // ItemId 400-419 belong to the rewards plan (adventure-overview): crystal, gadgets, the Alpine set
        var ours = ChestLoot.Cosmetics.Concat(ChestLoot.Gadgets).Append(ItemId.Bergkristall).Distinct().ToList();
        Assert.Equal(11, ours.Count);
        Assert.All(ours, id => Assert.InRange((int)id, 400, 419));
        Assert.All(Enum.GetValues<ItemId>().Where(id => (int)id is >= 400 and <= 419), id => Assert.Contains(id, ours));
    }

    [Fact]
    public void Forged_ids_name_no_tile()
    {
        Assert.False(RewardRules.TryTile("xx:1_2", _ => null, out _));
        Assert.False(RewardRules.TryTile("ch:", _ => null, out _));
        Assert.False(RewardRules.TryTile("ch:peak:nowhere.00:0", _ => null, out _));
        Assert.True(RewardRules.TryTile("ew:2600150_1200340", _ => null, out var t));
        Assert.Equal((2600, 1200), t);
    }

    [Fact]
    public void The_fixture_set_has_every_seal_and_kind()
    {
        var cat = PoiCatalogue.Fixture();
        var spots = RewardSpots.Fixture(PoiCatalogue.FixtureE, PoiCatalogue.FixtureN, 500, cat);
        foreach (var seal in new[] { SealKind.None, SealKind.Bonfire, SealKind.Bells, SealKind.Rings, SealKind.Plates, SealKind.Guide })
            Assert.Contains(spots, s => s.Kind == SpotKind.Chest && s.Seal == seal);
        Assert.Contains(spots, s => s.Tier == ChestTier.Luxurious);
        Assert.True(spots.Count(s => s.Kind == SpotKind.Edelweiss) >= 5);
        Assert.Contains(spots, s => s.Kind == SpotKind.Cairn);
        Assert.Equal(spots.Count, spots.Select(s => s.Id).Distinct().Count());
    }
}

public class ChestLootTests
{
    private static ulong Seed(int i) => AdventureHash.Of(i, 77, 0x10UL);

    [Theory]
    [InlineData(ChestTier.Common, 0)]
    [InlineData(ChestTier.Fine, 0)]
    [InlineData(ChestTier.Fine, 5)]
    [InlineData(ChestTier.Precious, 0)]
    [InlineData(ChestTier.Precious, 7)]
    [InlineData(ChestTier.Luxurious, 0)]
    public void Chance_agrees_with_200k_rolls(ChestTier tier, int pity)
    {
        const int N = 200_000;
        var seen = new Dictionary<ItemId, int>();
        for (int i = 0; i < N; i++)
        {
            var roll = ChestLoot.Roll(tier, AdventureHash.Rng(Seed(i)), pity);
            seen[ItemId.Francs] = seen.GetValueOrDefault(ItemId.Francs) + 1;
            foreach (var id in roll.Items.Select(l => l.Item).Distinct()) seen[id] = seen.GetValueOrDefault(id) + 1;
        }
        foreach (var item in ChestLoot.Candidates(tier).Concat(seen.Keys).Distinct())
        {
            double sampled = seen.GetValueOrDefault(item) / (double)N, exact = ChestLoot.Chance(tier, item, pity);
            Assert.True(Math.Abs(sampled - exact) < 0.02, $"{tier} pity {pity} {item}: rolled {sampled:P2}, Chance {exact:P2}");
        }
    }

    [Fact]
    public void A_precious_chest_gives_a_cosmetic_by_the_8th_without_one()
    {
        int pity = 0, longest = 0, run = 0;
        for (int i = 0; i < 20_000; i++)
        {
            var roll = ChestLoot.Roll(ChestTier.Precious, AdventureHash.Rng(Seed(i)), pity);
            run = roll.Cosmetic ? 0 : run + 1;
            longest = Math.Max(longest, run);
            pity = ChestLoot.PityAfter(ChestTier.Precious, pity, roll.Cosmetic);
        }
        Assert.True(longest <= ChestLoot.PreciousPity, $"{longest} Precious openings in a row without a cosmetic");
        Assert.Equal(1, ChestLoot.CosmeticChance(ChestTier.Precious, 7));
        Assert.Equal(0.15, ChestLoot.CosmeticChance(ChestTier.Precious, 6), 6);
    }

    [Fact]
    public void A_fine_chest_raises_its_cosmetic_chance_two_points_a_miss()
    {
        Assert.Equal(0.04, ChestLoot.CosmeticChance(ChestTier.Fine, 0), 6);
        Assert.Equal(0.10, ChestLoot.CosmeticChance(ChestTier.Fine, 3), 6);
        Assert.Equal(0, ChestLoot.CosmeticChance(ChestTier.Common, 9));
        Assert.Equal(1, ChestLoot.CosmeticChance(ChestTier.Luxurious, 0));
        Assert.Equal(0, ChestLoot.PityAfter(ChestTier.Fine, 4, gotCosmetic: true));
        Assert.Equal(5, ChestLoot.PityAfter(ChestTier.Fine, 4, gotCosmetic: false));
        Assert.Equal(0, ChestLoot.PityAfter(ChestTier.Common, 4, gotCosmetic: false));
    }

    [Fact]
    public void Francs_and_crystal_follow_the_table()
    {
        for (int i = 0; i < 2000; i++)
        {
            var lux = ChestLoot.Roll(ChestTier.Luxurious, AdventureHash.Rng(Seed(i)), 0);
            Assert.InRange(lux.Francs, 400, 1000);
            Assert.InRange(lux.Items.Single(l => l.Item == ItemId.Bergkristall).Count, 5, 8);
            Assert.True(lux.Cosmetic);
            var common = ChestLoot.Roll(ChestTier.Common, AdventureHash.Rng(Seed(i)), 0);
            Assert.InRange(common.Francs, 20, 60);
            Assert.DoesNotContain(common.Items, l => Array.IndexOf(ChestLoot.Cosmetics, l.Item) >= 0 || Array.IndexOf(ChestLoot.Gadgets, l.Item) >= 0);
        }
    }

    [Fact]
    public void A_cosmetic_already_owned_becomes_crystal()
    {
        var owned = new HashSet<string>();
        var roll = new ChestRoll(500, new List<LootLine> { new(ItemId.GuideHat, 1), new(ItemId.Rope, 2) }, true);
        var (first, note1) = ChestLoot.Settle(roll, owned);
        Assert.Contains(first, l => l.Item == ItemId.GuideHat);
        Assert.Equal("", note1);
        var (second, note2) = ChestLoot.Settle(roll, owned);
        Assert.DoesNotContain(second, l => l.Item == ItemId.GuideHat);
        Assert.Contains(second, l => l.Item == ItemId.Bergkristall && l.Count == ChestLoot.DuplicateCrystals);
        Assert.Contains("already yours", note2);
    }

    [Fact]
    public void Each_player_rolls_a_chest_differently()
    {
        var a = ChestLoot.Roll(ChestTier.Precious, AdventureHash.Rng(AdventureHash.Of("ch:2600000_1200000", "lea", 0)), 0);
        var b = ChestLoot.Roll(ChestTier.Precious, AdventureHash.Rng(AdventureHash.Of("ch:2600000_1200000", "tom", 0)), 0);
        var a2 = ChestLoot.Roll(ChestTier.Precious, AdventureHash.Rng(AdventureHash.Of("ch:2600000_1200000", "lea", 0)), 0);
        Assert.Equal(a.Francs, a2.Francs);
        Assert.True(a.Francs != b.Francs || !a.Items.SequenceEqual(b.Items));
    }
}

public class SealBookTests
{
    private static SealLayout Layout(SealKind kind)
    {
        var spot = new RewardSpot("ch:2600500_1200500", SpotKind.Chest, 2600500, 1200500, 500, ChestTier.Precious, kind, Hash: 12345);
        return Seals.Layout(spot, (_, _) => 500, (_, _) => 10);
    }

    [Fact]
    public void Three_fires_in_90_s_open_a_hoehenfeuer()
    {
        var l = Layout(SealKind.Bonfire);
        Assert.Equal(3, l.Props.Length);
        var book = new SealBook();
        Assert.Equal(SealStep.Progress, book.Step("a", "c", l, 0, 10));
        Assert.Equal(SealStep.Ignored, book.Step("a", "c", l, 0, 20));
        Assert.Equal(SealStep.Progress, book.Step("a", "c", l, 2, 40));
        Assert.Equal(SealStep.Done, book.Step("a", "c", l, 1, 95));
        Assert.True(book.IsDone("a", "c"));
        Assert.Equal(SealStep.AlreadyDone, book.Step("a", "c", l, 1, 96));
        // too slow: the first fires burnt down
        var slow = new SealBook();
        slow.Step("a", "c", l, 0, 0);
        slow.Step("a", "c", l, 1, 50);
        Assert.Equal(SealStep.Reset, slow.Step("a", "c", l, 2, 120));
        Assert.False(slow.IsDone("a", "c"));
        Assert.Equal(1 << 2, slow.MaskOf("a", "c"));
    }

    [Fact]
    public void Rings_count_in_order_and_in_time()
    {
        var l = Layout(SealKind.Rings);
        Assert.InRange(l.Props.Length, 4, 6);
        var book = new SealBook();
        Assert.Equal(SealStep.Ignored, book.Step("a", "c", l, 1, 0));
        Assert.Equal(SealStep.Progress, book.Step("a", "c", l, 0, 0));
        Assert.Equal(SealStep.Ignored, book.Step("a", "c", l, 2, 1));
        SealStep last = SealStep.Ignored;
        for (int i = 1; i < l.Props.Length; i++) last = book.Step("a", "c", l, i, i * 3);
        Assert.Equal(SealStep.Done, last);
        var slow = new SealBook();
        slow.Step("a", "c", l, 0, 0);
        Assert.Equal(SealStep.Reset, slow.Step("a", "c", l, 1, l.Seconds + 1));
    }

    [Fact]
    public void A_wrong_cowbell_starts_the_tune_again()
    {
        var l = Layout(SealKind.Bells);
        Assert.Equal(4, l.Tune.Length);
        Assert.Equal(5, l.Props.Length);
        var book = new SealBook();
        Assert.Equal(SealStep.Progress, book.Step("a", "c", l, l.Tune[0], 0));
        int wrong = (l.Tune[1] + 1) % 4;
        Assert.Equal(SealStep.Reset, book.Step("a", "c", l, wrong, 1));
        SealStep r = SealStep.Ignored;
        for (int i = 0; i < 4; i++) r = book.Step("a", "c", l, l.Tune[i], 2 + i);
        Assert.Equal(SealStep.Done, r);
    }

    [Fact]
    public void Plates_and_guides_complete()
    {
        var plates = Layout(SealKind.Plates);
        Assert.Equal(2, plates.Props.Length);
        var book = new SealBook();
        Assert.Equal(SealStep.Done, book.Step("a", "p", plates, 0, 0));
        var guide = Layout(SealKind.Guide);
        Assert.InRange(guide.Steps, 2, 4);
        Assert.Equal(GuideKind.Marmot, guide.Guide);
        for (int leg = 1; leg < guide.Steps; leg++) Assert.Equal(SealStep.Progress, book.Step("a", "g", guide, leg, leg));
        Assert.Equal(SealStep.Done, book.Step("a", "g", guide, guide.Steps, 9));
    }

    [Fact]
    public void Progress_is_forgotten_after_10_minutes_or_on_leaving()
    {
        var l = Layout(SealKind.Bonfire);
        var book = new SealBook();
        book.Step("a", "c", l, 0, 0);
        book.Step("b", "c", l, 0, 500);
        Assert.Equal(1, book.Prune(Seals.ForgetAfter + 1));
        Assert.Equal(0, book.MaskOf("a", "c"));
        Assert.NotEqual(0, book.MaskOf("b", "c"));
        book.Forget("b");
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void A_party_step_counts_for_members_within_60_m()
    {
        var keys = Seals.Coop("a", new[] { ("a", 0.0, 0.0), ("b", 30.0, 40.0), ("c", 70.0, 0.0) }, 0, 0);
        Assert.Equal(new[] { "a", "b" }, keys);
    }

    [Fact]
    public void Layouts_are_the_same_everywhere_and_stay_on_their_tile()
    {
        foreach (var kind in new[] { SealKind.Bonfire, SealKind.Rings, SealKind.Plates, SealKind.Bells, SealKind.Guide })
        {
            var corner = new RewardSpot("ch:x", SpotKind.Chest, 2600003, 1200997, 500, ChestTier.Fine, kind, Hash: 99);
            var a = Seals.Layout(corner, (_, _) => 500);
            var b = Seals.Layout(corner, (_, _) => 500);
            Assert.Equal(a.Props, b.Props);
            Assert.All(a.Props, p => Assert.True(p.E > 2600000 && p.E < 2601000 && p.N > 1200000 && p.N < 1201000, $"{kind} prop off the tile"));
        }
        var cliff = new RewardSpot("ch:y", SpotKind.Chest, 2600500, 1200500, 500, ChestTier.Fine, SealKind.Guide, Hash: 5);
        Assert.Equal(GuideKind.Vulture, Seals.Layout(cliff, (_, _) => 500, (_, _) => 50).Guide);
    }
}

public class GuideBookTests
{
    [Fact]
    public void A_guide_waits_for_its_follower_then_runs_on()
    {
        var spot = new RewardSpot("ch:g", SpotKind.Chest, 2600500, 1200500, 500, ChestTier.Precious, SealKind.Guide, Hash: 31);
        var l = Seals.Layout(spot, (_, _) => 500);
        var book = new GuideBook();
        var m = book.Wake("a", "ch:g", l, 100);
        Assert.Equal(1, m.Leg);
        var w1 = l.Props[1];
        // still running: nothing yet, even standing at the waypoint
        Assert.Null(book.Follow("a", w1.E, w1.N, 100.5));
        double t = 100 + GuideBook.LegSeconds(l, 1) + 0.1;
        // arrived, but the follower is far behind
        Assert.Null(book.Follow("a", l.Props[0].E, l.Props[0].N, t));
        var next = book.Follow("a", w1.E + 3, w1.N, t);
        Assert.Equal(2, next?.Leg);
        for (int leg = 2; leg < l.Props.Length; leg++)
        {
            t += GuideBook.LegSeconds(l, leg) + 0.1;
            var w = l.Props[leg];
            next = book.Follow("a", w.E, w.N, t);
        }
        Assert.True(next?.Done);
        Assert.Equal(0, book.Count);
        var (e, n, _) = GuideBook.At(l, 1, 100, 100 + GuideBook.LegSeconds(l, 1) / 2);
        Assert.InRange(e, Math.Min(l.Props[0].E, w1.E) - 0.01, Math.Max(l.Props[0].E, w1.E) + 0.01);
        Assert.InRange(n, Math.Min(l.Props[0].N, w1.N) - 0.01, Math.Max(l.Props[0].N, w1.N) + 0.01);
    }
}

public class EdelweissTests
{
    [Fact]
    public void Five_edelweiss_make_a_stamina_step_ten_at_most()
    {
        var c = new CollectProgress { Held = 12 };
        var steps = Offering.Offer(c);
        Assert.Equal(2, steps.Count);
        Assert.Equal(2, c.Held);
        Assert.Equal(10, c.Offered);
        Assert.All(steps, s => Assert.True(s.Stamina));
        c.Held = 60;
        steps = Offering.Offer(c);
        Assert.Equal(12, steps.Count);
        Assert.Equal(70, c.Offered);
        Assert.Equal(8, steps.Count(s => s.Stamina));   // steps 3..10
        Assert.Equal(new[] { 5, 10 }, steps.Where(s => s.Precious).Select(s => s.Step).Take(2));
        Assert.Equal(Offering.MaxSteps, Offering.StaminaSteps(c));
        Assert.Empty(Offering.Offer(new CollectProgress { Held = 4 }));
    }

    [Fact]
    public void Shrines_are_crosses_and_chapels()
    {
        var cat = PoiCatalogue.Fixture();
        Assert.True(Offering.IsShrine(cat.OfKind(PoiKind.Chapel)[0]));
        Assert.True(Offering.IsShrine(cat.OfKind(PoiKind.Peak)[0]));   // crossed
        Assert.False(Offering.IsShrine(cat.OfKind(PoiKind.Castle)[0]));
    }

    [Fact]
    public void Each_canton_has_at_most_twenty_cairns_the_same_everywhere()
    {
        var cat = PoiCatalogue.Fixture();
        var a = Cairns.Of(cat, "VS");
        Assert.InRange(a.Count, 1, Cairns.PerCanton);
        Assert.All(a, id => Assert.True(Cairns.Eligible(cat.Resolve(id)!)));
        Assert.Equal(a, Cairns.Of(PoiCatalogue.Fixture(), "VS"));
    }
}

public class PhotoJudgeTests
{
    private static readonly PhotoSpot Spot = new("ph:test", "Test", 2600000, 1200000, 150, 2600000, 1205000, 2000, "A peak");

    [Fact]
    public void The_target_in_the_frame_counts()
    {
        // looking north, 3° up toward a peak 5 km off and 1500 m higher (~16.7°): pitch it
        float eye = 502;
        double elev = Math.Atan2(2000 - eye, 5000) * 180 / Math.PI;
        Assert.Equal(PhotoVerdict.Taken, PhotoJudge.Judge(Spot, 2600010, 1200020, eye, 2, (float)elev, 40, (_, _) => 500));
    }

    [Fact]
    public void Ninety_degrees_off_is_refused()
    {
        Assert.Equal(PhotoVerdict.OffTarget, PhotoJudge.Judge(Spot, 2600000, 1200000, 502, 90, 16, 40, (_, _) => 500));
        Assert.Equal(PhotoVerdict.OffTarget, PhotoJudge.Judge(Spot, 2600000, 1200000, 502, 0, -20, 40, (_, _) => 500));
    }

    [Fact]
    public void Out_of_the_disc_or_behind_a_ridge_is_refused()
    {
        Assert.Equal(PhotoVerdict.NotThere, PhotoJudge.Judge(Spot, 2600000, 1200300, 502, 0, 16, 40, (_, _) => 500));
        // a 1500 m ridge 2 km north
        Func<double, double, float?> ridge = (_, n) => n > 1201900 && n < 1202100 ? 1500 : 500;
        Assert.Equal(PhotoVerdict.Blocked, PhotoJudge.Judge(Spot, 2600000, 1200000, 502, 0, 16.7f, 40, ridge));
        // unknown terrain is taken as clear
        Assert.Equal(PhotoVerdict.Taken, PhotoJudge.Judge(Spot, 2600000, 1200000, 502, 0, 16.7f, 40, (_, _) => null));
    }

    [Fact]
    public void The_curated_spots_are_thirty_in_switzerland_for_review()
    {
        Assert.Equal(30, PhotoSpots.Curated.Length);
        Assert.Equal(30, PhotoSpots.Curated.Select(s => s.Id).Distinct().Count());
        Assert.All(PhotoSpots.Curated, s =>
        {
            Assert.InRange(s.StandE, 2480000, 2840000);
            Assert.InRange(s.StandN, 1070000, 1300000);
            Assert.True(s.Review);
            Assert.True(Math.Abs(s.StandE - s.TargetE) + Math.Abs(s.StandN - s.TargetN) > 50, $"{s.Id} stands on its target");
        });
        Assert.Same(PhotoSpots.Curated[0], PhotoSpots.Resolve(PhotoSpots.Curated[0].Id, PoiCatalogue.Fixture()));
        Assert.Null(PhotoSpots.Resolve("ph:nowhere", PoiCatalogue.Fixture()));
    }

    [Fact]
    public void Angles_fold_the_short_way()
    {
        Assert.Equal(-20, PhotoJudge.AngleDiff(350, 10), 6);
        Assert.Equal(20, PhotoJudge.AngleDiff(10, 350), 6);
        Assert.Equal(180, PhotoJudge.AngleDiff(180, 0), 6);
    }
}

public class TrialsTests
{
    [Fact]
    public void The_fixture_chapel_has_a_run_with_gates_every_200_m()
    {
        var cat = PoiCatalogue.Fixture();
        var chapel = cat.OfKind(PoiKind.Chapel)[0];
        var run = Assert.Single(Trials.At(chapel, cat), t => t.Kind == TrialKind.Run);
        Assert.InRange(run.Length, Trials.RunMin, Trials.RunMax);
        Assert.Same(run, run);
        Assert.Equal(run, Trials.Resolve(run.Id, cat));
        Assert.Null(Trials.Resolve("tr:" + chapel.Id + ":glide", cat));
        Assert.Null(Trials.Resolve("tr:forged", cat));
        var gates = Trials.Course(run, (_, _) => 500);
        Assert.Equal((int)Math.Ceiling(run.Length / Trials.RunGateEvery), gates.Length);
        Assert.Equal(run.ToE, gates[^1].E, 3);
        var par = Trials.Par(run, gates);
        Assert.True(par.Gold < par.Silver && par.Silver < par.Bronze);
        Assert.Equal(3, Trials.Medal(par.Gold - 1, par));
        Assert.Equal(2, Trials.Medal(par.Gold + 1, par));
        Assert.Equal(1, Trials.Medal(par.Bronze, par));
        Assert.Equal(0, Trials.Medal(par.Bronze + 1, par));
    }

    [Fact]
    public void A_glide_keeps_over_the_terrain()
    {
        var d = new TrialDef("tr:x:glide", "x", TrialKind.Glide, "a", "b", 2600000, 1200000, 2000, 2604000, 1200000, 1000);
        var gates = Trials.Course(d, (e, _) => e > 2602000 && e < 2602500 ? 1800f : 900f);
        Assert.Equal(10, gates.Length);
        Assert.All(gates, g => Assert.True(g.Alt >= (g.E > 2602000 && g.E < 2602500 ? 1825 : 925) - 0.01));
    }

    [Fact]
    public void The_board_keeps_each_players_best_top_ten()
    {
        var b = new TrialBoard();
        Assert.Equal(0, b.Submit("t", "lea", "Lea", 90, 1));
        Assert.Equal(-1, b.Submit("t", "lea", "Lea", 95, 2));
        Assert.Equal(0, b.Submit("t", "tom", "Tom", 80, 3));
        Assert.Equal(0, b.Submit("t", "lea", "Lea", 70, 4));
        Assert.Equal(new[] { "lea", "tom" }, b.Of("t").Select(r => r.Key));
        for (int i = 0; i < 12; i++) b.Submit("t", "p" + i, "P", 100 + i, 5);
        Assert.Equal(TrialBoard.Top, b.Of("t").Count);
        Assert.Equal(-1, b.Submit("t", "late", "Late", 500, 6));
        var back = TrialBoard.FromData(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<TrialEntry>>>(
            System.Text.Json.JsonSerializer.Serialize(b.ToData())));
        Assert.Equal(b.Of("t").Select(r => r.Key), back.Of("t").Select(r => r.Key));
    }

    [Fact]
    public void Gates_count_in_order_at_a_possible_pace()
    {
        var d = new TrialDef("tr:x:run", "x", TrialKind.Run, "a", "b", 2600000, 1200000, 500, 2600600, 1200000, 500);
        var gates = Trials.Course(d, (_, _) => 500);
        var runs = new TrialRuns();
        runs.Start("a", d, gates, 0);
        Assert.Equal(TrialGateResult.Ignored, runs.Gate("a", d.Id, 1, 30, out _));
        Assert.Equal(TrialGateResult.Next, runs.Gate("a", d.Id, 0, 40, out _));
        Assert.Equal(TrialGateResult.Next, runs.Gate("a", d.Id, 1, 80, out _));
        Assert.Equal(TrialGateResult.Finished, runs.Gate("a", d.Id, 2, 120, out double secs));
        Assert.Equal(120, secs, 6);
        Assert.False(runs.Running("a", out _, out _, out _));
        // 200 m in 2 s on foot: out
        runs.Start("b", d, gates, 0);
        Assert.Equal(TrialGateResult.TooFast, runs.Gate("b", d.Id, 0, 2, out _));
        Assert.Equal(TrialGateResult.NotRunning, runs.Gate("b", d.Id, 1, 50, out _));
    }

    [Fact]
    public void A_ghost_round_trips_and_forged_bytes_are_refused()
    {
        var samples = Enumerable.Range(0, 240).Select(i => new GhostTrack.Sample(2600000 + i * 1.5, 1200000 + i * 0.5, 500 + i * 0.1f, i * 0.01f)).ToList();
        var bytes = GhostTrack.Encode(samples);
        Assert.True(bytes.Length < 4200);
        var back = GhostTrack.Decode(bytes)!;
        Assert.Equal(samples.Count, back.Count);
        Assert.Equal(samples[^1].E, back[^1].E, 2);
        Assert.Equal(samples[^1].Alt, back[^1].Alt, 3);
        Assert.Null(GhostTrack.Decode(new byte[30]));
        var cut = bytes[..^4];
        Assert.Null(GhostTrack.Decode(cut));
    }
}

public class RewardSectionTests
{
    [Fact]
    public void Every_rewards_section_round_trips()
    {
        var p = new PlayerProgress("k");
        var chests = p.Get(ChestsProgress.Section);
        chests.Opened.Add("ch:1_2");
        chests.Pity[2] = 3;
        chests.Owned.Add(nameof(ItemId.GuideHat));
        chests.ByTier[1] = 4;
        chests.ByCanton["VS"] = 2;
        p.Touch(ChestsProgress.Section);
        var collect = p.Get(CollectProgress.Section);
        collect.Taken.Add("ew:1_2");
        collect.Held = 3;
        collect.Offered = 10;
        collect.Fauna["Cow"] = 2;
        p.Touch(CollectProgress.Section);
        p.Get(TrialsProgress.Section).Best["tr:a:run"] = new TrialBest { Seconds = 61.5, Medal = 3, Runs = 2 };
        p.Touch(TrialsProgress.Section);
        p.Get(PhotosProgress.Section).Taken.Add("ph:chillon");
        p.Touch(PhotosProgress.Section);
        p.Get(GadgetsProgress.Section).Had["TreasureDetector"] = 1;
        p.Touch(GadgetsProgress.Section);
        Assert.Empty(p.Untouched());

        var back = AdventureBook.FromJson(AdventureBook.ToJson(p));
        var c2 = back.Get(ChestsProgress.Section);
        Assert.Contains("ch:1_2", c2.Opened);
        Assert.Equal(3, c2.Pity[2]);
        Assert.Contains("GuideHat", c2.Owned);
        Assert.Equal(4, c2.Count);
        Assert.Equal(2, c2.ByCanton["VS"]);
        var k2 = back.Get(CollectProgress.Section);
        Assert.Equal(3, k2.Held);
        Assert.Equal(10, k2.Offered);
        Assert.Equal(2, k2.Fauna["Cow"]);
        Assert.Equal(61.5, back.Get(TrialsProgress.Section).Best["tr:a:run"].Seconds);
        Assert.Contains("ph:chillon", back.Get(PhotosProgress.Section).Taken);
        Assert.Equal(1, back.Get(GadgetsProgress.Section).Had["TreasureDetector"]);
    }

    [Fact]
    public void Edelweiss_offered_is_an_adventure_event()
    {
        var bus = new AdventureEvents();
        AdventureEvent? got = null;
        bus.Raised += e => got = e;
        bus.Raise(new EdelweissOffered(1, "k", "chapel:x.00", 10));
        Assert.Equal(10, Assert.IsType<EdelweissOffered>(got).Offered);
    }
}
