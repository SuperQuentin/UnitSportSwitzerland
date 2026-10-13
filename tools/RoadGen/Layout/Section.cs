namespace UnitSport.Tools.RoadGen.Layout;

using System.Globalization;
using System.Text;
using UnitSport.Terrain.Format;

/// <summary>What a strip of an arm's cross-section is (#818). Part names: <c>docs/notes/tools/intersection-parts.md</c>.</summary>
public enum StripKind : byte
{
    /// <summary>Raised walkway beside the kerb.</summary>
    Sidewalk,
    /// <summary>Grass between a bike path and the sidewalk.</summary>
    Buffer,
    /// <summary>Separated bike path at sidewalk height (or halfway down).</summary>
    Path,
    /// <summary>Grass between the carriageway and a bike path.</summary>
    Verge,
    /// <summary>Painted bike lane in the carriageway (Radstreifen).</summary>
    BikeLane,
    /// <summary>A car lane.</summary>
    CarLane,
    /// <summary>Striped median (Sperrfläche): flush, no one drives on it.</summary>
    Hatch,
    /// <summary>Raised, kerbed island in the carriageway (a refuge, a signal's island).</summary>
    Island,
}

/// <summary>
/// Where an arriving bike lane's riders stop at the lights (#818, the user's rule, the same for every move): ahead of the
/// cars at an advanced line where the street they go into carries their way on (a bike lane or path), else in a bike box
/// in front of the cars (SSV 6.26; ASTRA: box 4.0 m, advanced line 3.0 m). None without lights.
/// </summary>
public enum BikeStop : byte { None, Advanced, Box }

/// <summary>Which way a strip's traffic goes, seen from the junction: toward it (in), away from it (out), or none.</summary>
public enum StripWay : sbyte { Out = -1, None = 0, In = 1 }

/// <summary>
/// One strip across an arm (#818): its kind, its width (m), its traffic's way, the moves an in-lane makes at the junction
/// and an id that stays the same along the arm, so the same id at both ends of a transition is the same lane.
/// <see cref="SharedWithBikes"/>: a car lane cyclists ride in too (a right pocket beside a bike lane between, the user's rule:
/// riders turning right share it where their own lane at the kerb did not fit: a bike connector from it, no bike symbol in it).
/// </summary>
public readonly record struct Strip(StripKind Kind, double Width, StripWay Way = StripWay.None, SignalMoves Moves = SignalMoves.None, int Id = 0,
    bool SharedWithBikes = false, BikeStop Stop = BikeStop.None)
{
    /// <summary>Lies between the carriageway's kerbs (lanes, hatches, islands).</summary>
    public bool InCarriageway => Kind is StripKind.BikeLane or StripKind.CarLane or StripKind.Hatch or StripKind.Island;

    public bool IsCarLane => Kind == StripKind.CarLane;

    public Strip WithWidth(double width) => this with { Width = width };
}

/// <summary>
/// An arm's cross-section at one station (#818): its strips left to right as seen driving in (toward the junction), side
/// bands included, and where the carriageway lies across the arm's axis. Offsets are metres to the right of the axis for a
/// driver arriving. The street's normal section comes from its tile attributes (<see cref="Normal"/>); the junction planner
/// makes the mouth section; a transition runs from one to the other.
/// </summary>
public sealed class Section
{
    public IReadOnlyList<Strip> Strips { get; }

    /// <summary>Offset of the carriageway's left edge (its left kerb) from the arm's axis.</summary>
    public double CarriagewayLeft { get; }

    /// <summary>Kerb heights, left and right (cm, 0 flush).</summary>
    public int KerbLeftCm { get; init; }
    public int KerbRightCm { get; init; }

    public Section(IReadOnlyList<Strip> strips, double carriagewayLeft)
    {
        Strips = strips;
        CarriagewayLeft = carriagewayLeft;
        int first = -1, last = -1;
        for (int k = 0; k < strips.Count; k++)
            if (strips[k].InCarriageway) { if (first < 0) first = k; last = k; }
        for (int k = first + 1; k < last; k++)
            if (!strips[k].InCarriageway) throw new ArgumentException("the carriageway's strips must lie side by side", nameof(strips));
        FirstCarriageway = first;
        LastCarriageway = last;
    }

