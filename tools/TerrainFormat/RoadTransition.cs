namespace UnitSport.Terrain.Format;

/// <summary>What a strip of a street's cross-section is (#818). Part names: <c>docs/notes/tools/intersection-parts.md</c>.</summary>
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

[Flags]
public enum TransitionStripFlags : byte
{
    None = 0,
    /// <summary>A car lane cyclists ride in too (a right pocket beside a bike lane between, #818).</summary>
    SharedWithBikes = 1,
}

/// <summary>One strip of a <see cref="RoadTransition"/>: what it is and its width (m) at each of the transition's stations.</summary>
public sealed class RoadTransitionStrip
{
    public StripKind Kind { get; init; }
    /// <summary>Stable along the arm (the same lane at both ends of a transition), as the junction planner numbers it.</summary>
    public int Id { get; init; }
    /// <summary>+1 its traffic goes along the segment's drawing, -1 against it, 0 none (a hatch, a band).</summary>
    public sbyte Travel { get; init; }
    /// <summary>The moves an arriving lane makes at the junction it leads to.</summary>
    public SignalMoves Moves { get; init; }
    public TransitionStripFlags Flags { get; init; }
    public required float[] Widths { get; init; }

    public bool InCarriageway => Kind is StripKind.BikeLane or StripKind.CarLane or StripKind.Hatch or StripKind.Island;
}

/// <summary>
/// A transition (#818, <c>TRNS</c>): a stretch of street going from one cross-section to another (a pocket opening, an exit's
/// hatch closing, a lane narrowing back), over one whole segment of its tile, which carries <see cref="RoadAttrFlags.InTransition"/>.
/// Stations are plan metres along the segment from its first point (the first 0, the last its length); between them every
/// strip's width is linear. The strips lie left to right as seen along the drawing; the centre line is the anchor: the strips
/// before <see cref="Anchor"/> stack out from it to the left, the rest to the right. Offsets are metres right of the drawing
/// (as <see cref="RoadPaint.Offset"/>). The game builds the carriageway, kerbs and side bands from it (heights from the
/// segment's points, kerb heights and the path's kind from its sides); lines along it are <see cref="RoadPaint"/>s with
/// <see cref="RoadPaint.Transition"/> set.
/// </summary>
public sealed class RoadTransition
{
    public required RoadSegment Segment { get; init; }
    public required float[] Stations { get; init; }
    /// <summary>The centre line's offset at each station.</summary>
    public required float[] Centre { get; init; }
    public required int Anchor { get; init; }
    public required List<RoadTransitionStrip> Strips { get; init; }

    /// <summary>A value rounded to the centimetre the file keeps.</summary>
    public static float Cm(double v) => (float)(Math.Round(v * 100) / 100);

    private float Lerp(float[] values, double s)
    {
        var at = Stations;
        if (s <= at[0]) return values[0];
        for (int k = 1; k < at.Length; k++)
            if (s <= at[k])
                return at[k] - at[k - 1] < 1e-6f ? values[k] : (float)(values[k - 1] + (values[k] - values[k - 1]) * (s - at[k - 1]) / (at[k] - at[k - 1]));
        return values[^1];
    }

    public float WidthAt(int strip, double s) => Lerp(Strips[strip].Widths, s);

    public float CentreAt(double s) => Lerp(Centre, s);

    /// <summary>Strip <paramref name="strip"/>'s left and right offsets at station <paramref name="s"/>.</summary>
    public (float From, float To) SpanAt(int strip, double s)
    {
        float at = CentreAt(s);
        if (strip >= Anchor)
        {
            for (int k = Anchor; k < strip; k++) at += WidthAt(k, s);
            return (at, at + WidthAt(strip, s));
        }
        for (int k = Anchor - 1; k > strip; k--) at -= WidthAt(k, s);
        return (at - WidthAt(strip, s), at);
    }

