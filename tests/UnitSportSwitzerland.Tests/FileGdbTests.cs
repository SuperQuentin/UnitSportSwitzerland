using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The parts of the FileGDB reader that are pure arithmetic (#537). The reader as a whole is
/// checked against GDAL on the real swisstopo data — 88,676 and 51,745 route keys, and all 3,586
/// building solids of a sheet vertex by vertex — but that needs the external drive, so what lives
/// here is the decoding every one of those results rests on.
/// </summary>
public class FileGdbTests
{
    private static ulong VarUInt(params byte[] bytes)
    {
        int at = 0;
        return FileGdbGeometry.ReadVarUInt(bytes, ref at);
    }

    private static long VarInt(params byte[] bytes)
    {
        int at = 0;
        return FileGdbGeometry.ReadVarInt(bytes, ref at);
    }

    [Theory]
    [InlineData(0u, new byte[] { 0x00 })]
    [InlineData(1u, new byte[] { 0x01 })]
    [InlineData(127u, new byte[] { 0x7F })]
    [InlineData(128u, new byte[] { 0x80, 0x01 })]
    [InlineData(368u, new byte[] { 0xF0, 0x02 })]        // a real vertex count
    [InlineData(16383u, new byte[] { 0xFF, 0x7F })]
    public void Unsigned_varints_decode(ulong expected, byte[] bytes) => Assert.Equal(expected, VarUInt(bytes));

    /// <summary>
    /// The signed form keeps its sign in bit 6 of the FIRST byte and continues from bit 6, which is
    /// not zigzag. Read as zigzag every delta halves, and a building comes out at half scale rather
    /// than visibly broken — so this is the test that would catch it.
    /// </summary>
    [Theory]
    [InlineData(0L, new byte[] { 0x00 })]
    [InlineData(1L, new byte[] { 0x01 })]
    [InlineData(-1L, new byte[] { 0x41 })]
    [InlineData(63L, new byte[] { 0x3F })]
    [InlineData(-63L, new byte[] { 0x7F })]
    [InlineData(64L, new byte[] { 0x80, 0x01 })]
    [InlineData(-64L, new byte[] { 0xC0, 0x01 })]
    public void Signed_varints_decode(long expected, byte[] bytes) => Assert.Equal(expected, VarInt(bytes));

    [Fact]
    public void A_varint_that_never_ends_is_refused()
    {
        var runaway = new byte[16];
        Array.Fill(runaway, (byte)0x80);
        Assert.Throws<InvalidDataException>(() => VarUInt(runaway));
    }

    /// <summary>
    /// The cursor must land exactly after each value: a reader that is one byte out decodes the
    /// first vertex correctly and turns everything after it into noise.
    /// </summary>
    [Fact]
    public void Reading_advances_by_exactly_the_bytes_consumed()
    {
        byte[] stream = [0xF0, 0x02, 0x7F, 0x80, 0x01];
        int at = 0;
        Assert.Equal(368ul, FileGdbGeometry.ReadVarUInt(stream, ref at));
        Assert.Equal(2, at);
        Assert.Equal(127ul, FileGdbGeometry.ReadVarUInt(stream, ref at));
        Assert.Equal(3, at);
        Assert.Equal(128ul, FileGdbGeometry.ReadVarUInt(stream, ref at));
        Assert.Equal(5, at);
    }

    [Fact]
    public void A_shape_that_is_not_a_multipatch_is_refused()
    {
        // shape type 1 (point), no flags
        var grid = new FileGdb.GeometryGrid(0, 0, 10000, 0, 10000, 0, 10000, HasZ: true, HasM: false,
            Bounds: (0, 0, 0, 0));
        Assert.Throws<InvalidDataException>(() => FileGdbGeometry.DecodeMultiPatch(new byte[] { 0x01 }, grid));
    }

    /// <summary>
    /// A ring part is closed — its last vertex repeats its first — so fanning it must produce one
    /// triangle for a 4-vertex ring, not two. Getting this wrong doubles every building's triangles.
    /// </summary>
    [Fact]
    public void A_closed_ring_of_four_vertices_makes_one_triangle()
    {
        var v = new FileGdbGeometry.Vertex[]
        {
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 0),
        };
        var shape = new FileGdbGeometry.Shape(v, [new FileGdbGeometry.Part(0, 4, FileGdbGeometry.PartKind.OuterRing)]);
        Assert.Single(FileGdbGeometry.Triangles(shape));
    }

    [Fact]
    public void A_triangle_strip_makes_one_triangle_per_vertex_after_the_second()
    {
        var v = new FileGdbGeometry.Vertex[]
        {
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0), new(0, 2, 0),
        };
        var shape = new FileGdbGeometry.Shape(v, [new FileGdbGeometry.Part(0, 5, FileGdbGeometry.PartKind.TriangleStrip)]);
        Assert.Equal(3, FileGdbGeometry.Triangles(shape).Count);
    }

    [Fact]
    public void A_triangle_fan_shares_its_first_vertex()
    {
        var v = new FileGdbGeometry.Vertex[]
        {
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
        };
        var shape = new FileGdbGeometry.Shape(v, [new FileGdbGeometry.Part(0, 4, FileGdbGeometry.PartKind.TriangleFan)]);
        var triangles = FileGdbGeometry.Triangles(shape);
        Assert.Equal(2, triangles.Count);
        Assert.All(triangles, t => Assert.Equal(v[0], t.A));
    }
}
