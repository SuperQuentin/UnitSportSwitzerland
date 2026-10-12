using System.IO.Compression;
using System.Text;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Minimal OpenStreetMap PBF reader: node positions, tagged ways, the tags of chosen nodes, and
/// chosen relations with their members (no metadata). The format is a length-prefixed sequence of
/// zlib blobs holding protobuf blocks (https://wiki.openstreetmap.org/wiki/PBF_Format); decoding
/// the handful of fields we need by hand is smaller than a package, needs no GDAL, and decodes
/// blocks in parallel. OsmSharp 6.2 was measured on the same Switzerland extract: 238 s
/// single-threaded and 33 transitive packages (protobuf-net 2.3 plus netstandard1.x shims),
/// against this file's parallel decode.
/// </summary>
public static class PbfReader
{
    public sealed record Way(long Id, long[] Refs, Dictionary<string, string> Tags);

    /// <summary>A kept tagged node (signals, bike boxes); its position is in <see cref="Data.Nodes"/> too.</summary>
    public sealed record Node(long Id, double Lat, double Lon, Dictionary<string, string> Tags);

    public enum MemberType : byte { Node = 0, Way = 1, Relation = 2 }

    public readonly record struct Member(MemberType Type, long Ref, string Role);

    public sealed record Relation(long Id, Member[] Members, Dictionary<string, string> Tags);

    /// <summary>
    /// What to keep. <see cref="KeepNode"/> filters positions (lat, lon in degrees); each Keep*
    /// predicate sees an element's tags, already narrowed to its *Tags key set, and decides. A
    /// null node or relation predicate keeps none of them. A tagged node must also pass KeepNode.
    /// </summary>
    public sealed class Filter
    {
        public required Func<double, double, bool> KeepNode { get; init; }
        public required Func<Dictionary<string, string>, bool> KeepWay { get; init; }
        public required IReadOnlySet<string> WayTags { get; init; }
        /// <summary>Also keeps these ways whatever their tags (the untagged outlines of a multipolygon relation).</summary>
        public Func<long, bool>? KeepWayId { get; init; }
        public Func<Dictionary<string, string>, bool>? KeepTaggedNode { get; init; }
        public IReadOnlySet<string> NodeTags { get; init; } = new HashSet<string>();
        public Func<Dictionary<string, string>, bool>? KeepRelation { get; init; }
        public IReadOnlySet<string> RelationTags { get; init; } = new HashSet<string>();
    }

    public sealed record Data(Dictionary<long, (double Lat, double Lon)> Nodes, List<Way> Ways,
        List<Node> TaggedNodes, List<Relation> Relations);