    /// <summary>Index of the leftmost and rightmost carriageway strip, -1 without any.</summary>
    public int FirstCarriageway { get; }
    public int LastCarriageway { get; }

    public double CarriagewayWidth
    {
        get
        {
            double w = 0;
            for (int k = FirstCarriageway; k >= 0 && k <= LastCarriageway; k++) w += Strips[k].Width;
            return w;
        }
    }

    public double CarriagewayRight => CarriagewayLeft + CarriagewayWidth;

    /// <summary>Where strip <paramref name="index"/> lies across the arm: its left and right offsets from the axis.</summary>
    public (double From, double To) Span(int index)
    {
        double at = CarriagewayLeft;
        if (FirstCarriageway < 0) return (at, at);
        if (index >= FirstCarriageway)
            for (int k = FirstCarriageway; k < index; k++) at += Strips[k].Width;
        else
            for (int k = FirstCarriageway - 1; k >= index; k--) at -= Strips[k].Width;
        return (at, at + Strips[index].Width);
    }

    /// <summary>
    /// The centre line: the boundary between the last strip whose traffic leaves and the first whose traffic arrives, as an
    /// offset from the axis; on a one-way carriageway its edge on the side nothing comes from. Null without car lanes.
    /// </summary>
    public double? CentreLine
    {
        get
        {
            int lastOut = -1, firstIn = -1;
            for (int k = FirstCarriageway; k >= 0 && k <= LastCarriageway; k++)
            {
                if (Strips[k].Way == StripWay.Out) lastOut = k;
                if (Strips[k].Way == StripWay.In && firstIn < 0) firstIn = k;
            }
            if (firstIn >= 0) return Span(firstIn).From;
            if (lastOut >= 0) return Span(lastOut).To;
            return null;
        }
    }

    /// <summary>The strips that are car lanes going <paramref name="way"/>, left to right, with their index.</summary>
    public IEnumerable<(int Index, Strip Strip)> CarLanes(StripWay way)
    {
        for (int k = 0; k < Strips.Count; k++)
            if (Strips[k].IsCarLane && Strips[k].Way == way) yield return (k, Strips[k]);
    }

    /// <summary>The departing side carries riders on: a departing bike lane, or a path on that side (left, seen driving in).</summary>
    public bool CarriesBikesAway =>
        Strips.Any(x => x.Kind == StripKind.BikeLane && x.Way == StripWay.Out)
        || Strips.Take(Math.Max(0, FirstCarriageway)).Any(x => x.Kind == StripKind.Path);

    /// <summary>The car lanes arriving, left to right, as the test region names them: "L|T|R" (#386).</summary>
    public string CarLanesIn() =>
        string.Join("|", CarLanes(StripWay.In).Select(l => MovesName(l.Strip.Moves)));

    public static string MovesName(SignalMoves m) =>
        (m.HasFlag(SignalMoves.Left) ? "L" : "") + (m.HasFlag(SignalMoves.Through) ? "T" : "") + (m.HasFlag(SignalMoves.Right) ? "R" : "");

