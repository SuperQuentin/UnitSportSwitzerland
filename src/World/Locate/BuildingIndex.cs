using System.Buffers.Binary;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Locate;

/// <summary>What the index knows of a building beyond its <see cref="BuildingKind"/>. Stored: never renumber.</summary>
[Flags]
public enum IndexedFlags : byte
{
    None = 0,
    /// <summary>The hall of a church group (<c>BuildingTypes</c>), with or without a tower.</summary>
    Church = 1,
    /// <summary>A church whose tower rises above its hall.</summary>
    Spire = 2,
    /// <summary>A block whose box and roll take an underground garage ramp (<c>GarageRule.RampFor</c>); the footprint has the last word.</summary>
    GarageRoll = 4,
    /// <summary>Planned as a bank (<c>BuildingFootprint.IsBank</c>).</summary>
    Bank = 8,
}

/// <summary>One building of the index: its plan centre (LV95), the tile and index it is read from, its facts.</summary>
public readonly record struct IndexedBuilding(double E, double N, int TileE, int TileN, int Index, BuildingKind Kind,
    byte Floors, ushort Year, float Height, IndexedFlags Flags)
{
    public bool Is(IndexedFlags f) => (Flags & f) == f;
}

/// <summary>
/// Every building of the region in a few bytes each (#1277), for <c>/locate</c>'s building nouns and
/// selectors: built once by the server from its own <c>.bldg</c> tiles (<c>BuildingIndexBuilder</c>, a
/// worker at boot, cached on disk), bucketed per 1 km tile for the nearest search. The 6 699-tile dev
/// region is about 600 000 buildings, ~17 MB in memory. Pure C#, tier 0 (<c>LocateTests</c>).
/// </summary>
public sealed class BuildingIndex
{
    public const uint Magic = 0x3142434C; // "LCB1"
    /// <summary>Bump when what a flag means changes (a church rule, the garage rule): the cache is rebuilt.</summary>
    public const ushort Version = 1;
    private const int RecordSize = 29;
    private const double OffE = 2_400_000, OffN = 1_000_000;

    private readonly IndexedBuilding[] _all;
    private readonly Dictionary<long, int[]> _buckets = new();
    private readonly int _minTE, _maxTE, _minTN, _maxTN;

    public static BuildingIndex Empty { get; } = new([]);

    public int Count => _all.Length;
    public IReadOnlyList<IndexedBuilding> All => _all;

    public BuildingIndex(IReadOnlyList<IndexedBuilding> buildings)
    {
        _all = buildings.ToArray();
        var lists = new Dictionary<long, List<int>>();
        _minTE = _minTN = int.MaxValue;
        _maxTE = _maxTN = int.MinValue;
        for (int i = 0; i < _all.Length; i++)
        {
            int te = (int)Math.Floor(_all[i].E / 1000), tn = (int)Math.Floor(_all[i].N / 1000);
            long key = Key(te, tn);
            if (!lists.TryGetValue(key, out var l)) lists[key] = l = new List<int>();
            l.Add(i);
            _minTE = Math.Min(_minTE, te); _maxTE = Math.Max(_maxTE, te);
            _minTN = Math.Min(_minTN, tn); _maxTN = Math.Max(_maxTN, tn);
        }
        foreach (var (k, l) in lists) _buckets[k] = l.ToArray();
    }

    private static long Key(int te, int tn) => ((long)te << 32) | (uint)tn;

    /// <summary>
    /// The <paramref name="max"/> buildings nearest the point that pass <paramref name="keep"/>, nearest first,
    /// within <paramref name="radius"/> metres. Rings of 1 km tiles outward, stopping once a ring cannot hold
    /// anything nearer than the last one kept.
    /// </summary>
    public List<(IndexedBuilding Building, double Distance)> Nearest(double e, double n, Func<IndexedBuilding, bool> keep, int max,
        double radius = double.MaxValue)
    {
        var found = new List<(IndexedBuilding, double)>();
        if (_all.Length == 0 || max <= 0) return found;
        int ce = (int)Math.Floor(e / 1000), cn = (int)Math.Floor(n / 1000);
        int farthest = Math.Max(Math.Max(Math.Abs(ce - _minTE), Math.Abs(ce - _maxTE)), Math.Max(Math.Abs(cn - _minTN), Math.Abs(cn - _maxTN)));
        for (int r = 0; r <= farthest; r++)
        {
            // a ring r tiles out is at least (r - 1) km away
            double ringNear = Math.Max(0, (r - 1) * 1000.0);
            if (ringNear > radius) break;
            if (found.Count >= max && ringNear > found[max - 1].Item2) break;
            for (int te = ce - r; te <= ce + r; te++)
                for (int tn = cn - r; tn <= cn + r; tn++)
                {
                    if (Math.Abs(te - ce) != r && Math.Abs(tn - cn) != r) continue;
                    if (!_buckets.TryGetValue(Key(te, tn), out var bucket)) continue;
                    foreach (int i in bucket)
                    {
                        var b = _all[i];
                        double d = Math.Sqrt((b.E - e) * (b.E - e) + (b.N - n) * (b.N - n));
                        if (d > radius || !keep(b)) continue;
                        found.Add((b, d));
                    }
                }
            found.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            if (found.Count > max) found.RemoveRange(max, found.Count - max);
        }
        return found;
    }

    public void Write(Stream output)
    {
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], _all.Length);
        output.Write(header);
        var rec = new byte[RecordSize];
        foreach (var b in _all)
        {
            BinaryPrimitives.WriteInt32LittleEndian(rec, (int)Math.Round((b.E - OffE) * 10));
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(4), (int)Math.Round((b.N - OffN) * 10));
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(8), b.TileE);
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(12), b.TileN);
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(16), b.Index);
            rec[20] = (byte)b.Kind;
            rec[21] = b.Floors;
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(22), b.Year);
            BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(24), b.Height);
            rec[28] = (byte)b.Flags;
            output.Write(rec);
        }
    }

    /// <summary>The index written by <see cref="Write"/>, or null if it is another version or cut short.</summary>
    public static BuildingIndex? Read(Stream input)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            input.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Version)
                return null;
            int count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            if (count < 0) return null;
            var all = new IndexedBuilding[count];
            var rec = new byte[RecordSize];
            for (int i = 0; i < count; i++)
            {
                input.ReadExactly(rec);
                all[i] = new IndexedBuilding(
                    BinaryPrimitives.ReadInt32LittleEndian(rec) / 10.0 + OffE,
                    BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(4)) / 10.0 + OffN,
                    BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(8)),
                    BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(12)),
                    BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(16)),
                    (BuildingKind)rec[20], rec[21],
                    BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(22)),
                    BinaryPrimitives.ReadSingleLittleEndian(rec.AsSpan(24)),
                    (IndexedFlags)rec[28]);
            }
            return new BuildingIndex(all);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}
