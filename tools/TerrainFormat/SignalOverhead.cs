namespace UnitSport.Terrain.Format;

/// <summary>What carries an approach's overhead heads (#1037, <c>docs/plans/overhead-traffic-lights.md</c>).</summary>
public enum OverheadKind : byte
{
    /// <summary>An L mast arm (Winkelmast) from a post beside the right kerb's main pole, reaching left over the lanes.</summary>
    Arm = 0,
    /// <summary>The same from a post on the refuge (the left repeater's island), reaching right over the lanes.</summary>
    RefugeArm = 1,
    /// <summary>A portal (Signalbrücke): a beam on two posts, beside the right kerb and the left kerb (or the refuge).</summary>
    Portal = 2,
    /// <summary>A tram street's span wire between two anchor poles at the kerbs, the heads hung from it.</summary>
    Span = 3,
}

/// <summary>A 300 mm head over the lanes (#1037): metres along its structure from foot A, and the moves of the lanes under it.</summary>
public readonly record struct OverheadHead(float Along, SignalMoves Moves);

/// <summary>
/// An approach's overhead structure (#1037, section <c>OVHD</c>), tile-local like its signal: foot A (the post: beside the
/// main pole, or on the refuge) and the structure's far end (an arm's tip, at A's ground; a portal's or span's second
/// post's foot), the plates' lower edge (y, the road's surface plus SSV Art. 71's 4.50-5.50 m), the heading its heads face
/// (toward the arriving drivers, as <see cref="SignalPole.CarHeading"/>), the plan arm it serves, and its heads.
/// </summary>
public sealed class SignalOverhead
{
    public OverheadKind Kind { get; init; }
    public byte Arm { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float EndX { get; init; }
    public float EndY { get; init; }
    public float EndZ { get; init; }
    public float Bottom { get; init; }
    public float Heading { get; init; }
    public List<OverheadHead> Heads { get; init; } = new();

    /// <summary>Horizontal length from A to the far end.</summary>
    public float Length => MathF.Sqrt((EndX - X) * (EndX - X) + (EndZ - Z) * (EndZ - Z));

    public const byte SectionVersion = 1;

    /// <summary>
    /// Writes every signal's structures: <c>version u8</c>; <c>count varint</c>, per structure <c>signal varint, kind u8, arm u8,
    /// x y z, end x y z, bottom, heading f32, heads u8</c>, per head <c>along f32, moves u8</c>.
    /// </summary>
    public static void Write(BinaryWriter w, IReadOnlyList<RoadSignal> signals)
    {
        w.Write(SectionVersion);
        w.Write7BitEncodedInt(signals.Sum(s => s.Overheads.Count));
        for (int i = 0; i < signals.Count; i++)
            foreach (var o in signals[i].Overheads)
            {
                w.Write7BitEncodedInt(i);
                w.Write((byte)o.Kind);
                w.Write(o.Arm);
                foreach (float f in (ReadOnlySpan<float>)[o.X, o.Y, o.Z, o.EndX, o.EndY, o.EndZ, o.Bottom, o.Heading]) w.Write(f);
                w.Write(checked((byte)o.Heads.Count));
                foreach (var h in o.Heads) { w.Write(h.Along); w.Write((byte)h.Moves); }
            }
    }

    /// <summary>Reads the structures onto <paramref name="signals"/> (after <c>SGNL</c>); false for another version (the caller skips it).</summary>
    public static bool Read(BinaryReader r, IReadOnlyList<RoadSignal> signals)
    {
        if (r.ReadByte() != SectionVersion) return false;
        int n = r.Read7BitEncodedInt();
        for (int k = 0; k < n; k++)
        {
            int i = r.Read7BitEncodedInt();
            var kind = (OverheadKind)r.ReadByte();
            byte arm = r.ReadByte();
            float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle(), ex = r.ReadSingle(), ey = r.ReadSingle(), ez = r.ReadSingle();
            float bottom = r.ReadSingle(), heading = r.ReadSingle();
            var heads = new List<OverheadHead>();
            for (int h = r.ReadByte(); h > 0; h--) heads.Add(new OverheadHead(r.ReadSingle(), (SignalMoves)r.ReadByte()));
            if (i < signals.Count)
                signals[i].Overheads.Add(new SignalOverhead
                {
                    Kind = kind, Arm = arm, X = x, Y = y, Z = z, EndX = ex, EndY = ey, EndZ = ez, Bottom = bottom, Heading = heading, Heads = heads,
                });
        }
        return true;
    }
}
