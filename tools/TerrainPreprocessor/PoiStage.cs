using System.Diagnostics;
using System.Text.Json;
using UnitSport.Terrain.Format;
using static UnitSport.Tools.Preprocessor.PoiPick;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// <c>--pois</c> (#1224, <c>docs/plans/adventure-places.md</c> 1): the region's points of interest into
/// <c>pois.bin</c> (<see cref="PoiFile"/>) from swissTLM3D (summits, passes, summit crosses, waterfalls,
/// lakes, towers, churches, chapels, huts, covered bridges, dams, stations, bus stops, landings, cable
/// cars), castles from OSM and the GWR (<see cref="CastleStage"/>'s candidates, with <c>--osm-pbf</c>),
/// a canton per POI from swissBOUNDARIES3D (<see cref="CantonRings"/>, <c>--boundaries</c>; else the
/// nearest <c>places.json</c> town's). Every candidate of the whole table is read and its id made before
/// the region filter, so a region build and the nationwide build give the same ids
/// (<c>docs/notes/adventure/poi-pipeline.md</c>). Standalone, and after every <c>--places</c> pass with
/// <c>--tlm</c>. Re-runnable, one region-wide file, a minute.
/// </summary>
public static class PoiStage
{
    /// <summary>The curated Atlas cards (aliases), shipped with the game, read when present.</summary>
    public const string DefaultCards = "assets/adventure/atlas_cards.json";

    public sealed record Options(string? Boundaries = null, string? OsmPbf = null, string? GwrPath = null,
        string? File = null, string? Cards = null, int Jobs = 4);

