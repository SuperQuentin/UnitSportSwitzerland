using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace UnitSport.Terrain.Format;

/// <summary>What a point of interest is (#1224, <c>docs/plans/adventure-places.md</c>). Stored as a byte: never renumber.</summary>
public enum PoiKind : byte
{
    Peak = 1,
    Pass = 2,
    SummitCross = 3,
    Waterfall = 4,
    Lake = 5,
    LookoutTower = 6,
    Church = 7,
    Chapel = 8,
    Castle = 9,
    Bridge = 10,
    Dam = 11,
    RailStation = 12,
    BusStop = 13,
    ShipLanding = 14,
    CableCarStation = 15,
    Hut = 16,
}

[Flags]
public enum PoiFlags : byte
{
    None = 0,
    /// <summary>Synced by interacting: reveals the map around it (summit crosses, towers, cable-car tops, road passes).</summary>
    Viewpoint = 1,
    /// <summary>A peak with a summit cross on it (the TLM <c>Gipfelkreuz</c> joined within 150 m).</summary>
    Cross = 2,
    /// <summary>The higher end of a cable car.</summary>
    Top = 4,
    /// <summary>The bus stop shown for a town when a map cluster is zoomed out (one per <c>places.json</c> town).</summary>
    VillageStop = 8,
    /// <summary>The source names it; otherwise the name is made up ("Summit cross near X").</summary>
    Named = 16,
}

/// <summary>One point of interest as the file holds it; positions are LV95 metres, kept to the decimetre.</summary>
public sealed class PoiRecord
{
    /// <summary>40 bits of SHA-256 over the source identity (<see cref="PoiId.Hash"/>): what an id is matched on.</summary>
    public ulong Hash;
    public PoiKind Kind;
    public PoiFlags Flags;
    /// <summary>1 Landmark (a curated card), 2 Notable, 3 Minor (shown and stamped, not counted).</summary>
    public byte Tier = 3;
    /// <summary>Two letters ("UR"), "" outside every canton.</summary>
    public string Canton = "";
    /// <summary>Metres, horizontal (stored in 2 m steps, up to 510 m).</summary>
    public float DiscoverRadius;
    /// <summary>Metres a viewpoint reveals (stored in 100 m steps, up to 25.5 km); 0 for the rest.</summary>
    public float RevealRadius;
    public double E, N;
    public short Elevation;
    public string Name = "";
    /// <summary>Cable cars: the line's bottom station LV95 rounded to a metre, <c>E * 10^7 + N</c> (<c>Ropeway.Key</c>); else 0.</summary>
    public long LineKey;
    /// <summary>A lake's outline as E, N pairs (LV95 m, decimetres), or null.</summary>
    public double[]? Outline;

    public string Id => PoiId.Make(Kind, Name, Hash);
}

/// <summary>A canton of the region, with the totals completion is counted against.</summary>
public sealed record PoiCanton(string Code, int Tier1, int Tier2, int LandCells);

/// <summary>The whole <c>pois.bin</c>: cantons and records.</summary>
public sealed class PoiData
{
    public List<PoiCanton> Cantons { get; } = new();
    public List<PoiRecord> Pois { get; } = new();
}

/// <summary>
/// POI ids (#1224): <c>&lt;tag&gt;:&lt;slug&gt;.&lt;hash&gt;</c>, e.g. <c>peak:rigi-kulm.3fa9c01b2e</c>. The hash is
/// 40 bits of SHA-256 over the source identity (the TLM uuid, ...), the slug is cosmetic: an id is
/// matched on its tag and hash only, so a rerun or a TLM rename keeps every player's progress.
/// </summary>
public static class PoiId
{
    public const int SlugMax = 32;
    public const ulong HashMask = (1UL << 40) - 1;

    private static readonly string[] Tags =
    {
        "", "peak", "pass", "cross", "waterfall", "lake", "tower", "church", "chapel", "castle",
        "bridge", "dam", "station", "busstop", "ship", "cablecar", "hut",
    };

    public static string Tag(PoiKind kind) => (int)kind < Tags.Length ? Tags[(int)kind] : "";

    public static bool TryKind(string tag, out PoiKind kind)
    {
        int i = Array.IndexOf(Tags, tag);
        kind = (PoiKind)Math.Max(i, 0);
        return i > 0;
    }

    /// <summary>The first 40 bits of SHA-256 over the UTF-8 source identity, big-endian.</summary>
    public static ulong Hash(string sourceId)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(sourceId), digest);
        ulong h = 0;
        for (int i = 0; i < 5; i++) h = h << 8 | digest[i];
        return h;
    }

    /// <summary>Lower case, accents stripped (<see cref="PlaceIndex.Normalize"/>), anything else to one '-', cut at 32.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder(SlugMax);
        foreach (char c in PlaceIndex.Normalize(name))
        {
            bool keep = c is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (keep) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= SlugMax) break;
        }
        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.Length == 0 ? "unnamed" : sb.ToString();
    }

    public static string Make(PoiKind kind, string name, ulong hash) =>
        $"{Tag(kind)}:{Slug(name)}.{(hash & HashMask).ToString("x10", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The tag and hash of a canonical id (any slug); false for an alias or anything malformed.</summary>
    public static bool TryParse(string id, out PoiKind kind, out ulong hash)
    {
        kind = 0;
        hash = 0;
        int colon = id.IndexOf(':');
        int dot = id.LastIndexOf('.');
        if (colon <= 0 || dot <= colon || id.Length - dot - 1 != 10) return false;
        if (!TryKind(id[..colon], out kind)) return false;
        return ulong.TryParse(id.AsSpan(dot + 1), System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture, out hash);
    }
}

