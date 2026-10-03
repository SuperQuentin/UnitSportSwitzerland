namespace UnitSport.Tools.RoadGen.Import;

using System.Globalization;

/// <summary>
/// Reads <c>osm_nodes.tsv</c> (#347, written beside <c>osm_overlay.tsv</c> by
/// <c>TerrainPreprocessor --osm-overlay</c>, format in docs/notes/tools/osm-overlay.md): OSM traffic
/// signals, bike boxes and turn restrictions, each on a TLM line (uuid, part, along-line metre) with
/// the line end it belongs to. Nothing in the network stage reads it yet (#348, #349).
/// </summary>
public sealed class OsmNodesReader
{
    public const string FileName = "osm_nodes.tsv";

    public enum NodeKind : byte { Signal, Asl, Restriction }

    /// <summary>Node: on an OSM node where car ways meet. Approach: within 30 m of <see cref="Entry.LineEnd"/>. Via: a restriction.</summary>
    public enum JunctionKind : byte { Node, Approach, Mid, Via }

    public enum End : byte { None, Start, End }

    /// <summary>The traffic a signal or bike box faces, relative to TLM drawing order; None = not tagged.</summary>
    public enum Facing : byte { None, Forward, Backward, Both }

    /// <summary>
    /// One row. For a restriction, the TLM line (and <see cref="LineEnd"/>) is the from-line, the
    /// position is the via node, and <see cref="Value"/> the restriction (<c>no_left_turn</c>, ...).
    /// </summary>
    public sealed record Entry(NodeKind Kind, long OsmId, double E, double N, string Uuid, int Part, double Along,
        JunctionKind Junction, End LineEnd, double EndE, double EndN, Facing Dir, string Value,
        string ToUuid, int ToPart, End ToEnd, IReadOnlyDictionary<string, string> Tags)
    {
        /// <summary>A pedestrian-only signal (<c>crossing=traffic_signals</c> without <c>highway=traffic_signals</c>).</summary>
        public bool PedestrianOnly => Kind == NodeKind.Signal && Tags.GetValueOrDefault("highway") != "traffic_signals";
    }

    private readonly List<Entry> _all = new();
    private readonly Dictionary<(string Uuid, int Part), List<Entry>> _byLine = new();

    public IReadOnlyList<Entry> All => _all;

    public static OsmNodesReader? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        var reader = new OsmNodesReader();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("kind\t", StringComparison.Ordinal)) continue;
            var c = line.Split('\t');
            if (c.Length < 17) continue;
            var entry = new Entry(
                c[0] switch { "signal" => NodeKind.Signal, "asl" => NodeKind.Asl, _ => NodeKind.Restriction },
                long.Parse(c[1], CultureInfo.InvariantCulture), D(c[2]), D(c[3]), c[4], int.Parse(c[5], CultureInfo.InvariantCulture), D(c[6]),
                c[7] switch { "node" => JunctionKind.Node, "approach" => JunctionKind.Approach, "via" => JunctionKind.Via, _ => JunctionKind.Mid },
                EndOf(c[8]), c[9] == "" ? 0 : D(c[9]), c[10] == "" ? 0 : D(c[10]),
                c[11] switch { "+" => Facing.Forward, "-" => Facing.Backward, "both" => Facing.Both, _ => Facing.None },
                c[12], c[13], c[14] == "" ? -1 : int.Parse(c[14], CultureInfo.InvariantCulture), EndOf(c[15]), Tags(c[16]));
            reader._all.Add(entry);
            var key = (entry.Uuid, entry.Part);
            if (!reader._byLine.TryGetValue(key, out var list)) reader._byLine[key] = list = new List<Entry>();
            list.Add(entry);
        }
        return reader;
    }

    /// <summary>Every row on a TLM line (for a restriction: its from-line), in along-line order.</summary>
    public IReadOnlyList<Entry> ForLine(string uuid, int part) =>
        _byLine.TryGetValue((uuid, part), out var list) ? list : [];

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static End EndOf(string s) => s switch { "start" => End.Start, "end" => End.End, _ => End.None };

    /// <summary><c>k=v;k=v</c> (a <c>;</c> inside a value was written as <c>,</c>).</summary>
    private static Dictionary<string, string> Tags(string s)
    {
        var tags = new Dictionary<string, string>();
        foreach (var kv in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = kv.IndexOf('=');
            if (eq > 0) tags[kv[..eq]] = kv[(eq + 1)..];
        }
        return tags;
    }
}
