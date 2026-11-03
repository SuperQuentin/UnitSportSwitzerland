using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace UnitSport.Terrain.Format;

/// <summary>OSM <c>piste:type</c> of a prepared-snow slope (#887). <b>Append-only</b>: saved in piste files.</summary>
public enum PisteType : byte { Downhill = 1, Connection = 2, SnowPark = 3, Nordic = 4, Sled = 5 }

/// <summary>OSM <c>piste:difficulty</c>. <b>Append-only</b>.</summary>
public enum PisteDifficulty : byte
{
    Unknown = 0, Novice = 1, Easy = 2, Intermediate = 3, Advanced = 4, Expert = 5, Freeride = 6, Extreme = 7,
}

/// <summary>OSM <c>piste:grooming</c>; <see cref="Unknown"/> is read as groomed for a downhill slope. <b>Append-only</b>.</summary>
public enum PisteGrooming : byte { Unknown = 0, Classic = 1, Skating = 2, Mogul = 3, Backcountry = 4 }

/// <summary>
/// One slope as it lies in a tile. <see cref="Path"/> is in metres east and north of the tile's
/// south-west corner, interleaved (east, north): the centreline of a line slope, or the outline of
/// an area one (<see cref="IsArea"/>), kept whole even where it runs outside the tile so markers
/// can be laid without seams. <see cref="Id"/> is the OSM id (top bit set for a relation), the same
/// in every tile the slope touches.
/// </summary>
/// <param name="AltLow">The slope's ground in this tile, 10th percentile of altitude (m).</param>
/// <param name="AltHigh">90th percentile of altitude (m): the snow state is evaluated at both and blended by height.</param>
/// <param name="Sun">Sun exposure from the mean aspect: 0 a steep north face, 0.5 flat, 1 a steep south face.</param>
/// <param name="SteepDeg">Mean slope angle (degrees), for the moguls.</param>
public sealed record PisteRecord(ulong Id, PisteType Type, PisteDifficulty Difficulty, PisteGrooming Grooming,
    bool IsArea, float WidthM, string Name, string Ref, float[] Path,
    float AltLow = 2000, float AltHigh = 2000, float Sun = 0.5f, float SteepDeg = 15)
{
    /// <summary>Groomed unless OSM says moguls or backcountry: what the 06:00 reset applies to.</summary>
    public bool Groomed => Grooming is not (PisteGrooming.Mogul or PisteGrooming.Backcountry)
                           && Difficulty is not (PisteDifficulty.Freeride or PisteDifficulty.Extreme);
}

/// <summary>A decoded piste tile: the records and the rasters on the terrain grid.</summary>
/// <param name="Slot">Per grid vertex (row 0 = north edge, as <see cref="CoverFormat"/>): record index + 1, 0 = no slope.</param>
/// <param name="Aux">Per grid vertex: edge mask in the high byte (255 = at least <see cref="PisteFormat.EdgeRampM"/> inside),
/// groom phase in the low byte: the point's offset across the run, modulo <see cref="PisteFormat.PhaseM"/>, 256 steps
/// (the signed distance from a line slope's centreline, or across an area's long axis), so corduroy runs along the slope.</param>
public sealed record PisteTile(TileId Id, IReadOnlyList<PisteRecord> Records, byte[] Slot, ushort[] Aux)
{
    /// <summary>The grid vertex nearest an LV95 point inside this tile, or -1.</summary>
    public int IndexAt(double e, double n)
    {
        int col = (int)Math.Round(e - Id.MinE), row = (int)Math.Round(Id.MaxN - n);
        if ((uint)col >= PisteFormat.Size || (uint)row >= PisteFormat.Size) return -1;
        return row * PisteFormat.Size + col;
    }

    /// <summary>The slope under an LV95 point, or null.</summary>
    public PisteRecord? At(double e, double n)
    {
        int i = IndexAt(e, n);
        if (i < 0 || Slot[i] == 0) return null;
        return Records[Slot[i] - 1];
    }
}