/// <summary>
/// <c>pois.bin</c> (#1224): the region's points of interest in one region-wide file next to the
/// chunks, shared by the preprocessor (<c>PoiStage</c>) and the game (<c>PoiCatalogue</c>).
/// Little-endian:
/// <list type="bullet">
/// <item>header (24 B): "POI1", version u16, reserved u16, then string, canton, record and outline counts (i32);</item>
/// <item>strings: u16 byte length + UTF-8 each (names, deduplicated);</item>
/// <item>cantons: two ASCII letters, tier 1 total, tier 2 total, land fog cells (i32 each);</item>
/// <item>records (<see cref="RecordSize"/> B): hash (5), kind, flags, tier, canton (index + 1, 0 none),
/// discover radius (m / 2), reveal radius (x 100 m), E and N (u32 decimetres from 2 480 000 / 1 070 000),
/// elevation (i16 m), name index (i32), line key (i64), outline index (i32, -1 none);</item>
/// <item>outlines: point count (i32), the first point (u32 decimetres as a record's), then i32 decimetre
/// offsets from it.</item>
/// </list>
/// </summary>
public static class PoiFile
{
    /// <summary>"POI1" read as little-endian uint32.</summary>
    public const uint Magic = 0x31494F50;
    public const ushort Version = 1;
    public const string FileName = "pois.bin";
    public const int HeaderSize = 24;
    public const int RecordSize = 37;
    /// <summary>The <c>CountryData</c> lattice origin, LV95 metres.</summary>
    public const double OriginE = 2_480_000, OriginN = 1_070_000;

    private static uint Dm(double v, double origin) => (uint)Math.Clamp(Math.Round((v - origin) * 10), 0, uint.MaxValue);

