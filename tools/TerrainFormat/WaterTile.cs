namespace UnitSport.Terrain.Format;

/// <summary>
/// One tile's still water as a chunk source hands it over (#299): where there is water and the
/// altitude of its still surface, on a 2 m lattice. The runtime turns it into a
/// <c>Terrain.WaterLayer</c> (adds the wave scale from fetch and depth) and answers
/// <c>ChunkManager.TryGetWaterLevel</c> from it.
///
/// <para>
/// Who fills it: the fixture courses build it in code; the preprocessor's water layer (#298, a file
/// beside the tile, its codec next to this class) will decode into it, with the swissBATHY3D bed in
/// the height grid underneath. A source that returns null leaves the runtime to derive the legacy
/// layer from the cover raster: the surface on the terrain at every <c>CoverClass.Water</c> cell,
/// 0.12 m above it, which is how every tile built before #298 draws its lakes.
/// </para>
///
/// <para>
/// Layout: <see cref="Size"/> x <see cref="Size"/> samples, row-major, row 0 the NORTH edge and
/// column 0 the WEST edge, exactly like <c>ChunkGrid</c> at stride <see cref="Stride"/>: sample
/// (c, r) sits at LV95 E = MinE + 2c, N = MaxN - 2r and on height-grid vertex (2c, 2r). Edge samples
/// are shared with the neighbouring tile and must agree with it.
/// </para>
/// </summary>
public sealed class WaterTile
{
    /// <summary>Metres between samples: every second vertex of the 1 m height grid.</summary>
    public const int Stride = 2;

    /// <summary>Samples per side (501).</summary>
    public const int Size = (ChunkFormat.GridSize - 1) / Stride + 1;

    /// <summary>
    /// Altitude of the still water surface in metres at each sample; <see cref="float.NaN"/> where
    /// there is no water. A lake is one value everywhere; a river slopes downstream.
    /// </summary>
    public required float[] Level { get; init; }

    /// <summary>
    /// Optional: the fetch at each sample, in metres: how far the wind can blow over open water to
    /// reach it (a lake's width, a river's width). It scales the waves: a river or a pond stays flat,
    /// a big lake gets the whole sea state. Null: the runtime derives one from the distance to the
    /// shore inside this tile, which is right for rivers and ponds and an underestimate on a lake
    /// that spans tiles, so a source that knows the whole lake (the preprocessor) should fill it.
    /// </summary>
    public float[]? FetchM { get; init; }

    public static int Index(int col, int row) => row * Size + col;
}
