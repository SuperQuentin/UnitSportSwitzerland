using UnitSport.Terrain.Format;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

namespace UnitSport.Farming;

/// <summary>
/// One tile's farm fields as the runtime reads them (#494): which field owns each 4 m cell
/// (<see cref="FieldFormat.Rasterise"/>), and each field's id and crop. Built on a worker from the
/// decoded <c>fields_E_N.fld</c>, kept only for tiles near a player, dropped when they are left.
/// 125 KB a tile (one ushort a cell).
/// </summary>
public sealed class FieldTile
{
    /// <summary>Cells per side of a drawing chunk: a tile is <see cref="ChunksPerSide"/>² chunks of 100 m.</summary>
    public const int ChunkCells = 25;
    public const int ChunksPerSide = FieldFormat.CellsPerSide / ChunkCells;
    public const int ChunkCount = ChunksPerSide * ChunksPerSide;

    public TileId Id { get; }
    /// <summary>Per cell: index into <see cref="FieldIds"/> / <see cref="Crops"/> + 1, 0 off every field.</summary>
    public ushort[] Owner { get; }
    public uint[] FieldIds { get; }
    public CropKind[] Crops { get; }
    /// <summary>The outlines (tile-local metres), index = owner - 1: the drawing clips the edge cells to them.</summary>
    public IReadOnlyList<FieldPolygon> Fields { get; }
    /// <summary>Field cells in all.</summary>
    public int CellCount { get; }
    /// <summary>Per drawing chunk: how many field cells it holds (0: nothing to draw).</summary>
    public int[] ChunkCellCounts { get; }

    private FieldTile(TileId id, ushort[] owner, uint[] ids, CropKind[] crops, IReadOnlyList<FieldPolygon> fields)
    {
        Id = id;
        Owner = owner;
        FieldIds = ids;
        Crops = crops;
        Fields = fields;
        ChunkCellCounts = new int[ChunkCount];
        for (int c = 0; c < owner.Length; c++)
        {
            if (owner[c] == 0) continue;
            CellCount++;
            ChunkCellCounts[ChunkOf(c)]++;
        }
    }

    /// <summary>Rasterises a tile's fields (worker thread). An empty list gives a tile with no field cell.</summary>
    public static FieldTile Build(TileId id, IReadOnlyList<FieldPolygon> fields)
    {
        var owner = FieldFormat.Rasterise(fields);
        int n = Math.Min(fields.Count, ushort.MaxValue);
        var ids = new uint[n];
        var crops = new CropKind[n];
        for (int i = 0; i < n; i++) { ids[i] = fields[i].Id; crops[i] = fields[i].Crop; }
        return new FieldTile(id, owner, ids, crops, fields);
    }

    /// <summary>The field's crop at a cell, <see cref="CropKind.None"/> off every field.</summary>
    public CropKind CropAt(int cell) => Owner[cell] is var o and > 0 ? Crops[o - 1] : CropKind.None;

    /// <summary>The field's id at a cell, 0 off every field.</summary>
    public uint FieldAt(int cell) => Owner[cell] is var o and > 0 ? FieldIds[o - 1] : 0;

    /// <summary>The drawing chunk a cell is in: column + row × <see cref="ChunksPerSide"/>, row 0 the south edge.</summary>
    public static int ChunkOf(int cell) =>
        cell / FieldFormat.CellsPerSide / ChunkCells * ChunksPerSide + cell % FieldFormat.CellsPerSide / ChunkCells;
}
