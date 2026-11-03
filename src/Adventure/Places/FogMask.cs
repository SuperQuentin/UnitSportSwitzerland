using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace UnitSport.Adventure.Places;

// Plain C#, no Godot: linked into the unit tests (FogMaskTests; docs/notes/adventure/fog.md).

/// <summary>
/// A player's fog of war (#1224, places plan 4): which 250 m cells of the Swiss lattice
/// (<c>CountryData</c>: west 2 480 km, south 1 070 km, 360 x 230 km) have been seen. Sparse: a 1 km tile
/// holds 4 x 4 cells as one <see cref="ushort"/> (bit <c>row * 4 + col</c>, row 0 the southern), and only
/// tiles with a revealed cell are kept. Stored deflated (<see cref="Encode"/>): tile indices as delta
/// varints, then each mask; the whole country is under 5 KB.
/// </summary>
public sealed class FogMask
{
    public const double MinE = 2_480_000, MinN = 1_070_000;
    public const int TilesE = 360, TilesN = 230;
    public const double CellM = 250;
    public const int CellsPerTile = 4;
    /// <summary>The lattice in cells: 1 440 x 920.</summary>
    public const int Width = TilesE * CellsPerTile, Height = TilesN * CellsPerTile;
    public const ushort Full = 0xFFFF;

    private readonly Dictionary<int, ushort> _tiles = new();

    public int TileCount => _tiles.Count;
    public IReadOnlyDictionary<int, ushort> Tiles => _tiles;

    /// <summary>The tile index of a 1 km tile (west-east first), or -1 off the lattice.</summary>
    public static int TileIndex(int tileE, int tileN)
    {
        int x = tileE - (int)(MinE / 1000), y = tileN - (int)(MinN / 1000);
        return x < 0 || y < 0 || x >= TilesE || y >= TilesN ? -1 : y * TilesE + x;
    }

    /// <summary>A tile index's 1 km tile (LV95 km).</summary>
    public static (int E, int N) TileOf(int index) => (index % TilesE + (int)(MinE / 1000), index / TilesE + (int)(MinN / 1000));

    /// <summary>The cell (column from the west, row from the south) holding a point; false off the lattice.</summary>
    public static bool CellAt(double e, double n, out int col, out int row)
    {
        col = (int)Math.Floor((e - MinE) / CellM);
        row = (int)Math.Floor((n - MinN) / CellM);
        return col >= 0 && row >= 0 && col < Width && row < Height;
    }

    /// <summary>A cell's centre (LV95).</summary>
    public static (double E, double N) CentreOf(int col, int row) => (MinE + (col + 0.5) * CellM, MinN + (row + 0.5) * CellM);

    public bool RevealedCell(int col, int row)
    {
        if (col < 0 || row < 0 || col >= Width || row >= Height) return false;
        int tile = row / CellsPerTile * TilesE + col / CellsPerTile;
        return _tiles.TryGetValue(tile, out ushort m) && (m & Bit(col, row)) != 0;
    }

    public bool Revealed(double e, double n) => CellAt(e, n, out int c, out int r) && RevealedCell(c, r);

    private static ushort Bit(int col, int row) => (ushort)(1 << (row % CellsPerTile * CellsPerTile + col % CellsPerTile));

    /// <summary>
    /// Reveals the cells whose centre lies within <paramref name="radiusM"/> of a point. Adds the tiles that
    /// changed to <paramref name="changed"/> (once each, if given); returns how many cells were new.
    /// </summary>
    public int Reveal(double e, double n, double radiusM, ICollection<int>? changed = null)
    {
        int c0 = (int)Math.Floor((e - radiusM - MinE) / CellM), c1 = (int)Math.Floor((e + radiusM - MinE) / CellM);
        int r0 = (int)Math.Floor((n - radiusM - MinN) / CellM), r1 = (int)Math.Floor((n + radiusM - MinN) / CellM);
        c0 = Math.Max(c0, 0); r0 = Math.Max(r0, 0);
        c1 = Math.Min(c1, Width - 1); r1 = Math.Min(r1, Height - 1);
        double r2 = radiusM * radiusM;
        int added = 0;
        for (int row = r0; row <= r1; row++)
            for (int col = c0; col <= c1; col++)
            {
                var (ce, cn) = CentreOf(col, row);
                if ((ce - e) * (ce - e) + (cn - n) * (cn - n) > r2) continue;
                int tile = row / CellsPerTile * TilesE + col / CellsPerTile;
                ushort was = _tiles.GetValueOrDefault(tile), bit = Bit(col, row);
                if ((was & bit) != 0) continue;
                _tiles[tile] = (ushort)(was | bit);
                added++;
                if (changed != null && !changed.Contains(tile)) changed.Add(tile);
            }
        return added;
    }

