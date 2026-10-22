namespace UnitSport.Terrain.Format;

/// <summary>Who moves along a conflict line (#979). <c>Tram</c>: room for #1057's tram movements (written by no stage yet, #1060).</summary>
public enum ConflictMover : byte { Car = 0, Bike = 1, Walker = 2, Tram = 3 }

/// <summary>
/// A movement of a lit junction as its timing took it (#979, section <c>CPAR</c>): its line from its stop line (tile-local x, y, z,
/// the signal's tile), its group, who moves on it, its clearing and entering speeds (m/s), its amber (s, none for walkers: the
/// last steps off as the green ends) and its movers' length (m). For the debug view's trails and movers; the plan holds the timing.
/// </summary>
public sealed class RoadConflictLine
{
    public byte Group { get; init; }
    public ConflictMover Mover { get; init; }
    public float ClearSpeed { get; init; }
    public float EnterSpeed { get; init; }
    public float Amber { get; init; }
    public float Length { get; init; }
    public required float[] Points { get; init; }
}

/// <summary>
/// The pair of movements that set a signal group's green start or end (#979): the last mover of the ending line (it crosses its stop
/// line as its amber ends and clears at <see cref="ClearSpeed"/>) and the first of the starting one (it crosses its line at green
/// and enters at <see cref="EnterSpeed"/>); the first reaches the point <see cref="Enter"/> m along its line just as the last,
/// <see cref="Clear"/> m along its own, has cleared it by its length. Its lines are two of the signal's
/// <see cref="RoadSignal.ConflictLines"/> (the same arrays); the rest is theirs.
/// </summary>
public sealed class RoadConflictPair
{
    public byte Ending { get; init; }
    public byte Starting { get; init; }
    public ConflictMover EndingMover { get; init; }
    public ConflictMover StartingMover { get; init; }
    /// <summary>Along the ending line to past the binding point, and along the starting line to it (m).</summary>
    public float Clear { get; init; }
    public float Enter { get; init; }
    public float ClearSpeed { get; init; }
    public float EnterSpeed { get; init; }
    /// <summary>The ending mover's length (m) and the ending group's amber (s), as the time was worked out.</summary>
    public float Length { get; init; }
    public float Amber { get; init; }
    /// <summary>The conflict time (s), from the end of the ending group's green to the start of the starting group's.</summary>
    public float Time { get; init; }
    public required float[] EndingLine { get; init; }
    public required float[] StartingLine { get; init; }

    /// <summary>2 writes every line once and the pairs by index (1, the pairs with their own lines, is skipped).</summary>
    public const byte SectionVersion = 2;

    /// <summary>
    /// Writes every signal's lines and pairs: <c>version u8</c>; <c>count varint</c>, per line <c>signal varint, group u8, mover
    /// u8, clearSpeed, enterSpeed, amber, length f32, points (count varint, x y z f32 each)</c>; <c>count varint</c>, per pair
    /// <c>signal varint, ending line, starting line varint</c> (indices in the signal's lines) <c>clear, enter, time f32</c>.
    /// </summary>
    public static void Write(BinaryWriter w, IReadOnlyList<RoadSignal> signals)
    {
        w.Write(SectionVersion);
        w.Write7BitEncodedInt(signals.Sum(s => s.ConflictLines.Count));
        for (int i = 0; i < signals.Count; i++)
            foreach (var l in signals[i].ConflictLines)
            {
                w.Write7BitEncodedInt(i);
                w.Write(l.Group);
                w.Write((byte)l.Mover);
                foreach (float f in (ReadOnlySpan<float>)[l.ClearSpeed, l.EnterSpeed, l.Amber, l.Length]) w.Write(f);
                w.Write7BitEncodedInt(l.Points.Length / 3);
                foreach (float v in l.Points) w.Write(v);
            }
        w.Write7BitEncodedInt(signals.Sum(s => s.ConflictPairs.Count));
        for (int i = 0; i < signals.Count; i++)
        {
            var lines = signals[i].ConflictLines;
            foreach (var p in signals[i].ConflictPairs)
            {
                int ending = lines.FindIndex(l => ReferenceEquals(l.Points, p.EndingLine));
                int starting = lines.FindIndex(l => ReferenceEquals(l.Points, p.StartingLine));
                if (ending < 0 || starting < 0) throw new InvalidOperationException("a conflict pair's lines are not its signal's");
                w.Write7BitEncodedInt(i);
                w.Write7BitEncodedInt(ending);
                w.Write7BitEncodedInt(starting);
                w.Write(p.Clear);
                w.Write(p.Enter);
                w.Write(p.Time);
            }
        }
    }

    /// <summary>Reads the lines and pairs onto <paramref name="signals"/>; false for a section of another version (the caller skips it).</summary>
    public static bool Read(BinaryReader r, IReadOnlyList<RoadSignal> signals)
    {
        if (r.ReadByte() != SectionVersion) return false;
        var lines = signals.Select(_ => new List<RoadConflictLine>()).ToList();
        int n = r.Read7BitEncodedInt();
        for (int k = 0; k < n; k++)
        {
            int i = r.Read7BitEncodedInt();
            byte group = r.ReadByte(), mover = r.ReadByte();
            float clear = r.ReadSingle(), enter = r.ReadSingle(), amber = r.ReadSingle(), length = r.ReadSingle();
            var points = new float[r.Read7BitEncodedInt() * 3];
            for (int j = 0; j < points.Length; j++) points[j] = r.ReadSingle();
            var line = new RoadConflictLine
            {
                Group = group, Mover = (ConflictMover)mover, ClearSpeed = clear, EnterSpeed = enter, Amber = amber, Length = length, Points = points,
            };
            if (i < signals.Count) { signals[i].ConflictLines.Add(line); lines[i].Add(line); }
        }
        n = r.Read7BitEncodedInt();
        for (int k = 0; k < n; k++)
        {
            int i = r.Read7BitEncodedInt(), ending = r.Read7BitEncodedInt(), starting = r.Read7BitEncodedInt();
            float clear = r.ReadSingle(), enter = r.ReadSingle(), time = r.ReadSingle();
            if (i >= signals.Count || ending >= lines[i].Count || starting >= lines[i].Count) continue;
            var (a, b) = (lines[i][ending], lines[i][starting]);
            signals[i].ConflictPairs.Add(new RoadConflictPair
            {
                Ending = a.Group, Starting = b.Group, EndingMover = a.Mover, StartingMover = b.Mover, Clear = clear, Enter = enter,
                ClearSpeed = a.ClearSpeed, EnterSpeed = b.EnterSpeed, Length = a.Length, Amber = a.Amber, Time = time,
                EndingLine = a.Points, StartingLine = b.Points,
            });
        }
        return true;
    }
}
