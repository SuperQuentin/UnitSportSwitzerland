using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Castles (#859): which solids of the built <c>.bldg</c> tiles are castles, written as a C# list
/// the game compiles in (<c>src/Interiors/Castles.List.cs</c>), one LV95 point per solid.
///
/// <para>
/// No dataset says "castle". swissBUILDINGS3D draws Chillon as one solid and the GWR files it as
/// GKLAS 1274, "other minor structure", so it was dressed as a shed. Two sources name them:
/// OpenStreetMap's <c>historic=castle</c> (outlines and points, the famous ones the GWR leaves
/// unnamed: Lenzburg, Castelgrande, Thun) and the GWR building name (<c>GBEZ</c> "Château de
/// Chillon", "Schloss Kyburg"), filtered so a Schlosserei, a Burgstrasse or a Château d'Eau is not one.
/// </para>
///
/// <para>
/// Each candidate is resolved against the tiles here, once: an OSM outline takes every solid whose
/// centre is inside it; a point takes the solid whose real footprint covers it, then the old solids
/// touching that one (a castle is often several). Each solid gets a point inside one of its own roof
/// triangles, checked with <see cref="Building.Covers"/>, so the game's match is exact and unique
/// and stays a pure function of the tile, like <c>Landmarks</c> (#501).
/// </para>
/// </summary>
public static class CastleStage
{
    // a castle by name: the first word, then not an address, a business or an outbuilding
    private static readonly Regex Head = new(
        @"^(schloss|burg|burgruine|ruine|château|chateau|castello|castel|castelgrande|festung|forteresse|fort)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotCastle = new(
        @"\d|d'eau|d’eau|hotel|restaurant|betrieb|garage|scheune|stall|remise|wohn|strasse|weg\b|gasse|rue|chemin|route|avenue|garten|gut\b|park|mühle|oekonom|ökonom|ferme|grange|dépendance|dependance|annexe|écurie|ecurie|pavillon|kapelle|chapelle|kirche|église|eglise|schopf|schuppen|werkstatt|büro|halle|bad\b|schule|école|ecole|spital|heim|keller|cave|trotte|pressoir|waschhaus|haus\b|buvette|kiosk|toilett|gärtnerei|treibhaus|orangerie|villa|bauernhaus|depot|dépôt|lager|tenne|speicher|stöckli|pächter|verwalter|concierge|loge|portier|bureau|parking|hof\b|matte|berg\b|halde|acker|feld|egg\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Built before this, or of unknown year, an old solid can belong to a castle.</summary>
    public const int OldBefore = 1850;
    /// <summary>A GWR castle point in a courtyard takes the nearest qualifying solid this close.</summary>
    private const float CourtyardReach = 25f;
    /// <summary>How far from its first solid a castle's touching solids may reach.</summary>
    private const float GroupReach = 110f;

    /// <summary>What a castle is, as the game's <c>CastleKind</c>: 0 a fortress, 1 a stately home or manor, 2 a ruin; -1 unknown yet.</summary>
    private const int Unknown = -1, Fortress = 0, Manor = 1, Ruin = 2;
    /// <summary>
    /// A castle whose solids were mostly built from this year on is a stately home, not a fortress.
    /// Late: the GWR year is often a rebuild's (Tarasp's says 1628), and Swiss OSM tags nearly every
    /// "Schloss" <c>stately</c>, Thun's keep included, so the game dresses a keep-like solid of a
    /// manor as a fortress anyway.
    /// </summary>
    public const int ManorFrom = 1650;

    private static readonly Regex RuinName = new(@"ruine|rovine|diroccat|\bruin", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record Candidate(string Name, string Source, double E, double N, List<(double E, double N)>? Outline, int Kind = Unknown);

    /// <summary>
    /// The kind OSM gives (castle_type, ruins) or the name says. "schloss" and "stately" are the
    /// country seats and bailiffs' houses, "defensive", "burg", "castrum" the fortresses.
    /// </summary>
    private static int KindOf(Dictionary<string, string> tags, string name)
    {
        if (tags.GetValueOrDefault("ruins") == "yes" || RuinName.IsMatch(name)) return Ruin;
        var type = tags.GetValueOrDefault("castle_type") ?? "";
        if (type.Contains("ruin")) return Ruin;
        if (type is "stately" or "manor" or "palace" or "schloss" or "chateau" or "château") return Manor;
        if (type is "defensive" or "fortress" or "castrum" or "burg" or "citadel" or "kastell" or "fort") return Fortress;
        return Unknown;
    }

    public static int Run(string chunkDir, string gwrPath, string pbf, string outFile, int jobs)
    {
        var sw = Stopwatch.StartNew();
        var candidates = new List<Candidate>();
        candidates.AddRange(FromOsm(pbf, jobs));
        int osm = candidates.Count;
        candidates.AddRange(FromGwr(gwrPath));
        Console.WriteLine($"castles: {osm} OSM + {candidates.Count - osm} GWR candidates ({sw.Elapsed.TotalSeconds:F0} s)");

        var tiles = new Dictionary<TileId, BuildingTile?>();
        BuildingTile? Tile(TileId id)
        {
            if (tiles.TryGetValue(id, out var t)) return t;
            var path = Path.Combine(chunkDir, BuildingFormat.FileName(id));
            if (File.Exists(path)) { using var fs = File.OpenRead(path); t = BuildingCodec.Decode(fs); }
            return tiles[id] = t;
        }

        var canton = Cantons(gwrPath);

        // OSM first: its outlines are the better source, and its names are the common ones
        var claimed = new HashSet<(TileId, int)>();
        var castles = new List<Castle>();
        int unresolved = 0;
        foreach (var c in candidates)
        {
            var solids = c.Outline != null ? InOutline(c, Tile) : AtPoint(c, Tile);
            var points = new List<(double E, double N)>();
            var years = new List<int>();
            foreach (var (id, i) in solids)
            {
                if (!claimed.Add((id, i))) continue;
                var b = Tile(id)!.Buildings[i];
                if (InsidePoint(id, b) is { } p) points.Add(p);
                if (b.YearBuilt > 0) years.Add(b.YearBuilt);
            }
            if (points.Count == 0)
            {
                if (solids.Count == 0) unresolved++;
                continue;
            }
            // no word from OSM or the name: the solids' own years decide, a fortress when unknown
            int kind = c.Kind != Unknown ? c.Kind
                : years.Count > 0 && years.Order().ElementAt(years.Count / 2) >= ManorFrom ? Manor : Fortress;
            castles.Add(new Castle(c.Name, canton(points[0].E, points[0].N), kind, points));
        }
        castles.Sort((a, b) => a.Points[0].E != b.Points[0].E ? a.Points[0].E.CompareTo(b.Points[0].E) : a.Points[0].N.CompareTo(b.Points[0].N));
        Write(outFile, castles);
        Console.WriteLine($"castles: {castles.Count} castles, {castles.Sum(c => c.Points.Count)} solids; " +
                          $"{unresolved} candidates without a solid (ruins, unbuilt tiles) -> {outFile} ({sw.Elapsed.TotalSeconds:F0} s)");
        Console.WriteLine("castles by kind: " + string.Join(", ", castles.GroupBy(c => c.Kind).OrderBy(g => g.Key)
            .Select(g => $"{(g.Key == Fortress ? "fortress" : g.Key == Manor ? "manor" : "ruin")} {g.Count()}")));
        Console.WriteLine("castles by canton: " + string.Join(", ", castles.GroupBy(c => c.Canton).OrderByDescending(g => g.Count())
            .Select(g => $"{(g.Key == "" ? "?" : g.Key)} {g.Count()}")));
        return 0;
    }

    private static IEnumerable<Candidate> FromOsm(string pbf, int jobs)
    {
        var keys = new HashSet<string> { "historic", "name", "castle_type", "ruins" };
        static bool IsCastle(Dictionary<string, string> t) => t.GetValueOrDefault("historic") == "castle";
        // pass 1: the castle ways and points; pass 2: the positions of the ways' nodes only
        var first = PbfReader.Read(pbf, jobs, new PbfReader.Filter
        {
            KeepNode = (_, _) => false, KeepWay = IsCastle, WayTags = keys,
            KeepTaggedNode = IsCastle, NodeTags = keys, TaggedNodesAnywhere = true,
        });
        var refs = first.Ways.SelectMany(w => w.Refs).ToHashSet();
        var nodes = PbfReader.Read(pbf, jobs, new PbfReader.Filter
        {
            KeepNode = (_, _) => false, KeepNodeId = refs.Contains, KeepWay = _ => false, WayTags = keys,
        }).Nodes;

        foreach (var w in first.Ways)
        {
            if (w.Refs.Length < 4 || w.Refs[0] != w.Refs[^1]) continue; // only closed outlines
            var ring = new List<(double E, double N)>();
            foreach (var r in w.Refs)
                if (nodes.TryGetValue(r, out var ll)) ring.Add(SwissProjection.ToLv95(ll.Lat, ll.Lon));
            if (ring.Count < 4) continue;
            yield return new Candidate(NameOf(w.Tags), $"way/{w.Id}", ring.Average(p => p.E), ring.Average(p => p.N), ring, KindOf(w.Tags, NameOf(w.Tags)));
        }
        foreach (var n in first.TaggedNodes)
        {
            var (e, nn) = SwissProjection.ToLv95(n.Lat, n.Lon);
            yield return new Candidate(NameOf(n.Tags), $"node/{n.Id}", e, nn, null, KindOf(n.Tags, NameOf(n.Tags)));
        }
    }

    private static string NameOf(Dictionary<string, string> tags) => tags.GetValueOrDefault("name") ?? "Castle";

    private static IEnumerable<Candidate> FromGwr(string gwrPath)
    {
        using var conn = GeoPackageReader.Open(gwrPath);
        using var cmd = conn.CreateCommand();
        // existing buildings (GSTAT 1004) or no status; a name, a position and some floor area
        cmd.CommandText = """
            select GBEZ, GBAUJ, GKODE, GKODN, GAREA, GKLAS, EGID from building
            where GBEZ is not null and GKODE is not null and GKODN is not null
              and (GSTAT is null or GSTAT = 1004)
            order by EGID
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string name = r.GetString(0).Trim();
            if (!Head.IsMatch(name) || NotCastle.IsMatch(name)) continue;
            int year = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture);
            double area = r.IsDBNull(4) ? 0 : Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture);
            int gklas = r.IsDBNull(5) ? 0 : Convert.ToInt32(r.GetValue(5), CultureInfo.InvariantCulture);
            if (year >= OldBefore || area < 120) continue;
            // a house or a barn named "Schloss" is the castle's farm unless it is medieval itself
            if (gklas is 1110 or 1121 or 1122 or 1242 or 1251 or 1271 or 1276 or 1277 or 1278 && (year == 0 || year >= 1600)) continue;
            int kind = RuinName.IsMatch(name) ? Ruin : year >= ManorFrom ? Manor : year > 0 ? Fortress : Unknown;
            yield return new Candidate(name, $"egid/{r.GetValue(6)}", r.GetDouble(2), r.GetDouble(3), null, kind);
        }
    }

    private sealed record Castle(string Name, string Canton, int Kind, List<(double E, double N)> Points);

    /// <summary>
    /// The canton at a point: the one most GWR buildings of its kilometre cell are in, else of the
    /// nearest cell around it that has any; "" where there is none (a castle across the border).
    /// </summary>
    private static Func<double, double, string> Cantons(string gwrPath)
    {
        var cells = new Dictionary<(int, int), Dictionary<string, int>>();
        using (var conn = GeoPackageReader.Open(gwrPath))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select GDEKT, GKODE, GKODN from building where GDEKT is not null and GKODE is not null and GKODN is not null";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var cell = ((int)Math.Floor(r.GetDouble(1) / 1000), (int)Math.Floor(r.GetDouble(2) / 1000));
                if (!cells.TryGetValue(cell, out var count)) cells[cell] = count = new Dictionary<string, int>();
                var canton = r.GetString(0);
                count[canton] = count.GetValueOrDefault(canton) + 1;
            }
        }
        var best = cells.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).First().Key);
        return (e, n) =>
        {
            int ce = (int)Math.Floor(e / 1000), cn = (int)Math.Floor(n / 1000);
            for (int ring = 0; ring <= 3; ring++)
                for (int de = -ring; de <= ring; de++)
                    for (int dn = -ring; dn <= ring; dn++)
                        if (Math.Max(Math.Abs(de), Math.Abs(dn)) == ring && best.TryGetValue((ce + de, cn + dn), out var canton))
                            return canton;
            return "";
        };
    }

    /// <summary>Whether a solid may be part of a castle: old, or of unknown year and not a home or a works.</summary>
    private static bool Old(Building b) => b.YearBuilt is > 0 and < OldBefore
        || b.YearBuilt == 0 && b.Kind is BuildingKind.Annex or BuildingKind.Other or BuildingKind.Civic or BuildingKind.Sacral;

    private static IEnumerable<TileId> Around(double e, double n, int reach = 1)
    {
        var c = TileId.FromLv95(e, n);
        for (int de = -reach; de <= reach; de++)
            for (int dn = -reach; dn <= reach; dn++)
                yield return new TileId(c.E + de, c.N + dn);
    }

    private static (float X, float Z) Local(TileId id, double e, double n) => ((float)(e - id.MinE), (float)(id.MaxN - n));

    private static (float X, float Z) Centre(Building b)
    {
        double x = 0, z = 0;
        var t = b.Triangles;
        for (int i = 0; i < t.Length; i += 3) { x += t[i]; z += t[i + 2]; }
        int n = t.Length / 3;
        return ((float)(x / n), (float)(z / n));
    }

    private static List<(TileId, int)> InOutline(Candidate c, Func<TileId, BuildingTile?> tile)
    {
        var found = new List<(TileId, int)>();
        foreach (var id in Around(c.E, c.N))
        {
            if (tile(id) is not { } t) continue;
            var ring = c.Outline!.Select(p => Local(id, p.E, p.N)).ToList();
            for (int i = 0; i < t.Buildings.Count; i++)
            {
                var (x, z) = Centre(t.Buildings[i]);
                if (Inside(ring, x, z)) found.Add((id, i));
            }
        }
        return found;
    }

    private static List<(TileId, int)> AtPoint(Candidate c, Func<TileId, BuildingTile?> tile)
    {
        (TileId Id, int Index, float Dist)? seed = null;
        foreach (var id in Around(c.E, c.N))
        {
            if (tile(id) is not { } t) continue;
            var (x, z) = Local(id, c.E, c.N);
            for (int i = 0; i < t.Buildings.Count; i++)
            {
                var b = t.Buildings[i];
                if (b.Covers(x, z)) { seed = (id, i, 0f); break; }
                if (!Old(b)) continue;
                float d = Nearest(b, x, z);
                if (d <= CourtyardReach && (seed == null || d < seed.Value.Dist)) seed = (id, i, d);
            }
            if (seed is { Dist: 0f }) break;
        }
        if (seed is not { } s) return new();

        // the old solids touching it, and those touching them, in its own tile
        var st = tile(s.Id)!;
        var group = new List<(TileId, int)> { (s.Id, s.Index) };
        var (sx, sz) = Centre(st.Buildings[s.Index]);
        for (int k = 0; k < group.Count; k++)
        {
            var m = st.Buildings[group[k].Item2];
            for (int i = 0; i < st.Buildings.Count; i++)
            {
                if (group.Contains((s.Id, i))) continue;
                var b = st.Buildings[i];
                var (cx, cz) = Centre(b);
                if (!Old(b) || MathF.Sqrt((cx - sx) * (cx - sx) + (cz - sz) * (cz - sz)) > GroupReach) continue;
                if (Touch(m, b)) group.Add((s.Id, i));
            }
        }
        return group;
    }

    // the plan distance from a point to a solid's nearest vertex
    private static float Nearest(Building b, float x, float z)
    {
        float best = float.MaxValue;
        var t = b.Triangles;
        for (int i = 0; i < t.Length; i += 3)
            best = MathF.Min(best, (t[i] - x) * (t[i] - x) + (t[i + 2] - z) * (t[i + 2] - z));
        return MathF.Sqrt(best);
    }

    // two solids share a wall: some vertex of one within 0.6 m of a vertex of the other, in plan
    private static bool Touch(Building a, Building b)
    {
        if (a.MinY > b.MaxY + 1 || b.MinY > a.MaxY + 1) return false;
        var ta = a.Triangles;
        var tb = b.Triangles;
        var cells = new HashSet<(int, int)>();
        for (int i = 0; i < ta.Length; i += 3) cells.Add(((int)MathF.Floor(ta[i] / 0.6f), (int)MathF.Floor(ta[i + 2] / 0.6f)));
        for (int i = 0; i < tb.Length; i += 3)
        {
            int cx = (int)MathF.Floor(tb[i] / 0.6f), cz = (int)MathF.Floor(tb[i + 2] / 0.6f);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (cells.Contains((cx + dx, cz + dz))) return true;
        }
        return false;
    }

    private static bool Inside(List<(float X, float Z)> ring, float x, float z)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].Z > z) != (ring[j].Z > z)
                && x < (ring[j].X - ring[i].X) * (z - ring[i].Z) / (ring[j].Z - ring[i].Z) + ring[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>
    /// An LV95 point (to 0.1 m) on the solid's footprint: the centre of its biggest triangle seen
    /// from above, kept only if the rounded point still lands on the solid.
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
            var (x, z) = Local(id, e, n);
            if (b.Covers(x, z)) return (e, n);
        }
        return null;
    }

    private static void Write(string path, List<Castle> castles)
    {
        var sb = new StringBuilder();
        sb.Append("""
            // <auto-generated>
            // Written by `TerrainPreprocessor --castles` (tools/TerrainPreprocessor/CastleStage.cs, #859)
            // from OpenStreetMap historic=castle (ODbL) and the GWR building names, resolved against
            // the built .bldg tiles. Do not edit: regenerate (docs/notes/terrain/castles.md).
            // </auto-generated>
            namespace UnitSport.Interiors;

            public static partial class Castles
            {
                /// <summary>
                /// Every castle: its name, its canton (the GWR's two letters; "" across the border), what
                /// it is, then LV95 E, N pairs, one point inside each of its solids.
                /// </summary>
                public static readonly (string Name, string Canton, CastleKind Kind, double[] Points)[] List =
                {

            """);
        foreach (var (name, canton, kind, points) in castles)
        {
            sb.Append("        (\"").Append(name.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\", \"").Append(canton)
              .Append("\", CastleKind.").Append(kind == Manor ? "Manor" : kind == Ruin ? "Ruin" : "Fortress").Append(", new[] { ");
            sb.Append(string.Join(", ", points.Select(p =>
                p.E.ToString("0.0", CultureInfo.InvariantCulture) + ", " + p.N.ToString("0.0", CultureInfo.InvariantCulture))));
            sb.Append(" }),\n");
        }
        sb.Append("    };\n}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
