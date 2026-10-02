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

/// <summary>A harbour jetty from swissTLM3D (<c>tlm_bauten_verkehrsbaute_lin</c>, <c>objektart = Hafensteg</c>).</summary>
public sealed class Jetty
{
    /// <summary>The TLM feature's uuid.</summary>
    public string Id { get; set; } = "";

    public required PierRibbon Ribbon { get; set; }
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

    /// <summary>The landing nearest an LV95 point, or null when there are none.</summary>
    public Landing? Nearest(double e, double n) =>
        Landings.Count == 0 ? null : Landings.MinBy(l => (l.E - e) * (l.E - e) + (l.N - n) * (l.N - n));
}
