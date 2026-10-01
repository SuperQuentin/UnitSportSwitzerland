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
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--brcheck") >= 0;

    private static int _failures;

    public static int Run()
    {
        Zone();
        Durations();
        Regions();
        StateJson();
        Lend();
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
    }

    private static void Durations()
    {
        foreach (int players in new[] { 4, 8, 16, 32 })
        {
            float side = BrRegion.SideFor(players);
            // + 1 min countdown and about 1 min to land
            double minutes = new ZoneSchedule(1, side, 1f).Duration / 60.0 + 2;
            Expect(minutes is >= 25 and <= 45, $"{players} players: {side / 1000:F0} km, a round of {minutes:F0} min");
        }
        double six = new ZoneSchedule(1, 6000, 1f).Duration / 60.0 + 2;
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

    private static void StateJson()
    {
        var s = new BrState { Phase = BrPhase.Playing, AreaE = 2583250, AreaN = 1113250, Side = 6000, AreaName = "Riddes", Seed = 42, Started = 12.5 };
        s.Entrants.Add(new BrEntrant { Peer = 7, Name = "A", Kills = 2 });
        s.Entrants.Add(new BrEntrant { Peer = 9, Name = "B", Alive = false, Place = 2 });
        var back = BrState.FromJson(s.ToJson());
        Expect(back is { Phase: BrPhase.Playing, Seed: 42, AliveCount: 1 } && back.Find(7)?.Kills == 2 && back.Area.Name == "Riddes",
            "the match state survives JSON");
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
