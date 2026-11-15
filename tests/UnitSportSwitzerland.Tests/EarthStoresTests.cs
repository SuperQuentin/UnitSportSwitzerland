using System.Text.Json.Nodes;
using UnitSport.Adventure.Errands;
using UnitSport.Adventure.Rewards;
using UnitSport.Core;
using UnitSport.Loot;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using UnitSport.World;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// G2 D2's stores (#1726, <c>docs/plans/global-tiling-g2-d.md</c> section 2): each migration from the LV95 keys converts an
/// old save by position, and a second run changes nothing.
/// </summary>
public class EarthStoresTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "usw_earthstores_" + Guid.NewGuid().ToString("N"));

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir).ToDictionary(Path.GetFileName, f => File.ReadAllText(f))!;

    // ---- paths and names ------------------------------------------------------------------------------------------

    [Fact]
    public void Earth_paths_sit_under_user_earth_and_leave_other_paths_alone()
    {
        Assert.Equal("user://earth/loot", EarthStores.Earth("user://loot"));
        Assert.Equal("user://earth/loot", EarthStores.Earth("user://earth/loot"));   // once only
        Assert.Equal("C:/checks/farm", EarthStores.Earth("C:/checks/farm"));
        Assert.Equal("user://loot", EarthStores.For("user://loot", earth: false));
    }

    [Fact]
    public void A_leaf_file_stem_names_its_leaf_and_reads_back()
    {
        var at = GlobalPos.FromLv95(2_600_123, 1_200_456, 500);
        var id = LeafTileName.At(at);
        Assert.True(LeafTileName.TryLeaf(id, out var leaf));
        Assert.Equal(CubeKey.FromEcef(at.Ecef, 14), leaf);
        string stem = LeafTileName.FileStem(id);
        Assert.StartsWith("F", stem);
        Assert.True(LeafTileName.TryParseStem(stem, out var back));
        Assert.Equal(id, back);
        // an LV95 tile keeps its flat name, so a flat store's files read as before
        Assert.Equal("2600_1200", LeafTileName.FileStem(new TileId(2600, 1200)));
        Assert.True(LeafTileName.TryParseStem("2600_1200", out var flat));
        Assert.Equal(new TileId(2600, 1200), flat);
        Assert.False(LeafTileName.TryParseStem("checkpoint", out _));
    }

    [Fact]
    public void Leaf_rings_hold_across_a_face_edge()
    {
        // the edge between face cells: the leaf at a face's last column and its neighbour over the edge are one ring apart
        var a = new CubeKey(0, 14, (1 << 14) - 1, 5000);
        var b = a.Neighbour(1, 0)!.Value;
        Assert.NotEqual(a.Face, b.Face);
        Assert.Equal(1, LeafTileName.Rings(LeafTileName.Id(a), LeafTileName.Id(b), 4));
        Assert.Contains(LeafTileName.Id(b), LeafTileName.Around(LeafTileName.Id(a), 2));
        Assert.Equal(25, LeafTileName.Around(LeafTileName.Id(new CubeKey(2, 14, 900, 900)), 2).Count());
    }

    // ---- containers --------------------------------------------------------------------------------------------------

    private static ContainerRecord Rec(long oid, int gen, double e, double n, string kind = ContainerRecord.Vehicle) =>
        new(oid, gen, kind, $"veh_{oid}", e, n, 100 + oid, $"data{oid}");

    [Fact]
    public void Flat_containers_move_to_their_leaves_by_place_once()
    {
        string root = TempDir(), flatDir = Path.Combine(root, "flat"), earthDir = Path.Combine(root, "earth");
        try
        {
            var flat = new ContainerFileDisk(flatDir);
            Directory.CreateDirectory(flatDir);
            var a = Rec(1, 3, 2_600_100.5, 1_200_200.25);
            var b = Rec(2, 1, 2_600_900, 1_200_900, ContainerRecord.Item);
            var far = Rec(3, 1, 2_750_000, 1_150_000, ContainerRecord.Radio);
            flat.WriteTile(new TileId(2600, 1200), new List<ContainerRecord> { a, b, Rec(4, 1, 2_600_500, 1_200_500) });
            flat.WriteTile(new TileId(2750, 1150), new List<ContainerRecord> { far });
            // the checkpoint holds a newer copy of 4 (it woke after it slept): the higher generation wins
            flat.WriteCheckpoint(new List<ContainerRecord> { Rec(4, 2, 2_600_510, 1_200_520) });
            File.WriteAllText(Path.Combine(flatDir, AwakeSlots.FileName), "{\"version\":2,\"slots\":{}}");
            var flatBefore = Snapshot(flatDir);

            Assert.Equal(4, ContainerMigration.FlatToEarth(flatDir, earthDir));

            var earth = new ContainerFileDisk(earthDir, earth: true);
            var tiles = earth.Tiles().ToList();
            Assert.All(tiles, t => Assert.True(LeafTileName.TryLeaf(t, out _)));
            var all = tiles.SelectMany(earth.ReadTile).ToDictionary(r => r.Oid);
            Assert.Equal(new[] { 1L, 2, 3, 4 }, all.Keys.Order());
            foreach (var r in new[] { a, b, far })
            {
                // each in the leaf under its place, at its place (an Earth position, to the millimetre)
                var got = all[r.Oid];
                Assert.Equal(r.Leaf, tiles.Single(t => earth.ReadTile(t).Any(x => x.Oid == r.Oid)));
                Assert.True(GlobalPos.FromLv95(got.E, got.N, 0).DistanceTo(GlobalPos.FromLv95(r.E, r.N, 0)) < 0.002);
                Assert.Equal((r.Gen, r.Kind, r.Name, r.Data, r.SavedAt), (got.Gen, got.Kind, got.Name, got.Data, got.SavedAt));
            }
            Assert.Equal(2, all[4].Gen);
            // the files say no LV95: an "at" in ECEF, no E or N
            string text = File.ReadAllText(Directory.EnumerateFiles(earthDir, "F*.json").First());
            var json = JsonNode.Parse(text)!;
            Assert.Equal(EarthRecord.Version, (int)json["version"]!);
            Assert.NotNull(json["records"]![0]!["at"]);
            Assert.Null(json["records"]![0]!["E"]);
            Assert.True(File.Exists(Path.Combine(earthDir, AwakeSlots.FileName)));
            // the flat folder untouched (the rollback reads it)
            Assert.Equal(flatBefore, Snapshot(flatDir));

            // a second run changes nothing
            var earthBefore = Snapshot(earthDir);
            Assert.Equal(-1, ContainerMigration.FlatToEarth(flatDir, earthDir));
            Assert.Equal(earthBefore, Snapshot(earthDir));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void An_earth_book_files_by_leaf_and_wakes_its_leaf()
    {
        string dir = TempDir();
        try
        {
            var book = new ContainerBook(new ContainerFileDisk(dir, earth: true), ContainerRecord.CellOf(earth: true));
            book.Load(0);
            var r = Rec(1, 0, 2_600_100, 1_200_100);
            book.Track(r);
            book.Sleep(new[] { r }, 5);
            Assert.True(book.HasFile(r.Leaf));
            var again = new ContainerBook(new ContainerFileDisk(dir, earth: true), ContainerRecord.CellOf(earth: true));
            Assert.Equal(1, again.Load(6));
            var wake = ContainerRules.ToWake(new[] { LeafTileName.At(GlobalPos.FromLv95(2_600_900, 1_200_100, 0)) }, again.HasFile);
            Assert.Contains(r.Leaf, wake);
            Assert.Equal(1, again.Wake(r.Leaf, 7).Single().Oid);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Woken_slots_keep_the_leaves_owners_and_drop_the_lv95_ones()
    {
        var leafOwner = LeafTileName.At(GlobalPos.FromLv95(2_600_100, 1_200_100, 0));
        var slots = new Dictionary<string, AwakeSlot>
        {
            ["2600_1200|3"] = new(10, 2_600_100, 1_200_100, 0.5f, 7),
            ["2600_1200_12|0"] = new(11),
            [$"{leafOwner.E}_{leafOwner.N}|2"] = new(12, 2_600_100, 1_200_100, 0.25f, 7),
        };
        string earth = AwakeSlots.ToEarth(AwakeSlots.ToJson(slots));
        Assert.Equal(AwakeSlots.EarthVersion, (int)JsonNode.Parse(earth)!["version"]!);
        var back = AwakeSlots.FromJson(earth, out bool old);
        Assert.False(old);
        Assert.Equal(new[] { $"{leafOwner.E}_{leafOwner.N}|2" }, back.Keys);
        Assert.Equal(0.25f, back.Values.Single().Yaw, 4);
        // a second run changes nothing
        Assert.Equal(earth, AwakeSlots.ToEarth(earth));
    }

    // ---- the adventure's spots ------------------------------------------------------------------------------------

    [Fact]
    public void A_spot_id_saved_before_the_flip_converts_to_the_placer_s_new_id()
    {
        // a spot anywhere in its 10 m cell: the placer's id is the old id's conversion, exactly
        foreach (var (e, n) in new[] { (2_600_153.2, 1_200_347.9), (2_485_009.99, 1_075_000.0), (2_830_001, 1_290_999.5) })
        {
            string old = $"ch:{(long)Math.Floor(e / 10) * 10}_{(long)Math.Floor(n / 10) * 10}";
            string id = RewardRules.CellId(RewardRules.ChestPrefix, e, n);
            Assert.Matches(@"^ch:F\d_L22_\d+_\d+$", id);
            Assert.Equal(id, RewardRules.ToEarthId(old));
            Assert.Equal(id, RewardRules.ToEarthId(id));   // twice: nothing more
            // the tile it is on reads from the new id as from the old
            Assert.True(RewardRules.TryTile(id, _ => null, out var t));
            Assert.Equal(((int)Math.Floor(e / 1000), (int)Math.Floor(n / 1000)), t);
        }
        // neighbouring cells stay apart
        Assert.NotEqual(RewardRules.CellId("ew", 2_600_000, 1_200_000), RewardRules.CellId("ew", 2_600_010, 1_200_000));
        Assert.NotEqual(RewardRules.CellId("ew", 2_600_000, 1_200_000), RewardRules.CellId("ew", 2_600_000, 1_200_010));
        // a Luxurious chest's edition, a POI chest and a cairn
        Assert.Equal(RewardRules.CellId("ch", 2_600_150, 1_200_340) + "#2026", RewardRules.ToEarthId("ch:2600150_1200340#2026"));
        Assert.Equal("ch:peak:matterhorn.1a2b:1", RewardRules.ToEarthId("ch:peak:matterhorn.1a2b:1"));
        Assert.Equal("cn:peak:matterhorn.1a2b", RewardRules.ToEarthId("cn:peak:matterhorn.1a2b"));
    }

    [Fact]
    public void The_chests_and_collect_sections_convert_their_ids_once()
    {
        var chests = JsonNode.Parse("{\"opened\":[\"ch:2600150_1200340\",\"ch:2600150_1200340#2026\",\"ch:hut:x.1:0\"],\"pity\":[1,0,0,0],\"byTier\":[3,0,0,0]}")!;
        var c = ChestsProgress.Section.DecodeTyped(chests, 1);
        string cell = RewardRules.CellId("ch", 2_600_155, 1_200_345);
        Assert.Equal(new[] { cell, cell + "#2026", "ch:hut:x.1:0" }.Order(), c.Opened.Order());
        Assert.Equal(1, c.PityOf(ChestTier.Common));
        Assert.Equal(3, c.Count);
        var again = ChestsProgress.Section.DecodeTyped(ChestsProgress.Section.Encode(c), ChestsProgress.Section.Version);
        Assert.Equal(c.Opened.Order(), again.Opened.Order());

        var collect = JsonNode.Parse("{\"taken\":[\"ew:2700010_1100020\",\"cn:pass:g.2\"],\"held\":2}")!;
        var k = CollectProgress.Section.DecodeTyped(collect, 1);
        Assert.Equal(new[] { RewardRules.CellId("ew", 2_700_010, 1_100_020), "cn:pass:g.2" }.Order(), k.Taken.Order());
        Assert.Equal(2, k.Held);
        var kAgain = CollectProgress.Section.DecodeTyped(CollectProgress.Section.Encode(k), CollectProgress.Section.Version);
        Assert.Equal(k.Taken.Order(), kAgain.Taken.Order());
    }

    // ---- the building-keyed stores --------------------------------------------------------------------------------

    [Fact]
    public void A_leaf_s_shop_records_go_by_its_leaf_and_the_building_s_earth_identity()
    {
        var leaf = new CubeKey(4, 14, 8300, 9100);
        var id = LeafTileName.Id(leaf);
        string key = $"{id.E}_{id.N}_7";
        Fnv.Alias(key, "b4620412_614871");
        Assert.Equal((leaf.FileStem, "b4620412_614871"), ShopLedger.Split(key));
        Assert.Equal("b4620412_614871", EarthStores.Building(key));
        // a flat key keeps its tile file and index
        Assert.Equal(("2600_1200", "7"), ShopLedger.Split("2600_1200_7"));
        Assert.Equal("2600_1200_7", EarthStores.Building("2600_1200_7"));
    }
}