    public static int Run(string tlmPath, string outDir, IReadOnlySet<TileId> available, Options o)
    {
        if (!File.Exists(tlmPath))
        {
            Console.Error.WriteLine($"swissTLM3D GeoPackage not found: {tlmPath}");
            return 1;
        }
        if (available.Count == 0)
        {
            Console.Error.WriteLine("--pois: the region has no tiles");
            return 2;
        }
        var sw = Stopwatch.StartNew();
        var towns = ReadTowns(outDir);

        // ---- every candidate of the whole table ------------------------------------------------------
        var all = new List<Candidate>();
        using (var conn = GeoPackageReader.Open(tlmPath))
        {
            var peaks = Names(conn, all);
            var crosses = new List<Candidate>();
            EinzelObjekte(conn, crosses, all);
            all.AddRange(JoinCrosses(peaks, crosses));
            Lakes(conn, all);
            Footprints(conn, all);
            CoveredBridges(conn, all, towns);
            Dams(conn, all, towns);
            var cableStops = Stops(conn, all);
            CableCars(conn, all, cableStops);
        }
        if (o.OsmPbf != null) Castles(o.OsmPbf, o.GwrPath, o.Jobs, all);
        Console.WriteLine($"POIs: {all.Count} candidates nationwide ({sw.Elapsed.TotalSeconds:F0} s): " + string.Join(", ",
            all.GroupBy(c => c.Kind).OrderBy(g => g.Key).Select(g => $"{PoiId.Tag(g.Key)} {g.Count()}")));

        var collisions = Collisions(all);
        if (collisions.Count > 0)
        {
            foreach (var c in collisions) Console.Error.WriteLine($"  POI id collision: {c}");
            Console.Error.WriteLine("POIs: two sources share an id; the build stops (PoiPick.Collisions)");
            return 1;
        }

        // ---- the region ------------------------------------------------------------------------------
        var region = all.Where(c => available.Contains(TileId.FromLv95(c.E, c.N))).ToList();
        int villageStops = PickVillageStops(region.Where(c => c.Kind == PoiKind.BusStop).ToList(),
            towns.Select(t => (t.Name, t.E, t.N)));

        CantonRings? rings = null;
        if (o.Boundaries != null)
        {
            if (File.Exists(o.Boundaries)) rings = CantonRings.Read(o.Boundaries);
            else Console.Error.WriteLine($"  swissBOUNDARIES3D not found: {o.Boundaries}; cantons from the nearest town");
        }
        string CantonAt(double e, double n) => rings != null ? rings.CodeAt(e, n) : NearestTownCanton(towns, e, n);

        var manifestTiles = available is HashSet<TileId> hs ? hs : available.ToHashSet();
        var sampler = new LandingStage.TileSampler(outDir, manifestTiles);
        var data = new PoiData();
        var records = new List<PoiRecord>(region.Count);
        int sampled = 0;
        foreach (var c in region.OrderBy(c => c.Kind).ThenBy(c => c.E).ThenBy(c => c.N))
        {
            double z = c.Z;
            if (!double.IsFinite(z) || z == 0)
            {
                z = sampler.Ground(c.E, c.N);
                sampled++;
            }
            var flags = c.Flags;
            records.Add(new PoiRecord
            {
                Hash = PoiId.Hash(c.SourceId),
                Kind = c.Kind,
                Flags = flags,
                Tier = Tier(c),
                Canton = CantonAt(c.E, c.N),
                DiscoverRadius = DiscoverRadius(c.Kind),
                RevealRadius = RevealRadius(c.Kind, flags, double.IsFinite(z) ? z : 0),
                E = c.E,
                N = c.N,
                Elevation = (short)Math.Clamp(double.IsFinite(z) ? Math.Round(z) : 0, short.MinValue, short.MaxValue),
                Name = c.Name,
                LineKey = c.LineKey,
                Outline = c.Outline,
            });
        }

        // ---- curated cards: tier 1 -------------------------------------------------------------------
        var unresolved = ApplyCards(o.Cards, records);

        // ---- the cantons and their totals ------------------------------------------------------------
        var lakes = records.Where(r => r.Kind == PoiKind.Lake && r.Outline != null).ToList();
        var landCells = LandCells(manifestTiles, CantonAt, lakes);
        foreach (var code in records.Select(r => r.Canton).Concat(landCells.Keys).Where(c => c.Length == 2).Distinct().Order(StringComparer.Ordinal))
            data.Cantons.Add(new PoiCanton(code,
                records.Count(r => r.Canton == code && r.Tier == 1),
                records.Count(r => r.Canton == code && r.Tier == 2),
                landCells.GetValueOrDefault(code)));
        data.Pois.AddRange(records);

        string file = o.File ?? Path.Combine(outDir, PoiFile.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var bytes = PoiFile.Encode(data);
        File.WriteAllBytes(file, bytes);

        Console.WriteLine($"POIs: {records.Count} in the region -> {file} ({bytes.Length / 1024} KB), " +
                          $"{villageStops} village stops, {sampled} elevations from the tiles, " +
                          $"cantons {(rings != null ? "from swissBOUNDARIES3D" : "from the nearest town")}, in {sw.Elapsed.TotalSeconds:F0} s");
        PrintStats(data, unresolved);
        return 0;
    }

    /// <summary><c>--poisstats</c>: the counts of a built <c>pois.bin</c> and the curated aliases that do not resolve.</summary>
    public static int Stats(string outDir, string? file, string? cards)
    {
        file ??= Path.Combine(outDir, PoiFile.FileName);
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"no {file}: build it with --pois");
            return 1;
        }
        var data = PoiFile.Decode(File.ReadAllBytes(file));
        var unresolved = ApplyCards(cards, data.Pois, setTier: false);
        PrintStats(data, unresolved);
        return 0;
    }

    private static void PrintStats(PoiData data, List<string> unresolved)
    {
        Console.WriteLine("  by kind: " + string.Join(", ", data.Pois.GroupBy(p => p.Kind).OrderBy(g => g.Key)
            .Select(g => $"{PoiId.Tag(g.Key)} {g.Count()}")));
        Console.WriteLine("  by tier: " + string.Join(", ", data.Pois.GroupBy(p => p.Tier).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}: {g.Count()}")));
        Console.WriteLine($"  viewpoints {data.Pois.Count(p => p.Flags.HasFlag(PoiFlags.Viewpoint))}, " +
                          $"crossed peaks {data.Pois.Count(p => p.Kind == PoiKind.Peak && p.Flags.HasFlag(PoiFlags.Cross))}, " +
                          $"cable-car tops {data.Pois.Count(p => p.Flags.HasFlag(PoiFlags.Top))}, " +
                          $"village stops {data.Pois.Count(p => p.Flags.HasFlag(PoiFlags.VillageStop))}, " +
                          $"unnamed {data.Pois.Count(p => !p.Flags.HasFlag(PoiFlags.Named))}");
        Console.WriteLine("  by canton: " + string.Join(", ", data.Cantons.Select(c =>
            $"{c.Code} {data.Pois.Count(p => p.Canton == c.Code)} (tier 1/2 {c.Tier1}/{c.Tier2}, {c.LandCells} land cells)"))
            + $", none {data.Pois.Count(p => p.Canton.Length == 0)}");
        Console.WriteLine("  top: " + string.Join(", ", data.Pois.Where(p => p.Kind == PoiKind.Peak)
            .OrderByDescending(p => p.Elevation).Take(6).Select(p => $"{p.Name} ({p.Elevation} m)")));
        Console.WriteLine(unresolved.Count == 0 ? "  curated aliases: all resolve"
            : $"  curated aliases that do not resolve ({unresolved.Count}): {string.Join(", ", unresolved)}");
    }

    // ---- sources ------------------------------------------------------------------------------------

    /// <summary>The TLM uuid as the source identity: braces off, lower case, so a release's formatting cannot move an id.</summary>
    private static string Uuid(object v) => SourceUuid(v as string);

    private static readonly Dictionary<string, PoiKind> NameKinds = new(StringComparer.Ordinal)
    {
        ["Hauptgipfel"] = PoiKind.Peak, ["Alpiner Gipfel"] = PoiKind.Peak, ["Gipfel"] = PoiKind.Peak,
        ["Haupthuegel"] = PoiKind.Peak, ["Huegel"] = PoiKind.Peak, ["Felskopf"] = PoiKind.Peak,
        ["Strassenpass"] = PoiKind.Pass, ["Pass"] = PoiKind.Pass,
    };

    /// <summary>Peaks and passes (<c>tlm_namen_name_pkt</c>, POINT): named, with the surveyed <c>hoehe</c>. Returns the peaks.</summary>
    private static List<Candidate> Names(Microsoft.Data.Sqlite.SqliteConnection conn, List<Candidate> all)
    {
        var peaks = new List<Candidate>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select objektart, name, hoehe, uuid, geom from tlm_namen_name_pkt where name is not null and geom is not null";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!NameKinds.TryGetValue(r.GetString(0), out var kind)) continue;
            string name = FirstName(r.GetString(1));
            if (name.Length == 0) continue;
            var pts = GeoPackageReader.ParsePoints((byte[])r[4]);
            if (pts.Count == 0) continue;
            var c = new Candidate
            {
                Kind = kind, SourceId = Uuid(r[3]), Name = name, E = pts[0].E, N = pts[0].N,
                Z = r.IsDBNull(2) ? pts[0].Z : r.GetDouble(2), Sort = r.GetString(0), Flags = PoiFlags.Named,
            };
            // a road pass is a viewpoint: a telescope stands there (plan 5)
            if (c.Sort == "Strassenpass") c.Flags |= PoiFlags.Viewpoint;
            all.Add(c);
            if (kind == PoiKind.Peak) peaks.Add(c);
        }
        return peaks;
    }

    /// <summary>Summit crosses (all, joined to peaks later) and named waterfalls (<c>tlm_eo_einzelobjekt</c>).</summary>
    private static void EinzelObjekte(Microsoft.Data.Sqlite.SqliteConnection conn, List<Candidate> crosses, List<Candidate> all)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select objektart, name, uuid, geom from tlm_eo_einzelobjekt where objektart in ('Gipfelkreuz', 'Wasserfall') and geom is not null";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string name = r.IsDBNull(1) ? "" : FirstName(r.GetString(1));
            bool cross = r.GetString(0) == "Gipfelkreuz";
            if (!cross && name.Length == 0) continue;
            var pts = GeoPackageReader.ParsePoints((byte[])r[3]);
            if (pts.Count == 0) continue;
            var c = new Candidate
            {
                Kind = cross ? PoiKind.SummitCross : PoiKind.Waterfall, SourceId = Uuid(r[2]), Name = name,
                E = pts[0].E, N = pts[0].N, Z = pts[0].Z, Sort = r.GetString(0),
                Flags = !cross ? PoiFlags.Named : PoiFlags.None,
            };
            (cross ? crosses : all).Add(c);
        }
    }

    /// <summary>
    /// Named lakes (<c>tlm_gewaesser_stehendes_gewaesser</c> See, shore LINESTRINGs): the lines of one
    /// lake name (<c>tlm_gewaesser_name_uuid</c>, its identity) joined into rings, the largest kept if
    /// at least 1 ha, simplified to 50 m and at most 128 points.
    /// </summary>
    private static void Lakes(Microsoft.Data.Sqlite.SqliteConnection conn, List<Candidate> all)
    {
        var lines = new Dictionary<string, (string Name, List<double[]> Lines, double ZSum, int ZCount)>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                select tlm_gewaesser_name_uuid, name, geom from tlm_gewaesser_stehendes_gewaesser
                where objektart = 'See' and name is not null and geom is not null
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string name = FirstName(r.GetString(1));
                string id = r.IsDBNull(0) ? "name:" + name : Uuid(r[0]);
                if (!lines.TryGetValue(id, out var lake)) lake = (name, new List<double[]>(), 0, 0);
                foreach (var l in GeoPackageReader.ParseLines((byte[])r[2]))
                {
                    var xy = new double[l.E.Length * 2];
                    for (int i = 0; i < l.E.Length; i++) { xy[i * 2] = l.E[i]; xy[i * 2 + 1] = l.N[i]; lake.ZSum += l.Z[i]; lake.ZCount++; }
                    lake.Lines.Add(xy);
                }
                lines[id] = lake;
            }
        }
        foreach (var (id, lake) in lines.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var ring = Rings(lake.Lines).OrderByDescending(Area).FirstOrDefault();
            if (ring == null) continue;
            double area = Area(ring);
            if (area < LakeMinArea) continue;
            var (e, n) = PointIn(ring);
            all.Add(new Candidate
            {
                Kind = PoiKind.Lake, SourceId = id, Name = lake.Name, E = e, N = n, Size = area,
                Z = lake.ZCount > 0 ? lake.ZSum / lake.ZCount : double.NaN, Outline = Simplify(ring), Flags = PoiFlags.Named,
            });
        }
    }

    /// <summary>The ring's centroid when inside it, else the vertex nearest the centroid (a crescent lake).</summary>
    private static (double E, double N) PointIn(double[] ring)
    {
        int n = ring.Length / 2;
        double e = 0, nn = 0;
        for (int i = 0; i < n; i++) { e += ring[i * 2]; nn += ring[i * 2 + 1]; }
        e /= n; nn /= n;
        if (Inside(ring, e, nn)) return (e, nn);
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double d = (ring[i * 2] - e) * (ring[i * 2] - e) + (ring[i * 2 + 1] - nn) * (ring[i * 2 + 1] - nn);
            if (d < bestD) { bestD = d; best = i; }
        }
        return (ring[best * 2], ring[best * 2 + 1]);
    }

    /// <summary>Named footprints (<c>tlm_bauten_gebaeude_footprint</c>): towers, churches, chapels, and huts by their name.</summary>
    private static void Footprints(Microsoft.Data.Sqlite.SqliteConnection conn, List<Candidate> all)
    {
        // a building of several footprints (the Berner Muenster has 31) shares one name object: that is its identity
        var groups = new Dictionary<(PoiKind, string), (Candidate C, double E, double N, double Z, int Count)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select objektart, name, uuid, tlm_bauten_name_uuid, geom from tlm_bauten_gebaeude_footprint where name is not null and geom is not null";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string sort = r.GetString(0), name = FirstName(r.GetString(1));
                if (name.Length == 0) continue;
                PoiKind kind = sort switch
                {
                    "Turm" => PoiKind.LookoutTower,
                    "Sakrales Gebaeude" or "Sakraler Turm" => PoiKind.Church,
                    "Kapelle" => PoiKind.Chapel,
                    _ when IsHut(name) => PoiKind.Hut,
                    _ => 0,
                };
                if (kind == 0) continue;
                var rings = GeoPackageReader.ParsePolygons((byte[])r[4]);
                if (rings.Count == 0 || rings[0].Count == 0) continue;
                var xyz = rings[0].Xyz;
                int n = rings[0].Count;
                double e = 0, nn = 0, z = 0;
                for (int i = 0; i < n; i++) { e += xyz[i * 3]; nn += xyz[i * 3 + 1]; z += xyz[i * 3 + 2]; }
                string id = r.IsDBNull(3) ? Uuid(r[2]) : Uuid(r[3]);
                if (!groups.TryGetValue((kind, id), out var g))
                    g = (new Candidate { Kind = kind, SourceId = id, Name = name, Sort = sort, Flags = PoiFlags.Named }, 0, 0, 0, 0);
                groups[(kind, id)] = (g.C, g.E + e, g.N + nn, g.Z + z, g.Count + n);
            }
        }
        var found = new List<Candidate>();
        foreach (var (c, e, n, z, count) in groups.Values)
        {
            c.E = e / count;
            c.N = n / count;
            c.Z = z / count;
            found.Add(c);
        }
        // a church and its tower named alike, a hut drawn as two buildings: one place each
        foreach (var kind in found.Select(c => c.Kind).Distinct().Order())
            all.AddRange(Dedup(found.Where(c => c.Kind == kind), DedupReach));
    }

    /// <summary>Covered bridges (<c>tlm_strassen_strasse</c> kunstbaute <c>Gedeckte Bruecke</c>), merged per structure, at least 20 m.</summary>
    private static void CoveredBridges(Microsoft.Data.Sqlite.SqliteConnection conn, List<Candidate> all, List<Place> towns)
    {
        var segments = new List<Segment>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select uuid, name, geom from tlm_strassen_strasse where kunstbaute = 'Gedeckte Bruecke' and geom is not null";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                foreach (var l in GeoPackageReader.ParseLines((byte[])r[2]))
                    if (l.E.Length >= 2) segments.Add(new Segment(Uuid(r[0]), r.IsDBNull(1) ? "" : r.GetString(1), l.E, l.N, l.Z));
        }
        foreach (var b in MergeSegments(segments, PoiKind.Bridge, BridgeMinLength))
        {
            if (b.Name.Length == 0) b.Name = "Covered bridge" + (NearestTown(towns, b.E, b.N) is { } t ? " near " + t.Name : "");
            all.Add(b);
        }
    }

    /// <summary>Dams and weirs (<c>tlm_bauten_staubaute</c> Staumauer, Staudamm, Wehr, POLYGON) at least 50 m long.</summary>
    private static void Dams(Microsoft.Data.Sqlite.SqliteConnection conn, List<Candidate> all, List<Place> towns)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select objektart, name, uuid, geom from tlm_bauten_staubaute where objektart in ('Staumauer', 'Staudamm', 'Wehr') and geom is not null";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rings = GeoPackageReader.ParsePolygons((byte[])r[3]);
            if (rings.Count == 0 || rings[0].Count == 0) continue;
            var xyz = rings[0].Xyz;
            int n = rings[0].Count;
            double length = 0, e = 0, nn = 0, z = 0;
            for (int i = 0; i < n; i++)
            {
                e += xyz[i * 3]; nn += xyz[i * 3 + 1]; z += xyz[i * 3 + 2];
                for (int j = i + 1; j < n; j++)
                    length = Math.Max(length, Math.Sqrt(Math.Pow(xyz[i * 3] - xyz[j * 3], 2) + Math.Pow(xyz[i * 3 + 1] - xyz[j * 3 + 1], 2)));
            }
            if (length < DamMinLength) continue;
            e /= n; nn /= n;
            string name = r.IsDBNull(1) ? "" : FirstName(r.GetString(1));
            string sort = r.GetString(0);
            all.Add(new Candidate
            {
                Kind = PoiKind.Dam, SourceId = Uuid(r[2]), E = e, N = nn, Z = z / n, Sort = sort, Size = length,
                Name = name.Length > 0 ? name : $"{(sort == "Wehr" ? "Weir" : "Dam")}{(NearestTown(towns, e, nn) is { } t ? " near " + t.Name : "")}",
                Flags = name.Length > 0 ? PoiFlags.Named : PoiFlags.None,
            });
        }
    }

    /// <summary>Stations, bus stops and boat landings (<c>tlm_oev_haltestelle</c>); returns the cable-car stops (<c>Uebrige Bahnen</c>) to name line ends.</summary>
    private static List<Candidate> Stops(Microsoft.Data.Sqlite.SqliteConnection conn, List<Candidate> all)
    {
        var cable = new List<Candidate>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select objektart, name, uuid, geom from tlm_oev_haltestelle where geom is not null";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            PoiKind kind = r.GetString(0) switch
            {
                "Haltestelle Bahn" => PoiKind.RailStation,
                "Haltestelle Bus" => PoiKind.BusStop,
                "Haltestelle Schiff" => PoiKind.ShipLanding,
                "Uebrige Bahnen" => PoiKind.CableCarStation,
                _ => 0,
            };
            if (kind == 0) continue;
            var pts = GeoPackageReader.ParsePoints((byte[])r[3]);
            if (pts.Count == 0) continue;
            string name = r.IsDBNull(1) ? "" : FirstName(r.GetString(1));
            var c = new Candidate
            {
                Kind = kind, SourceId = Uuid(r[2]), Name = name.Length > 0 ? name : "Stop", E = pts[0].E, N = pts[0].N, Z = pts[0].Z,
                Sort = r.GetString(0), Flags = name.Length > 0 ? PoiFlags.Named : PoiFlags.None,
            };
            if (kind == PoiKind.CableCarStation) cable.Add(c);
            else all.Add(c);
        }
        return cable;
    }

    /// <summary>A cable-car end takes the name of the stop within this many metres.</summary>
    private const double CableStopReach = 150;

    /// <summary>
    /// Cable cars (<c>tlm_oev_uebrige_bahn</c> Luftseilbahn, Gondelbahn): both ends of every line, the
    /// higher one <see cref="PoiFlags.Top"/> (a viewpoint), identified by the line's uuid + <c>/top</c> or
    /// <c>/bottom</c>, named after the nearest <c>Uebrige Bahnen</c> stop; <c>LineKey</c> is the bottom
    /// end's metre (<c>Ropeway.Key</c>). Chairlifts, drag lifts and goods ropeways are not stations.
    /// </summary>
    private static void CableCars(Microsoft.Data.Sqlite.SqliteConnection conn, List<Candidate> all, List<Candidate> stops)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select objektart, name, uuid, geom from tlm_oev_uebrige_bahn where objektart in ('Luftseilbahn', 'Gondelbahn') and geom is not null";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var lines = GeoPackageReader.ParseLines((byte[])r[3]);
            if (lines.Count == 0) continue;
            var first = lines[0];
            var last = lines[^1];
            var a = (E: first.E[0], N: first.N[0], Z: first.Z[0]);
            var b = (E: last.E[^1], N: last.N[^1], Z: last.Z[^1]);
            if (Math.Abs(a.E - b.E) + Math.Abs(a.N - b.N) < 1) continue;
            var (bottom, top) = a.Z <= b.Z ? (a, b) : (b, a);
            long key = (long)Math.Round(bottom.E) * 10_000_000L + (long)Math.Round(bottom.N);
            string uuid = Uuid(r[2]);
            string line = r.IsDBNull(1) ? "" : FirstName(r.GetString(1));
            foreach (var (end, isTop) in new[] { (bottom, false), (top, true) })
            {
                var stop = stops.Where(s => (s.E - end.E) * (s.E - end.E) + (s.N - end.N) * (s.N - end.N) <= CableStopReach * CableStopReach)
                    .OrderBy(s => (s.E - end.E) * (s.E - end.E) + (s.N - end.N) * (s.N - end.N)).FirstOrDefault();
                string name = stop?.Name ?? (line.Length > 0 ? $"{line} ({(isTop ? "top" : "bottom")})" : isTop ? "Cable car top" : "Cable car");
                all.Add(new Candidate
                {
                    Kind = PoiKind.CableCarStation, SourceId = uuid + (isTop ? "/top" : "/bottom"), Name = name,
                    E = end.E, N = end.N, Z = end.Z, Sort = r.GetString(0), LineKey = key,
                    Flags = (isTop ? PoiFlags.Top | PoiFlags.Viewpoint : PoiFlags.None) | (stop != null || line.Length > 0 ? PoiFlags.Named : PoiFlags.None),
                });
            }
        }
    }

    /// <summary>
    /// Castles: <see cref="CastleStage"/>'s OSM <c>historic=castle</c> candidates (ODbL), then its GWR names
    /// when <c>--gwr</c> is given; one per name within a kilometre, OSM first; unnamed ones dropped.
    /// Identity <c>osm:way/123</c>, <c>osm:node/123</c>, <c>egid:123</c>.
    /// </summary>
    private static void Castles(string pbf, string? gwr, int jobs, List<Candidate> all)
    {
        if (!File.Exists(pbf))
        {
            Console.Error.WriteLine($"  OSM extract not found: {pbf}; no castles");
            return;
        }
        var raw = CastleStage.FromOsm(pbf, jobs).Select(c => (c, "osm:" + c.Source)).ToList();
        if (gwr != null && File.Exists(gwr))
            raw.AddRange(CastleStage.FromGwr(gwr).Select(c => (c, "egid:" + c.Source["egid/".Length..])));
        var kept = new List<Candidate>();
        foreach (var (c, id) in raw)
        {
            if (c.Name is "" or "Castle") continue;
            string key = PlaceIndex.Normalize(c.Name);
            if (kept.Any(k => PlaceIndex.Normalize(k.Name) == key && Math.Abs(k.E - c.E) < 1000 && Math.Abs(k.N - c.N) < 1000)) continue;
            kept.Add(new Candidate { Kind = PoiKind.Castle, SourceId = id, Name = c.Name, E = c.E, N = c.N, Flags = PoiFlags.Named });
        }
        Console.WriteLine($"  castles: {kept.Count} of {raw.Count} candidates");
        all.AddRange(kept);
    }

    // ---- towns, cantons, cards ---------------------------------------------------------------------

    private static List<Place> ReadTowns(string outDir)
    {
        string path = Path.Combine(outDir, PlaceIndex.FileName);
        if (!File.Exists(path))
        {
            Console.WriteLine($"  no {PlaceIndex.FileName}: no village stops, and cantons only from --boundaries (run --places first)");
            return new List<Place>();
        }
        return PlaceIndex.FromJson(File.ReadAllText(path)).Places.Where(p => p.Kind == PlaceKind.Town).ToList();
    }

    private static Place? NearestTown(List<Place> towns, double e, double n, double reach = 5000)
    {
        Place? best = null;
        double bestD = reach * reach;
        foreach (var t in towns)
        {
            double d = (t.E - e) * (t.E - e) + (t.N - n) * (t.N - n);
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    /// <summary>The canton of the nearest town within 5 km (the GWR's <c>GDEKT</c>), "" when none.</summary>
    private static string NearestTownCanton(List<Place> towns, double e, double n) =>
        NearestTown(towns, e, n) is { Canton.Length: 2 } t ? t.Canton : "";

    /// <summary>
    /// The land fog cells (250 m, 16 a tile) of the region per canton: a cell's centre in the canton and
    /// in no lake outline of the region.
    /// </summary>
    private static Dictionary<string, int> LandCells(HashSet<TileId> tiles, Func<double, double, string> cantonAt, List<PoiRecord> lakes)
    {
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var boxes = lakes.Select(l =>
        {
            var o = l.Outline!;
            double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
            for (int i = 0; i < o.Length; i += 2)
            {
                minE = Math.Min(minE, o[i]); maxE = Math.Max(maxE, o[i]);
                minN = Math.Min(minN, o[i + 1]); maxN = Math.Max(maxN, o[i + 1]);
            }
            return (o, minE, minN, maxE, maxN);
        }).ToList();
        Parallel.ForEach(tiles, t =>
        {
            for (int ce = 0; ce < 4; ce++)
                for (int cn = 0; cn < 4; cn++)
                {
                    double e = t.MinE + 125 + ce * 250, n = t.MinN + 125 + cn * 250;
                    string code = cantonAt(e, n);
                    if (code.Length == 0) continue;
                    if (boxes.Any(b => e >= b.minE && e <= b.maxE && n >= b.minN && n <= b.maxN && Inside(b.o, e, n))) continue;
                    counts.AddOrUpdate(code, 1, (_, v) => v + 1);
                }
        });
        return new Dictionary<string, int>(counts, StringComparer.Ordinal);
    }

    private sealed record Card(string Alias, string Id);

    /// <summary>
    /// Reads the curated cards (<c>{ "alias", "id", ... }</c>): every POI a card resolves to is tier 1
    /// (when <paramref name="setTier"/>). Returns the aliases that resolve to no POI of the region.
    /// </summary>
    private static List<string> ApplyCards(string? path, List<PoiRecord> records, bool setTier = true)
    {
        path ??= File.Exists(DefaultCards) ? DefaultCards : null;
        var unresolved = new List<string>();
        if (path == null || !File.Exists(path)) return unresolved;
        var cards = JsonSerializer.Deserialize<List<Card>>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var byHash = records.GroupBy(r => (r.Kind, r.Hash)).ToDictionary(g => g.Key, g => g.First());
        foreach (var card in cards)
        {
            if (PoiId.TryParse(card.Id ?? "", out var kind, out ulong hash) && byHash.TryGetValue((kind, hash), out var poi))
            {
                if (setTier) poi.Tier = 1;
            }
            else unresolved.Add(card.Alias ?? card.Id ?? "?");
        }
        return unresolved;
    }
}
