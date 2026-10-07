namespace UnitSport.Tools.RoadGen.Import;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>
/// Reads <c>osm_overlay.tsv</c> (written by <c>TerrainPreprocessor --osm-overlay</c>, format in
/// docs/notes/tools/osm-overlay.md) and turns the rows covering a piece of a TLM line into v3
/// attributes. Every per-side and per-direction column is already in TLM drawing order.
/// <c>turn_lanes_fwd</c>/<c>turn_lanes_bwd</c> (#347) come parsed per lane (<see cref="TurnLanes"/>):
/// forward = traffic in TLM drawing order, each list left to right as its drivers see it.
/// </summary>
public sealed class OsmOverlayReader
{
    public sealed record Row(double From, double To, string Highway, string OneWay, string Lanes,
        string LanesFwd, string LanesBwd, string Width, string SidewalkLeft, string SidewalkRight,
        string CyclewayLeft, string CyclewayRight, bool Roundabout, bool Tram,
        TurnMove[] TurnLanesFwd, TurnMove[] TurnLanesBwd, int MaxSpeedFwd = 0, int MaxSpeedBwd = 0)
    {
        /// <summary>The speed limit (km/h, #711) for traffic in TLM drawing order (forward) or against it, 0 where OSM has none.</summary>
        public int MaxSpeed(bool forward) => forward ? MaxSpeedFwd : MaxSpeedBwd;
    }

    private readonly Dictionary<(string Uuid, int Part), List<Row>> _rows = new();

    public int RowCount { get; private set; }

    public static OsmOverlayReader? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        var reader = new OsmOverlayReader();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("uuid\t", StringComparison.Ordinal)) continue;
            var c = line.Split('\t');
            if (c.Length < 20) continue;
            var key = (c[0], int.Parse(c[1], CultureInfo.InvariantCulture));
            if (!reader._rows.TryGetValue(key, out var list)) reader._rows[key] = list = new List<Row>();
            list.Add(new Row(D(c[2]), D(c[3]), c[6], c[7], c[8], c[9], c[10], c[11],
                c[12], c[13], c[14], c[15], c[18] == "1", c[19] == "1", TurnLanes.Parse(c[16]), TurnLanes.Parse(c[17]),
                c.Length > 21 ? Int(c[20]) : 0, c.Length > 21 ? Int(c[21]) : 0));   // v2: maxspeed_fwd, maxspeed_bwd (#711)
            reader.RowCount++;
        }
        return reader;
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>
    /// The row overlapping [from, to] of the line the most, if it covers at least half of it.
    /// A segment gets one row: splitting segments where OSM changes is for the issues that need it.
    /// </summary>
    public Row? Best(string uuid, int part, double from, double to)
    {
        if (!_rows.TryGetValue((uuid, part), out var list)) return null;
        double length = Math.Max(to - from, 0.1);
        Row? best = null;
        double bestOverlap = 0;
        foreach (var row in list)
        {
            double overlap = Math.Min(to, row.To) - Math.Max(from, row.From);
            if (overlap > bestOverlap) { bestOverlap = overlap; best = row; }
        }
        return bestOverlap >= 0.5 * length ? best : null;
    }

    /// <summary>
    /// The row at one end of a line (#700): OSM maps a widened approach as its own short way, which
    /// never covers half of the TLM line, so <see cref="Best"/> does not see it. The row overlapping
    /// the <paramref name="window"/> metres before <paramref name="m"/> (traffic arriving at it,
    /// <paramref name="towardEnd"/>) or after it the most; none where no row reaches that stretch.
    /// </summary>
    public Row? AtEnd(string uuid, int part, double m, bool towardEnd, double window = 12)
    {
        if (!_rows.TryGetValue((uuid, part), out var list)) return null;
        double from = towardEnd ? m - window : m, to = towardEnd ? m : m + window;
        Row? best = null;
        double bestOverlap = 0;
        foreach (var row in list)
        {
            double overlap = Math.Min(to, row.To) - Math.Max(from, row.From);
            if (overlap > bestOverlap) { bestOverlap = overlap; best = row; }
        }
        return best;
    }

    /// <summary>
    /// Folds a row into the attributes. OneWay is only taken where <paramref name="a"/> has none
    /// yet: the caller infers divided carriageways first, and TLM wins (the overlay already emptied
    /// OSM directions that contradict a divided line).
    /// </summary>
    public static RoadAttributes Apply(RoadAttributes a, Row row)
    {
        sbyte oneWay = a.OneWay != 0 ? a.OneWay
            : row.OneWay switch { "1" => (sbyte)1, "-1" => (sbyte)-1, _ => (sbyte)0 };

        int lanes = Int(row.Lanes), fwd = Int(row.LanesFwd), bwd = Int(row.LanesBwd);
        if (fwd == 0 && bwd == 0 && lanes > 0)
        {
            if (oneWay == 1) fwd = lanes;
            else if (oneWay == -1) bwd = lanes;
            else if (lanes % 2 == 0) fwd = bwd = lanes / 2;
        }

        ushort widthCm = a.WidthCm;
        if (double.TryParse(row.Width, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w > 0)
            widthCm = (ushort)Math.Round(Math.Min(w, 600) * 100);

        var flags = a.Flags | RoadAttrFlags.Osm;
        if (row.Roundabout) flags |= RoadAttrFlags.Roundabout;
        if (row.Tram) flags |= RoadAttrFlags.Tram;

        var left = Side(a.Left, row.SidewalkLeft, row.CyclewayLeft);
        var right = Side(a.Right, row.SidewalkRight, row.CyclewayRight);

        // Placeholder until #119 measures facades: a street is where OSM maps a sidewalk or tags
        // the way as a residential street.
        if (left.SidewalkDm > 0 || right.SidewalkDm > 0
            || row.Highway is "residential" or "living_street" or "pedestrian")
            flags |= RoadAttrFlags.Urban;

        return a with
        {
            Flags = flags, OneWay = oneWay,
            LanesForward = (byte)Math.Min(fwd, 15), LanesBackward = (byte)Math.Min(bwd, 15),
            WidthCm = widthCm, Left = left, Right = right,
        };
    }

    private static RoadSide Side(RoadSide side, string sidewalk, string cycleway)
    {
        // OSM gives presence, not width: 1.5 m sidewalk (#119's minimum), 1.5 m lane, 2 m track
        if (sidewalk is "yes" or "both") side = side with { SidewalkDm = 15 };
        return cycleway switch
        {
            "lane" or "opposite_lane" => side with { Bike = BikeKind.Lane, BikeDm = 15 },
            "track" or "opposite_track" => side with { Bike = BikeKind.Track, BikeDm = 20 },
            "shared_lane" or "share_busway" => side with { Bike = BikeKind.Shared, BikeDm = 0 },
            _ => side,
        };
    }

    private static int Int(string s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
}
