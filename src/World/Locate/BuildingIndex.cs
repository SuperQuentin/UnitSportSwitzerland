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

    // filled tile by tile while it is built (#1277: the tiles round the players first); every read and write
    // holds the lock, a few hundred microseconds a query
    private readonly object _lock = new();
    private readonly List<IndexedBuilding> _all = new();
    private readonly Dictionary<long, List<int>> _buckets = new();
    private int _minTE = int.MaxValue, _maxTE = int.MinValue, _minTN = int.MaxValue, _maxTN = int.MinValue;
    /// <summary>The tiles still to read; empty once complete (and always for an index built whole).</summary>
    private readonly HashSet<long> _missing = new();
    private int _expected;

    public static BuildingIndex Empty { get; } = new([]);

    public int Count { get { lock (_lock) return _all.Count; } }

    /// <summary>Every building so far (a copy).</summary>
    public IReadOnlyList<IndexedBuilding> All { get { lock (_lock) return _all.ToArray(); } }

    /// <summary>Whether every tile is in: then any answer is the true nearest, wherever it is asked from.</summary>
    public bool Complete { get { lock (_lock) return _missing.Count == 0; } }

    /// <summary>Tiles read of those expected (the whole index: both its tile count).</summary>
    public (int Done, int Total) Progress { get { lock (_lock) return (_expected - _missing.Count, _expected); } }

    /// <summary>An index built whole (a cache read, a test).</summary>
    public BuildingIndex(IReadOnlyList<IndexedBuilding> buildings)
    {
        foreach (var b in buildings) Put(b);
    }

    private BuildingIndex() { }

    /// <summary>An empty index that fills as <see cref="AddTile"/> is called for each of <paramref name="tiles"/>.</summary>
    public static BuildingIndex Growing(IEnumerable<TileId> tiles)
    {
        var index = new BuildingIndex();
        foreach (var t in tiles) index._missing.Add(Key(t.E, t.N));
        index._expected = index._missing.Count;
        return index;
    }

    /// <summary>One tile's buildings, from any thread; the tile counts as read even when it holds none.</summary>
    public void AddTile(TileId tile, IReadOnlyList<IndexedBuilding> buildings)
    {
        lock (_lock)
        {
            foreach (var b in buildings) Put(b);
            _missing.Remove(Key(tile.E, tile.N));
        }
    }

    private void Put(IndexedBuilding b)
    {
        int te = (int)Math.Floor(b.E / 1000), tn = (int)Math.Floor(b.N / 1000);
        long key = Key(te, tn);
        if (!_buckets.TryGetValue(key, out var l)) _buckets[key] = l = new List<int>();
        l.Add(_all.Count);
        _all.Add(b);
        _minTE = Math.Min(_minTE, te); _maxTE = Math.Max(_maxTE, te);
        _minTN = Math.Min(_minTN, tn); _maxTN = Math.Max(_maxTN, tn);
    }

    private static long Key(int te, int tn) => ((long)te << 32) | (uint)tn;

    /// <summary>
    /// How far round the point every tile is read, m: within it the index holds everything there is, so a hit
    /// nearer than this is the true nearest. Infinite once complete.
    /// </summary>
    public double CoveredRadius(double e, double n)
    {
        lock (_lock)
        {
            if (_missing.Count == 0) return double.PositiveInfinity;
            int ce = (int)Math.Floor(e / 1000), cn = (int)Math.Floor(n / 1000);
            double best = double.PositiveInfinity;
            // the ring holding the nearest missing tile, and the next (a tile of ring r + 1 can be nearer than a corner of ring r)
            for (int r = 0; r < 4000; r++)
            {
                if (Math.Max(0, (r - 1) * 1000.0) > best) break;
                for (int te = ce - r; te <= ce + r; te++)
                    for (int tn = cn - r; tn <= cn + r; tn++)
                    {
                        if (Math.Abs(te - ce) != r && Math.Abs(tn - cn) != r) continue;
                        if (!_missing.Contains(Key(te, tn))) continue;
                        // the point to the tile's square, 0 inside it
                        double dx = Math.Max(0, Math.Max(te * 1000.0 - e, e - (te + 1) * 1000.0));
                        double dy = Math.Max(0, Math.Max(tn * 1000.0 - n, n - (tn + 1) * 1000.0));
                        best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
                    }
            }
            return best;
        }
    }

    /// <summary>
    /// The <paramref name="max"/> buildings nearest the point that pass <paramref name="keep"/>, nearest first,
    /// within <paramref name="radius"/> metres. Rings of 1 km tiles outward, stopping once a ring cannot hold
    /// anything nearer than the last one kept.
    /// </summary>
    public List<(IndexedBuilding Building, double Distance)> Nearest(double e, double n, Func<IndexedBuilding, bool> keep, int max,
        double radius = double.MaxValue)
    {
        var found = new List<(IndexedBuilding, double)>();
        lock (_lock)
        {
            if (_all.Count == 0 || max <= 0) return found;
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
        }
        return found;
    }

    public void Write(Stream output)
    {
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        var all = All;
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], all.Count);
        output.Write(header);
        var rec = new byte[RecordSize];
        foreach (var b in all)
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
