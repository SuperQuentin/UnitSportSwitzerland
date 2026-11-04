using System.Buffers.Binary;
using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// How a stage writes leaves (#1507, G2 B3): its sections per leaf of a frame, one
/// <see cref="CubeBundle.UpdateLeaves"/> per frame (the stage's kinds replaced, every other kind kept bit for
/// bit), after a seam pass for the lattice kinds that must match exactly where leaves meet, and a
/// <see cref="CubeManifest"/> of the region at the end (<see cref="WriteManifest"/>).
///
/// <para>
/// <b>The seam pass, an owner per shared vertex.</b> A lattice vertex on a leaf's edge is held by two leaves
/// (four at a corner, three at a cube corner). Its owner is the holder that comes first in the frames'
/// order (<see cref="LeafSeam.FrameOrder"/>: the frame's face, y, x, then the leaf's y, x) among those that
/// have the kind; every other holder copies the owner's value. Stages run frames in that order, so a
/// frame's owners are always in that frame or an earlier one, already on disk: the copy reads them
/// there, and a frame is written once. A frame that is rerun alone takes its earlier neighbours' values;
/// the frames after it must then be rerun to take its new ones.
/// </para>
/// </summary>
public sealed class LeafWriter
{
    private readonly string _dir;
    private readonly int _frameLevel;
    private readonly IReadOnlyCollection<LeafKind> _stageKinds;
    private readonly IReadOnlyList<LatticeKind> _seams;

    public LeafWriter(string dir, int frameLevel, IReadOnlyCollection<LeafKind> stageKinds, IReadOnlyList<LatticeKind>? seams = null)
    {
        _dir = dir;
        _frameLevel = frameLevel;
        _stageKinds = stageKinds;
        _seams = seams ?? Array.Empty<LatticeKind>();
        Directory.CreateDirectory(dir);
    }

    /// <summary>What a seam pass did: shared boundary samples looked at, those copied from their owner, and of those across a frame edge.</summary>
    public sealed record SeamStats(LeafKind Kind, long Samples, long Copied, long CopiedAcrossFrames, int LeavesChanged);

    public string PathOf(CubeKey frame) => Path.Combine(_dir, CubeBundle.LeafFileName(frame));

    /// <summary>The seam pass over a frame's sections, then the frame's one bundle update.</summary>
    public List<SeamStats> WriteFrame(CubeKey frame, Dictionary<CubeKey, List<LeafSection>> sections)
    {
        var stats = new List<SeamStats>();
        if (sections.Count == 0 && !File.Exists(PathOf(frame))) return stats;
        foreach (var kind in _seams)
        {
            if (!_stageKinds.Contains(kind.Kind)) continue;
            stats.Add(Seam(frame, kind, sections));
        }
        CubeBundle.UpdateLeaves(PathOf(frame), frame, sections, _stageKinds);
        return stats;
    }

    private SeamStats Seam(CubeKey frame, LatticeKind kind, Dictionary<CubeKey, List<LeafSection>> sections)
    {
        var own = new ConcurrentDictionary<CubeKey, BoundaryValues>();
        Parallel.ForEach(sections, e =>
        {
            if (e.Value.FirstOrDefault(s => s.Kind == kind.Kind) is { } s) own[e.Key] = BoundaryValues.Of(kind.Decode(s));
        });
        var order = LeafSeam.FrameOrder(_frameLevel);
        var disk = new Dictionary<CubeKey, BoundaryValues?>();
        BoundaryValues? Present(CubeKey k)
        {
            if (own.TryGetValue(k, out var v)) return v;
            var f = k.AncestorAt(_frameLevel);
            // only earlier frames can own a vertex this frame holds; this one's are all in memory
            if (LeafSeam.CompareCells(f, frame) >= 0) return null;
            if (disk.TryGetValue(k, out var d)) return d;
            d = null;
            string path = PathOf(f);
            if (File.Exists(path) && CubeBundle.ReadBytes(path, k, CubeBundle.LeafMagic) is { } bytes
                && CubeLeafCodec.ReadSection(bytes, kind.Kind) is { } s)
                d = BoundaryValues.Of(kind.Decode(s));
            disk[k] = d;
            return d;
        }
        long samples = 0, copied = 0, across = 0;
        var changed = new List<CubeKey>();
        foreach (var key in own.Keys.OrderBy(k => k.Y).ThenBy(k => k.X))
        {
            var (n, c, a) = LeafSeam.Stitch(key, own[key], kind.Stride, Present, order, _frameLevel);
            samples += n;
            copied += c;
            across += a;
            if (c > 0) changed.Add(key);
        }
        // the leaves that copied: decoded again, their edges put in, encoded
        Parallel.ForEach(changed, key =>
        {
            List<LeafSection> list;
            lock (sections) list = sections[key];
            int at = list.FindIndex(s => s.Kind == kind.Kind);
            var full = kind.Decode(list[at]);
            own[key].ApplyTo(full);
            var encoded = kind.Encode(full);
            lock (sections) list[at] = encoded;
        });
        return new SeamStats(kind.Kind, samples, copied, across, changed.Count);
    }