    /// <summary>The boundary on the left of strip <paramref name="strip"/> (the strips' count: the right of the last).</summary>
    public float BoundaryAt(int strip, double s) => strip < Strips.Count ? SpanAt(strip, s).From : SpanAt(Strips.Count - 1, s).To;

    /// <summary>The indices of the first and last carriageway strip (-1 without).</summary>
    public (int First, int Last) Carriageway
    {
        get
        {
            int first = -1, last = -1;
            for (int k = 0; k < Strips.Count; k++)
                if (Strips[k].InCarriageway) { if (first < 0) first = k; last = k; }
            return (first, last);
        }
    }

    /// <summary>The carriageway's left and right edges at station <paramref name="s"/>.</summary>
    public (float Left, float Right) CarriagewayAt(double s)
    {
        var (first, last) = Carriageway;
        if (first < 0) { float c = CentreAt(s); return (c, c); }
        return (SpanAt(first, s).From, SpanAt(last, s).To);
    }

    /// <summary>
    /// One side's bands at station <paramref name="s"/>, outward from the kerb (verge, path, buffer, sidewalk as the strips lie):
    /// every band strip of the side, zero wide where it is not, so the profile keeps one shape along the transition.
    /// </summary>
    public List<(StripKind Kind, float Width)> BandsAt(bool right, double s)
    {
        var (first, last) = Carriageway;
        var bands = new List<(StripKind, float)>();
        if (first < 0) return bands;
        if (right) for (int k = last + 1; k < Strips.Count; k++) bands.Add((Strips[k].Kind, WidthAt(k, s)));
        else for (int k = first - 1; k >= 0; k--) bands.Add((Strips[k].Kind, WidthAt(k, s)));
        return bands;
    }

    // ---- TRNS: version u8, then per transition its segment, stations, centre, anchor, strips and lines (road-format-v3)

    public const byte SectionVersion = 1;

