using UnitSport.Terrain.Format;
using UnitSport.World;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The object containers' book (src/World/ContainerRules.cs, #689): an entity is live or filed,
/// exactly once, through every sleep, wake, pick-up and crash.
/// </summary>
public class ContainerBookTests
{
    /// <summary>A disk in memory that can crash before any write.</summary>
    private sealed class FakeDisk : IContainerDisk
    {
        public readonly Dictionary<TileId, List<ContainerRecord>> Files = new();
        public List<ContainerRecord> Checkpoint = new();
        /// <summary>Writes left before the next one throws; null = never.</summary>
        public int? CrashIn;
        public int Writes;

        public IEnumerable<TileId> Tiles() => Files.Keys.ToList();
        public List<ContainerRecord> ReadTile(TileId id) => Files.TryGetValue(id, out var l) ? l.ToList() : new();
        public List<ContainerRecord> ReadCheckpoint() => Checkpoint.ToList();

        public void WriteTile(TileId id, List<ContainerRecord> records)
        {
            Tick();
            if (records.Count == 0) Files.Remove(id);
            else Files[id] = records.ToList();
        }

        public void WriteCheckpoint(List<ContainerRecord> records)
        {
            Tick();
            Checkpoint = records.ToList();
        }

        private void Tick()
        {
            Writes++;
            if (CrashIn is { } n)
            {
                if (n == 0) { CrashIn = null; throw new Crash(); }
                CrashIn = n - 1;
            }
        }

        public List<ContainerRecord> All() => Files.Values.SelectMany(l => l).Concat(Checkpoint).ToList();
    }

    private sealed class Crash : Exception { }

    private static ContainerRecord Rec(long oid, int tileE, int tileN, string kind = ContainerRecord.Vehicle, double savedAt = 0) =>
        new(oid, 0, kind, $"veh_{oid}", tileE * 1000.0 + 500, tileN * 1000.0 + 500, savedAt, $"data{oid}");

    [Fact]
    public void Sleep_files_by_tile_and_wake_brings_it_back_one_generation_on()
    {
        var disk = new FakeDisk();
        var book = new ContainerBook(disk);
        book.Load(0);
        book.Track(Rec(1, 2600, 1200));
        book.Track(Rec(2, 2601, 1200));
        Assert.Equal(new HashSet<long> { 1, 2 }, book.Sleep(new[] { Rec(1, 2600, 1200), Rec(2, 2601, 1200) }, 10));
        Assert.Equal(0, book.LiveCount);
        Assert.True(book.HasFile(new TileId(2600, 1200)));
        Assert.True(book.IsFiled("veh_1"));
        Assert.Empty(disk.Checkpoint);
        Assert.Equal(1, disk.Files[new TileId(2600, 1200)].Single().Gen);

        var woken = book.Wake(new TileId(2600, 1200), 20);
        Assert.Equal(2, woken.Single().Gen);
        Assert.True(book.IsLive(1));
        Assert.False(book.IsFiled("veh_1"));
        Assert.False(disk.Files.ContainsKey(new TileId(2600, 1200)));
        Assert.Equal(1, disk.Checkpoint.Single().Oid);
        Assert.True(book.IsFiled("veh_2"));
    }

    [Fact]
    public void Untrack_only_removes_the_node_of_that_name()
    {
        var book = new ContainerBook(new FakeDisk());
        book.Load(0);
        book.Track(Rec(7, 0, 0));
        // a thrower left: the same item respawned as drop_srv_1, the old node leaves after
        book.Track(Rec(7, 0, 0) with { Name = "drop_srv_1" });
        Assert.False(book.Untrack(7, "veh_7"));
        Assert.True(book.IsLive(7));
        Assert.True(book.Untrack(7, "drop_srv_1"));
        Assert.False(book.IsLive(7));
    }

    [Fact]
    public void Load_files_what_was_live_and_keeps_the_higher_generation()
    {
        var disk = new FakeDisk();
        // a crash between a wake's two writes: the tile still has gen 1, the checkpoint gen 2
        disk.Files[new TileId(5, 5)] = new() { Rec(1, 5, 5) with { Gen = 1, Data = "old" } };
        disk.Checkpoint = new() { Rec(1, 6, 5) with { Gen = 2, Data = "new" }, Rec(3, 5, 5) };
        var book = new ContainerBook(disk);
        Assert.Equal(2, book.Load(0));
        var all = disk.All();
        Assert.Equal(2, all.Count);
        Assert.Equal("new", all.Single(r => r.Oid == 1).Data);
        Assert.Equal(new TileId(6, 5), all.Single(r => r.Oid == 1).Tile);
        Assert.Empty(disk.Checkpoint);
    }

    [Fact]
    public void Items_expire_and_tiles_are_capped_oldest_first()
    {
        var disk = new FakeDisk();
        disk.Files[new TileId(0, 0)] = new() { Rec(1, 0, 0, ContainerRecord.Item, savedAt: 0) };
        var book = new ContainerBook(disk);
        Assert.Equal(0, book.Load(ContainerBook.ItemLifetime + 1));
        Assert.Empty(disk.Files);

        var list = Enumerable.Range(0, ContainerBook.MaxVehiclesPerTile + 3).Select(i => Rec(i, 0, 0, savedAt: i)).ToList();
        Assert.Equal(3, ContainerBook.Cap(list));
        Assert.Equal(3, list.Min(r => r.Oid));
    }

