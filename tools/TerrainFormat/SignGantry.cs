namespace UnitSport.Terrain.Format;

/// <summary>
/// A direction sign's colour (SSV Art. 51/52, #1110): white on green to a motorway or expressway (4.31), white on blue to a
/// destination reached mainly by main roads (4.32), black on white by minor roads (4.33); a lane sign over the carriageway
/// (Einspurtafel 4.41 main roads, 4.42 minor) takes its field's colour by the same rule (Art. 53).
/// </summary>
public enum SignColour : byte { Blue = 0, Green = 1, White = 2 }

/// <summary>A lane under a gantry's sign (#1110): metres along the gantry from foot A, its moves (the arrow on the black band).</summary>
public readonly record struct GantryLane(float Along, SignalMoves Moves);

/// <summary>
/// One sign on a gantry (#1110): its middle along the gantry from foot A and its width (m), colour, up to three lines of
/// destinations (a leading <see cref="SignGantry.MotorwayMark"/> on a line draws the motorway symbol before it), and the lanes
/// under it, each with its arrow on the black band below the sign (and, on a lit gantry, a horizontal head beside the arrow).
/// </summary>
public sealed class GantrySign
{
    public float Along { get; init; }
    public float Width { get; init; }
    public SignColour Colour { get; init; }
    public List<string> Lines { get; init; } = new();
    public List<GantryLane> Lanes { get; init; } = new();
}

/// <summary>
/// A gantry over an approach carrying lane signs (Einspurtafeln, SSV Art. 53) (#1110, section <c>GNTY</c>,
/// <c>docs/notes/tools/direction-signs.md</c>), tile-local like its signal: two posts (foot A beside the right kerb, foot B
/// beside the left kerb or on the refuge), the signs' lower edge (the black band's: SSV Art. 71's 4.50-5.50 m over the road),
/// the heading its signs face (toward the arriving drivers, as <see cref="SignalPole.CarHeading"/>), the plan arm it serves.
/// <see cref="Lit"/>: a horizontal 300 mm head in the band beside each lane's arrow (SSV Art. 70: red left, green right), as
/// Geneva builds them; the approach then has no other overhead heads.
/// </summary>
public sealed class SignGantry
{
    public byte Arm { get; init; }
    public bool Lit { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float EndX { get; init; }
    public float EndY { get; init; }
    public float EndZ { get; init; }
    public float Bottom { get; init; }
    public float Heading { get; init; }
    public List<GantrySign> Signs { get; init; } = new();

    /// <summary>A line starting with this character shows the motorway symbol (SSV 4.01) before its text.</summary>
    public const char MotorwayMark = '▲';

    /// <summary>Horizontal length from A to B.</summary>
    public float Length => MathF.Sqrt((EndX - X) * (EndX - X) + (EndZ - Z) * (EndZ - Z));

    public const byte SectionVersion = 1;

    /// <summary>
    /// Writes every signal's gantries: <c>version u8</c>; <c>count varint</c>, per gantry <c>signal varint, arm u8, lit u8,
    /// x y z, end x y z, bottom, heading f32, signs u8</c>, per sign <c>along, width f32, colour u8, lines u8 (each a varint byte count and its UTF-8),
    /// lanes u8</c>, per lane <c>along f32, moves u8</c>.
    /// </summary>
    public static void Write(BinaryWriter w, IReadOnlyList<RoadSignal> signals)
    {
        w.Write(SectionVersion);
        w.Write7BitEncodedInt(signals.Sum(s => s.Gantries.Count));
        for (int i = 0; i < signals.Count; i++)
            foreach (var g in signals[i].Gantries)
            {
                w.Write7BitEncodedInt(i);
                w.Write(g.Arm);
                w.Write(g.Lit ? (byte)1 : (byte)0);
                foreach (float f in (ReadOnlySpan<float>)[g.X, g.Y, g.Z, g.EndX, g.EndY, g.EndZ, g.Bottom, g.Heading]) w.Write(f);
                w.Write(checked((byte)g.Signs.Count));
                foreach (var s in g.Signs)
                {
                    w.Write(s.Along);
                    w.Write(s.Width);
                    w.Write((byte)s.Colour);
                    w.Write(checked((byte)s.Lines.Count));
                    // UTF-8 whatever the writer's encoding (the road file's is ASCII: "Genève" came back "Gen?ve")
                    foreach (var line in s.Lines)
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                        w.Write7BitEncodedInt(bytes.Length);
                        w.Write(bytes);
                    }
                    w.Write(checked((byte)s.Lanes.Count));
                    foreach (var l in s.Lanes) { w.Write(l.Along); w.Write((byte)l.Moves); }
                }
            }
    }

    /// <summary>Reads the gantries onto <paramref name="signals"/> (after <c>SGNL</c>); false for another version (the caller skips it).</summary>
    public static bool Read(BinaryReader r, IReadOnlyList<RoadSignal> signals)
    {
        if (r.ReadByte() != SectionVersion) return false;
        int n = r.Read7BitEncodedInt();
        for (int k = 0; k < n; k++)
        {
            int i = r.Read7BitEncodedInt();
            byte arm = r.ReadByte();
            bool lit = r.ReadByte() != 0;
            float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle(), ex = r.ReadSingle(), ey = r.ReadSingle(), ez = r.ReadSingle();
            float bottom = r.ReadSingle(), heading = r.ReadSingle();
            var signs = new List<GantrySign>();
            for (int s = r.ReadByte(); s > 0; s--)
            {
                float along = r.ReadSingle(), width = r.ReadSingle();
                var colour = (SignColour)r.ReadByte();
                var lines = new List<string>();
                for (int l = r.ReadByte(); l > 0; l--) lines.Add(System.Text.Encoding.UTF8.GetString(r.ReadBytes(r.Read7BitEncodedInt())));
                var lanes = new List<GantryLane>();
                for (int l = r.ReadByte(); l > 0; l--) lanes.Add(new GantryLane(r.ReadSingle(), (SignalMoves)r.ReadByte()));
                signs.Add(new GantrySign { Along = along, Width = width, Colour = colour, Lines = lines, Lanes = lanes });
            }
            if (i < signals.Count)
                signals[i].Gantries.Add(new SignGantry
                {
                    Arm = arm, Lit = lit, X = x, Y = y, Z = z, EndX = ex, EndY = ey, EndZ = ez, Bottom = bottom, Heading = heading, Signs = signs,
                });
        }
        return true;
    }
}
