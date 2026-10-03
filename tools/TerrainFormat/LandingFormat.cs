using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.Terrain.Format;

/// <summary>What a pier deck is built as: a CGN landing's concrete pier, or a marina's timber jetty.</summary>
public enum PierKind
{
    /// <summary>A CGN landing: a concrete pier on piles, a timber deck, rails along its neck.</summary>
    Pier = 0,
    /// <summary>A harbour jetty (swissTLM3D <c>Hafensteg</c>): a narrow timber deck just over the water.</summary>
    Jetty = 1,
}

/// <summary>
/// One walkable deck: a centreline with the deck's height at each point, a width, mitred at the
/// bends. Consecutive points share their height, so the deck has no step anywhere: every change of
/// height is a ramp (the walk has no step-up, <c>docs/notes/player/walk-aboard.md</c>).
/// </summary>
public sealed class PierRibbon
{
    public PierKind Kind { get; set; }

    /// <summary>Deck width, m.</summary>
    public double Width { get; set; }

    /// <summary>Rails along both edges (a pier's neck); a jetty and a berth's face have none.</summary>
    public bool Rails { get; set; }

    /// <summary>The centreline: [LV95 E, LV95 N, deck top height (LN02)] per point, at least two.</summary>
    public List<double[]> Points { get; set; } = new();

    /// <summary>The middle of the centreline's extent, which decides the one tile that builds the ribbon.</summary>
    [JsonIgnore]
    public (double E, double N) Middle
    {
        get
        {
            double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
            foreach (var p in Points)
            {
                minE = Math.Min(minE, p[0]); maxE = Math.Max(maxE, p[0]);
                minN = Math.Min(minN, p[1]); maxN = Math.Max(maxN, p[1]);
            }
            return ((minE + maxE) * 0.5, (minN + maxN) * 0.5);
        }
    }
}

/// <summary>
/// Where the paddle steamer (#303) lies at a landing, alongside the pier's head with a gangway on
/// its deck. The API #379's steamer AI reads (<c>docs/notes/world/landings.md</c>).
/// </summary>
public sealed class LandingBerth
{
    /// <summary>LV95 of the steamer's keel under its centre of mass (its node's origin), m.</summary>
    public double E { get; set; }
    public double N { get; set; }

    /// <summary>The bow's heading, degrees clockwise from grid north (LV95), 0..360.</summary>
    public double Heading { get; set; }

    /// <summary>The ship's side against the pier: 0 port, 1 starboard (the steamer's gangway door index).</summary>
    public int Side { get; set; }

    /// <summary>The still water level at the berth, m (LN02).</summary>
    public double Level { get; set; }

    /// <summary>The least water under the hull's footprint, m (the bathymetry, #298).</summary>
    public double Depth { get; set; }

    /// <summary>The pier head's deck height where the gangway lands, m (LN02).</summary>
    public double Deck { get; set; }

    /// <summary>Whether <see cref="Depth"/> floats the steamer with its clearance; false: the berth is where the real boats lie, the survey says it is too shallow.</summary>
    public bool Fits { get; set; }
}

/// <summary>A boat stop (swissTLM3D <c>Haltestelle Schiff</c>, or a ferry's end): its pier and the steamer's berth.</summary>
public sealed class Landing
{
    public required string Name { get; set; }

    /// <summary>LV95 of the stop as surveyed (on the pier head, as a rule), m.</summary>
    public double E { get; set; }
    public double N { get; set; }

    /// <summary>Where the stop comes from: <c>Haltestelle Schiff</c>, or the ferry's kind for a ferry's end.</summary>
    public string Source { get; set; } = "Haltestelle Schiff";

    /// <summary>Null when no pier could be planned (no shore within reach).</summary>
    public LandingBerth? Berth { get; set; }

    /// <summary>The pier: its neck from the shore and its head (the berth's face).</summary>
    public List<PierRibbon> Ribbons { get; set; } = new();

    /// <summary>Bollards on the head's face: [E, N, deck height].</summary>
    public List<double[]> Bollards { get; set; } = new();
}

/// <summary>
/// A boat's place alongside a jetty (#383): LV95 of the boat's keel, its heading (degrees from grid
/// north), which boat (<see cref="Speedboat"/> or a jetski), the jetty's side (+1 left of its line,
/// -1 right) and a name every peer and every restart gives it alike.
/// </summary>
public readonly record struct BoatBerth(string Id, double E, double N, double Heading, bool Speedboat, int Side);

/// <summary>A harbour jetty from swissTLM3D (<c>tlm_bauten_verkehrsbaute_lin</c>, <c>objektart = Hafensteg</c>).</summary>
public sealed class Jetty
{
    /// <summary>The TLM feature's uuid.</summary>
    public string Id { get; set; } = "";

    public required PierRibbon Ribbon { get; set; }

    /// <summary>Metres between boat places along a jetty, and kept clear at its ends.</summary>
    public const double SlotSpacing = 9, SlotEnds = 4;

    /// <summary>Boats parked at a jetty at most.</summary>
    public const int MaxBoats = 4;