    [Fact]
    public void A_name_another_filed_entity_holds_does_not_sleep()
    {
        var book = new ContainerBook(new FakeDisk());
        book.Load(0);
        book.Track(Rec(1, 0, 0));
        book.Sleep(new[] { Rec(1, 0, 0) }, 1);
        book.Track(Rec(2, 0, 0) with { Name = "veh_1" });
        Assert.Empty(book.Sleep(new[] { Rec(2, 0, 0) with { Name = "veh_1" } }, 2));
        Assert.True(book.IsLive(2));
    }

    [Fact]
    public void File_disk_round_trips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "usw_containers_" + Guid.NewGuid().ToString("N"));
        try
        {
            var book = new ContainerBook(new ContainerFileDisk(dir));
            book.Load(0);
            book.Track(Rec(1, 2500, 1100) with { Data = "{\"x\": \"Vector3(1, 2, 3)\"}" });
            book.Sleep(new[] { Rec(1, 2500, 1100) with { Data = "{\"x\": \"Vector3(1, 2, 3)\"}" } }, 5);
            Assert.True(File.Exists(Path.Combine(dir, "2500_1100.json")));

            var again = new ContainerBook(new ContainerFileDisk(dir));
            Assert.Equal(1, again.Load(6));
            var woken = again.Wake(new TileId(2500, 1100), 7).Single();
            Assert.Equal("{\"x\": \"Vector3(1, 2, 3)\"}", woken.Data);
            Assert.Equal(2, woken.Gen);
            Assert.False(File.Exists(Path.Combine(dir, "2500_1100.json")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Woken_slots_stay_awake_a_week_or_while_their_vehicle_sleeps()
    {
        double now = 10 * ContainerRules.AwakeFor;
        var saved = new Dictionary<string, AwakeSlot>
        {
            ["2600_1200|3"] = new(now - 3600),                         // woken an hour ago
            ["2600_1200|4"] = new(now - ContainerRules.AwakeFor - 1),  // long ago, its car gone
            ["2600_1200|5"] = new(0),                                  // long ago, its car asleep in a container
        };
        var keep = ContainerRules.KeepAwake(saved, now, key => key == "2600_1200|5");
        Assert.Equal(new[] { "2600_1200|3", "2600_1200|5" }, keep.Keys.Order());
    }

    [Fact]
    public void A_car_is_back_in_its_bay_only_as_the_slot_left_it()
    {
        var bay = new AwakeSlot(0, 2600100.0, 1200200.0, 1.5f, KindId: 70, Train: 0, Load: 0.5f);
        // the woken car never moved, or was parked back 20 cm off and 3 degrees round
        Assert.True(ContainerRules.BackInBay(bay, 2600100, 1200200, 1.5f, 70, 0, 0.5f, pristine: true));
        Assert.True(ContainerRules.BackInBay(bay, 2600100.2, 1200200, 1.5f + 3 * MathF.PI / 180, 70, 0, 0.5f, true));
        // a heading a whole turn round is the same heading
        Assert.True(ContainerRules.BackInBay(bay, 2600100, 1200200, 1.5f + 2 * MathF.PI, 70, 0, 0.5f, true));
        // half a metre off, backed in, another car, a trailer on, damaged or tuned: it stays a real car
        Assert.False(ContainerRules.BackInBay(bay, 2600100.5, 1200200, 1.5f, 70, 0, 0.5f, true));
        Assert.False(ContainerRules.BackInBay(bay, 2600100, 1200200, 1.5f + MathF.PI, 70, 0, 0.5f, true));
        Assert.False(ContainerRules.BackInBay(bay, 2600100, 1200200, 1.5f, 71, 0, 0.5f, true));
        Assert.False(ContainerRules.BackInBay(bay, 2600100, 1200200, 1.5f, 70, 3, 0.5f, true));
        Assert.False(ContainerRules.BackInBay(bay, 2600100, 1200200, 1.5f, 70, 0, 0.5f, pristine: false));
        // a slot restored without its pose (an old save) can only stay awake
        Assert.False(ContainerRules.BackInBay(new AwakeSlot(0), 2600100, 1200200, 1.5f, 70, 0, 0.5f, true));
    }

    [Fact]
    public void Radios_have_their_own_cap()
    {
        var list = Enumerable.Range(0, ContainerBook.MaxRadiosPerTile + 2).Select(i => Rec(i, 0, 0, ContainerRecord.Radio, savedAt: i))
            .Concat(new[] { Rec(1000, 0, 0) }).ToList();
        Assert.Equal(2, ContainerBook.Cap(list));
        Assert.Equal(ContainerBook.MaxRadiosPerTile, list.Count(r => r.Kind == ContainerRecord.Radio));
        Assert.Contains(list, r => r.Oid == 1000);
    }

    [Fact]
    public void Awake_slots_round_trip_through_json()
    {
        var saved = new Dictionary<string, AwakeSlot> { ["1_2|3"] = new(5, 1.5, 2.5, 0.25f, 70, 0, 0.5f), ["1_2|4"] = new(6) };
        var back = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, AwakeSlot>>(System.Text.Json.JsonSerializer.Serialize(saved))!;
        Assert.Equal(saved["1_2|3"], back["1_2|3"]);
        Assert.Null(back["1_2|4"].E);
    }

    [Fact]
    public void Wake_takes_the_tiles_within_two_rings_of_any_player()
    {
        var files = new HashSet<TileId> { new(10, 10), new(12, 10), new(13, 10), new(0, 0) };
        var wake = ContainerRules.ToWake(new[] { new TileId(10, 10), new TileId(11, 10) }, files.Contains);
        Assert.Equal(new[] { new TileId(10, 10), new TileId(12, 10), new TileId(13, 10) }, wake.OrderBy(t => t.E));
        Assert.Equal(0, ContainerRules.Lonely(42, 5, new TileId(13, 10), new[] { new TileId(10, 10) }));
        Assert.Equal(47, ContainerRules.Lonely(42, 5, new TileId(14, 10), new[] { new TileId(10, 10) }));
    }

    /// <summary>
    /// Thousands of random sleeps, wakes, moves, pick-ups and new entities, with a crash before
    /// some write now and then and a restart from the disk after it. After every restart each
    /// entity is on the disk exactly once; the only ones that may be missing are one being taken
    /// out (its pick-up was never granted, so it may also still be there) or one just appearing.
    /// </summary>
    [Fact]
    public void Model_check_every_entity_stays_exactly_once_through_crashes()
    {
        var rng = new Random(689);
        for (int run = 0; run < 60; run++)
        {
            var disk = new FakeDisk();
            var book = new ContainerBook(disk);
            book.Load(0);
            var alive = new HashSet<long>();
            long next = 1;
            double now = 0;
            TileId RandomTile() => new(rng.Next(4), rng.Next(4));

            for (int step = 0; step < 400; step++)
            {
                now += 1;
                if (rng.Next(25) == 0) disk.CrashIn = rng.Next(3);
                long? adding = null, removing = null;
                try
                {
                    switch (rng.Next(6))
                    {
                        case 0:
                            adding = next++;
                            var t = RandomTile();
                            book.Track(Rec(adding.Value, t.E, t.N, rng.Next(2) == 0 ? ContainerRecord.Item : ContainerRecord.Vehicle, now));
                            alive.Add(adding.Value);
                            break;
                        case 1:
                            if (LiveOids(book, alive) is { Count: > 0 } live)
                            {
                                removing = live[rng.Next(live.Count)];
                                if (book.Untrack(removing.Value, $"veh_{removing}")) alive.Remove(removing.Value);
                            }
                            break;
                        case 2:
                            {
                                var live2 = LiveOids(book, alive);
                                var some = live2.Where(_ => rng.Next(2) == 0).Select(o => book.LiveRecord(o)!).ToList();
                                book.Sleep(some, now);
                            }
                            break;
                        case 3:
                            if (book.FiledTiles.Count > 0)
                                book.Wake(book.FiledTiles.ElementAt(rng.Next(book.FiledTiles.Count)), now);
                            break;
                        case 4:
                            foreach (var o in LiveOids(book, alive))
                            {
                                var t2 = RandomTile();
                                book.Update(book.LiveRecord(o)! with { E = t2.E * 1000.0 + 1, N = t2.N * 1000.0 + 1, SavedAt = now });
                            }
                            book.FlushCheckpoint();
                            break;
                        case 5:
                            Restart();
                            break;
                    }
                    adding = removing = null;
                }
                catch (Crash)
                {
                    Restart();
                }

                void Restart()
                {
                    disk.CrashIn = null;
                    book = new ContainerBook(disk);
                    book.Load(now);
                    var all = disk.All();
                    // exactly once: no oid twice, nothing left on the checkpoint
                    Assert.Equal(all.Count, all.Select(r => r.Oid).Distinct().Count());
                    Assert.Empty(disk.Checkpoint);
                    var onDisk = all.Select(r => r.Oid).ToHashSet();
                    // items past their lifetime may go; the test's clock never gets there
                    var expected = alive.ToHashSet();
                    if (removing is { } r1) { expected.Remove(r1); onDisk.Remove(r1); }
                    if (adding is { } a1) { expected.Remove(a1); onDisk.Remove(a1); }
                    Assert.Equal(expected, onDisk);
                    // the model follows what the disk says for the one in flight
                    if (removing is { } r2 && all.Any(r => r.Oid == r2)) alive.Add(r2);
                    if (adding is { } a2 && !all.Any(r => r.Oid == a2)) alive.Remove(a2);
                    adding = removing = null;
                }

                // between crashes, the book itself accounts for everything
                Assert.Equal(alive.Count, book.LiveCount + book.FiledCount);
            }
        }
    }

    private static List<long> LiveOids(ContainerBook book, HashSet<long> alive) => alive.Where(book.IsLive).OrderBy(o => o).ToList();
}