    /// <summary>
    /// The region's manifest from what is on disk (every <c>.cubl</c> of <paramref name="dir"/>): each leaf
    /// with the kinds it holds, its entry's bytes and its heights' range; the kinds of the region; its levels.
    /// </summary>
    public static CubeManifest WriteManifest(string dir, int leafLevel, int frameLevel, IEnumerable<string> sources)
    {
        var manifest = new CubeManifest
        {
            LeafLevel = leafLevel,
            Bits = CubeKey.LatticeBits(leafLevel),
            BundleLevel = frameLevel,
            Levels = { new CubeManifestLevel { Level = leafLevel, Bits = CubeKey.LatticeBits(leafLevel), BundleLevel = frameLevel } },
            Kinds = new List<string>(),
        };
        manifest.Sources.AddRange(sources);
        var kinds = new SortedSet<LeafKind>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.cubl").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var fs = File.OpenRead(path);
            foreach (var (key, (offset, length)) in CubeBundle.ReadIndex(fs, CubeBundle.LeafMagic).OrderBy(e => e.Value.Offset))
            {
                var head = new byte[CubeLeafCodec.HeadBytes];
                fs.Position = offset;
                fs.ReadExactly(head);
                int count = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6));
                var entry = new byte[Math.Min(length, CubeLeafCodec.HeadBytes + count * CubeLeafCodec.TableEntryBytes)];
                fs.Position = offset;
                fs.ReadExactly(entry);
                // the table alone: ReadTable checks the payload lengths, so pad to the entry's length
                var padded = new byte[length];
                entry.CopyTo(padded, 0);
                var table = CubeLeafCodec.ReadTable(padded).Table;
                var tile = new CubeManifestTile { Key = key.ToString(), Bytes = length, Kinds = table.Select(t => t.Kind.ToString()).ToList() };
                foreach (var t in table) kinds.Add(t.Kind);
                if (table.FirstOrDefault(t => t.Kind == LeafKind.Heights) is { Length: >= CubeTileCodec.HeaderBytes } h)
                {
                    var hh = new byte[CubeTileCodec.HeaderBytes];
                    fs.Position = offset + h.Offset;
                    fs.ReadExactly(hh);
                    (_, _, tile.Min, tile.Max) = CubeTileCodec.ReadHeader(hh);
                }
                manifest.Tiles.Add(tile);
            }
        }
        manifest.Kinds.AddRange(kinds.Select(k => k.ToString()));
        manifest.Tiles.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        File.WriteAllText(Path.Combine(dir, CubeManifest.FileName), manifest.ToJson());
        return manifest;
    }
}

/// <summary>A lattice kind's values, one 64-bit word a sample (compared and copied as bits): what the seam pass moves.</summary>
public interface ILatticeValues
{
    /// <summary>Samples a side: 2^bits / stride + 1.</summary>
    int Side { get; }

    ulong Get(int a, int b);

    void Set(int a, int b, ulong value);
}

/// <summary>A lattice kind the seam pass knows: how to read its section as values and write them back.</summary>
public abstract class LatticeKind
{
    public abstract LeafKind Kind { get; }

    /// <summary>Leaf lattice vertices between the kind's samples (1, or 2 for water).</summary>
    public abstract int Stride { get; }

    public abstract ILatticeValues Decode(LeafSection s);

    public abstract LeafSection Encode(ILatticeValues values);

    public static readonly LatticeKind Heights = new HeightsKind();
    public static readonly LatticeKind Cover = new CoverKind();
    public static readonly LatticeKind Water = new WaterKind();

    private sealed class HeightsKind : LatticeKind
    {
        public override LeafKind Kind => LeafKind.Heights;
        public override int Stride => 1;