    /// <summary>
    /// Every place a boat could lie along this jetty (#383): every <see cref="SlotSpacing"/> metres
    /// from <see cref="SlotEnds"/> in, either side, the boat's side 0.4 m off the deck's edge, lying
    /// along it. Pure geometry: whether there is water enough there is the runtime's to say.
    /// </summary>
    public List<BoatBerth> BoatSlots()
    {
        var slots = new List<BoatBerth>();
        var p = Ribbon.Points;
        if (p.Count < 2) return slots;
        var cum = new double[p.Count];
        for (int i = 1; i < p.Count; i++) cum[i] = cum[i - 1] + Math.Sqrt(Sq(p[i][0] - p[i - 1][0]) + Sq(p[i][1] - p[i - 1][1]));
        double total = cum[^1];
        int seg = 0, k = 0;
        for (double at = SlotEnds; at <= total - SlotEnds; at += SlotSpacing, k++)
        {
            while (seg < p.Count - 2 && cum[seg + 1] < at) seg++;
            double len = cum[seg + 1] - cum[seg];
            if (len < 1e-6) continue;
            double t = (at - cum[seg]) / len;
            double ue = (p[seg + 1][0] - p[seg][0]) / len, un = (p[seg + 1][1] - p[seg][1]) / len;
            double ce = p[seg][0] + (p[seg + 1][0] - p[seg][0]) * t, cn = p[seg][1] + (p[seg + 1][1] - p[seg][1]) * t;
            foreach (int side in new[] { 1, -1 })
            {
                uint h = Hash($"{Id}/{k}/{side}");
                bool speedboat = h % 5 < 2;
                double off = Ribbon.Width * 0.5 + 0.4 + (speedboat ? 1.2 : 0.65);
                // left of the line: (-un, ue)
                double e = ce - un * off * side, n = cn + ue * off * side;
                double heading = Math.Atan2(ue, un) * 180 / Math.PI + ((h >> 3 & 1) == 0 ? 0 : 180);
                slots.Add(new BoatBerth($"m{Hash(Id):x8}_{k}{(side > 0 ? 'l' : 'r')}", Math.Round(e, 3), Math.Round(n, 3),
                    Math.Round((heading + 360) % 360, 2), speedboat, side));
            }
        }
        return slots;
    }

    /// <summary>The places that get a boat (#383): one slot in three, by its name's hash, at most <see cref="MaxBoats"/>.</summary>
    public List<BoatBerth> BoatBerths()
    {
        var chosen = new List<BoatBerth>();
        foreach (var b in BoatSlots())
            if (Hash(b.Id + "/boat") % 3 == 0 && chosen.Count < MaxBoats) chosen.Add(b);
        return chosen;
    }

    private static double Sq(double v) => v * v;

    /// <summary>FNV-1a of a name: the same on every peer and every run.</summary>
    private static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}

/// <summary>
/// The region's landings and jetties (#377), <c>landings.json</c> beside the manifest, written by
/// the preprocessor's <c>--landings</c> pass (<see cref="LandingPlanner"/>) and streamed to clients
/// like <c>places.json</c>. Every peer builds the same piers from it.
/// </summary>
public sealed class LandingIndex
{
    public const string FileName = "landings.json";

    public int Version { get; set; } = 1;
    public List<Landing> Landings { get; set; } = new();
    public List<Jetty> Jetties { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static LandingIndex FromJson(string json) =>
        JsonSerializer.Deserialize<LandingIndex>(json, Options) ?? new LandingIndex();

    /// <summary>Every ribbon, landings' and jetties'.</summary>
    public IEnumerable<PierRibbon> Ribbons()
    {
        foreach (var l in Landings)
            foreach (var r in l.Ribbons) yield return r;
        foreach (var j in Jetties) yield return j.Ribbon;
    }

    /// <summary>The ribbons a tile builds: those whose middle lies in it (each ribbon is built by one tile only).</summary>
    public IEnumerable<PierRibbon> RibbonsOf(TileId id)
    {
        foreach (var r in Ribbons())
        {
            var (e, n) = r.Middle;
            if (e >= id.MinE && e < id.MinE + ChunkFormat.TileSizeM && n > id.MaxN - ChunkFormat.TileSizeM && n <= id.MaxN)
                yield return r;
        }
    }

    /// <summary>The bollards a tile builds: those standing in it.</summary>
    public IEnumerable<double[]> BollardsOf(TileId id)
    {
        foreach (var l in Landings)
            foreach (var b in l.Bollards)
                if (b[0] >= id.MinE && b[0] < id.MinE + ChunkFormat.TileSizeM && b[1] > id.MaxN - ChunkFormat.TileSizeM && b[1] <= id.MaxN)
                    yield return b;
    }

    /// <summary>The landing whose name starts with <paramref name="name"/> (case and accents as given), or null.</summary>
    public Landing? Find(string name) =>
        Landings.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? Landings.FirstOrDefault(l => l.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The jetties grouped into harbours (#383): jetties within <paramref name="reach"/> metres of
    /// one another (any two of their points) are one harbour. Ordered as the jetties are.
    /// </summary>
    public List<List<Jetty>> Harbours(double reach = 120)
    {
        var parent = Enumerable.Range(0, Jetties.Count).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i < Jetties.Count; i++)
            for (int j = i + 1; j < Jetties.Count; j++)
                if (Near(Jetties[i], Jetties[j], reach)) parent[Root(i)] = Root(j);
        return Enumerable.Range(0, Jetties.Count).GroupBy(Root).Select(g => g.Select(i => Jetties[i]).ToList()).ToList();
    }

    private static bool Near(Jetty a, Jetty b, double reach)
    {
        foreach (var p in a.Ribbon.Points)
            foreach (var q in b.Ribbon.Points)
                if ((p[0] - q[0]) * (p[0] - q[0]) + (p[1] - q[1]) * (p[1] - q[1]) < reach * reach) return true;
        return false;
    }

    /// <summary>The landing nearest an LV95 point, or null when there are none.</summary>
    public Landing? Nearest(double e, double n) =>
        Landings.Count == 0 ? null : Landings.MinBy(l => (l.E - e) * (l.E - e) + (l.N - n) * (l.N - n));
}
