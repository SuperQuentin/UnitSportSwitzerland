namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Decodes the geometry blobs <see cref="FileGdb"/> hands back (#537), far enough for the one
/// thing this project needs from a FileGDB's geometry: swissBUILDINGS3D's 3D solids.
///
/// <para>
/// <b>Coordinates are not floating point.</b> They are stored as signed varint deltas of scaled
/// integers, accumulated along the shape, and only turned into metres through the column's
/// <see cref="FileGdb.GeometryGrid"/>. Ignoring that does not produce slightly wrong buildings —
/// it produces buildings near the South Pole — which is why the grid is a required argument rather
/// than something a caller may forget.
/// </para>
///
/// <para>
/// Only what the swisstopo data actually contains is decoded; anything else throws rather than
/// returning a shape that looks plausible and is not. Everything here was derived from the real
/// files and checked feature by feature against GDAL.
/// </para>
/// </summary>
public static class FileGdbGeometry
{
    /// <summary>The shape codes the format uses. Only the ones met in this data are named.</summary>
    private const int GeneralMultiPatch = 54;

    /// <summary>Flags packed above the shape code in the leading value.</summary>
    private const uint HasZFlag = 0x8000_0000, HasMFlag = 0x4000_0000, CurveFlag = 0x2000_0000;

    /// <summary>How a multipatch part's vertices make triangles.</summary>
    public enum PartKind
    {
        TriangleStrip = 0,
        TriangleFan = 1,
        OuterRing = 2,
        InnerRing = 3,
        FirstRing = 4,
        Ring = 5,
    }

    public readonly record struct Vertex(double X, double Y, double Z);

    /// <summary>One run of the vertex list, and how to read it.</summary>
    public readonly record struct Part(int Start, int Count, PartKind Kind);

    public sealed record Shape(IReadOnlyList<Vertex> Vertices, IReadOnlyList<Part> Parts);

    /// <summary>
    /// Decodes one multipatch blob. Throws on anything that is not a Z-bearing general multipatch,
    /// because this exists to read buildings and a wrong answer elsewhere would be worse than none.
    /// </summary>
    public static Shape DecodeMultiPatch(ReadOnlySpan<byte> blob, FileGdb.GeometryGrid grid)
    {
        int at = 0;
        uint header = (uint)ReadVarUInt(blob, ref at);
        int code = (int)(header & 0xFF);
        bool hasZ = (header & HasZFlag) != 0;
        bool hasM = (header & HasMFlag) != 0;

        if (code != GeneralMultiPatch)
            throw new InvalidDataException($"geometry is shape type {code}, not a general multipatch ({GeneralMultiPatch})");
        if ((header & CurveFlag) != 0)
            throw new InvalidDataException("multipatch carries curves, which this reader does not decode");
        if (!hasZ)
            throw new InvalidDataException("multipatch has no Z; a building solid without height is not usable");

        int vertexCount = (int)ReadVarUInt(blob, ref at);
        // A length the format carries here that nothing needs: the shape is fully described by the
        // counts and deltas that follow, and this is the size an uncompressed copy would take.
        // Read so the cursor stays aligned, and deliberately unused.
        _ = ReadVarUInt(blob, ref at);
        int partCount = (int)ReadVarUInt(blob, ref at);
        if (vertexCount <= 0 || partCount <= 0 || vertexCount > 1 << 24 || partCount > vertexCount)
            throw new InvalidDataException($"multipatch claims {vertexCount} vertices in {partCount} parts");

        // Bounding box: the first two absolute, the second two as deltas from them. Not kept —
        // the vertices below are the authority — but it has to be stepped over exactly.
        ReadVarUInt(blob, ref at);
        ReadVarUInt(blob, ref at);
        ReadVarUInt(blob, ref at);
        ReadVarUInt(blob, ref at);

        // Per-part vertex counts, the last implied by what is left over.
        var parts = new Part[partCount];
        var counts = new int[partCount];
        int counted = 0;
        for (int i = 0; i < partCount - 1; i++)
        {
            counts[i] = (int)ReadVarUInt(blob, ref at);
            counted += counts[i];
        }
        counts[partCount - 1] = vertexCount - counted;
        if (counts[partCount - 1] <= 0)
            throw new InvalidDataException("multipatch part vertex counts overrun its vertex count");

        for (int i = 0; i < partCount; i++)
        {
            var kind = (PartKind)ReadVarUInt(blob, ref at);
            if (!Enum.IsDefined(kind))
                throw new InvalidDataException($"multipatch part {i} has unknown type {(int)kind}");
            parts[i] = new Part(0, counts[i], kind);
        }

        // X and Y come as interleaved pairs — one dx then one dy per vertex, each a signed delta
        // from the vertex before — and only then does Z follow as a run of its own. Reading X and Y
        // as two separate runs decodes the first vertex correctly and every one after it wrongly,
        // which is a far more confusing failure than an exception.
        var vertices = new Vertex[vertexCount];
        long x = 0, y = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            x += ReadVarInt(blob, ref at);
            y += ReadVarInt(blob, ref at);
            vertices[i] = new Vertex(x / grid.XyScale + grid.XOrigin, y / grid.XyScale + grid.YOrigin, 0);
        }
        long z = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            z += ReadVarInt(blob, ref at);
            vertices[i] = vertices[i] with { Z = z / grid.ZScale + grid.ZOrigin };
        }
        // M is written after Z when present. Nothing here uses it, and it is the last run, so it
        // is simply left unread.

        int start = 0;
        for (int i = 0; i < partCount; i++)
        {
            parts[i] = parts[i] with { Start = start };
            start += parts[i].Count;
        }
        return new Shape(vertices, parts);
    }

    /// <summary>
    /// The shape's triangles. A ring part is a closed polygon (its last vertex repeats its first)
    /// and is fanned; strips and fans are expanded the way their names say. This is the form
    /// <c>BuildingExtractor</c> wants, and the only one this project reads multipatches for.
    /// </summary>
    public static List<(Vertex A, Vertex B, Vertex C)> Triangles(Shape shape)
    {
        var triangles = new List<(Vertex, Vertex, Vertex)>();
        foreach (var part in shape.Parts)
        {
            var v = shape.Vertices;
            int s = part.Start, n = part.Count;
            switch (part.Kind)
            {
                case PartKind.TriangleStrip:
                    for (int i = 2; i < n; i++)
                        // every other triangle is wound the other way round
                        triangles.Add(i % 2 == 0
                            ? (v[s + i - 2], v[s + i - 1], v[s + i])
                            : (v[s + i - 1], v[s + i - 2], v[s + i]));
                    break;

                case PartKind.TriangleFan:
                    for (int i = 2; i < n; i++) triangles.Add((v[s], v[s + i - 1], v[s + i]));
                    break;

                default:
                    // A ring: closed, so the repeated last vertex is dropped before fanning it.
                    int last = n;
                    if (n >= 2 && Same(v[s], v[s + n - 1])) last = n - 1;
                    for (int i = 2; i < last; i++) triangles.Add((v[s], v[s + i - 1], v[s + i]));
                    break;
            }
        }
        return triangles;
    }

    private static bool Same(Vertex a, Vertex b) =>
        Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6 && Math.Abs(a.Z - b.Z) < 1e-6;

    /// <summary>Seven bits a byte, high bit means another follows — the format's unsigned varint.</summary>
    internal static ulong ReadVarUInt(ReadOnlySpan<byte> blob, ref int at)
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            byte b = blob[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("varint longer than 64 bits");
        }
    }

    /// <summary>
    /// The signed form: the first byte carries the sign in bit 6, the rest continue as usual. This
    /// is not the zigzag encoding protobuf uses, and reading it as zigzag halves every delta —
    /// which looks like a building drawn at half scale rather than like an error.
    /// </summary>
    internal static long ReadVarInt(ReadOnlySpan<byte> blob, ref int at)
    {
        byte first = blob[at++];
        bool negative = (first & 0x40) != 0;
        long value = first & 0x3F;
        int shift = 6;
        if ((first & 0x80) != 0)
        {
            while (true)
            {
                byte b = blob[at++];
                value |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 63) throw new InvalidDataException("varint longer than 64 bits");
            }
        }
        return negative ? -value : value;
    }
}