    /// <summary>The mask of a tile (0 when nothing of it is revealed).</summary>
    public ushort MaskOf(int tile) => _tiles.GetValueOrDefault(tile);

    /// <summary>Sets a tile's mask (a delta from the server); 0 removes it.</summary>
    public void Set(int tile, ushort mask)
    {
        if (tile < 0 || tile >= TilesE * TilesN) return;
        if (mask == 0) _tiles.Remove(tile);
        else _tiles[tile] = mask;
    }

    public void Clear() => _tiles.Clear();

    /// <summary>Revealed cells in all.</summary>
    public int Count
    {
        get
        {
            int n = 0;
            foreach (ushort m in _tiles.Values) n += System.Numerics.BitOperations.PopCount(m);
            return n;
        }
    }

    /// <summary>Revealed cells whose centre passes <paramref name="where"/> (column, row).</summary>
    public int CountWhere(Func<int, int, bool> where)
    {
        int n = 0;
        foreach (var (tile, m) in _tiles)
        {
            int tx = tile % TilesE, ty = tile / TilesE;
            for (int bit = 0; bit < 16; bit++)
            {
                if ((m & (1 << bit)) == 0) continue;
                int col = tx * CellsPerTile + bit % CellsPerTile, row = ty * CellsPerTile + bit / CellsPerTile;
                if (where(col, row)) n++;
            }
        }
        return n;
    }

    // ---- the stored and wire form ----------------------------------------------------------------

    /// <summary>Deflated: a varint count, then per tile (ascending) the varint gap from the last index and the mask (2 bytes LE).</summary>
    public byte[] ToBytes()
    {
        var keys = new List<int>(_tiles.Keys);
        keys.Sort();
        using var raw = new MemoryStream();
        WriteVarint(raw, (uint)keys.Count);
        int last = -1;
        foreach (int k in keys)
        {
            WriteVarint(raw, (uint)(k - last));
            last = k;
            ushort m = _tiles[k];
            raw.WriteByte((byte)m);
            raw.WriteByte((byte)(m >> 8));
        }
        using var packed = new MemoryStream();
        using (var z = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(z);
        return packed.ToArray();
    }

    public static FogMask FromBytes(ReadOnlySpan<byte> deflated)
    {
        var mask = new FogMask();
        if (deflated.Length == 0) return mask;
        using var z = new DeflateStream(new MemoryStream(deflated.ToArray()), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        raw.Position = 0;
        uint count = ReadVarint(raw);
        int at = -1;
        for (uint i = 0; i < count; i++)
        {
            at += (int)ReadVarint(raw);
            int lo = raw.ReadByte(), hi = raw.ReadByte();
            if (lo < 0 || hi < 0) throw new InvalidDataException("fog mask cut short");
            mask.Set(at, (ushort)(lo | hi << 8));
        }
        return mask;
    }

    /// <summary>The section's string: <see cref="ToBytes"/> in base64 ("" when nothing is revealed).</summary>
    public string Encode() => _tiles.Count == 0 ? "" : Convert.ToBase64String(ToBytes());

    public static FogMask Decode(string? text) =>
        string.IsNullOrEmpty(text) ? new FogMask() : FromBytes(Convert.FromBase64String(text));

    private static void WriteVarint(Stream s, uint v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private static uint ReadVarint(Stream s)
    {
        uint v = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            int b = s.ReadByte();
            if (b < 0) throw new InvalidDataException("fog mask cut short");
            v |= (uint)(b & 0x7F) << shift;
            if (b < 0x80) return v;
        }
        throw new InvalidDataException("fog mask varint too long");
    }
}