    /// <summary>
    /// Writes the transitions and their lines (<paramref name="paint"/> entries whose <see cref="RoadPaint.Transition"/> is one of
    /// them, in order): <c>version u8, count varint</c>; per transition <c>segment varint, stationCount varint, stations varint cm
    /// deltas, centre zigzag cm per station, anchor varint, stripCount varint</c>, per strip <c>kind u8, id varint, travel i8,
    /// moves u8, flags u8, widths varint cm per station</c>; <c>lineCount varint</c>, per line <c>type u8, rgba u32, width, dash,
    /// gap f32, boundary varint, offset zigzag mm, from varint cm, to varint cm (0 = to the end)</c>.
    /// </summary>
    public static void Write(BinaryWriter w, IReadOnlyList<RoadTransition> transitions, IReadOnlyList<RoadSegment> segments, IReadOnlyList<RoadPaint> paint)
    {
        var index = new Dictionary<RoadSegment, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < segments.Count; i++) index[segments[i]] = i;
        w.Write(SectionVersion);
        w.Write7BitEncodedInt(transitions.Count);
        foreach (var t in transitions)
        {
            w.Write7BitEncodedInt(index[t.Segment]);
            w.Write7BitEncodedInt(t.Stations.Length);
            int prev = 0;
            foreach (float s in t.Stations)
            {
                int cm = (int)Math.Round(s * 100.0);
                w.Write7BitEncodedInt(cm - prev);
                prev = cm;
            }
            foreach (float c in t.Centre) Zigzag(w, (int)Math.Round(c * 100.0));
            w.Write7BitEncodedInt(t.Anchor);
            w.Write7BitEncodedInt(t.Strips.Count);
            foreach (var x in t.Strips)
            {
                w.Write((byte)x.Kind);
                w.Write7BitEncodedInt(x.Id);
                w.Write(x.Travel);
                w.Write((byte)x.Moves);
                w.Write((byte)x.Flags);
                foreach (float width in x.Widths) w.Write7BitEncodedInt((int)Math.Round(width * 100.0));
            }
            var lines = paint.Where(p => ReferenceEquals(p.Transition, t)).ToList();
            w.Write7BitEncodedInt(lines.Count);
            foreach (var p in lines)
            {
                w.Write((byte)p.Type);
                w.Write(p.Rgba);
                w.Write(p.Width);
                w.Write(p.Dash);
                w.Write(p.Gap);
                w.Write7BitEncodedInt(p.Boundary);
                Zigzag(w, (int)Math.Round(p.Offset * 1000.0));
                w.Write7BitEncodedInt((int)Math.Round(p.From * 100.0));
                w.Write7BitEncodedInt(float.IsPositiveInfinity(p.To) ? 0 : Math.Max(1, (int)Math.Round(p.To * 100.0)));
            }
        }
    }

    /// <summary>A transition as read, its lines still to be rebuilt once the segments are read.</summary>
    public sealed record Raw(int Segment, float[] Stations, float[] Centre, int Anchor, List<RoadTransitionStrip> Strips,
        List<(PaintType Type, uint Rgba, float Width, float Dash, float Gap, int Boundary, float Offset, float From, float To)> Lines);

    /// <summary>Reads TRNS; null for a section version this build does not know (skipped whole).</summary>
    public static List<Raw>? Read(BinaryReader r)
    {
        if (r.ReadByte() != SectionVersion) return null;
        int count = r.Read7BitEncodedInt();
        var result = new List<Raw>(count);
        for (int i = 0; i < count; i++)
        {
            int segment = r.Read7BitEncodedInt();
            int n = r.Read7BitEncodedInt();
            var stations = new float[n];
            int cm = 0;
            for (int k = 0; k < n; k++) { cm += r.Read7BitEncodedInt(); stations[k] = cm / 100f; }
            var centre = new float[n];
            for (int k = 0; k < n; k++) centre[k] = ReadZigzag(r) / 100f;
            int anchor = r.Read7BitEncodedInt();
            int stripCount = r.Read7BitEncodedInt();
            var strips = new List<RoadTransitionStrip>(stripCount);
            for (int k = 0; k < stripCount; k++)
            {
                var kind = (StripKind)r.ReadByte();
                int id = r.Read7BitEncodedInt();
                sbyte travel = r.ReadSByte();
                var moves = (SignalMoves)r.ReadByte();
                var flags = (TransitionStripFlags)r.ReadByte();
                var widths = new float[n];
                for (int j = 0; j < n; j++) widths[j] = r.Read7BitEncodedInt() / 100f;
                strips.Add(new RoadTransitionStrip { Kind = kind, Id = id, Travel = travel, Moves = moves, Flags = flags, Widths = widths });
            }
            int lineCount = r.Read7BitEncodedInt();
            var lines = new List<(PaintType, uint, float, float, float, int, float, float, float)>(lineCount);
            for (int k = 0; k < lineCount; k++)
            {
                var type = (PaintType)r.ReadByte();
                uint rgba = r.ReadUInt32();
                float width = r.ReadSingle(), dash = r.ReadSingle(), gap = r.ReadSingle();
                int boundary = r.Read7BitEncodedInt();
                float offset = ReadZigzag(r) / 1000f;
                float from = r.Read7BitEncodedInt() / 100f;
                int to = r.Read7BitEncodedInt();
                lines.Add((type, rgba, width, dash, gap, boundary, offset, from, to == 0 ? float.PositiveInfinity : to / 100f));
            }
            result.Add(new Raw(segment, stations, centre, anchor, strips, lines));
        }
        return result;
    }

    private static void Zigzag(BinaryWriter w, int v) => w.Write7BitEncodedInt((v << 1) ^ (v >> 31));

    private static int ReadZigzag(BinaryReader r)
    {
        int v = r.Read7BitEncodedInt();
        return (int)((uint)v >> 1) ^ -(v & 1);
    }
}
