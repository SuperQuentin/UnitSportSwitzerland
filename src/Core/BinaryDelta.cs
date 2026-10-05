namespace UnitSport.Core;

/// <summary>
/// A binary diff of one file (#532), rsync style: the old file's blocks are indexed by a rolling
/// hash, the new file is walked byte by byte and written as COPY (a run of the old file) or
/// INSERT (new bytes). Matches extend past their block both ways, and after a change the walk
/// first tries to resume on the same diagonal, so a few patched bytes in a 100 MB executable cost
/// a few bytes, and content shifted by an insertion (a .pck) is still found. No Godot: the release
/// tool writes it, the game applies it, tier-0 tests cover both.
/// </summary>
public static class BinaryDelta
{
    public const int Block = 2048;
    /// <summary>Bytes that must agree to resume on the last copy's diagonal.</summary>
    private const int Resume = 16;
    private const byte OpCopy = 0, OpInsert = 1;

    /// <summary>The patch that turns <paramref name="old"/> into <paramref name="nu"/>.</summary>
    public static byte[] Create(byte[] old, byte[] nu)
    {
        var index = new Dictionary<uint, int>();
        for (int blk = 0; (long)(blk + 1) * Block <= old.Length; blk++)
            index.TryAdd(Weak(old, blk * Block, out _, out _), blk);

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write7BitEncodedInt64(nu.Length);

        int pos = 0, lit = 0;
        long diag = 0;                       // old offset - new offset of the last copy; files start aligned
        bool hashed = false;
        uint a = 0, b = 0;
        while (pos < nu.Length)
        {
            long at = -1;
            long d = pos + diag;
            if (d >= 0 && d + Resume <= old.Length && pos + Resume <= nu.Length
                && old.AsSpan((int)d, Resume).SequenceEqual(nu.AsSpan(pos, Resume)))
                at = d;
            if (at < 0 && pos + Block <= nu.Length && index.Count > 0)
            {
                if (!hashed) { Weak(nu, pos, out a, out b); hashed = true; }
                if (index.TryGetValue(a | (b << 16), out int blk)
                    && old.AsSpan(blk * Block, Block).SequenceEqual(nu.AsSpan(pos, Block)))
                    at = (long)blk * Block;
            }

            if (at >= 0)
            {
                int o = (int)at;
                while (pos > lit && o > 0 && old[o - 1] == nu[pos - 1]) { pos--; o--; }
                Insert(w, nu, lit, pos - lit);
                int len = old.AsSpan(o).CommonPrefixLength(nu.AsSpan(pos));
                w.Write(OpCopy);
                w.Write7BitEncodedInt64(o);
                w.Write7BitEncodedInt64(len);
                diag = (long)o - pos;
                pos += len;
                lit = pos;
                hashed = false;
                continue;
            }

            if (hashed && pos + Block < nu.Length)
            {
                // roll the window one byte: drop nu[pos], take nu[pos + Block]
                uint outB = nu[pos], inB = nu[pos + Block];
                a = (a - outB + inB) & 0xFFFF;
                b = (b - Block * outB + a) & 0xFFFF;
            }
            else hashed = false;
            pos++;
        }
        Insert(w, nu, lit, nu.Length - lit);
        w.Flush();
        return ms.ToArray();
    }

    private static void Insert(BinaryWriter w, byte[] data, int from, int len)
    {
        if (len <= 0) return;
        w.Write(OpInsert);
        w.Write7BitEncodedInt64(len);
        w.Write(data, from, len);
    }

    /// <summary>The rolling hash of <see cref="Block"/> bytes at <paramref name="at"/>.</summary>
    private static uint Weak(byte[] data, int at, out uint a, out uint b)
    {
        a = 0; b = 0;
        for (int i = 0; i < Block; i++)
        {
            a += data[at + i];
            b += (uint)(Block - i) * data[at + i];
        }
        a &= 0xFFFF; b &= 0xFFFF;
        return a | (b << 16);
    }

    /// <summary>
    /// Writes the new file: <paramref name="old"/> must be seekable. Throws
    /// <see cref="InvalidDataException"/> on a patch that reads outside the old file or does not
    /// add up to the length it announced.
    /// </summary>
    public static void Apply(Stream old, Stream patch, Stream output)
    {
        var r = new BinaryReader(patch);
        long total = r.Read7BitEncodedInt64(), written = 0;
        if (total < 0) throw new InvalidDataException("negative length");
        var buf = new byte[1 << 16];
        while (written < total)
        {
            byte op = r.ReadByte();
            if (op == OpCopy)
            {
                long off = r.Read7BitEncodedInt64(), len = r.Read7BitEncodedInt64();
                if (off < 0 || len <= 0 || off + len > old.Length || written + len > total)
                    throw new InvalidDataException($"copy {off}+{len} outside the old file");
                old.Position = off;
                Pump(old, output, len, buf);
                written += len;
            }
            else if (op == OpInsert)
            {
                long len = r.Read7BitEncodedInt64();
                if (len <= 0 || written + len > total) throw new InvalidDataException("insert past the end");
                Pump(patch, output, len, buf);
                written += len;
            }
            else throw new InvalidDataException($"unknown op {op}");
        }
    }

    private static void Pump(Stream from, Stream to, long len, byte[] buf)
    {
        while (len > 0)
        {
            int n = from.Read(buf, 0, (int)Math.Min(buf.Length, len));
            if (n <= 0) throw new InvalidDataException("truncated");
            to.Write(buf, 0, n);
            len -= n;
        }
    }
}
