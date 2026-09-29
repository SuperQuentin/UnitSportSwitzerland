using System.IO.Compression;
using System.Text;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.MapSetup;

/// <summary>A named place for the search box: a town (ranked by building count) or a summit/pass.</summary>
public sealed record Town(string Name, string Canton, double E, double N, PlaceKind Kind, int Rank)
{
    public TileId Tile => TileId.FromLv95(E, N);
}

/// <summary>A canton: its two-letter code, display name and id in <see cref="CountryData.CantonAt"/>.</summary>
public sealed record Canton(byte Id, string Code, string Name);

/// <summary>
/// One swissBUILDINGS3D sheet. Buildings are not published per kilometre but per map sheet
/// (a quarter of an LK25 sheet, about 4.4 x 3 km), so a selection pulls whichever sheets it touches.
/// </summary>
public sealed record BuildingSheet(string Key, double MinE, double MinN, double MaxE, double MaxN, long Size)
{
    /// <summary>Strict overlap, 5 m inside the tile: sheets are cut on the kilometre lines too (same test as swiss_data.py).</summary>
    public bool Touches(TileId t) => t.MinE + 5 < MaxE && t.MinE + 995 > MinE && t.MinN + 5 < MaxN && t.MaxN - 5 > MinN;
}

/// <summary>
/// Everything the setup tool knows about Switzerland before downloading anything: which 1 km
/// swissALTI3D tiles exist (which is the country outline), how big each zip is, how high it goes,
/// which canton it is in, the named places, the buildings sheets and the sizes of the nationwide
/// files. Produced once by <c>--bake</c> and committed as <see cref="FileName"/>.
/// </summary>
public sealed class CountryData
{
    public const string FileName = "switzerland.bin";
    private const string Magic = "CHMAP1";

    // The lattice covers swissALTI3D's whole footprint (E 2485..2834, N 1074..1296 km) with margin.
    public const int MinE = 2480, MinN = 1070, Width = 360, Height = 230;
    public const int MaxE = MinE + Width - 1, MaxN = MinN + Height - 1;

    /// <summary>Zip size in KB per tile; 0 means no swissALTI3D tile there (outside the country).</summary>
    private readonly uint[] _sizeKb = new uint[Width * Height];
    /// <summary>Survey year minus 2000, 0 when unknown.</summary>
    private readonly byte[] _year = new byte[Width * Height];
    /// <summary>Canton id (index into <see cref="Cantons"/> + 1), 0 when outside every canton.</summary>
    private readonly byte[] _canton = new byte[Width * Height];
    /// <summary>Highest terrain in the tile, metres; 0 when the bake had no built terrain there.</summary>
    private readonly short[] _maxElev = new short[Width * Height];

    public List<Canton> Cantons { get; } = new();
    public List<Town> Towns { get; } = new();
    public List<BuildingSheet> Sheets { get; } = new();

    /// <summary>
    /// Sizes of the files that do not follow the tile lattice: "swisstlm3d", "veloland",
    /// "mountainbikeland", "gwr_ch" and "gwr_&lt;canton&gt;" (lower-case code). Bytes.
    /// </summary>
    public Dictionary<string, long> Extras { get; } = new();

    /// <summary>UTC time the bake ran — sizes drift a little as swisstopo re-flies areas.</summary>
    public DateTime BakedAt { get; set; }

    public int TileCount { get; private set; }

    public static bool InGrid(int e, int n) => e >= MinE && e <= MaxE && n >= MinN && n <= MaxN;
    private static int Index(int e, int n) => (n - MinN) * Width + (e - MinE);

    public bool Covered(TileId t) => InGrid(t.E, t.N) && _sizeKb[Index(t.E, t.N)] != 0;
    public long SizeBytes(TileId t) => InGrid(t.E, t.N) ? _sizeKb[Index(t.E, t.N)] * 1024L : 0;
    public int Year(TileId t) => InGrid(t.E, t.N) && _year[Index(t.E, t.N)] != 0 ? 2000 + _year[Index(t.E, t.N)] : 0;
    public int MaxElevation(TileId t) => InGrid(t.E, t.N) ? _maxElev[Index(t.E, t.N)] : 0;

    /// <summary>The canton a tile's centre lies in, or null outside Switzerland (or at a border miss).</summary>
    public Canton? CantonAt(TileId t)
    {
        if (!InGrid(t.E, t.N)) return null;
        byte id = _canton[Index(t.E, t.N)];
        return id == 0 || id > Cantons.Count ? null : Cantons[id - 1];
    }

    public Canton? CantonByCode(string code) =>
        Cantons.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<TileId> AllTiles()
    {
        for (int n = MinN; n <= MaxN; n++)
            for (int e = MinE; e <= MaxE; e++)
                if (_sizeKb[Index(e, n)] != 0)
                    yield return new TileId(e, n);
    }

