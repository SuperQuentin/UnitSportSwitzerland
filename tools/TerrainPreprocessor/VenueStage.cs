using System.Diagnostics;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Entertainment venues (#1232): every bowling centre, cinema, arcade, restaurant, café and bar
/// OpenStreetMap knows, written to <c>venues.tsv.gz</c>, which the game embeds
/// (<c>src/Interiors/Venues.cs</c>).
///
/// <para>
/// No dataset we build from says "cinema": swissBUILDINGS3D draws a solid and the GWR files it as
/// commercial. OSM names them, as points (most) or outlines. An outline gives a point inside it. Where
/// built <c>.bldg</c> tiles are at hand, a point that lies on no solid's footprint (mapped on the
/// pavement in front, as many are) is moved onto the nearest solid within <see cref="SnapReach"/>,
/// the way castles are resolved (#859). The game then matches by containment alone, which is exact,
/// unique and a pure function of the tile. Without tiles the OSM point is kept as it is.
/// </para>
/// </summary>
public static class VenueStage
{
    /// <summary>A point this close to a solid, and on none, is moved onto it.</summary>
    public const float SnapReach = 12f;

    private static readonly HashSet<string> Keys = new()
    {
        "amenity", "leisure", "name", "cuisine", "outdoor_seating", "sport", "billiards", "darts", "pinball",
        "table_soccer", "live_music", "karaoke", "lanes", "screen", "amusement_arcade", "disused",
    };

    public static VenueType KindOf(Dictionary<string, string> t)
    {
        if (t.ContainsKey("disused")) return VenueType.None;
        switch (t.GetValueOrDefault("leisure"))
        {
            case "bowling_alley": return VenueType.Bowling;
            case "amusement_arcade": return VenueType.Arcade;
        }
        return t.GetValueOrDefault("amenity") switch
        {
            "restaurant" => VenueType.Restaurant,
            "cafe" => VenueType.Cafe,
            "fast_food" => VenueType.FastFood,
            "bar" => VenueType.Bar,
            "pub" => VenueType.Pub,
            "nightclub" => VenueType.Nightclub,
            "biergarten" => VenueType.Biergarten,
            "cinema" => VenueType.Cinema,
            _ => VenueType.None,
        };
    }

    public static VenueFlags FlagsOf(Dictionary<string, string> t)
    {
        var f = VenueFlags.None;
        var outdoor = t.GetValueOrDefault("outdoor_seating");
        if (outdoor == "no") f |= VenueFlags.NoOutdoor;
        else if (outdoor != null) f |= VenueFlags.Outdoor;
        var sport = ";" + (t.GetValueOrDefault("sport") ?? "") + ";";
        if (sport.Contains(";billiards;") || sport.Contains(";snooker;") || sport.Contains(";pool;") || Yes(t, "billiards"))
            f |= VenueFlags.Billiards;
        if (sport.Contains(";table_soccer;") || Yes(t, "table_soccer")) f |= VenueFlags.TableSoccer;
        if (sport.Contains(";darts;") || Yes(t, "darts")) f |= VenueFlags.Darts;
        if (sport.Contains(";9pin;")) f |= VenueFlags.NinePin;
        if (Yes(t, "pinball") || t.GetValueOrDefault("amusement_arcade") == "pinball") f |= VenueFlags.Pinball;
        if (Yes(t, "live_music")) f |= VenueFlags.LiveMusic;
        if (Yes(t, "karaoke")) f |= VenueFlags.Karaoke;
        return f;
    }

    private static bool Yes(Dictionary<string, string> t, string key) =>
        t.TryGetValue(key, out var v) && v != "no" && v != "0";

    private static int CountOf(Dictionary<string, string> t)
    {
        var v = t.GetValueOrDefault("lanes") ?? t.GetValueOrDefault("screen") ?? "";
        return int.TryParse(v.Split(';')[0], out int n) && n > 0 && n < 100 ? n : 0;
    }

    private static string CuisineOf(Dictionary<string, string> t) =>
        (t.GetValueOrDefault("cuisine") ?? "").Split(';')[0].Trim().ToLowerInvariant();

    public static int Run(string? chunkDir, string pbf, string outFile, int jobs)
    {
        var sw = Stopwatch.StartNew();
        static bool IsVenue(Dictionary<string, string> t) => KindOf(t) != VenueType.None;
        // pass 1: the venue points and outlines; pass 2: the positions of the outlines' nodes only
        var first = PbfReader.Read(pbf, jobs, new PbfReader.Filter
        {
            KeepNode = (_, _) => false, KeepWay = IsVenue, WayTags = Keys,
            KeepTaggedNode = IsVenue, NodeTags = Keys, TaggedNodesAnywhere = true,
        });
        var refs = first.Ways.SelectMany(w => w.Refs).ToHashSet();
        var nodes = PbfReader.Read(pbf, jobs, new PbfReader.Filter
        {
            KeepNode = (_, _) => false, KeepNodeId = refs.Contains, KeepWay = _ => false, WayTags = Keys,
        }).Nodes;
        Console.WriteLine($"venues: {first.TaggedNodes.Count} points, {first.Ways.Count} outlines read ({sw.Elapsed.TotalSeconds:F0} s)");

        var raw = new List<VenueRecord>();
        foreach (var n in first.TaggedNodes)
        {
            var (e, nn) = SwissProjection.ToLv95(n.Lat, n.Lon);
            raw.Add(Record($"n{n.Id}", n.Tags, e, nn));
        }
        int open = 0;
        foreach (var w in first.Ways)
        {
            var ring = new List<(double E, double N)>();
            foreach (var r in w.Refs)
                if (nodes.TryGetValue(r, out var ll)) ring.Add(SwissProjection.ToLv95(ll.Lat, ll.Lon));
            if (ring.Count < 3) continue;
            if (w.Refs[0] != w.Refs[^1]) { open++; continue; }   // a line is no place to stand a venue
            var (e, n) = PointIn(ring);
            raw.Add(Record($"w{w.Id}", w.Tags, e, n));
        }
        // inside Switzerland's box only (the extract carries a strip of its neighbours)
        raw.RemoveAll(r => r.E < 2480000 || r.E > 2840000 || r.N < 1070000 || r.N > 1300000);

        // tiles, when there are any: on a solid, snapped onto one, or out in the open
        var tiles = new Dictionary<TileId, BuildingTile?>();
        BuildingTile? Tile(TileId id)
        {
            if (chunkDir == null) return null;
            lock (tiles)
                if (tiles.TryGetValue(id, out var t)) return t;
            var path = Path.Combine(chunkDir, BuildingFormat.FileName(id));
            BuildingTile? read = null;
            if (File.Exists(path)) { using var fs = File.OpenRead(path); read = BuildingCodec.Decode(fs); }
            lock (tiles) return tiles[id] = read;
        }
        var rows = new VenueRecord[raw.Count];
        Parallel.For(0, raw.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i => rows[i] = Place(raw[i], Tile));

        var list = rows.OrderBy(r => r.E).ThenBy(r => r.N).ThenBy(r => r.Osm, StringComparer.Ordinal).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        using (var fs = File.Create(outFile))
            VenueFormat.Write(fs, $"osm={Path.GetFileName(pbf)} tiles={(chunkDir == null ? "none" : tiles.Values.Count(t => t != null).ToString())}", list);

        Console.WriteLine($"venues: {list.Count} venues ({open} unclosed outlines skipped) -> {outFile} ({sw.Elapsed.TotalSeconds:F0} s)");
        Console.WriteLine("venues by kind: " + string.Join(", ", list.GroupBy(r => r.Type).OrderBy(g => g.Key)
            .Select(g => $"{g.Key} {g.Count()}")));
        Console.WriteLine("venues by fix: " + string.Join(", ", list.GroupBy(r => r.Fix).OrderBy(g => g.Key)
            .Select(g => $"{g.Key} {g.Count()}")));
        foreach (var k in new[] { VenueType.Bowling, VenueType.Cinema, VenueType.Arcade })
            Console.WriteLine($"venues {k}: " + string.Join(", ", list.Where(r => r.Type == k).GroupBy(r => r.Fix).OrderBy(g => g.Key)
                .Select(g => $"{g.Key} {g.Count()}")));
        Console.WriteLine("venues flags: " + string.Join(", ", Enum.GetValues<VenueFlags>().Where(f => f != 0)
            .Select(f => $"{f} {list.Count(r => r.Flags.HasFlag(f))}")));
        return 0;
    }

    private static VenueRecord Record(string osm, Dictionary<string, string> t, double e, double n) =>
        new(osm, KindOf(t), Math.Round(e, 1), Math.Round(n, 1), VenueFix.Raw, FlagsOf(t), CountOf(t), CuisineOf(t),
            t.GetValueOrDefault("name") ?? "");

    /// <summary>A point inside a closed outline: its centroid when that is inside, else the inside sample nearest it.</summary>
    public static (double E, double N) PointIn(List<(double E, double N)> ring)
    {
        double ce = ring.Average(p => p.E), cn = ring.Average(p => p.N);
        if (Inside(ring, ce, cn)) return (ce, cn);
        double e0 = ring.Min(p => p.E), e1 = ring.Max(p => p.E), n0 = ring.Min(p => p.N), n1 = ring.Max(p => p.N);
        (double E, double N)? best = null;
        double bestD = double.MaxValue;
        const int steps = 24;
        for (int i = 1; i < steps; i++)
            for (int j = 1; j < steps; j++)
            {
                double e = e0 + (e1 - e0) * i / steps, n = n0 + (n1 - n0) * j / steps;
                if (!Inside(ring, e, n)) continue;
                double d = (e - ce) * (e - ce) + (n - cn) * (n - cn);
                if (d < bestD) { bestD = d; best = (e, n); }
            }
        return best ?? (ce, cn);
    }

    private static bool Inside(List<(double E, double N)> ring, double e, double n)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].N > n) != (ring[j].N > n)
                && e < (ring[j].E - ring[i].E) * (n - ring[i].N) / (ring[j].N - ring[i].N) + ring[i].E)
                inside = !inside;
        return inside;
    }

    /// <summary>The row against the tiles: kept on its solid, moved onto the nearest, or marked raw or open.</summary>
    private static VenueRecord Place(VenueRecord r, Func<TileId, BuildingTile?> tile)
    {
        bool any = false;
        (TileId Id, int Index, float Dist)? near = null;
        foreach (var id in Around(r.E, r.N))
        {
            if (tile(id) is not { } t) continue;
            any = true;
            float x = (float)(r.E - id.MinE), z = (float)(id.MaxN - r.N);
            for (int i = 0; i < t.Buildings.Count; i++)
            {
                var b = t.Buildings[i];
                if (b.Covers(x, z)) return r with { Fix = VenueFix.Inside };
                float d = PlanDistance(b, x, z);
                if (d <= SnapReach && (near == null || d < near.Value.Dist)) near = (id, i, d);
            }
        }
        if (near is { } s && InsidePoint(s.Id, tile(s.Id)!.Buildings[s.Index]) is { } p)
            return r with { E = p.E, N = p.N, Fix = VenueFix.Snapped };
        return r with { Fix = any ? VenueFix.Open : VenueFix.Raw };
    }

    // the tile holding the point and the eight round it: a solid belongs to the tile holding its centre
    private static IEnumerable<TileId> Around(double e, double n)
    {
        var c = TileId.FromLv95(e, n);
        yield return c;
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
                if (de != 0 || dn != 0) yield return new TileId(c.E + de, c.N + dn);
    }

    /// <summary>The plan distance from a point to a solid's footprint edges (roof and floor triangles' edges, seen from above).</summary>
    private static float PlanDistance(Building b, float x, float z)
    {
        var t = b.Triangles;
        float best = float.MaxValue;
        for (int i = 0; i + 8 < t.Length; i += 9)
        {
            best = MathF.Min(best, Segment(x, z, t[i], t[i + 2], t[i + 3], t[i + 5]));
            best = MathF.Min(best, Segment(x, z, t[i + 3], t[i + 5], t[i + 6], t[i + 8]));
            best = MathF.Min(best, Segment(x, z, t[i + 6], t[i + 8], t[i], t[i + 2]));
        }
        return best;
    }

    private static float Segment(float px, float pz, float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az, len = dx * dx + dz * dz;
        float u = len < 1e-6f ? 0 : Math.Clamp(((px - ax) * dx + (pz - az) * dz) / len, 0, 1);
        float ex = ax + u * dx - px, ez = az + u * dz - pz;
        return MathF.Sqrt(ex * ex + ez * ez);
    }

    /// <summary>
    /// An LV95 point (to 0.1 m) on the solid's footprint: the centre of its biggest triangle seen
    /// from above, kept only if the rounded point still lands on the solid (as castles do).
    /// </summary>
    private static (double E, double N)? InsidePoint(TileId id, Building b)
    {
        var t = b.Triangles;
        var order = Enumerable.Range(0, b.TriangleCount)
            .Select(i => (i, Area: MathF.Abs((t[i * 9 + 3] - t[i * 9]) * (t[i * 9 + 8] - t[i * 9 + 2])
                                            - (t[i * 9 + 5] - t[i * 9 + 2]) * (t[i * 9 + 6] - t[i * 9]))))
            .Where(p => p.Area > 0.5f).OrderByDescending(p => p.Area).ThenBy(p => p.i);
        foreach (var (i, _) in order)
        {
            int o = i * 9;
            double e = Math.Round(id.MinE + (t[o] + t[o + 3] + t[o + 6]) / 3.0, 1);
            double n = Math.Round(id.MaxN - (t[o + 2] + t[o + 5] + t[o + 8]) / 3.0, 1);
            if (b.Covers((float)(e - id.MinE), (float)(id.MaxN - n))) return (e, n);
        }
        return null;
    }
}
