namespace UnitSport.Terrain.Format;

/// <summary>Whose overhead line (#1052): a tram's single contact wire, or a main line's catenary (a messenger above the contact wire).</summary>
public enum CatenaryLine : byte { Tram = 0, Main = 1 }

/// <summary>What holds an overhead line up at one place (#1052).</summary>
public enum CatenarySupportKind : byte
{
    /// <summary>A mast beside the track with an arm out over it.</summary>
    Cantilever = 0,
    /// <summary>A span wire across a street between two anchors (a facade or a mast each), the contact wires hung from it.</summary>
    Span = 1,
    /// <summary>A mast between a double track (A, its foot) with an arm out to each track (<see cref="CatenarySupport.Over"/>).</summary>
    CentreMast = 2,
}

/// <summary>
/// One support of an overhead line (#1052, section <c>CTNY</c>), tile-local: anchor A (a cantilever's mast foot; a span's first
/// end) and B (the cantilever's arm tip, over its track; the span's other end), whether each end is a facade (a wall plate) or a
/// mast foot, the contact wire's height there, and where the wires pass (x, z per track).
/// </summary>
public sealed class CatenarySupport
{
    public CatenarySupportKind Kind { get; init; }
    public CatenaryLine Line { get; init; }
    public float AX { get; init; }
    public float AY { get; init; }
    public float AZ { get; init; }
    public bool AWall { get; init; }
    public float BX { get; init; }
    public float BY { get; init; }
    public float BZ { get; init; }
    public bool BWall { get; init; }
    /// <summary>The contact wire's height (tile-local y) at this support.</summary>
    public float WireY { get; init; }
    /// <summary>Where the contact wires pass, x and z per track.</summary>
    public required float[] Over { get; set; }
}

/// <summary>A contact wire over one track (#1052), tile-local x y z triples; a main line's messenger and droppers are drawn from it.</summary>
public sealed class CatenaryWire
{
    public CatenaryLine Line { get; init; }
    public required float[] Points { get; init; }
}

/// <summary>A tile's overhead lines (#1052): <c>version u8</c>; supports; wires.</summary>
public static class RoadCatenary
{
    public const byte SectionVersion = 1;

    /// <summary>
    /// Main line: the messenger stands this far above the contact wire (system height), droppers this far apart; a tram's wire
    /// hangs this far below its span or arm (m).
    /// </summary>
    public const float SystemHeight = 1.2f, DropperStep = 9f, TramHang = 0.35f;

    public static void Write(BinaryWriter w, IReadOnlyList<CatenarySupport> supports, IReadOnlyList<CatenaryWire> wires)
    {
        w.Write(SectionVersion);
        w.Write7BitEncodedInt(supports.Count);
        foreach (var s in supports)
        {
            w.Write((byte)s.Kind);
            w.Write((byte)s.Line);
            w.Write((byte)((s.AWall ? 1 : 0) | (s.BWall ? 2 : 0)));
            foreach (float f in (ReadOnlySpan<float>)[s.AX, s.AY, s.AZ, s.BX, s.BY, s.BZ, s.WireY]) w.Write(f);
            w.Write(checked((byte)(s.Over.Length / 2)));
            foreach (float f in s.Over) w.Write(f);
        }
        w.Write7BitEncodedInt(wires.Count);
        foreach (var wire in wires)
        {
            w.Write((byte)wire.Line);
            w.Write7BitEncodedInt(wire.Points.Length / 3);
            foreach (float f in wire.Points) w.Write(f);
        }
    }

    /// <summary>False for a section of another version (the caller skips it).</summary>
    public static bool Read(BinaryReader r, List<CatenarySupport> supports, List<CatenaryWire> wires)
    {
        if (r.ReadByte() != SectionVersion) return false;
        for (int n = r.Read7BitEncodedInt(); n > 0; n--)
        {
            var kind = (CatenarySupportKind)r.ReadByte();
            var line = (CatenaryLine)r.ReadByte();
            byte walls = r.ReadByte();
            float ax = r.ReadSingle(), ay = r.ReadSingle(), az = r.ReadSingle(), bx = r.ReadSingle(), by = r.ReadSingle(), bz = r.ReadSingle();
            float wireY = r.ReadSingle();
            var over = new float[r.ReadByte() * 2];
            for (int i = 0; i < over.Length; i++) over[i] = r.ReadSingle();
            supports.Add(new CatenarySupport
            {
                Kind = kind, Line = line, AX = ax, AY = ay, AZ = az, AWall = (walls & 1) != 0, BX = bx, BY = by, BZ = bz, BWall = (walls & 2) != 0,
                WireY = wireY, Over = over,
            });
        }
        for (int n = r.Read7BitEncodedInt(); n > 0; n--)
        {
            var line = (CatenaryLine)r.ReadByte();
            var points = new float[r.Read7BitEncodedInt() * 3];
            for (int i = 0; i < points.Length; i++) points[i] = r.ReadSingle();
            wires.Add(new CatenaryWire { Line = line, Points = points });
        }
        return true;
    }
}
