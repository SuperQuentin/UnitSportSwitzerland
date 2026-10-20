namespace UnitSport.Terrain.Format;

/// <summary>Who moves along a conflict pair's line (#979).</summary>
public enum ConflictMover : byte { Car = 0, Bike = 1, Walker = 2 }

/// <summary>
/// The pair of movements that set a signal group's green start (#979, section <c>CPAR</c>): the last mover of the ending group
/// (it crosses its stop line as its amber ends and clears at <see cref="ClearSpeed"/>) and the first of the starting one (it
/// crosses its line at green and enters at <see cref="EnterSpeed"/>); the first reaches the conflict
/// <see cref="Enter"/> m along its line just as the last, <see cref="Clear"/> m along its own, has cleared it by its length.
/// Lines from their stop lines, tile-local x, y, z (the signal's tile). For the debug view's movers; the plan holds the timing.
/// </summary>
public sealed class RoadConflictPair
{
    public byte Ending { get; init; }
    public byte Starting { get; init; }
    public ConflictMover EndingMover { get; init; }
    public ConflictMover StartingMover { get; init; }
    /// <summary>Along the ending line to past the conflict area, and along the starting line to it (m).</summary>
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

    /// <summary>1 is the first; a reader skips a section of a version it does not know.</summary>
    public const byte SectionVersion = 1;

    /// <summary>
    /// Writes every signal's pairs: <c>version u8, count varint</c>; per pair <c>signal varint, ending u8, starting u8, movers
    /// u8 (ending | starting &lt;&lt; 4), clear, enter, clearSpeed, enterSpeed, length, amber, time f32, two lines (count
    /// varint, x y z f32 each)</c>.
    /// </summary>
    public static void Write(BinaryWriter w, IReadOnlyList<RoadSignal> signals)
    {
        var all = signals.SelectMany((s, i) => s.ConflictPairs.Select(p => (i, p))).ToList();
        w.Write(SectionVersion);
        w.Write7BitEncodedInt(all.Count);
        foreach (var (i, p) in all)
        {
            w.Write7BitEncodedInt(i);
            w.Write(p.Ending);
            w.Write(p.Starting);
            w.Write((byte)((byte)p.EndingMover | (byte)p.StartingMover << 4));
            foreach (float f in (ReadOnlySpan<float>)[p.Clear, p.Enter, p.ClearSpeed, p.EnterSpeed, p.Length, p.Amber, p.Time]) w.Write(f);
            WriteLine(w, p.EndingLine);
            WriteLine(w, p.StartingLine);
        }
    }

    /// <summary>Reads the pairs onto <paramref name="signals"/>; false for a section of a newer version (the caller skips it).</summary>
    public static bool Read(BinaryReader r, IReadOnlyList<RoadSignal> signals)
    {
        if (r.ReadByte() is var version && version is < 1 or > SectionVersion) return false;
        int n = r.Read7BitEncodedInt();
        for (int k = 0; k < n; k++)
        {
            int i = r.Read7BitEncodedInt();
            byte ending = r.ReadByte(), starting = r.ReadByte(), movers = r.ReadByte();
            var f = new float[7];
            for (int j = 0; j < 7; j++) f[j] = r.ReadSingle();
            var pair = new RoadConflictPair
            {
                Ending = ending, Starting = starting, EndingMover = (ConflictMover)(movers & 15), StartingMover = (ConflictMover)(movers >> 4),
                Clear = f[0], Enter = f[1], ClearSpeed = f[2], EnterSpeed = f[3], Length = f[4], Amber = f[5], Time = f[6],
                EndingLine = ReadLine(r), StartingLine = ReadLine(r),
            };
            if (i < signals.Count) signals[i].ConflictPairs.Add(pair);
        }
        return true;
    }

    private static void WriteLine(BinaryWriter w, float[] line)
    {
        w.Write7BitEncodedInt(line.Length / 3);
        foreach (float v in line) w.Write(v);
    }

    private static float[] ReadLine(BinaryReader r)
    {
        var line = new float[r.Read7BitEncodedInt() * 3];
        for (int j = 0; j < line.Length; j++) line[j] = r.ReadSingle();
        return line;
    }
}
