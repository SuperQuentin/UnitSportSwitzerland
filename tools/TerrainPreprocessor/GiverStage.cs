using System.Diagnostics;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The errand givers of OpenStreetMap and the built tiles (#1339, errands plan part 1), written to <c>givers.tsv.gz</c>, which the
/// game embeds (<c>src/Adventure/Errands/givers.tsv.gz</c>, <c>GiverHost</c>): post offices (<c>amenity=post_office</c>), alp and
/// village dairies (<c>craft=cheese_maker</c>, <c>shop=cheese|dairy</c>, a Käserei / Sennerei / Chäsi / Fromagerie / Caseificio by
/// name; split at 1 200 m), Beizen (<c>amenity=restaurant</c>, cuisine regional or swiss; one per 2 km cell) and farms (a barn of
/// 120 m² or more with a dwelling within 40 m in the building tiles, or <c>place=farm</c> / <c>landuse=farmyard</c>; one per 2 km
/// cell). Only the region's tiles count, so a small region gives a small file. A point inside a solid is moved out to its edge, the
/// figure facing away from the building. Heights come from the tiles, cantons from swissBOUNDARIES3D (<c>--boundaries</c>) or the
/// nearest town of <c>places.json</c>. Reruns on the same data give the same bytes. ODbL, like the venues (<c>tools/osm-odbl-licence</c>).
/// </summary>
public static class GiverStage
{
    private const double OutReachM = 40, OutStepM = 1.5;

    public static int Run(string chunkDir, string pbf, string outFile, string? boundaries, int jobs)
    {
        var sw = Stopwatch.StartNew();
        var manifestPath = Path.Combine(chunkDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"givers: no manifest.json in {chunkDir}");
            return 2;
        }
        var region = TerrainManifest.FromJson(File.ReadAllText(manifestPath)).Tiles.Select(t => t.Id).ToHashSet();

        // pass 1: the tagged points and outlines; pass 2: the positions of the outlines' nodes only
        static bool IsGiver(Dictionary<string, string> t) => GiverPick.KindOf(t) != 0;
        var first = PbfReader.Read(pbf, jobs, new PbfReader.Filter
        {
            KeepNode = (_, _) => false, KeepWay = IsGiver, WayTags = GiverPick.Keys,
            KeepTaggedNode = IsGiver, NodeTags = GiverPick.Keys, TaggedNodesAnywhere = true,
        });
        var refs = first.Ways.SelectMany(w => w.Refs).ToHashSet();
        var nodes = PbfReader.Read(pbf, jobs, new PbfReader.Filter
        {
            KeepNode = (_, _) => false, KeepNodeId = refs.Contains, KeepWay = _ => false, WayTags = GiverPick.Keys,
        }).Nodes;
        Console.WriteLine($"givers: {first.TaggedNodes.Count} points, {first.Ways.Count} outlines read ({sw.Elapsed.TotalSeconds:F0} s)");

        var candidates = new List<GiverPick.Candidate>();
        void Add(string source, Dictionary<string, string> tags, double e, double n)
        {
            if (!region.Contains(TileId.FromLv95(e, n))) return;
            candidates.Add(new GiverPick.Candidate(source, GiverPick.KindOf(tags), e, n, tags.GetValueOrDefault("name") ?? ""));
        }
        foreach (var n in first.TaggedNodes)
        {
            var (e, nn) = SwissProjection.ToLv95(n.Lat, n.Lon);
            Add($"osm:n{n.Id}", n.Tags, e, nn);
        }
        foreach (var w in first.Ways)
        {
            if (w.Refs.Length < 4 || w.Refs[0] != w.Refs[^1]) continue;
            var ring = new List<(double E, double N)>();
            foreach (long r in w.Refs)
                if (nodes.TryGetValue(r, out var ll)) ring.Add(SwissProjection.ToLv95(ll.Lat, ll.Lon));
            if (ring.Count < 3) continue;
            var (e, n) = VenueStage.PointIn(ring);
            Add($"osm:w{w.Id}", w.Tags, e, n);
        }
        int osmCount = candidates.Count;

        // the tiles: the farms among the solids, and where a point stands on a solid
        var tiles = new Dictionary<TileId, BuildingTile?>();
        BuildingTile? Tile(TileId id)
        {
            lock (tiles)
                if (tiles.TryGetValue(id, out var t)) return t;
            var path = Path.Combine(chunkDir, BuildingFormat.FileName(id));
            BuildingTile? read = null;
            if (File.Exists(path)) { using var fs = File.OpenRead(path); read = BuildingCodec.Decode(fs); }
            lock (tiles) return tiles[id] = read;
        }
        var farms = new List<GiverPick.Candidate>();
        foreach (var id in region.OrderBy(t => t.E).ThenBy(t => t.N))
        {
            if (Tile(id) is not { } tile) continue;
            var solids = new List<GiverPick.Solid>(tile.Buildings.Count);
            for (int i = 0; i < tile.Buildings.Count; i++)
            {
                var b = tile.Buildings[i];
                var (cx, cz, area) = Box(b);
                solids.Add(new GiverPick.Solid(id.MinE + cx, id.MaxN - cz, area, b.Kind == BuildingKind.Agricultural, b.Kind is BuildingKind.House or BuildingKind.Apartment,
                    b.Egid != 0 ? $"gwr:{b.Egid}" : $"bldg:{id.E}_{id.N}:{i}"));
            }
            farms.AddRange(GiverPick.Farms(solids));
        }
        candidates.AddRange(farms);
        Console.WriteLine($"givers: {osmCount} from OSM, {farms.Count} farms among the solids ({tiles.Values.Count(t => t != null)} tiles, {sw.Elapsed.TotalSeconds:F0} s)");

        // the height, the canton, the door side
        var sampler = new LandingStage.TileSampler(chunkDir, region);
        CantonRings? rings = boundaries != null && File.Exists(boundaries) ? CantonRings.Read(boundaries) : null;
        var towns = rings == null ? PoiStage.ReadTowns(chunkDir) : null;
        string CantonAt(double e, double n) => rings != null ? rings.CodeAt(e, n) : PoiStage.NearestTownCanton(towns!, e, n);
        var rows = GiverPick.Pick(candidates, sampler.Ground, CantonAt);
        for (int i = 0; i < rows.Count; i++) rows[i] = Outside(rows[i], Tile);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        using (var fs = File.Create(outFile))
            GiverFormat.Write(fs, $"osm={Path.GetFileName(pbf)} tiles={tiles.Values.Count(t => t != null)}", rows);
        Console.WriteLine($"givers: {rows.Count} givers -> {outFile} ({new FileInfo(outFile).Length / 1024} KB, {sw.Elapsed.TotalSeconds:F0} s)");
        Console.WriteLine("givers by kind: " + string.Join(", ", rows.GroupBy(r => GiverPick.Tag(r.Kind)).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        Console.WriteLine("givers by canton: " + string.Join(", ", rows.GroupBy(r => r.Canton).OrderBy(g => g.Key).Select(g => $"{(g.Key.Length == 0 ? "?" : g.Key)} {g.Count()}")));
        return 0;
    }

    /// <summary>The centre (tile-local) and the box area of a solid's footprint.</summary>
    private static (float X, float Z, double Area) Box(Building b)
    {
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        var t = b.Triangles;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            x0 = MathF.Min(x0, t[i]); x1 = MathF.Max(x1, t[i]);
            z0 = MathF.Min(z0, t[i + 2]); z1 = MathF.Max(z1, t[i + 2]);
        }
        return t.Length == 0 ? (0, 0, 0) : ((x0 + x1) / 2, (z0 + z1) / 2, (double)(x1 - x0) * (z1 - z0));
    }

    /// <summary>
    /// A giver standing on a solid is moved out of it: away from the solid's centre in 1.5 m steps, up to 40 m, to where no
    /// solid covers the point; it faces outward (the door side). A point on no solid keeps its place and heading.
    /// </summary>
    private static GiverRecord Outside(GiverRecord r, Func<TileId, BuildingTile?> tile)
    {
        var hit = SolidAt(r.E, r.N, tile);
        if (hit == null) return r;
        var (id, b) = hit.Value;
        var (cx, cz, _) = Box(b);
        double e = r.E, n = r.N;
        double dx = r.E - (id.MinE + cx), dn = r.N - (id.MaxN - cz);
        double len = Math.Sqrt(dx * dx + dn * dn);
        if (len < 0.5) { dx = Math.Sin(r.Heading); dn = Math.Cos(r.Heading); len = 1; }
        dx /= len; dn /= len;
        for (double d = OutStepM; d <= OutReachM; d += OutStepM)
        {
            double ne = r.E + dx * d, nn = r.N + dn * d;
            if (SolidAt(ne, nn, tile) != null) continue;
            e = ne; n = nn;
            double heading = Math.Atan2(dx, dn);
            return r with { E = Math.Round(e, 1), N = Math.Round(n, 1), Heading = (float)(heading < 0 ? heading + Math.Tau : heading) };
        }
        return r;
    }

    private static (TileId, Building)? SolidAt(double e, double n, Func<TileId, BuildingTile?> tile)
    {
        var c = TileId.FromLv95(e, n);
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
            {
                var id = new TileId(c.E + de, c.N + dn);
                if (tile(id) is not { } t) continue;
                float x = (float)(e - id.MinE), z = (float)(id.MaxN - n);
                foreach (var b in t.Buildings)
                    if (b.Covers(x, z)) return (id, b);
            }
        return null;
    }
}