    /// <summary>Strips left to right with widths, e.g. <c>walk 2.0 | bike> 1.5 | car> 3.0 | car< 3.0 L</c> (> leaves, &lt; arrives).</summary>
    public override string ToString()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        for (int k = 0; k < Strips.Count; k++)
        {
            var s = Strips[k];
            if (k > 0) sb.Append(k == FirstCarriageway || k == LastCarriageway + 1 ? " ‖ " : " | ");
            sb.Append(Name(s.Kind)).Append(s.Way == StripWay.Out ? ">" : s.Way == StripWay.In ? "<" : "")
              .Append(' ').Append(s.Width.ToString("F2", inv));
            if (s.Moves != SignalMoves.None) sb.Append(' ').Append(MovesName(s.Moves));
            if (s.SharedWithBikes) sb.Append(" +bikes");
            if (s.Stop != BikeStop.None) sb.Append(s.Stop == BikeStop.Box ? " [box]" : " [adv]");
        }
        sb.Append(inv, $"  (carriageway {CarriagewayLeft:F2} .. {CarriagewayRight:F2})");
        return sb.ToString();
    }

    public static string Name(StripKind k) => k switch
    {
        StripKind.Sidewalk => "walk",
        StripKind.Buffer => "buffer",
        StripKind.Path => "path",
        StripKind.Verge => "verge",
        StripKind.BikeLane => "bike",
        StripKind.CarLane => "car",
        StripKind.Hatch => "hatch",
        StripKind.Island => "island",
        _ => "?",
    };

    /// <summary>
    /// A street's normal section from its tile attributes, seen driving in toward the junction at its last point
    /// (<paramref name="junctionAtEnd"/>) or its first: the side bands as the street planner laid them (#119, #120), the
    /// painted bike lanes, and the car lanes as the paint and traffic share them out (<see cref="RoadCrossSection.TwoWayLaneWidth"/>,
    /// #700); the carriageway centred on the axis. Lane ids count from 1 left to right; moves are unknown (none).
    /// </summary>
    public static Section Normal(RoadAttributes at, float width, bool junctionAtEnd)
    {
        // driving in along the drawing: its left is the drawing's left; against it, mirrored
        var leftSide = junctionAtEnd ? at.Left : at.Right;
        var rightSide = junctionAtEnd ? at.Right : at.Left;
        int fwd = at.LanesForward, bwd = at.LanesBackward;
        int lanesIn, lanesOut;
        if (at.OneWay != 0)
        {
            int n = Math.Max(1, Math.Max(fwd, bwd));
            bool arriving = (at.OneWay > 0) == junctionAtEnd;
            (lanesIn, lanesOut) = arriving ? (n, 0) : (0, n);
        }
        else
            (lanesIn, lanesOut) = junctionAtEnd ? (Math.Max(1, fwd), Math.Max(1, bwd)) : (Math.Max(1, bwd), Math.Max(1, fwd));
        float bikeLeft = leftSide.HasLane ? leftSide.BikeDm / 10f : 0, bikeRight = rightSide.HasLane ? rightSide.BikeDm / 10f : 0;
        double lane = (width - bikeLeft - bikeRight) / Math.Max(1, lanesIn + lanesOut);

        var strips = new List<Strip>();
        int id = 1;
        Bands(strips, leftSide, outward: false, ref id);
        if (bikeLeft > 0) strips.Add(new Strip(StripKind.BikeLane, bikeLeft, StripWay.Out, Id: id++));
        for (int k = 0; k < lanesOut; k++) strips.Add(new Strip(StripKind.CarLane, lane, StripWay.Out, Id: id++));
        for (int k = 0; k < lanesIn; k++) strips.Add(new Strip(StripKind.CarLane, lane, StripWay.In, Id: id++));
        if (bikeRight > 0) strips.Add(new Strip(StripKind.BikeLane, bikeRight, StripWay.In, Id: id++));
        Bands(strips, rightSide, outward: true, ref id);
        return new Section(strips, -width * 0.5) { KerbLeftCm = leftSide.KerbCm, KerbRightCm = rightSide.KerbCm };
    }

    /// <summary>A side's bands (#119, #120), outward from the kerb: verge, path, buffer, sidewalk; added left to right.</summary>
    private static void Bands(List<Strip> strips, RoadSide side, bool outward, ref int id)
    {
        var bands = new List<Strip>();
        if (side.HasTrack && side.KerbCm > 0)
        {
            if (side.VergeDm > 0) bands.Add(new Strip(StripKind.Verge, side.VergeDm / 10.0));
            bands.Add(new Strip(StripKind.Path, side.BikeDm / 10.0));
            if (side.BufferDm > 0) bands.Add(new Strip(StripKind.Buffer, side.BufferDm / 10.0));
        }
        if (side.SidewalkDm > 0) bands.Add(new Strip(StripKind.Sidewalk, side.SidewalkDm / 10.0));
        if (!outward) bands.Reverse();
        foreach (var b in bands) strips.Add(b with { Id = id++ });
    }
}
