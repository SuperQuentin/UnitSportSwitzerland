using System.Buffers.Binary;

namespace UnitSport.Terrain.Format;

/// <summary>
/// What grows on a farm field (#494), from the field's LNF code in the federal land-use data
/// (BLW "Nutzungsflächen", MGDM 153.1) or, where a canton does not publish it, from OpenStreetMap.
/// Several LNF codes share one kind: the game grows wheat, not "Winterweizen ohne Futterweizen".
///
/// <para><b>Append-only</b>: saved in field files and replicated as a byte.</para>
/// </summary>
public enum CropKind : byte
{
    None = 0,
    /// <summary>Bread and feed wheat, spelt, rye, triticale, emmer.</summary>
    Wheat = 1,
    /// <summary>Barley, oats, mixed feed grain.</summary>
    Barley = 2,
    /// <summary>Grain and silage maize.</summary>
    Maize = 3,
    /// <summary>Ware and seed potatoes.</summary>
    Potato = 4,
    /// <summary>Sugar and fodder beet.</summary>
    SugarBeet = 5,
    /// <summary>Winter and summer rapeseed.</summary>
    Rapeseed = 6,
    Sunflower = 7,
    /// <summary>Field vegetables (open-air, canning).</summary>
    Vegetables = 8,
    /// <summary>Peas, field beans, soya, lupins.</summary>
    Legumes = 9,
    /// <summary>Temporary and permanent meadows: mown for hay.</summary>
    Meadow = 10,
    /// <summary>Grazed pasture: grass the year round.</summary>
    Pasture = 11,
    /// <summary>Fallow, flower strips, set-aside.</summary>
    Fallow = 12,
    /// <summary>Any other arable crop (tobacco, hemp, herbs...): drawn as a generic green crop.</summary>
    OtherArable = 13,
}

/// <summary>Where a field's outline came from.</summary>
public enum FieldSource : byte { Lwb = 0, Osm = 1 }

/// <summary>
/// One farm field: an outline (first ring outer, the rest holes) in metres east and north of
/// its tile's south-west corner (<see cref="TileId.MinE"/>, <see cref="TileId.MinN"/>). A field
/// that crosses tile edges is written whole into every tile its bounds touch; <see cref="Id"/>
/// is the same in each, so a reader can tell it is one field.
/// </summary>
/// <param name="Id">Stable across rebuilds: a hash of the LWB identifier (and part), or of the OSM way.</param>
/// <param name="LnfCode">The LNF code (0 for OSM).</param>
public sealed record FieldPolygon(uint Id, CropKind Crop, FieldSource Source, ushort LnfCode, IReadOnlyList<float[]> Rings);

/// <summary>
/// A tile's farm fields (#494): <c>fields_E_N.fld</c>, written by the preprocessor's fields stage.
/// Absent for a tile with no fields. Ring points are interleaved (east, north) floats.
///
/// <para>
/// <b>Cells</b>: the game farms the ground in <see cref="CellSize"/> m squares on a grid aligned to
/// LV95 (a cell is <c>floor(E / 4), floor(N / 4)</c>), so every tile holds exactly
/// <see cref="CellsPerSide"/>² of them and a cell never straddles two tiles. A cell belongs to the
/// first field (file order) whose outline holds its centre: <see cref="Rasterise"/>.
/// </para>
/// </summary>
public static class FieldFormat
{
    /// <summary>"USFD" little-endian.</summary>
    public const uint Magic = 0x44465355;
    public const ushort Version = 1;

    public const float CellSize = 4f;
    public const int CellsPerSide = 250;
    public const int CellCount = CellsPerSide * CellsPerSide;

    public static string FileName(TileId id) => $"fields_{id.E}_{id.N}.fld";

    /// <summary>A cell's index in its tile: column east + row north × <see cref="CellsPerSide"/>.</summary>
    public static int CellIndex(int col, int row) => row * CellsPerSide + col;

    /// <summary>The tile and cell holding an LV95 point.</summary>
    public static (TileId Tile, int Cell) CellAt(double e, double n)
    {
        var tile = TileId.FromLv95(e, n);
        int col = Math.Clamp((int)((e - tile.MinE) / CellSize), 0, CellsPerSide - 1);
        int row = Math.Clamp((int)((n - tile.MinN) / CellSize), 0, CellsPerSide - 1);
        return (tile, CellIndex(col, row));
    }

