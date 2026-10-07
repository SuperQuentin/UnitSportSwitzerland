using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Farm fields from OpenStreetMap, for the cantons whose federal land-use data is not freely
/// published (VD, NE, TI, NW, OW; #494): closed ways and multipolygon relations tagged
/// <c>landuse=farmland</c> (crop from <c>crop=*</c>, else a hash-weighted pick by Swiss arable
/// shares) and <c>landuse=meadow</c> (Meadow). OSM is ODbL (<c>osm-odbl-licence</c>).
/// </summary>
public static class OsmFields
{
    /// <summary>A field outline in LV95: first ring outer. <paramref name="Id"/> is the way or relation id.</summary>
    public sealed record Poly(long Id, bool Relation, int Part, string? Landuse, string? Crop, List<double[]> Rings);

    private static readonly HashSet<string> WayKeys = ["landuse", "crop"];
    private static readonly HashSet<string> RelKeys = ["type", "landuse", "crop"];

    // lat0, lat1, lon0, lon1: generous boxes round VD + NE, TI, and NW + OW (nodes outside are never held)
    private static readonly (double, double, double, double)[] GatedBoxes =
    [
        (46.10, 47.15, 5.90, 7.30), (45.75, 46.70, 8.30, 9.30), (46.70, 47.10, 7.85, 8.60),
    ];

    public static bool IsField(Dictionary<string, string> tags) =>
        tags.TryGetValue("landuse", out var l) && l is "farmland" or "meadow";

    public static List<Poly> Load(string pbf, int jobs)
    {
        // pass 1: the multipolygon relations, to learn which untagged ways are their outlines
        var rels = PbfReader.Read(pbf, jobs, new PbfReader.Filter
        {
            KeepNode = (_, _) => false, KeepWay = _ => false, WayTags = new HashSet<string>(),
            KeepRelation = t => t.GetValueOrDefault("type") == "multipolygon" && IsField(t), RelationTags = RelKeys,
        }).Relations;
        var memberWays = new HashSet<long>();
        foreach (var r in rels)
            foreach (var m in r.Members) if (m.Type == PbfReader.MemberType.Way) memberWays.Add(m.Ref);

        var data = PbfReader.Read(pbf, jobs, new PbfReader.Filter
        {
            KeepNode = (lat, lon) => GatedBoxes.Any(b => lat >= b.Item1 && lat <= b.Item2 && lon >= b.Item3 && lon <= b.Item4),
            KeepWay = IsField, WayTags = WayKeys, KeepWayId = memberWays.Contains,
        });
        var nodes = data.Nodes;
        var byId = new Dictionary<long, PbfReader.Way>(data.Ways.Count);
        foreach (var w in data.Ways) byId[w.Id] = w;

        double[]? Ring(long[] ids)
        {
            var ring = new double[(ids.Length - 1) * 2]; // closed: the last id repeats the first
            for (int i = 0; i < ids.Length - 1; i++)
            {
                if (!nodes.TryGetValue(ids[i], out var ll)) return null;
                var (e, n) = SwissProjection.ToLv95(ll.Lat, ll.Lon);
                ring[i * 2] = e; ring[i * 2 + 1] = n;
            }
            return ring;
        }

        var result = new List<Poly>();
        foreach (var w in data.Ways)
        {
            if (!IsField(w.Tags) || w.Refs.Length < 4 || w.Refs[0] != w.Refs[^1]) continue;
            var ring = Ring(w.Refs);
            if (ring != null) result.Add(new Poly(w.Id, false, 0, w.Tags.GetValueOrDefault("landuse"), w.Tags.GetValueOrDefault("crop"), [ring]));
        }
        foreach (var rel in rels)
        {
            var outers = new List<long[]>();
            var inners = new List<long[]>();
            foreach (var m in rel.Members)
                if (m.Type == PbfReader.MemberType.Way && byId.TryGetValue(m.Ref, out var way))
                    (m.Role == "inner" ? inners : outers).Add(way.Refs);
            var outerRings = JoinRings(outers).Select(Ring).Where(r => r != null).Select(r => r!).ToList();
            var innerRings = JoinRings(inners).Select(Ring).Where(r => r != null).Select(r => r!).ToList();
            int part = 0;
            foreach (var rings in Assemble(outerRings, innerRings))
                result.Add(new Poly(rel.Id, true, part++, rel.Tags.GetValueOrDefault("landuse"), rel.Tags.GetValueOrDefault("crop"), rings));
        }
        return result;
    }

    /// <summary>Joins way fragments end to end into closed node-id rings (first id repeated last); open leftovers are dropped.</summary>
    public static List<long[]> JoinRings(List<long[]> ways)
    {
        var rings = new List<long[]>();
        var pool = ways.Where(w => w.Length >= 2).ToList();
        while (pool.Count > 0)
        {
            var cur = new List<long>(pool[^1]);
            pool.RemoveAt(pool.Count - 1);
            bool progress = true;
            while (cur[0] != cur[^1] && progress)
            {
                progress = false;
                for (int i = 0; i < pool.Count; i++)
                {
                    var w = pool[i];
                    if (w[0] == cur[^1]) cur.AddRange(w.Skip(1));
                    else if (w[^1] == cur[^1]) cur.AddRange(w.AsEnumerable().Reverse().Skip(1));
                    else if (w[^1] == cur[0]) cur.InsertRange(0, w.Take(w.Length - 1));
                    else if (w[0] == cur[0]) cur.InsertRange(0, w.AsEnumerable().Reverse().Take(w.Length - 1));
                    else continue;
                    pool.RemoveAt(i);
                    progress = true;
                    break;
                }
            }
            if (cur.Count >= 4 && cur[0] == cur[^1]) rings.Add(cur.ToArray());
        }
        return rings;
    }