/// <summary>
/// A tile's ski slopes (#887): <c>pistes_E_N.pst</c>, written by the preprocessor's piste stage from
/// OpenStreetMap, absent for a tile no slope touches. A record table, then the two rasters
/// (<see cref="PisteTile.Slot"/>, <see cref="PisteTile.Aux"/>) deflated. OSM data is ODbL, so the header
/// always carries <see cref="FlagOsm"/> (<c>tools/osm-odbl-licence</c>).
/// </summary>
public static class PisteFormat
{
    /// <summary>"USPS" little-endian.</summary>
    public const uint Magic = 0x53505355;
    public const ushort Version = 1;
    public const ushort FlagOsm = 1;
    public const int Size = ChunkFormat.GridSize;
    public const int MaxRecords = 255;
    /// <summary>The edge mask ramps from 0 at a slope's edge to full this far inside.</summary>
    public const float EdgeRampM = 3f;

    public static string FileName(TileId id) => $"pistes_{id.E}_{id.N}.pst";

    /// <summary>
    /// The groom phase repeats over this many metres: a whole number of corduroy ribs (1.25 m) and
    /// groomer lanes (5 m), and long enough that the phase turns only 18° between two 2 m texels.
    /// </summary>
    public const float PhaseM = 40f;

    public static ushort PackAux(float mask, double offsetM)
    {
        int m = Math.Clamp((int)MathF.Round(mask * 255f), 0, 255);
        double p = offsetM % PhaseM;
        if (p < 0) p += PhaseM;
        int d = (int)Math.Round(p / PhaseM * 256) & 255;
        return (ushort)(m << 8 | d);
    }

    public static float MaskOf(ushort aux) => (aux >> 8) / 255f;
    /// <summary>The groom phase in metres, 0 to <see cref="PhaseM"/>.</summary>
    public static float PhaseOf(ushort aux) => (aux & 255) / 256f * PhaseM;

    public static void Encode(PisteTile tile, Stream output)
    {
        if (tile.Records.Count > MaxRecords) throw new ArgumentException($"{tile.Records.Count} slopes in a tile, max {MaxRecords}");
        if (tile.Slot.Length != Size * Size || tile.Aux.Length != Size * Size) throw new ArgumentException("piste raster size");
        new TileHeader(Magic, Version, FlagOsm, tile.Id, (uint)tile.Records.Count).Write(output);
        using var w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        foreach (var r in tile.Records)
        {
            w.Write(r.Id);
            w.Write((byte)r.Type); w.Write((byte)r.Difficulty); w.Write((byte)r.Grooming); w.Write((byte)(r.IsArea ? 1 : 0));
            w.Write(r.WidthM);
            w.Write(r.AltLow); w.Write(r.AltHigh); w.Write(r.Sun); w.Write(r.SteepDeg);
            w.Write(r.Name); w.Write(r.Ref);
            w.Write(r.Path.Length / 2);
            foreach (float v in r.Path) w.Write(v);
        }
        w.Flush();
        using var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true);
        deflate.Write(tile.Slot);
        var aux = new byte[tile.Aux.Length * 2];
        for (int i = 0; i < tile.Aux.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(aux.AsSpan(i * 2), tile.Aux[i]);
        deflate.Write(aux);
    }

    public static PisteTile Decode(Stream input)
    {
        var header = TileHeader.Read(input, Magic, "piste");
        header.CheckVersion(Version, "piste");
        var records = new List<PisteRecord>((int)header.Count);
        using (var r = new BinaryReader(input, Encoding.UTF8, leaveOpen: true))
            for (uint i = 0; i < header.Count; i++)
            {
                ulong id = r.ReadUInt64();
                var type = (PisteType)r.ReadByte();
                var diff = (PisteDifficulty)r.ReadByte();
                var groom = (PisteGrooming)r.ReadByte();
                bool area = r.ReadByte() != 0;
                float width = r.ReadSingle();
                float altLow = r.ReadSingle(), altHigh = r.ReadSingle(), sun = r.ReadSingle(), steep = r.ReadSingle();
                string name = r.ReadString(), reference = r.ReadString();
                var path = new float[r.ReadInt32() * 2];
                for (int k = 0; k < path.Length; k++) path[k] = r.ReadSingle();
                records.Add(new PisteRecord(id, type, diff, groom, area, width, name, reference, path, altLow, altHigh, sun, steep));
            }
        var slot = new byte[Size * Size];
        var auxBytes = new byte[Size * Size * 2];
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true))
        {
            deflate.ReadExactly(slot);
            deflate.ReadExactly(auxBytes);
        }
        var aux = new ushort[Size * Size];
        for (int i = 0; i < aux.Length; i++) aux[i] = BinaryPrimitives.ReadUInt16LittleEndian(auxBytes.AsSpan(i * 2));
        return new PisteTile(header.Id, records, slot, aux);
    }
}
