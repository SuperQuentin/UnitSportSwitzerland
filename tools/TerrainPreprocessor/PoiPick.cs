using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The pure rules of <see cref="PoiStage"/> (#1224), on rows already read: the cross-to-peak join,
/// the village stop, the covered-bridge merge, deduplication, lake rings, radii and tiers. No
/// SQLite here, so tier 0 links this file (<c>PoiPickTests</c>).
/// </summary>
public static class PoiPick
{
    /// <summary>A summit cross this close to a peak stands on it.</summary>
    public const double CrossReach = 150;
    /// <summary>The fallback village stop: the nearest within this of the town's core.</summary>
    public const double VillageStopReach = 400;
    /// <summary>Two huts (or a church and its tower) of one name closer than this are one place.</summary>
    public const double DedupReach = 100;
    public const double BridgeMinLength = 20, DamMinLength = 50;
    public const double LakeMinArea = 10_000, NotableLakeArea = 100_000;
    public const double OutlineTolerance = 50;
    public const int OutlineMaxPoints = 128;

    /// <summary>A POI before it is a record: its source identity gives the hash.</summary>
    public sealed class Candidate
    {
        public PoiKind Kind;
        public string SourceId = "";
        public string Name = "";
        public double E, N, Z = double.NaN;
        public PoiFlags Flags;
        /// <summary>The TLM <c>objektart</c> (tiers), or "".</summary>
        public string Sort = "";
        public double[]? Outline;
        public long LineKey;
        public double Size;
    }

    /// <summary>A TLM uuid as a source identity: braces off, lower case, so a release's formatting cannot move an id.</summary>
    public static string SourceUuid(string? uuid) => (uuid ?? "").Trim().Trim('{', '}').ToLowerInvariant();

    /// <summary>The first form of a multilingual TLM name ("Nordend | Punta Nordend"), trimmed.</summary>
    public static string FirstName(string? name) => name == null ? "" : name.Split('|')[0].Trim();

    private static double Dist2(double e0, double n0, double e1, double n1) => (e0 - e1) * (e0 - e1) + (n0 - n1) * (n0 - n1);

    /// <summary>
    /// Crosses on a peak (within <see cref="CrossReach"/>) flag that peak <see cref="PoiFlags.Cross"/>
    /// and disappear; the rest stand alone, named after the nearest peak (or "Summit cross").
    /// Returns the standalone crosses.
    /// </summary>
    public static List<Candidate> JoinCrosses(List<Candidate> peaks, IEnumerable<Candidate> crosses)
    {
        var alone = new List<Candidate>();
        foreach (var c in crosses.OrderBy(c => c.SourceId, StringComparer.Ordinal))
        {
            Candidate? best = null;
            double bestD = double.MaxValue;
            foreach (var p in peaks)
            {
                double d = Dist2(p.E, p.N, c.E, c.N);
                if (d < bestD) { bestD = d; best = p; }
            }
            if (best != null && bestD <= CrossReach * CrossReach)
            {
                best.Flags |= PoiFlags.Cross | PoiFlags.Viewpoint;
                continue;
            }
            if (c.Name.Length > 0) c.Flags |= PoiFlags.Named;
            else c.Name = best != null && best.Name.Length > 0 ? $"Summit cross near {best.Name}" : "Summit cross";
            c.Flags |= PoiFlags.Viewpoint;
            alone.Add(c);
        }
        return alone;
    }

    /// <summary>
    /// One bus stop per town: the nearest to its core whose name holds the town's name, else the
    /// nearest within <see cref="VillageStopReach"/>. Flags them <see cref="PoiFlags.VillageStop"/>.
    /// </summary>
    public static int PickVillageStops(IReadOnlyList<Candidate> stops, IEnumerable<(string Name, double E, double N)> towns)
    {
        var keys = stops.Select(s => PlaceIndex.Normalize(s.Name)).ToArray();
        int picked = 0;
        foreach (var (name, e, n) in towns)
        {
            string town = PlaceIndex.Normalize(name);
            int byName = -1, near = -1;
            double byNameD = double.MaxValue, nearD = VillageStopReach * VillageStopReach;
            for (int i = 0; i < stops.Count; i++)
            {
                double d = Dist2(stops[i].E, stops[i].N, e, n);
                if (town.Length > 0 && keys[i].Contains(town, StringComparison.Ordinal) && d < byNameD) { byNameD = d; byName = i; }
                if (d <= nearD) { nearD = d; near = i; }
            }
            // a stop "Riddes, gare" 3 km away is still the village's; one named after another town is not
            int pick = byName >= 0 && byNameD <= 5000.0 * 5000.0 ? byName : near;
            if (pick < 0 || stops[pick].Flags.HasFlag(PoiFlags.VillageStop)) continue;
            stops[pick].Flags |= PoiFlags.VillageStop;
            picked++;
        }
        return picked;
    }

    /// <summary>A line segment of a structure: its uuid, name and points.</summary>
    public sealed record Segment(string Uuid, string Name, double[] E, double[] N, double[] Z)
    {
        public double Length
        {
            get
            {
                double l = 0;
                for (int i = 1; i < E.Length; i++) l += Math.Sqrt(Dist2(E[i - 1], N[i - 1], E[i], N[i]));
                return l;
            }
        }
    }

    /// <summary>
    /// Covered-bridge segments that touch end to end (within a metre) are one structure; each
    /// structure of at least <paramref name="minLength"/> m is one candidate, identified by its
    /// longest segment's uuid, at that segment's middle point.
    /// </summary>
    public static List<Candidate> MergeSegments(IReadOnlyList<Segment> segments, PoiKind kind, double minLength)
    {
        int n = segments.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        bool Touch(Segment a, Segment b)
        {
            foreach (int i in new[] { 0, a.E.Length - 1 })
                foreach (int j in new[] { 0, b.E.Length - 1 })
                    if (Dist2(a.E[i], a.N[i], b.E[j], b.N[j]) <= 1) return true;
            return false;
        }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (Math.Abs(segments[i].E[0] - segments[j].E[0]) < 2000 && Touch(segments[i], segments[j]))
                    parent[Find(i)] = Find(j);
        var result = new List<Candidate>();
        foreach (var group in Enumerable.Range(0, n).GroupBy(Find))
        {
            var parts = group.Select(i => segments[i]).ToList();
            double total = parts.Sum(p => p.Length);
            if (total < minLength) continue;
            var longest = parts.OrderByDescending(p => p.Length).ThenBy(p => p.Uuid, StringComparer.Ordinal).First();
            int mid = longest.E.Length / 2;
            double e = longest.E.Length % 2 == 1 ? longest.E[mid] : (longest.E[mid - 1] + longest.E[mid]) / 2;
            double nn = longest.N.Length % 2 == 1 ? longest.N[mid] : (longest.N[mid - 1] + longest.N[mid]) / 2;
            double z = longest.Z.Length % 2 == 1 ? longest.Z[mid] : (longest.Z[mid - 1] + longest.Z[mid]) / 2;
            string name = parts.Select(p => FirstName(p.Name)).FirstOrDefault(s => s.Length > 0) ?? "";
            result.Add(new Candidate
            {
                Kind = kind, SourceId = longest.Uuid, Name = name, E = e, N = nn, Z = z, Size = total,
                Flags = name.Length > 0 ? PoiFlags.Named : PoiFlags.None,
            });
        }
        return result;
    }

    /// <summary>
    /// Of the candidates sharing a (normalised) name within <paramref name="reach"/> m, keeps the
    /// first by source id: a church and its tower named alike, a hut drawn as two footprints.
    /// </summary>
    public static List<Candidate> Dedup(IEnumerable<Candidate> candidates, double reach)
    {
        var kept = new List<Candidate>();
        var byName = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        foreach (var c in candidates.OrderBy(c => c.SourceId, StringComparer.Ordinal))
        {
            string key = PlaceIndex.Normalize(c.Name);
            if (!byName.TryGetValue(key, out var same)) byName[key] = same = new List<Candidate>();
            if (same.Any(s => Dist2(s.E, s.N, c.E, c.N) <= reach * reach)) continue;
            same.Add(c);
            kept.Add(c);
        }
        return kept;
    }

    /// <summary>Joins polylines end to end (within a metre) into closed rings; open chains are dropped.</summary>
    public static List<double[]> Rings(IEnumerable<double[]> lines)
    {
        var open = lines.Where(l => l.Length >= 4).Select(l => l.ToList()).ToList();
        var rings = new List<double[]>();
        static bool Same(List<double> a, int i, List<double> b, int j) => Dist2(a[i], a[i + 1], b[j], b[j + 1]) <= 1;
        while (open.Count > 0)
        {
            var ring = open[^1];
            open.RemoveAt(open.Count - 1);
            bool grew = true;
            while (!Same(ring, 0, ring, ring.Count - 2) && grew)
            {
                grew = false;
                for (int k = 0; k < open.Count; k++)
                {
                    var l = open[k];
                    int last = ring.Count - 2, lLast = l.Count - 2;
                    if (Same(ring, last, l, 0)) ring.AddRange(l.Skip(2));
                    else if (Same(ring, last, l, lLast)) ring.AddRange(Reverse(l).Skip(2));
                    else if (Same(ring, 0, l, lLast)) ring.InsertRange(0, l.Take(l.Count - 2));
                    else if (Same(ring, 0, l, 0)) ring.InsertRange(0, Reverse(l).Take(l.Count - 2));
                    else continue;
                    open.RemoveAt(k);
                    grew = true;
                    break;
                }
            }
            if (ring.Count >= 8 && Same(ring, 0, ring, ring.Count - 2)) rings.Add(ring.ToArray());
        }
        return rings;
    }

    private static List<double> Reverse(List<double> pts)
    {
        var r = new List<double>(pts.Count);
        for (int i = pts.Count - 2; i >= 0; i -= 2) { r.Add(pts[i]); r.Add(pts[i + 1]); }
        return r;
    }

    /// <summary>The area of a ring of E, N pairs, m².</summary>
    public static double Area(double[] ring)
    {
        double a = 0;
        int n = ring.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++) a += (ring[j * 2] + ring[i * 2]) * (ring[j * 2 + 1] - ring[i * 2 + 1]);
        return Math.Abs(a / 2);
    }

    /// <summary>A point inside a ring (even-odd).</summary>
    public static bool Inside(double[] ring, double e, double n)
    {
        bool inside = false;
        int count = ring.Length / 2;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            double xi = ring[i * 2], yi = ring[i * 2 + 1], xj = ring[j * 2], yj = ring[j * 2 + 1];
            if ((yi > n) != (yj > n) && e < (xj - xi) * (n - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Douglas-Peucker at <paramref name="tolerance"/> m, the tolerance doubled until the ring has at
    /// most <paramref name="maxPoints"/> points. The ring stays closed (first point = last).
    /// </summary>
    public static double[] Simplify(double[] ring, double tolerance = OutlineTolerance, int maxPoints = OutlineMaxPoints)
    {
        int n = ring.Length / 2;
        if (n <= 4) return ring;
        for (double tol = tolerance; ; tol *= 2)
        {
            var keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            // split a closed ring at its far point too, or the chord 0..n-1 has no length
            int far = 1;
            double farD = 0;
            for (int i = 1; i < n - 1; i++)
            {
                double d = Dist2(ring[0], ring[1], ring[i * 2], ring[i * 2 + 1]);
                if (d > farD) { farD = d; far = i; }
            }
            keep[far] = true;
            Dp(ring, 0, far, tol, keep);
            Dp(ring, far, n - 1, tol, keep);
            int kept = keep.Count(k => k);
            if (kept <= maxPoints || tol > 1e6)
            {
                var r = new double[kept * 2];
                int w = 0;
                for (int i = 0; i < n; i++)
                    if (keep[i]) { r[w++] = ring[i * 2]; r[w++] = ring[i * 2 + 1]; }
                return r;
            }
        }
    }

    private static void Dp(double[] p, int a, int b, double tol, bool[] keep)
    {
        if (b <= a + 1) return;
        double ax = p[a * 2], ay = p[a * 2 + 1], bx = p[b * 2], by = p[b * 2 + 1];
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        int worst = -1;
        double worstD = tol * tol;
        for (int i = a + 1; i < b; i++)
        {
            double px = p[i * 2] - ax, py = p[i * 2 + 1] - ay;
            double t = len2 == 0 ? 0 : Math.Clamp((px * dx + py * dy) / len2, 0, 1);
            double ex = px - t * dx, ey = py - t * dy, d = ex * ex + ey * ey;
            if (d > worstD) { worstD = d; worst = i; }
        }
        if (worst < 0) return;
        keep[worst] = true;
        Dp(p, a, worst, tol, keep);
        Dp(p, worst, b, tol, keep);
    }

    /// <summary>How near a body must come to discover a POI, metres (the plan's section 3 table).</summary>
    public static float DiscoverRadius(PoiKind kind) => kind switch
    {
        PoiKind.Peak or PoiKind.SummitCross => 40,
        PoiKind.Hut => 50,
        PoiKind.Pass or PoiKind.Castle or PoiKind.Dam => 80,
        PoiKind.Lake => 60,   // the outline's buffer
        PoiKind.Waterfall or PoiKind.Church or PoiKind.Chapel or PoiKind.LookoutTower => 50,
        PoiKind.Bridge or PoiKind.BusStop => 30,
        PoiKind.RailStation or PoiKind.CableCarStation => 60,
        PoiKind.ShipLanding => 40,
        _ => 50,
    };

    /// <summary>
    /// A viewpoint's reveal disc, metres: towers and road passes 3 km; crosses and cable-car tops
    /// <c>clamp(2 km + 1.5 km x (elevation - 500 m) / 1 000 m, 2 km, 7 km)</c>; 0 for the rest.
    /// </summary>
    public static float RevealRadius(PoiKind kind, PoiFlags flags, double elevation)
    {
        if (!flags.HasFlag(PoiFlags.Viewpoint)) return 0;
        if (kind is PoiKind.LookoutTower or PoiKind.Pass) return 3000;
        return (float)Math.Clamp(2000 + 1500 * (elevation - 500) / 1000, 2000, 7000);
    }

    /// <summary>
    /// What counts for completion: 2 Notable = Hauptgipfel, Alpiner Gipfel, Strassenpass, castle,
    /// tower, named waterfall, lake of 10 ha, hut, cable-car top, covered bridge, dam; 3 the rest.
    /// Tier 1 (a curated card) is set afterwards.
    /// </summary>
    public static byte Tier(Candidate c) => c.Kind switch
    {
        PoiKind.Peak => (byte)(c.Sort is "Hauptgipfel" or "Alpiner Gipfel" ? 2 : 3),
        PoiKind.Pass => (byte)(c.Sort == "Strassenpass" ? 2 : 3),
        PoiKind.Castle or PoiKind.LookoutTower or PoiKind.Hut or PoiKind.Bridge or PoiKind.Dam => 2,
        PoiKind.Waterfall => (byte)(c.Flags.HasFlag(PoiFlags.Named) ? 2 : 3),
        PoiKind.Lake => (byte)(c.Size >= NotableLakeArea ? 2 : 3),
        PoiKind.CableCarStation => (byte)(c.Flags.HasFlag(PoiFlags.Top) ? 2 : 3),
        _ => 3,
    };

    /// <summary>Ids that collide within a kind (the same 40-bit hash from two source ids): the build must fail on any.</summary>
    public static List<string> Collisions(IEnumerable<Candidate> all, Func<string, ulong>? hash = null) =>
        all.GroupBy(c => (c.Kind, (hash ?? PoiId.Hash)(c.SourceId)))
            .Where(g => g.Select(c => c.SourceId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"{PoiId.Tag(g.Key.Kind)} {g.Key.Item2:x10}: {string.Join(", ", g.Select(c => c.SourceId))}")
            .ToList();

    /// <summary>A hut by its name: the SAC's and the cantons' words for one, in the four languages.</summary>
    public static bool IsHut(string name)
    {
        string n = PlaceIndex.Normalize(name);
        return n.Contains("hutte", StringComparison.Ordinal) || n.Contains("huette", StringComparison.Ordinal)
            || n.Contains("cabane", StringComparison.Ordinal) || n.Contains("capanna", StringComparison.Ordinal)
            || n.Contains("chamanna", StringComparison.Ordinal) || n.Contains("rifugio", StringComparison.Ordinal);
    }
}