        private sealed class V(CubeTile t) : ILatticeValues
        {
            public CubeTile Tile => t;
            public int Side => t.Side;
            public ulong Get(int a, int b) => BitConverter.SingleToUInt32Bits(t.Heights[b * t.Side + a]);
            public void Set(int a, int b, ulong value) => t.Heights[b * t.Side + a] = BitConverter.UInt32BitsToSingle((uint)value);
        }

        public override ILatticeValues Decode(LeafSection s) => new V(LeafHeights.Decode(s));
        public override LeafSection Encode(ILatticeValues values) => LeafHeights.Encode(((V)values).Tile);
    }

    private sealed class CoverKind : LatticeKind
    {
        public override LeafKind Kind => LeafKind.Cover;
        public override int Stride => 1;

        private sealed class V(byte[] cells, int side) : ILatticeValues
        {
            public byte[] Cells => cells;
            public int Side => side;
            public ulong Get(int a, int b) => cells[b * side + a];
            public void Set(int a, int b, ulong value) => cells[b * side + a] = (byte)value;
        }

        public override ILatticeValues Decode(LeafSection s)
        {
            var cells = LeafCover.Decode(s);
            return new V(cells, (int)Math.Round(Math.Sqrt(cells.Length)));
        }

        public override LeafSection Encode(ILatticeValues values)
        {
            var v = (V)values;
            return LeafCover.Encode(v.Cells, (int)Math.Round(Math.Log2(v.Side - 1)));
        }
    }

    private sealed class WaterKind : LatticeKind
    {
        public override LeafKind Kind => LeafKind.Water;
        public override int Stride => CubeWater.Stride;

        private sealed class V(CubeWater w) : ILatticeValues
        {
            public CubeWater Water => w;
            public int Side => w.Side;
            public ulong Get(int a, int b) => ((ulong)BitConverter.SingleToUInt32Bits(w.Level[b * w.Side + a]) << 8) | w.Fetch[b * w.Side + a];
            public void Set(int a, int b, ulong value)
            {
                w.Level[b * w.Side + a] = BitConverter.UInt32BitsToSingle((uint)(value >> 8));
                w.Fetch[b * w.Side + a] = (byte)value;
            }
        }

        public override ILatticeValues Decode(LeafSection s) => new V(LeafWater.Decode(s));
        public override LeafSection Encode(ILatticeValues values) => LeafWater.Encode(((V)values).Water);
    }
}

/// <summary>A lattice's outer samples alone (four rows of <see cref="Side"/>), so a frame's seam pass never holds whole lattices.</summary>
public sealed class BoundaryValues : ILatticeValues
{
    private readonly ulong[] _south, _north, _west, _east;

    public int Side { get; }

    private BoundaryValues(int side)
    {
        Side = side;
        _south = new ulong[side];
        _north = new ulong[side];
        _west = new ulong[side];
        _east = new ulong[side];
    }

    public static BoundaryValues Of(ILatticeValues full)
    {
        int s = full.Side;
        var b = new BoundaryValues(s);
        for (int k = 0; k < s; k++)
        {
            b._south[k] = full.Get(k, 0);
            b._north[k] = full.Get(k, s - 1);
            b._west[k] = full.Get(0, k);
            b._east[k] = full.Get(s - 1, k);
        }
        return b;
    }

    public ulong Get(int a, int b) =>
        b == 0 ? _south[a] : b == Side - 1 ? _north[a] : a == 0 ? _west[b] : a == Side - 1 ? _east[b]
        : throw new ArgumentOutOfRangeException(nameof(a), $"({a}, {b}) is not on the boundary");

    public void Set(int a, int b, ulong value)
    {
        bool any = false;
        if (b == 0) { _south[a] = value; any = true; }
        if (b == Side - 1) { _north[a] = value; any = true; }
        if (a == 0) { _west[b] = value; any = true; }
        if (a == Side - 1) { _east[b] = value; any = true; }
        if (!any) throw new ArgumentOutOfRangeException(nameof(a), $"({a}, {b}) is not on the boundary");
    }

    /// <summary>Puts every boundary sample into the full lattice.</summary>
    public void ApplyTo(ILatticeValues full)
    {
        for (int k = 0; k < Side; k++)
        {
            full.Set(k, 0, _south[k]);
            full.Set(k, Side - 1, _north[k]);
            full.Set(0, k, _west[k]);
            full.Set(Side - 1, k, _east[k]);
        }
    }
}

