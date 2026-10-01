using System.IO.Compression;
using System.Text;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Minimal OpenStreetMap PBF reader: node positions and tagged ways, nothing else (no relations,
/// no metadata). The format is a length-prefixed sequence of zlib blobs holding protobuf blocks
/// (https://wiki.openstreetmap.org/wiki/PBF_Format); decoding the handful of fields we need by
/// hand is smaller than a package, needs no GDAL, and decodes blocks in parallel. OsmSharp 6.2
/// was measured on the same Switzerland extract: 238 s single-threaded and 33 transitive
/// packages (protobuf-net 2.3 plus netstandard1.x shims), against this file's parallel decode.
/// </summary>
public static class PbfReader
{
    public sealed record Way(long Id, long[] Refs, Dictionary<string, string> Tags);

    /// <summary>
    /// Reads the whole file. <paramref name="keepNode"/> filters positions (lat, lon in degrees);
    /// <paramref name="keepWay"/> sees a way's tags and decides; <paramref name="tagKeys"/> is the
    /// only tags a kept way retains. Result order is the file's order, so it is deterministic.
    /// </summary>
    public static (Dictionary<long, (double Lat, double Lon)> Nodes, List<Way> Ways) Read(string path, int jobs,
        Func<double, double, bool> keepNode, Func<Dictionary<string, string>, bool> keepWay, IReadOnlySet<string> tagKeys)
    {
        var nodes = new Dictionary<long, (double, double)>();
        var ways = new List<Way>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var batch = new List<byte[]>();
        while (true)
        {
            var blob = NextDataBlob(fs);
            if (blob != null) batch.Add(blob);
            if (batch.Count < jobs * 8 && blob != null) continue; // bounded: each inflated block is ~1 MB

            var results = new (List<(long, double, double)> N, List<Way> W)[batch.Count];
            Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs },
                i => results[i] = DecodeBlock(Inflate(batch[i]), keepNode, keepWay, tagKeys));
            foreach (var (n, w) in results)
            {
                foreach (var (id, lat, lon) in n) nodes[id] = (lat, lon);
                ways.AddRange(w);
            }
            batch.Clear();
            if (blob == null) break;
        }
        return (nodes, ways);
    }

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

    private static (List<(long, double, double)>, List<Way>) DecodeBlock(byte[] block,
        Func<double, double, bool> keepNode, Func<Dictionary<string, string>, bool> keepWay, IReadOnlySet<string> tagKeys)
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

        var nodes = new List<(long, double, double)>();
        var ways = new List<Way>();
        void AddNode(long id, long lat, long lon)
        {
            double la = 1e-9 * (latOffset + granularity * lat), lo = 1e-9 * (lonOffset + granularity * lon);
            if (keepNode(la, lo)) nodes.Add((id, la, lo));
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
                        while (n.Next(out int nf, out int nw))
                            if (nf == 1) id = Pb.Zig(n.Varint());
                            else if (nf == 8) lat = Pb.Zig(n.Varint());
                            else if (nf == 9) lon = Pb.Zig(n.Varint());
                            else n.Skip(nw);
                        AddNode(id, lat, lon);
                        break;
                    }
                    case 2: // DenseNodes: delta-coded parallel arrays
                    {
                        var d = new Pb(g.Bytes());
                        List<long> ids = [], lats = [], lons = [];
                        while (d.Next(out int df, out int dw))
                            if (df == 1) d.PackedSigned(ids);
                            else if (df == 8) d.PackedSigned(lats);
                            else if (df == 9) d.PackedSigned(lons);
                            else d.Skip(dw);
                        long id = 0, lat = 0, lon = 0;
                        for (int i = 0; i < ids.Count; i++)
                        {
                            id += ids[i]; lat += lats[i]; lon += lons[i];
                            AddNode(id, lat, lon);
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
                        var tags = new Dictionary<string, string>();
                        for (int i = 0; i < keys.Count; i++)
                            if (tagKeys.Contains(strings[(int)keys[i]])) tags[strings[(int)keys[i]]] = strings[(int)vals[i]];
                        if (!keepWay(tags)) break;
                        var nd = new long[refs.Count];
                        long acc = 0;
                        for (int i = 0; i < refs.Count; i++) nd[i] = acc += refs[i];
                        ways.Add(new Way(id, nd, tags));
                        break;
                    }
                    default: g.Skip(w); break;
                }
        }
        return (nodes, ways);
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