    public static byte[] Encode(PoiData data)
    {
        var strings = new List<string>();
        var stringIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int Str(string s)
        {
            if (!stringIndex.TryGetValue(s, out int i)) { stringIndex[s] = i = strings.Count; strings.Add(s); }
            return i;
        }
        var cantonIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < data.Cantons.Count; i++) cantonIndex[data.Cantons[i].Code] = i;
        foreach (var p in data.Pois) Str(p.Name);

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);   // little-endian, as BinaryPrimitives below
        int outlines = data.Pois.Count(p => p.Outline is { Length: >= 2 });
        w.Write(Magic);
        w.Write(Version);
        w.Write((ushort)0);
        w.Write(strings.Count);
        w.Write(data.Cantons.Count);
        w.Write(data.Pois.Count);
        w.Write(outlines);
        foreach (var s in strings)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("POI name too long");
            w.Write((ushort)bytes.Length);
            w.Write(bytes);
        }
        foreach (var c in data.Cantons)
        {
            if (c.Code.Length != 2) throw new InvalidDataException($"canton code '{c.Code}' is not two letters");
            w.Write((byte)c.Code[0]);
            w.Write((byte)c.Code[1]);
            w.Write(c.Tier1);
            w.Write(c.Tier2);
            w.Write(c.LandCells);
        }
        Span<byte> rec = stackalloc byte[RecordSize];
        int outline = 0;
        foreach (var p in data.Pois)
        {
            rec.Clear();
            for (int i = 0; i < 5; i++) rec[i] = (byte)(p.Hash >> (8 * (4 - i)));
            rec[5] = (byte)p.Kind;
            rec[6] = (byte)p.Flags;
            rec[7] = p.Tier;
            rec[8] = p.Canton.Length == 0 ? (byte)0
                : cantonIndex.TryGetValue(p.Canton, out int ci) ? (byte)(ci + 1)
                : throw new InvalidDataException($"canton '{p.Canton}' is not in the canton table");
            rec[9] = (byte)Math.Clamp((int)Math.Round(p.DiscoverRadius / 2), 0, 255);
            rec[10] = (byte)Math.Clamp((int)Math.Round(p.RevealRadius / 100), 0, 255);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[11..], Dm(p.E, OriginE));
            BinaryPrimitives.WriteUInt32LittleEndian(rec[15..], Dm(p.N, OriginN));
            BinaryPrimitives.WriteInt16LittleEndian(rec[19..], p.Elevation);
            BinaryPrimitives.WriteInt32LittleEndian(rec[21..], Str(p.Name));
            BinaryPrimitives.WriteInt64LittleEndian(rec[25..], p.LineKey);
            BinaryPrimitives.WriteInt32LittleEndian(rec[33..], p.Outline is { Length: >= 2 } ? outline++ : -1);
            w.Write(rec);
        }
        foreach (var p in data.Pois)
        {
            if (p.Outline is not { Length: >= 2 } o) continue;
            int n = o.Length / 2;
            uint e0 = Dm(o[0], OriginE), n0 = Dm(o[1], OriginN);
            w.Write(n);
            w.Write(e0);
            w.Write(n0);
            for (int i = 1; i < n; i++)
            {
                w.Write((int)((long)Dm(o[i * 2], OriginE) - e0));
                w.Write((int)((long)Dm(o[i * 2 + 1], OriginN) - n0));
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    public static PoiData Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic)
            throw new InvalidDataException("not a pois.bin");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]);
        if (version > Version) throw new InvalidDataException($"pois.bin version {version} is newer than {Version}");
        int nStrings = BinaryPrimitives.ReadInt32LittleEndian(b[8..]);
        int nCantons = BinaryPrimitives.ReadInt32LittleEndian(b[12..]);
        int nPois = BinaryPrimitives.ReadInt32LittleEndian(b[16..]);
        int nOutlines = BinaryPrimitives.ReadInt32LittleEndian(b[20..]);
        if (nStrings < 0 || nCantons is < 0 or > 254 || nPois < 0 || nOutlines < 0) throw new InvalidDataException("pois.bin counts");
        int at = HeaderSize;
        ReadOnlySpan<byte> Take(ReadOnlySpan<byte> all, ref int pos, int n)
        {
            if (n < 0 || pos + n > all.Length) throw new InvalidDataException("pois.bin is truncated");
            var s = all.Slice(pos, n);
            pos += n;
            return s;
        }
        var strings = new string[nStrings];
        for (int i = 0; i < nStrings; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(Take(b, ref at, 2));
            strings[i] = Encoding.UTF8.GetString(Take(b, ref at, len));
        }
        var data = new PoiData();
        for (int i = 0; i < nCantons; i++)
        {
            var c = Take(b, ref at, 14);
            data.Cantons.Add(new PoiCanton(new string(new[] { (char)c[0], (char)c[1] }),
                BinaryPrimitives.ReadInt32LittleEndian(c[2..]), BinaryPrimitives.ReadInt32LittleEndian(c[6..]),
                BinaryPrimitives.ReadInt32LittleEndian(c[10..])));
        }
        var outlineOf = new int[nPois];
        for (int i = 0; i < nPois; i++)
        {
            var r = Take(b, ref at, RecordSize);
            ulong hash = 0;
            for (int k = 0; k < 5; k++) hash = hash << 8 | r[k];
            int canton = r[8];
            int name = BinaryPrimitives.ReadInt32LittleEndian(r[21..]);
            if (canton > nCantons || (uint)name >= (uint)nStrings) throw new InvalidDataException("pois.bin record index");
            data.Pois.Add(new PoiRecord
            {
                Hash = hash,
                Kind = (PoiKind)r[5],
                Flags = (PoiFlags)r[6],
                Tier = r[7],
                Canton = canton == 0 ? "" : data.Cantons[canton - 1].Code,
                DiscoverRadius = r[9] * 2f,
                RevealRadius = r[10] * 100f,
                E = OriginE + BinaryPrimitives.ReadUInt32LittleEndian(r[11..]) / 10.0,
                N = OriginN + BinaryPrimitives.ReadUInt32LittleEndian(r[15..]) / 10.0,
                Elevation = BinaryPrimitives.ReadInt16LittleEndian(r[19..]),
                Name = strings[name],
                LineKey = BinaryPrimitives.ReadInt64LittleEndian(r[25..]),
            });
            outlineOf[i] = BinaryPrimitives.ReadInt32LittleEndian(r[33..]);
        }
        var outlines = new double[nOutlines][];
        for (int i = 0; i < nOutlines; i++)
        {
            int n = BinaryPrimitives.ReadInt32LittleEndian(Take(b, ref at, 4));
            if (n < 1 || n > 1_000_000) throw new InvalidDataException("pois.bin outline size");
            var head = Take(b, ref at, 8);
            uint e0 = BinaryPrimitives.ReadUInt32LittleEndian(head), n0 = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            var pts = new double[n * 2];
            pts[0] = OriginE + e0 / 10.0;
            pts[1] = OriginN + n0 / 10.0;
            var rest = Take(b, ref at, (n - 1) * 8);
            for (int k = 1; k < n; k++)
            {
                pts[k * 2] = OriginE + (e0 + (long)BinaryPrimitives.ReadInt32LittleEndian(rest[((k - 1) * 8)..])) / 10.0;
                pts[k * 2 + 1] = OriginN + (n0 + (long)BinaryPrimitives.ReadInt32LittleEndian(rest[((k - 1) * 8 + 4)..])) / 10.0;
            }
            outlines[i] = pts;
        }
        for (int i = 0; i < nPois; i++)
            if (outlineOf[i] >= 0)
                data.Pois[i].Outline = outlineOf[i] < nOutlines ? outlines[outlineOf[i]] : throw new InvalidDataException("pois.bin outline index");
        return data;
    }
}
