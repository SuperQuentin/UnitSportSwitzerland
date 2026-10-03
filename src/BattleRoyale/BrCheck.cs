using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.BattleRoyale;

/// <summary>
/// <c>--brcheck</c>: the Battle Royale's pure parts, headless and without a world.
/// <list type="bullet">
/// <item>The zone is the same for a seed, and each circle lies inside the one before.</item>
/// <item>A round lasts 30 to 45 minutes at the automatic size and normal pace.</item>
/// <item>Over the real <c>places.json</c> and manifest when they are found, 200 seeds all give a
/// region that passes the hard rules, and five rounds in a row never land within 8 km of each other.</item>
/// <item>The match state survives a JSON round trip.</item>
/// <item>An inventory lent to a match comes back as it was.</item>
/// </list>
/// Prints a RESULT line and exits non-zero if any case is wrong.
/// </summary>
public static class BrCheck
{
    public static bool Requested => CmdArgs.Has("--brcheck");

    private static int _failures;

    public static int Run()
    {
        Zone();
        Durations();
        Flights();
        Regions();
        StateJson();
        Polish();
        Lend();
        Loot();
        GD.Print(_failures == 0 ? "[brcheck] RESULT PASS" : $"[brcheck] RESULT FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    private static void Zone()
    {
        for (int seed = 1; seed <= 300; seed++)
        {
            var a = new ZoneSchedule(seed, 6000, 1);
            var b = new ZoneSchedule(seed, 6000, 1);
            for (int i = 0; i <= ZoneSchedule.Phases; i++)
            {
                if (a.CentreOf(i) != b.CentreOf(i)) { Expect(false, $"seed {seed}: zone {i} differs between two builds"); return; }
                if (i > 0 && a.CentreOf(i).DistanceTo(a.CentreOf(i - 1)) + a.RadiusOf(i) > a.RadiusOf(i - 1) + 0.01f)
                { Expect(false, $"seed {seed}: circle {i} leaves circle {i - 1}"); return; }
                if (Math.Abs(a.CentreOf(i).X) > 3000 || Math.Abs(a.CentreOf(i).Y) > 3000)
                { Expect(false, $"seed {seed}: centre {i} outside the square"); return; }
            }
            // the live circle shrinks without ever growing
            float last = float.MaxValue;
            for (double t = 0; t <= a.Duration + 5; t += 2)
            {
                var z = a.At(t);
                if (z.Radius > last + 0.01f) { Expect(false, $"seed {seed}: the radius grew at {t:F0} s"); return; }
                last = z.Radius;
            }
        }
        Expect(true, "zone: 300 seeds deterministic, nested, inside the square, never growing");
        var s = new ZoneSchedule(7, 6000, 1);
        Expect(s.At(0).Phase == 0 && s.At(0).Dps == 0, "no damage while looting");
        Expect(s.At(s.Duration + 1).Over, "the zone is over once the last shrink ends");
        // /br zone (#425): from the loot time or a wait, the next shrink; none while shrinking or over
        double first = s.NextShrinkAt(0) ?? -1, wait2 = first;
        while (wait2 < s.Duration && s.At(wait2) is not { Phase: 2, Shrinking: false }) wait2 += 1;
        double second = s.NextShrinkAt(wait2) ?? -1;
        Expect(s.At(first) is { Phase: 1, Shrinking: true } && !s.At(first - 0.01).Shrinking && s.NextShrinkAt(first + 1) is null
            && s.At(second) is { Phase: 2, Shrinking: true } && !s.At(second - 0.01).Shrinking && s.NextShrinkAt(s.Duration + 1) is null,
            FormattableString.Invariant($"/br zone: the next shrink from the loot time ({first:F0} s) and from phase 2's wait ({second:F0} s), none while shrinking or over"));
    }

    private static double FlightMinutes(float side)
    {
        var f = new BrFlight(1, side, 1f, 0, 0);
        return f.ClosesAt / 60.0;
    }

    /// <summary>The cargo plane's line (#207): the doors open and close inside the square, long enough, seeded.</summary>
    private static void Flights()
    {
        int bad = 0;
        double shortest = double.MaxValue, longest = 0, slowest = 0;
        for (int seed = 1; seed <= 300; seed++)
            foreach (float side in new[] { 5000f, 6000f, 7000f })
            {
                var f = new BrFlight(seed, side, 1f, 100, 2000);
                var g = new BrFlight(seed, side, 1f, 100, 2000);
                var (open, shut) = f.JumpStretch;
                float h = side * 0.5f + 1f;
                bool inside = Math.Abs(open.X) <= h && Math.Abs(open.Y) <= h && Math.Abs(shut.X) <= h && Math.Abs(shut.Y) <= h;
                float stretch = open.DistanceTo(shut);
                bool outsideBefore = Math.Abs(f.From.X) > h - 2 || Math.Abs(f.From.Y) > h - 2;
                if (!inside || !outsideBefore || stretch < side * 0.55f || f.From != g.From || f.Dir != g.Dir) bad++;
                shortest = Math.Min(shortest, stretch / side);
                longest = Math.Max(longest, stretch / side);
                slowest = Math.Max(slowest, f.ClosesAt - f.Start);
            }
        Expect(bad == 0 && slowest < 150, FormattableString.Invariant($"plane lines: 900 seeded, doors open and close inside the square, a jump stretch of {shortest:F2}-{longest:F2} × the side, at most {slowest:F0} s to the doors closing ({bad} bad)"));
        var fast = new BrFlight(3, 5000, 0.05f, 0, 0);
        Expect(fast.Speed == BrFlight.Cruise * 2 && fast.At(fast.OpensAt).DistanceTo(fast.JumpStretch.A) < 1f,
            FormattableString.Invariant($"a test pace flies 2 × faster; the plane is at the door point when they open"));
        float alt = BrFlight.AltitudeOver(3, 6000, _ => 2900);
        Expect(alt == 2900 + BrFlight.Clearance && BrFlight.AltitudeOver(3, 6000, _ => 300) == BrFlight.MinAltitude,
            FormattableString.Invariant($"altitude: {BrFlight.Clearance:F0} m over the highest ground under the line, never under {BrFlight.MinAltitude:F0} m"));
    }

    private static void Durations()
    {
        foreach (int players in new[] { 4, 8, 16, 32 })
        {
            float side = BrRegion.SideFor(players);
            // + 1 min countdown, the flight to the doors closing (#207), about 1 min to land
            double minutes = new ZoneSchedule(1, side, 1f).Duration / 60.0 + 2 + FlightMinutes(side);
            Expect(minutes is >= 25 and <= 45, $"{players} players: {side / 1000:F0} km, a round of {minutes:F0} min");
        }
        double six = new ZoneSchedule(1, 6000, 1f).Duration / 60.0 + 2 + FlightMinutes(6000);
        Expect(six is >= 30 and <= 45, $"6 km normal: {six:F0} min, in the 30-45 min target");
    }

    private static void Regions()
    {
        var (places, tiles) = LoadWorld();
        if (places.Count == 0 || tiles.Count == 0)
        {
            GD.Print("[brcheck] no places.json/manifest found: region picking checked on generated ground only");
            var g = BrRegion.Pick(1, 6000, places, tiles, new List<(double, double)>(), (2583250, 1113250));
            Expect(g.Side == 6000, "generated ground still gets a region");
            return;
        }
        var byTile = tiles.ToDictionary(t => (t.E, t.N));
        var towns = places.Where(p => p.Kind == PlaceKind.Town && p.Buildings >= 15).ToList();
        int bad = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int seed = 1; seed <= 200; seed++)
        {
            var area = BrRegion.Pick(seed, BrRegion.SideFor(seed % 33), places, tiles, new List<(double, double)>(), (0, 0));
            if (BrRegion.Score(area, towns, byTile, new List<(double, double)>()) == null)
            {
                bad++;
                GD.Print($"[brcheck] seed {seed}: {area.Name} fails the rules");
            }
        }
        Expect(bad == 0, $"200 random regions pass the hard rules ({bad} failed, {watch.ElapsedMilliseconds / 200.0:F0} ms each)");

        var recent = new List<(double E, double N)>();
        var names = new List<string>();
        bool close = false;
        for (int round = 0; round < 5; round++)
        {
            var area = BrRegion.Pick(1000 + round, 6000, places, tiles, recent, (0, 0));
            close |= recent.Any(r => Math.Sqrt((r.E - area.E) * (r.E - area.E) + (r.N - area.N) * (r.N - area.N)) < BrRegion.RepeatKm * 1000);
            recent.Add((area.E, area.N));
            names.Add(area.Name);
        }
        Expect(!close, $"five rounds, none within {BrRegion.RepeatKm} km of another: {string.Join(", ", names)}");

        // the map of one of them, from the real tiles
        var mapped = BrRegion.Pick(77, 6000, places, tiles, new List<(double, double)>(), (0, 0));
        var source = new LocalChunkSource(TerrainPaths.FindChunkDir());
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var bytes = Task.Run(() => BrMapImage.BuildAsync(source, mapped)).GetAwaiter().GetResult();
        var image = Image.CreateFromData(BrMapImage.Size, BrMapImage.Size, false, Image.Format.Rgba8, bytes);
        string file = ProjectSettings.GlobalizePath("res://test_output/br_map_check.png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        image.SavePng(file);
        int colours = bytes.Chunk(4).Select(p => (p[0], p[1], p[2])).Distinct().Count();
        Expect(colours > 40, $"the map of {mapped.Name} built in {timer.ElapsedMilliseconds} ms, {colours} colours, {file}");
        Roadside(places, tiles);
    }

    private static (List<Place> Places, List<ManifestTile> Tiles) LoadWorld()
    {
        try
        {
            string dir = TerrainPaths.FindChunkDir();
            string pf = System.IO.Path.Combine(dir, PlaceIndex.FileName), mf = System.IO.Path.Combine(dir, "manifest.json");
            if (!System.IO.File.Exists(pf) || !System.IO.File.Exists(mf)) return (new(), new());
            var places = PlaceIndex.FromJson(System.IO.File.ReadAllText(pf)).Places;
            var manifest = System.Text.Json.JsonSerializer.Deserialize<TerrainManifest>(System.IO.File.ReadAllText(mf), TerrainManifest.JsonOptions);
            return (places, manifest?.Tiles ?? new());
        }
        catch (Exception e)
        {
            GD.Print($"[brcheck] could not read the world: {e.Message}");
            return (new(), new());
        }
    }

    /// <summary>Squads groundwork, stings and display choices (#231).</summary>
    private static void Polish()
    {
        // teams: filled in a seeded shuffle, the last one short; solo is all 0
        BrState Field(int size, int players)
        {
            var s = new BrState { Seed = 99, TeamSize = size };
            for (int i = 1; i <= players; i++) s.Entrants.Add(new BrEntrant { Peer = i * 10, Name = $"P{i}" });
            s.AssignTeams();
            return s;
        }
        var duos = Field(2, 7);
        var sizes = duos.Entrants.GroupBy(e => e.Team).OrderBy(g => g.Key).Select(g => g.Count()).ToList();
        bool same = Field(2, 7).Entrants.Select(e => e.Team).SequenceEqual(duos.Entrants.Select(e => e.Team));
        Expect(sizes.SequenceEqual(new[] { 2, 2, 2, 1 }) && same && Field(1, 5).Entrants.All(e => e.Team == 0),
            $"7 players in duos: teams of {string.Join("/", sizes)}, the same every time; solo has no teams");
        var p1 = duos.Entrants.First(e => e.Team == 1);
        var mate = duos.Entrants.First(e => e.Team == 1 && e != p1);
        var foe = duos.Entrants.First(e => e.Team == 2);
        Expect(!BrState.Hostile(p1, mate) && BrState.Hostile(p1, foe) && duos.MatesOf(p1.Peer).Single() == mate,
            "team-mates cannot hurt each other; other teams can");
        int teams = duos.TeamsAlive;
        foreach (var e in duos.Entrants.Where(e => e.Team != 1)) e.Alive = false;
        mate.Alive = false;
        Expect(teams == 4 && duos.TeamsAlive == 1, $"teams alive count each side once ({teams} at the start, 1 when only team 1 has someone up)");
        var solo = Field(1, 3);
        Expect(solo.TeamsAlive == 3 && BrState.Hostile(solo.Entrants[0], solo.Entrants[1]), "solo: every player is a side of their own");

        // the stings: built, heard, short
        var bad = BrSounds.All().Where(x => x.Stream.Data.Length < 2000 || x.Stream.GetLength() > 6.0).Select(x => x.Name).ToList();
        Expect(bad.Count == 0, $"{BrSounds.All().Count()} stings synthesised, each under 6 s ({string.Join(", ", BrSounds.All().Select(x => $"{x.Name} {x.Stream.GetLength():F1} s"))})");

        // display choices survive their file's JSON
        var prefs = new BrPrefs { Minimap = BrPrefs.MapSize.Large, MinimapTurns = true, Compass = false, Stings = false };
        var again = System.Text.Json.JsonSerializer.Deserialize<BrPrefs>(System.Text.Json.JsonSerializer.Serialize(prefs))!;
        Expect(again is { Minimap: BrPrefs.MapSize.Large, MinimapTurns: true, Compass: false, Stings: false } && again.MinimapSide == 290f,
            "display choices survive their file (large minimap, turning, no compass, no stings)");
    }

    private static void StateJson()
    {
        var s = new BrState { Phase = BrPhase.Playing, AreaE = 2583250, AreaN = 1113250, Side = 6000, AreaName = "Riddes", Seed = 42, Started = 12.5 };
        s.Entrants.Add(new BrEntrant { Peer = 7, Name = "A", Kills = 2 });
        s.Entrants.Add(new BrEntrant { Peer = 9, Name = "B", Alive = false, Place = 2 });
        var back = BrState.FromJson(s.ToJson());
        Expect(back is { Phase: BrPhase.Playing, Seed: 42, AliveCount: 1 } && back.Find(7)?.Kills == 2 && back.Area.Name == "Riddes",
            "the match state survives JSON");
    }

    private static void Loot()
    {
        // guns always come with rounds; the locked containers and the drops always hold their prize
        bool ammo = true, locker = true, drop = true;
        int full = 0;
        for (int seed = 0; seed < 4000; seed++)
        {
            foreach (var t in Enum.GetValues<UnitSport.Loot.MatchTable>())
            {
                var stacks = UnitSport.Loot.MatchLoot.Roll(t, new Random(seed * 13 + (int)t));
                foreach (var s in stacks)
                    if (Weapons.Get(s.Id) is { Melee: false } w && !stacks.Any(o => o.Id == w.Ammo)) ammo = false;
                if (t == UnitSport.Loot.MatchTable.GunLocker && !stacks.Any(s => s.Id is ItemId.Rifle or ItemId.HuntingRifle)) locker = false;
                if (t == UnitSport.Loot.MatchTable.Airdrop && !stacks.Any(s => s.Id is ItemId.Rifle or ItemId.HuntingRifle) ) drop = false;
                if (t == UnitSport.Loot.MatchTable.Furniture && stacks.Count > 0) full++;
            }
        }
        Expect(ammo, "every gun found comes with rounds for it");
        Expect(locker && drop, "gun lockers and supply drops always hold a rifle");
        Expect(full is > 1800 and < 2200, $"half the furniture holds something ({full / 40.0:F1} %)");
        int flares = Enumerable.Range(0, 2000).Count(i => UnitSport.Loot.MatchLoot.Roll(UnitSport.Loot.MatchTable.SacBox, new Random(i)).Any(st => st.Id == ItemId.FlareGun));
        bool bunkers = Enumerable.Range(0, 2000).All(i => UnitSport.Loot.MatchLoot.Roll(UnitSport.Loot.MatchTable.Bunker, new Random(i)).Any(st => st.Id == ItemId.HuntingRifle));
        Expect(bunkers && flares is > 500 and < 900, $"bunkers always hold a hunting rifle; SAC boxes a flare gun {flares / 20.0:F0} % of the time");
        Expect(BrCrates.Combination(12, 99).SequenceEqual(BrCrates.Combination(12, 99)) && !BrCrates.Combination(12, 99).SequenceEqual(BrCrates.Combination(13, 99)),
            "a locked crate's dial numbers are the same everywhere, and differ between crates");

        int drops = BrLoot.DropPhases.Sum(p => BrLoot.DropsAt(p, 6000));
        Expect(drops == 3 && BrLoot.DropsAt(3, 6000) == 0 && BrLoot.DropPhases.Sum(p => BrLoot.DropsAt(p, 5000)) == 2,
            $"supply drops: {drops} at 6 km over phases 2/4/6, 2 at 5 km");

        var s0 = new BrState { Phase = BrPhase.Playing, AreaE = 2583250, AreaN = 1113250, Side = 5000, Seed = 9 };
        BrManager.SetMatchLoot(s0);
        bool inside = UnitSport.Loot.LootTables.MatchEpoch?.Invoke("2583_1113_4") != null;
        bool outside = UnitSport.Loot.LootTables.MatchEpoch?.Invoke("2590_1113_4") == null;
        BrManager.SetMatchLoot(null);
        Expect(inside && outside && UnitSport.Loot.LootTables.MatchEpoch == null, "match loot only in the region's buildings, and only during the match");
    }

    private static void Roadside(IReadOnlyList<Place> places, IReadOnlyList<ManifestTile> tiles)
    {
        var area = BrRegion.Pick(5, 6000, places, tiles, new List<(double, double)>(), (0, 0));
        var source = new LocalChunkSource(TerrainPaths.FindChunkDir());
        var roads = Task.Run(() => BrLoot.RoadPoints(source, area)).GetAwaiter().GetResult();
        var crates = BrLoot.RoadsideCrates(roads, area, 5);
        int supply = crates.Count(c => c.Style == CrateStyle.Supply), army = crates.Count(c => c.Style == CrateStyle.Military);
        // the plane (#207): its altitude from the 100 m lattice clears the real ground under its line
        var horizonIndex = Task.Run(() => source.LoadHorizonAsync()).GetAwaiter().GetResult();
        foreach (int seed in new[] { 5, 41, 77 })
        {
            var region = BrRegion.Pick(seed, 6000, places, tiles, new List<(double, double)>(), (0, 0));
            float alt = BrFlight.AltitudeOver(seed, region.Side, p => BrMapImage.Height(horizonIndex, region.E + p.X, region.N + p.Y));
            var line = new BrFlight(seed, region.Side, 1f, 0, alt);
            double top = Task.Run(async () =>
            {
                var grids = new Dictionary<TileId, Terrain.Format.ChunkGrid?>();
                double best = double.MinValue;
                for (float t = 0; t <= line.Gone; t += 50f)
                {
                    var at = line.From + line.Dir * t;
                    double e = region.E + at.X, n = region.N + at.Y;
                    var id = TileId.FromLv95(e, n);
                    if (!grids.TryGetValue(id, out var g)) grids[id] = g = await source.LoadCoarseChunkAsync(id) ?? await source.LoadChunkAsync(id);
                    if (g != null) best = Math.Max(best, g.SampleHeight(e, n));
                }
                return best;
            }).GetAwaiter().GetResult();
            Expect(alt - top > 300, FormattableString.Invariant($"the plane over {region.Name} flies at {alt:F0} m, {alt - top:F0} m over the highest ground under its line ({top:F0} m)"));
        }

        // the outdoor sites (#198) of two real regions
        foreach (int seed in new[] { 5, 41 })
        {
            var region = BrRegion.Pick(seed, 6000, places, tiles, new List<(double, double)>(), (0, 0));
            var regionRoads = Task.Run(() => BrLoot.RoadPoints(source, region)).GetAwaiter().GetResult();
            var w = System.Diagnostics.Stopwatch.StartNew();
            var sites = Task.Run(() => BrSites.Place(source, region, seed, regionRoads)).GetAwaiter().GetResult();
            int Of(CrateStyle st) => sites.Crates.Count(c => c.Style == st);
            Expect(Of(CrateStyle.Bunker) >= 1 && Of(CrateStyle.HighSeat) >= 5 && Of(CrateStyle.Wreck) == 1
                && Of(CrateStyle.HayStash) + Of(CrateStyle.FishingHut) > 0 && sites.Crates.Where(c => c.Style == CrateStyle.Bunker).All(c => c.Locked)
                && sites.Crates.All(c => region.Contains(c.E, c.N) || Math.Abs(c.N - region.N) < region.Side / 2 + 20),
                $"{region.Name} sites in {w.ElapsedMilliseconds} ms: {Of(CrateStyle.Bunker)} bunker(s), {Of(CrateStyle.HighSeat)} high seats, "
                + $"{Of(CrateStyle.HayStash)} hay stashes ({sites.Bikes.Count} bikes), {Of(CrateStyle.SacBox)} SAC boxes, "
                + $"{Of(CrateStyle.Wreck)} wreck, {Of(CrateStyle.FishingHut)} fishing huts");
        }

        Expect(roads.Count > 500 && supply > 200 && army >= 3 && crates.All(c => area.Contains(c.E, c.N) || Math.Abs(c.E - area.E) < area.Side / 2 + 10),
            $"{area.Name}: {roads.Count} road points, {supply} supply crates, {army} army crates");
    }

    private static void Lend()
    {
        var inv = Inventory.Scratch();
        var before = Enumerable.Range(0, Inventory.Size).Select(i => inv[i]).ToList();
        inv.BeginMatch();
        Expect(Enumerable.Range(0, Inventory.Size).All(i => inv[i].IsEmpty) && inv.InMatch, "a match starts with an empty pack");
        inv.Add(ItemId.Rifle, 1);
        inv.Add(ItemId.Ammo75, 40);
        inv.EndMatch();
        var after = Enumerable.Range(0, Inventory.Size).Select(i => inv[i]).ToList();
        Expect(before.SequenceEqual(after) && !inv.InMatch && !inv.Contains(ItemId.Rifle), "the free-roam pack comes back, match loot does not");
    }

    private static void Expect(bool ok, string what)
    {
        GD.Print($"[brcheck] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