    public IEnumerable<TileId> TilesInCanton(byte cantonId) =>
        AllTiles().Where(t => _canton[Index(t.E, t.N)] == cantonId);

    /// <summary>Towns first by kind (towns before summits), then by rank; accent- and case-insensitive.</summary>
    public List<Town> Search(string query, int limit = 12)
    {
        var q = PlaceIndex.Normalize(query);
        if (q.Length == 0) return new();
        return Towns
            .Select(t => (t, n: PlaceIndex.Normalize(t.Name)))
            .Where(x => x.n.Contains(q))
            .OrderByDescending(x => x.n.StartsWith(q))
            .ThenBy(x => x.t.Kind)
            .ThenByDescending(x => x.t.Rank)
            .Take(limit)
            .Select(x => x.t)
            .ToList();
    }

    // ---- writing (bake) ------------------------------------------------------------------

    public void SetTile(TileId t, long sizeBytes, int year)
    {
        if (!InGrid(t.E, t.N)) return;
        int i = Index(t.E, t.N);
        _sizeKb[i] = (uint)Math.Max(1, (sizeBytes + 1023) / 1024);
        _year[i] = (byte)(year >= 2000 ? year - 2000 : 0);
    }

    public void SetCanton(TileId t, byte id)
    {
        if (InGrid(t.E, t.N)) _canton[Index(t.E, t.N)] = id;
    }

    public void SetMaxElevation(TileId t, float metres)
    {
        if (InGrid(t.E, t.N)) _maxElev[Index(t.E, t.N)] = (short)Math.Clamp(metres, short.MinValue, short.MaxValue);
    }

    public void Save(string path)
    {
        using var file = File.Create(path);
        using var z = new GZipStream(file, CompressionLevel.SmallestSize);
        using var w = new BinaryWriter(z, Encoding.UTF8);
        w.Write(Magic);
        w.Write(BakedAt.ToBinary());
        foreach (var v in _sizeKb) w.Write(v);
        w.Write(_year);
        w.Write(_canton);
        foreach (var v in _maxElev) w.Write(v);

        w.Write(Cantons.Count);
        foreach (var c in Cantons) { w.Write(c.Id); w.Write(c.Code); w.Write(c.Name); }

        w.Write(Towns.Count);
        foreach (var t in Towns)
        {
            w.Write(t.Name); w.Write(t.Canton);
            w.Write((int)Math.Round(t.E)); w.Write((int)Math.Round(t.N));
            w.Write((byte)t.Kind); w.Write(t.Rank);
        }

        w.Write(Sheets.Count);
        foreach (var s in Sheets)
        {
            w.Write(s.Key);
            w.Write((int)s.MinE); w.Write((int)s.MinN); w.Write((int)s.MaxE); w.Write((int)s.MaxN);
            w.Write(s.Size);
        }

        w.Write(Extras.Count);
        foreach (var (k, v) in Extras) { w.Write(k); w.Write(v); }
    }

    public static CountryData Load(string path)
    {
        using var file = File.OpenRead(path);
        using var z = new GZipStream(file, CompressionMode.Decompress);
        using var r = new BinaryReader(z, Encoding.UTF8);
        if (r.ReadString() != Magic)
            throw new InvalidDataException($"{path} is not a MapSetup country file (re-run --bake)");

        var d = new CountryData { BakedAt = DateTime.FromBinary(r.ReadInt64()) };
        for (int i = 0; i < d._sizeKb.Length; i++) d._sizeKb[i] = r.ReadUInt32();
        // ReadBytes, not Read(byte[]): a GZip stream may hand back less than asked for
        r.ReadBytes(d._year.Length).CopyTo(d._year, 0);
        r.ReadBytes(d._canton.Length).CopyTo(d._canton, 0);
        for (int i = 0; i < d._maxElev.Length; i++) d._maxElev[i] = r.ReadInt16();

        for (int i = r.ReadInt32(); i > 0; i--)
            d.Cantons.Add(new Canton(r.ReadByte(), r.ReadString(), r.ReadString()));
        for (int i = r.ReadInt32(); i > 0; i--)
            d.Towns.Add(new Town(r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadInt32(),
                (PlaceKind)r.ReadByte(), r.ReadInt32()));
        for (int i = r.ReadInt32(); i > 0; i--)
            d.Sheets.Add(new BuildingSheet(r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(),
                r.ReadInt32(), r.ReadInt64()));
        for (int i = r.ReadInt32(); i > 0; i--)
            d.Extras[r.ReadString()] = r.ReadInt64();

        d.TileCount = d._sizeKb.Count(v => v != 0);
        return d;
    }

    public void Recount() => TileCount = _sizeKb.Count(v => v != 0);
}