    /// <summary>A cell's centre, metres east and north of its tile's south-west corner.</summary>
    public static (float East, float North) CellCentre(int cell) =>
        ((cell % CellsPerSide + 0.5f) * CellSize, (cell / CellsPerSide + 0.5f) * CellSize);

    public static void Encode(TileId id, IReadOnlyList<FieldPolygon> fields, Stream output)
    {
        new TileHeader(Magic, Version, 0, id, (uint)fields.Count).Write(output);
        Span<byte> b = stackalloc byte[12];
        foreach (var f in fields)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, f.Id);
            b[4] = (byte)f.Crop;
            b[5] = (byte)f.Source;
            BinaryPrimitives.WriteUInt16LittleEndian(b[6..], f.LnfCode);
            BinaryPrimitives.WriteUInt16LittleEndian(b[8..], (ushort)f.Rings.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(b[10..], 0);
            output.Write(b);
            foreach (var ring in f.Rings)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)(ring.Length / 2));
                output.Write(b[..4]);
                foreach (float v in ring)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(b, v);
                    output.Write(b[..4]);
                }
            }
        }
    }

    public static List<FieldPolygon> Decode(Stream input)
    {
        var header = TileHeader.Read(input, Magic, "field");
        var fields = new List<FieldPolygon>((int)header.Count);
        var b = new byte[12];
        for (uint i = 0; i < header.Count; i++)
        {
            input.ReadExactly(b);
            uint fid = BinaryPrimitives.ReadUInt32LittleEndian(b);
            var crop = (CropKind)b[4];
            var source = (FieldSource)b[5];
            ushort lnf = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6));
            int ringCount = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8));
            var rings = new List<float[]>(ringCount);
            for (int r = 0; r < ringCount; r++)
            {
                input.ReadExactly(b, 0, 4);
                int points = (int)BinaryPrimitives.ReadUInt32LittleEndian(b);
                var bytes = new byte[points * 8];
                input.ReadExactly(bytes);
                var ring = new float[points * 2];
                for (int k = 0; k < ring.Length; k++) ring[k] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(k * 4));
                rings.Add(ring);
            }
            fields.Add(new FieldPolygon(fid, crop, source, lnf, rings));
        }
        return fields;
    }

    /// <summary>
    /// Which field owns each cell of the tile: index into <paramref name="fields"/> + 1, 0 for none.
    /// Even-odd scanline over every ring (holes cut out), cell centres only; the first field wins.
    /// </summary>
    public static ushort[] Rasterise(IReadOnlyList<FieldPolygon> fields)
    {
        var owner = new ushort[CellCount];
        var xs = new List<float>();
        for (int f = 0; f < fields.Count && f < ushort.MaxValue; f++)
        {
            var rings = fields[f].Rings;
            float minN = float.MaxValue, maxN = float.MinValue;
            foreach (var ring in rings)
                for (int k = 1; k < ring.Length; k += 2) { minN = Math.Min(minN, ring[k]); maxN = Math.Max(maxN, ring[k]); }
            int row0 = Math.Max(0, (int)MathF.Floor(minN / CellSize - 0.5f));
            int row1 = Math.Min(CellsPerSide - 1, (int)MathF.Ceiling(maxN / CellSize - 0.5f));
            for (int row = row0; row <= row1; row++)
            {
                float y = (row + 0.5f) * CellSize;
                xs.Clear();
                foreach (var ring in rings)
                {
                    int n = ring.Length / 2;
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        float yi = ring[i * 2 + 1], yj = ring[j * 2 + 1];
                        if ((yi > y) == (yj > y)) continue;
                        float xi = ring[i * 2], xj = ring[j * 2];
                        xs.Add(xi + (y - yi) / (yj - yi) * (xj - xi));
                    }
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int c0 = Math.Max(0, (int)MathF.Ceiling(xs[k] / CellSize - 0.5f));
                    int c1 = Math.Min(CellsPerSide - 1, (int)MathF.Floor(xs[k + 1] / CellSize - 0.5f));
                    for (int col = c0; col <= c1; col++)
                    {
                        int cell = CellIndex(col, row);
                        if (owner[cell] == 0) owner[cell] = (ushort)(f + 1);
                    }
                }
            }
        }
        return owner;
    }
}