    /// <summary>One polygon per outer ring, each inner ring going to the first outer that holds its first vertex.</summary>
    public static List<List<double[]>> Assemble(List<double[]> outers, List<double[]> inners)
    {
        var polys = outers.Select(o => new List<double[]> { o }).ToList();
        foreach (var hole in inners)
            foreach (var p in polys)
                if (FieldGeometry.InRing(p[0], hole[0], hole[1])) { p.Add(hole); break; }
        return polys;
    }

    // ---- crop choice -----------------------------------------------------------------------

    private static readonly (CropKind Crop, int Weight)[] Shares =
    [
        (CropKind.Wheat, 30), (CropKind.Maize, 20), (CropKind.Barley, 12), (CropKind.Rapeseed, 8), (CropKind.SugarBeet, 6),
        (CropKind.Potato, 4), (CropKind.Vegetables, 4), (CropKind.Sunflower, 2), (CropKind.Legumes, 4), (CropKind.OtherArable, 10),
    ];

    /// <summary>The crop of an OSM field. An unknown <c>crop</c> value falls back to the weighted pick, so it stays deterministic.</summary>
    public static CropKind CropOf(string? landuse, string? crop, uint id)
    {
        if (landuse == "meadow") return CropKind.Meadow;
        switch (crop)
        {
            case "wheat" or "spelt" or "rye" or "triticale" or "winter_wheat" or "grain" or "cereal": return CropKind.Wheat;
            case "barley" or "oat" or "oats": return CropKind.Barley;
            case "maize" or "corn": return CropKind.Maize;
            case "potato" or "potatoes": return CropKind.Potato;
            case "sugar_beet" or "beet" or "sugarbeet": return CropKind.SugarBeet;
            case "rapeseed" or "canola": return CropKind.Rapeseed;
            case "sunflower" or "sunflowers": return CropKind.Sunflower;
            case "vegetables" or "cabbage" or "carrot" or "lettuce" or "onion" or "leek": return CropKind.Vegetables;
            case "peas" or "beans" or "soy" or "soybeans" or "lupin" or "legumes": return CropKind.Legumes;
            case "grass" or "meadow" or "hay": return CropKind.Meadow;
            case "pasture": return CropKind.Pasture;
            case "fallow": return CropKind.Fallow;
        }
        int pick = (int)((id & 0x7FFFFFFF) % 100);
        foreach (var (kind, weight) in Shares)
            if ((pick -= weight) < 0) return kind;
        return CropKind.OtherArable;
    }

    // ---- which fields lie in a gated canton --------------------------------------------------

    /// <summary>Cantons whose federal data is not freely published.</summary>
    public static readonly IReadOnlySet<string> GatedCantons = new HashSet<string> { "VD", "NE", "TI", "NW", "OW" };

    /// <summary>
    /// "Is this point in a gated canton", from the GWR building register (swissTLM3D ships no canton
    /// boundaries): 100 m cells score +1 per gated building and -1 per other, and a point takes the
    /// sign of the nearest scored cells within 1.5 km. Good to about a cell at a canton border, and a
    /// field is always near a building.
    /// </summary>
    public sealed class GatedMask
    {
        private const double Cell = 100;
        private const int MaxRing = 15;
        private readonly Dictionary<long, int> _score = new();

        private static long Key(int ce, int cn) => ((long)ce << 32) ^ (uint)cn;

        public void Add(double e, double n, bool gated)
        {
            long k = Key((int)Math.Floor(e / Cell), (int)Math.Floor(n / Cell));
            _score[k] = _score.GetValueOrDefault(k) + (gated ? 1 : -1);
        }

        public bool Contains(double e, double n)
        {
            int ce = (int)Math.Floor(e / Cell), cn = (int)Math.Floor(n / Cell);
            for (int r = 0; r <= MaxRing; r++)
            {
                int sum = 0;
                bool any = false;
                for (int de = -r; de <= r; de++)
                    for (int dn = -r; dn <= r; dn++)
                    {
                        if (Math.Max(Math.Abs(de), Math.Abs(dn)) != r) continue;
                        if (_score.TryGetValue(Key(ce + de, cn + dn), out int s)) { sum += s; any = true; }
                    }
                if (any) return sum > 0;
            }
            return false;
        }

        public static GatedMask FromGwr(string gwrPath)
        {
            var mask = new GatedMask();
            using var conn = GeoPackageReader.Open(gwrPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select GDEKT, GKODE, GKODN from building where GKODE is not null and GKODN is not null and GDEKT is not null";
            using var r = cmd.ExecuteReader();
            while (r.Read()) mask.Add(r.GetDouble(1), r.GetDouble(2), GatedCantons.Contains(r.GetString(0)));
            return mask;
        }
    }
}