/// <summary>The owner rule of shared lattice vertices and the copy (#1507; <see cref="LeafWriter"/>).</summary>
public static class LeafSeam
{
    /// <summary>Cells of one level in the frames' order: face, y, x.</summary>
    public static int CompareCells(CubeKey a, CubeKey b) =>
        a.Face != b.Face ? a.Face.CompareTo(b.Face) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);

    /// <summary>The order frames are built in, then leaves within one: face, the frame's y and x, the leaf's y and x. The first holder of a vertex owns it.</summary>
    public static Comparison<CubeKey> FrameOrder(int frameLevel) => (a, b) =>
    {
        if (a.Face != b.Face) return a.Face.CompareTo(b.Face);
        int sa = a.Level - frameLevel, sb = b.Level - frameLevel;
        int c = (a.Y >> sa).CompareTo(b.Y >> sb);
        if (c != 0) return c;
        c = (a.X >> sa).CompareTo(b.X >> sb);
        if (c != 0) return c;
        c = a.Y.CompareTo(b.Y);
        return c != 0 ? c : a.X.CompareTo(b.X);
    };

    /// <summary>
    /// The leaves holding a boundary sample (a, b) of <paramref name="leaf"/> (besides it), with the sample's
    /// place in each: across the edges it lies on, and the diagonal at a corner (none past a cube corner).
    /// </summary>
    public static IEnumerable<(CubeKey Key, int A, int B)> Holders(CubeKey leaf, int stride, int a, int b)
    {
        int bits = CubeKey.LatticeBits(leaf.Level), cells = 1 << bits, i = a * stride, j = b * stride;
        int dx = i == 0 ? -1 : i == cells ? 1 : 0, dy = j == 0 ? -1 : j == cells ? 1 : 0;
        if (dx == 0 && dy == 0) yield break;
        int lv = leaf.Level + bits;
        long gi = ((long)leaf.X << bits) + i, gj = ((long)leaf.Y << bits) + j;
        var offsets = dx == 0 ? new[] { (0, dy) } : dy == 0 ? new[] { (dx, 0) } : new[] { (dx, 0), (0, dy), (dx, dy) };
        foreach (var (ox, oy) in offsets)
        {
            if (leaf.Neighbour(ox, oy) is not { } c) continue;
            (long, long)? at = c.Face == leaf.Face ? (gi, gj) : CubeSphere.LatticeOn(leaf.Face, lv, gi, gj, c.Face);
            if (at is not { } p) continue;
            long li = p.Item1 - ((long)c.X << bits), lj = p.Item2 - ((long)c.Y << bits);
            if (li < 0 || lj < 0 || li > cells || lj > cells) continue;
            yield return (c, (int)(li / stride), (int)(lj / stride));
        }
    }

    /// <summary>
    /// Copies into <paramref name="values"/> (a leaf's boundary) each shared sample's owner's value. Returns
    /// the shared samples looked at, those copied, and how many of those came from another frame.
    /// </summary>
    public static (long Samples, long Copied, long Across) Stitch(CubeKey leaf, ILatticeValues values, int stride,
        Func<CubeKey, ILatticeValues?> present, Comparison<CubeKey> order, int frameLevel)
    {
        int s = values.Side;
        long samples = 0, copied = 0, across = 0;
        var frame = leaf.AncestorAt(frameLevel);
        for (int b = 0; b < s; b++)
            for (int a = 0; a < s; a++)
            {
                if (a != 0 && b != 0 && a != s - 1 && b != s - 1) { a = s - 2; continue; }
                CubeKey owner = leaf;
                int oa = a, ob = b;
                ILatticeValues? ov = null;
                bool shared = false;
                foreach (var (k, ka, kb) in Holders(leaf, stride, a, b))
                {
                    shared = true;
                    if (order(k, owner) >= 0) continue;
                    if (present(k) is not { } kv) continue;
                    (owner, oa, ob, ov) = (k, ka, kb, kv);
                }
                if (!shared) continue;
                samples++;
                if (ov == null) continue;
                ulong v = ov.Get(oa, ob);
                if (v == values.Get(a, b)) continue;
                values.Set(a, b, v);
                copied++;
                if (owner.AncestorAt(frameLevel) != frame) across++;
            }
        return (samples, copied, across);
    }
}