    /// <summary>Reads the whole file. Result order is the file's order, so it is deterministic.</summary>
    public static Data Read(string path, int jobs, Filter filter)
    {
        var data = new Data(new Dictionary<long, (double, double)>(), new List<Way>(), new List<Node>(), new List<Relation>());
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var batch = new List<byte[]>();
        while (true)
        {
            var blob = NextDataBlob(fs);
            if (blob != null) batch.Add(blob);
            if (batch.Count < jobs * 8 && blob != null) continue; // bounded: each inflated block is ~1 MB

            var results = new Block[batch.Count];
            Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs },
                i => results[i] = DecodeBlock(Inflate(batch[i]), filter));
            foreach (var b in results)
            {
                foreach (var (id, lat, lon) in b.Nodes) data.Nodes[id] = (lat, lon);
                data.Ways.AddRange(b.Ways);
                data.TaggedNodes.AddRange(b.Tagged);
                data.Relations.AddRange(b.Relations);
            }
            batch.Clear();
            if (blob == null) break;
        }
        return data;
    }

    private sealed record Block(List<(long, double, double)> Nodes, List<Way> Ways, List<Node> Tagged, List<Relation> Relations);

    /// <summary>The next OSMData blob, skipping the header block; null at end of file.</summary>
    private static byte[]? NextDataBlob(Stream s)
    {
        var len = new byte[4];
        while (true)
        {
            if (s.ReadAtLeast(len, 4, throwOnEndOfStream: false) < 4) return null;
            var header = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(len)];
            s.ReadExactly(header);
            string type = "";
            int size = 0;
            var r = new Pb(header);
            while (r.Next(out int f, out int w))
                if (f == 1) type = Encoding.UTF8.GetString(r.Bytes());
                else if (f == 3) size = (int)r.Varint();
                else r.Skip(w);
            var blob = new byte[size];
            s.ReadExactly(blob);
            if (type == "OSMData") return blob;
        }
    }

    private static byte[] Inflate(byte[] blob)
    {
        var r = new Pb(blob);
        int rawSize = 0;
        byte[]? raw = null, zlib = null;
        while (r.Next(out int f, out int w))
            switch (f)
            {
                case 1: raw = r.Bytes().ToArray(); break;
                case 2: rawSize = (int)r.Varint(); break;
                case 3: zlib = r.Bytes().ToArray(); break;
                default:
                    if (f is 4 or 5 or 6 or 7) throw new NotSupportedException($"PBF blob compression field {f} (only zlib)");
                    r.Skip(w);
                    break;
            }
        if (raw != null) return raw;
        if (zlib == null) return [];
        var output = new byte[rawSize];
        using var z = new ZLibStream(new MemoryStream(zlib), CompressionMode.Decompress);
        z.ReadExactly(output);
        return output;
    }

    private static Block DecodeBlock(byte[] block, Filter filter)
    {
        var strings = new List<string>();
        var groups = new List<(int Start, int Length)>();
        long granularity = 100, latOffset = 0, lonOffset = 0;
        var r = new Pb(block);
        while (r.Next(out int f, out int w))
            switch (f)
            {
                case 1:
                    var st = new Pb(r.Bytes());
                    while (st.Next(out int sf, out int sw))
                        if (sf == 1) strings.Add(Encoding.UTF8.GetString(st.Bytes()));
                        else st.Skip(sw);
                    break;
                case 2: groups.Add(r.Range()); break;
                case 17: granularity = (long)r.Varint(); break;
                case 19: latOffset = (long)r.Varint(); break;
                case 20: lonOffset = (long)r.Varint(); break;
                default: r.Skip(w); break;
            }

        // which strings of this block are node tag keys we keep: almost every node carries none of
        // them, so a node's key/value pairs only become a dictionary when one shows up
        bool wantNodeTags = filter.KeepTaggedNode != null;
        var nodeKey = new bool[strings.Count];
        if (wantNodeTags)
            for (int i = 0; i < strings.Count; i++) nodeKey[i] = filter.NodeTags.Contains(strings[i]);

        var result = new Block(new(), new(), new(), new());
        Dictionary<string, string> Tags(List<long> keys, List<long> vals, IReadOnlySet<string> wanted)
        {
            var tags = new Dictionary<string, string>();
            for (int i = 0; i < keys.Count && i < vals.Count; i++)
                if (wanted.Contains(strings[(int)keys[i]])) tags[strings[(int)keys[i]]] = strings[(int)vals[i]];
            return tags;
        }
        void AddNode(long id, long lat, long lon, List<long>? keys, List<long>? vals)
        {
            double la = 1e-9 * (latOffset + granularity * lat), lo = 1e-9 * (lonOffset + granularity * lon);
            if (!filter.KeepNode(la, lo)) return;
            result.Nodes.Add((id, la, lo));
            if (keys == null || vals == null) return;
            var tags = Tags(keys, vals, filter.NodeTags);
            if (tags.Count > 0 && filter.KeepTaggedNode!(tags)) result.Tagged.Add(new Node(id, la, lo, tags));
        }

        foreach (var (start, length) in groups)
        {
            var g = new Pb(block.AsSpan(start, length));
            while (g.Next(out int f, out int w))
                switch (f)
                {
                    case 1: // plain Node
                    {
                        var n = new Pb(g.Bytes());
                        long id = 0, lat = 0, lon = 0;
                        List<long> keys = [], vals = [];
                        while (n.Next(out int nf, out int nw))
                            if (nf == 1) id = Pb.Zig(n.Varint());
                            else if (nf == 2 && wantNodeTags) n.PackedUnsigned(keys);
                            else if (nf == 3 && wantNodeTags) n.PackedUnsigned(vals);
                            else if (nf == 8) lat = Pb.Zig(n.Varint());
                            else if (nf == 9) lon = Pb.Zig(n.Varint());
                            else n.Skip(nw);
                        bool any = keys.Any(k => nodeKey[(int)k]);
                        AddNode(id, lat, lon, any ? keys : null, any ? vals : null);
                        break;
                    }
                    case 2: // DenseNodes: delta-coded parallel arrays
                    {
                        var d = new Pb(g.Bytes());
                        List<long> ids = [], lats = [], lons = [], kv = [];
                        while (d.Next(out int df, out int dw))
                            if (df == 1) d.PackedSigned(ids);
                            else if (df == 8) d.PackedSigned(lats);
                            else if (df == 9) d.PackedSigned(lons);
                            else if (df == 10 && wantNodeTags) d.PackedUnsigned(kv); // keys_vals: k, v, k, v, ..., 0 per node
                            else d.Skip(dw);
                        long id = 0, lat = 0, lon = 0;
                        int j = 0;
                        List<long> keys = [], vals = [];
                        for (int i = 0; i < ids.Count; i++)
                        {
                            id += ids[i]; lat += lats[i]; lon += lons[i];
                            keys.Clear(); vals.Clear();
                            bool any = false;
                            for (; j + 1 < kv.Count && kv[j] != 0; j += 2)
                            {
                                any |= nodeKey[(int)kv[j]];
                                keys.Add(kv[j]); vals.Add(kv[j + 1]);
                            }
                            j++; // the 0 that ends this node's pairs
                            AddNode(id, lat, lon, any ? keys : null, any ? vals : null);
                        }
                        break;
                    }
                    case 3: // Way
                    {
                        var wr = new Pb(g.Bytes());
                        long id = 0;
                        List<long> keys = [], vals = [], refs = [];
                        while (wr.Next(out int wf, out int ww))
                            if (wf == 1) id = (long)wr.Varint();
                            else if (wf == 2) wr.PackedUnsigned(keys);
                            else if (wf == 3) wr.PackedUnsigned(vals);
                            else if (wf == 8) wr.PackedSigned(refs);
                            else wr.Skip(ww);
                        var tags = Tags(keys, vals, filter.WayTags);
                        if (!filter.KeepWay(tags) && filter.KeepWayId?.Invoke(id) != true) break;
                        var nd = new long[refs.Count];
                        long acc = 0;
                        for (int i = 0; i < refs.Count; i++) nd[i] = acc += refs[i];
                        result.Ways.Add(new Way(id, nd, tags));
                        break;
                    }
                    case 4 when filter.KeepRelation != null: // Relation: members as parallel arrays, ids delta-coded
                    {
                        var rr = new Pb(g.Bytes());
                        long id = 0;
                        List<long> keys = [], vals = [], roles = [], memids = [], types = [];
                        while (rr.Next(out int rf, out int rw))
                            if (rf == 1) id = (long)rr.Varint();
                            else if (rf == 2) rr.PackedUnsigned(keys);
                            else if (rf == 3) rr.PackedUnsigned(vals);
                            else if (rf == 8) rr.PackedUnsigned(roles);
                            else if (rf == 9) rr.PackedSigned(memids);
                            else if (rf == 10) rr.PackedUnsigned(types);
                            else rr.Skip(rw);
                        var tags = Tags(keys, vals, filter.RelationTags);
                        if (!filter.KeepRelation(tags)) break;
                        var members = new Member[Math.Min(memids.Count, Math.Min(roles.Count, types.Count))];
                        long acc = 0;
                        for (int i = 0; i < members.Length; i++)
                            members[i] = new Member((MemberType)types[i], acc += memids[i], strings[(int)roles[i]]);
                        result.Relations.Add(new Relation(id, members, tags));
                        break;
                    }
                    default: g.Skip(w); break;
                }
        }
        return result;
    }

    /// <summary>Protobuf wire-format cursor over a span.</summary>
    private ref struct Pb
    {
        private readonly ReadOnlySpan<byte> _b;
        private int _p;
        public Pb(ReadOnlySpan<byte> b) { _b = b; _p = 0; }

        public bool Next(out int field, out int wire)
        {
            if (_p >= _b.Length) { field = wire = 0; return false; }
            ulong key = Varint();
            field = (int)(key >> 3);
            wire = (int)(key & 7);
            return true;
        }

        public ulong Varint()
        {
            ulong v = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte x = _b[_p++];
                v |= (ulong)(x & 0x7F) << shift;
                if (x < 0x80) return v;
            }
        }

        public static long Zig(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

        public ReadOnlySpan<byte> Bytes()
        {
            int n = (int)Varint();
            var s = _b.Slice(_p, n);
            _p += n;
            return s;
        }

        /// <summary>The next length-delimited field as (offset, length) into the whole buffer.</summary>
        public (int, int) Range()
        {
            int n = (int)Varint();
            var r = (_p, n);
            _p += n;
            return r;
        }

        public void PackedSigned(List<long> into)
        {
            var p = new Pb(Bytes());
            while (p._p < p._b.Length) into.Add(Zig(p.Varint()));
        }

        public void PackedUnsigned(List<long> into)
        {
            var p = new Pb(Bytes());
            while (p._p < p._b.Length) into.Add((long)p.Varint());
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: _p += 8; break;
                case 2: { int n = (int)Varint(); _p += n; break; } // not "_p += Varint()": that reads _p before the varint moves it
                case 5: _p += 4; break;
                default: throw new InvalidDataException($"protobuf wire type {wire}");
            }
        }
    }
}
