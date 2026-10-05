using System.IO.Compression;
using System.Security.Cryptography;

namespace UnitSport.Core;

/// <summary>
/// A delta update between two released builds of one platform (#532): every file added, patched
/// (<see cref="BinaryDelta"/>) or deleted, with the SHA-256 of the file it expects and of the file it
/// makes, Brotli-compressed. <c>tools/DeltaGen</c> writes it from two unpacked releases; the game
/// stages it with <see cref="Staging"/> into <c>.update/</c> beside the install, checks every hash,
/// and <see cref="UpdateScript"/> swaps the files in once the game has quit. No Godot.
/// </summary>
public static class UpdatePackage
{
    private const string Magic = "USDELTA1";
    private const byte KindAdd = 1, KindPatch = 2, KindDelete = 3;
    public const string StagingDir = ".update";

    public sealed record Stats(int Added, int Patched, int Deleted, int Unchanged);

    private sealed record Entry(byte Kind, string Path, bool Exec, byte[] Source, byte[] Target, byte[]? Patch, string? AddFrom);

    /// <summary>Writes the delta that turns <paramref name="oldDir"/> into <paramref name="newDir"/>.
    /// <paramref name="exec"/> lists the paths that must be executable when added on Linux / macOS.</summary>
    public static Stats Create(string oldDir, string newDir, string from, string to, Stream output,
        IReadOnlySet<string>? exec = null, Action<string>? log = null)
    {
        var olds = Files(oldDir);
        var news = Files(newDir);
        var entries = new List<Entry>();
        int unchanged = 0;
        foreach (string p in news)
        {
            string nf = Path.Combine(newDir, p);
            byte[] target = Hash(nf);
            bool x = exec?.Contains(p) == true;
            if (olds.Contains(p))
            {
                string of = Path.Combine(oldDir, p);
                byte[] source = Hash(of);
                if (source.AsSpan().SequenceEqual(target)) { unchanged++; continue; }
                byte[] nb = File.ReadAllBytes(nf);
                byte[] patch = BinaryDelta.Create(File.ReadAllBytes(of), nb);
                log?.Invoke($"patch {p}: {nb.Length} -> {patch.Length} bytes");
                if (patch.Length < nb.Length * 0.9) { entries.Add(new Entry(KindPatch, p, x, source, target, patch, null)); continue; }
            }
            else log?.Invoke($"add {p}");
            entries.Add(new Entry(KindAdd, p, x, Array.Empty<byte>(), target, null, nf));
        }
        foreach (string p in olds)
            if (!news.Contains(p))
            {
                log?.Invoke($"delete {p}");
                entries.Add(new Entry(KindDelete, p, false, Array.Empty<byte>(), Array.Empty<byte>(), null, null));
            }

        using (var z = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new BinaryWriter(z))
        {
            w.Write(Magic); w.Write(from); w.Write(to);
            w.Write7BitEncodedInt(entries.Count);
            foreach (var e in entries)
            {
                w.Write(e.Kind); w.Write(e.Path); w.Write(e.Exec);
                if (e.Kind == KindDelete) continue;
                if (e.Kind == KindPatch) w.Write(e.Source);
                w.Write(e.Target);
                if (e.Patch != null)
                {
                    w.Write7BitEncodedInt64(e.Patch.Length);
                    w.Write(e.Patch);
                }
                else
                {
                    using var f = File.OpenRead(e.AddFrom!);
                    w.Write7BitEncodedInt64(f.Length);
                    w.Flush();
                    f.CopyTo(z);
                }
            }
        }
        return new Stats(entries.Count(e => e.Kind == KindAdd), entries.Count(e => e.Kind == KindPatch),
            entries.Count(e => e.Kind == KindDelete), unchanged);
    }

    /// <summary>Every file under <paramref name="dir"/> as a '/' path relative to it, the staging folder left out.</summary>
    public static SortedSet<string> Files(string dir)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        string full = Path.GetFullPath(dir);
        foreach (string f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(full, f).Replace('\\', '/');
            if (rel != StagingDir && !rel.StartsWith(StagingDir + "/", StringComparison.Ordinal)) set.Add(rel);
        }
        return set;
    }

    public static byte[] Hash(string file)
    {
        using var f = File.OpenRead(file);
        return SHA256.HashData(f);
    }

    /// <summary>
    /// A '/' path from a delta, refused unless it stays inside the install: no rooted path, drive,
    /// backslash, empty / "." / ".." segment, or the staging folder itself.
    /// </summary>
    public static string SafePath(string p)
    {
        if (p.Length is 0 or > 1024 || p.IndexOfAny(new[] { '\\', ':', '\0', '\n', '\r' }) >= 0 || p[0] == '/')
            throw new InvalidDataException($"unsafe path '{p}'");
        foreach (string seg in p.Split('/'))
            if (seg is "" or "." or "..") throw new InvalidDataException($"unsafe path '{p}'");
        if (p == StagingDir || p.StartsWith(StagingDir + "/", StringComparison.Ordinal))
            throw new InvalidDataException($"unsafe path '{p}'");
        return p;
    }

    /// <summary>
    /// Deltas applied one after the other over an install, without touching it: every new file goes
    /// to <c>.update/files/</c>, deletions to <c>.update/delete.txt</c>. A later delta reads what an
    /// earlier one staged. Any hash that does not match throws; the caller then drops the folder.
    /// </summary>
    public sealed class Staging
    {
        private readonly string _root, _files;
        private readonly HashSet<string> _staged = new(StringComparer.Ordinal);
        private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
        private long _done;

        /// <summary>Bytes written so far, for a progress bar on another thread.</summary>
        public long Done => Interlocked.Read(ref _done);
        public string Dir { get; }

        public Staging(string root)
        {
            _root = Path.GetFullPath(root);
            Dir = Path.Combine(_root, StagingDir);
            _files = Path.Combine(Dir, "files");
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(_files);
        }

        private string Current(string p) =>
            _staged.Contains(p) ? Path.Combine(_files, p)
            : _deleted.Contains(p) ? throw new InvalidDataException($"{p} was deleted by an earlier delta")
            : Path.Combine(_root, p);

        /// <summary>Stages one delta; it must go from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public void Apply(Stream delta, string from, string to)
        {
            using var z = new BrotliStream(delta, CompressionMode.Decompress, leaveOpen: true);
            using var r = new BinaryReader(z);
            if (r.ReadString() != Magic) throw new InvalidDataException("not a UnitSport delta");
            string f = r.ReadString(), t = r.ReadString();
            if (f != from || t != to) throw new InvalidDataException($"delta is {f} -> {t}, expected {from} -> {to}");
            int count = r.Read7BitEncodedInt();
            if (count is < 0 or > 100_000) throw new InvalidDataException($"{count} entries");
            for (int i = 0; i < count; i++)
            {
                byte kind = r.ReadByte();
                string p = SafePath(r.ReadString());
                bool exec = r.ReadBoolean();
                string dst = Path.Combine(_files, p);
                if (kind == KindDelete)
                {
                    if (_staged.Remove(p)) File.Delete(dst);
                    _deleted.Add(p);
                    continue;
                }
                if (kind != KindAdd && kind != KindPatch) throw new InvalidDataException($"unknown entry kind {kind}");
                byte[]? source = kind == KindPatch ? r.ReadBytes(32) : null;
                byte[] target = r.ReadBytes(32);
                long len = r.Read7BitEncodedInt64();
                if (len < 0) throw new InvalidDataException("negative length");
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                string tmp = dst + ".tmp";
                string? oldFile = null;
                if (kind == KindPatch)
                {
                    oldFile = Current(p);
                    if (!File.Exists(oldFile) || !Hash(oldFile).AsSpan().SequenceEqual(source))
                        throw new InvalidDataException($"{p} is not the file this delta expects (modified install?)");
                    byte[] patch = r.ReadBytes(checked((int)len));
                    if (patch.Length != len) throw new InvalidDataException("truncated delta");
                    using var old = File.OpenRead(oldFile);
                    using var o = File.Create(tmp);
                    BinaryDelta.Apply(old, new MemoryStream(patch), o);
                }
                else
                {
                    using var o = File.Create(tmp);
                    var buf = new byte[1 << 16];
                    for (long left = len; left > 0;)
                    {
                        int n = z.Read(buf, 0, (int)Math.Min(buf.Length, left));
                        if (n <= 0) throw new InvalidDataException("truncated delta");
                        o.Write(buf, 0, n);
                        left -= n;
                    }
                }
                if (!Hash(tmp).AsSpan().SequenceEqual(target)) throw new InvalidDataException($"{p}: wrong result hash");
                if (!OperatingSystem.IsWindows())
                {
                    var mode = oldFile != null ? File.GetUnixFileMode(oldFile) : (UnixFileMode)0b110_100_100;
                    if (exec) mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                    File.SetUnixFileMode(tmp, mode);
                }
                File.Move(tmp, dst, overwrite: true);
                _staged.Add(p);
                _deleted.Remove(p);
                Interlocked.Add(ref _done, new FileInfo(dst).Length);
            }
        }

        /// <summary>Writes the deletion list; the staged folder is then ready for <see cref="UpdateScript"/>.</summary>
        public void Finish() => File.WriteAllLines(Path.Combine(Dir, "delete.txt"), _deleted.Order(StringComparer.Ordinal));
    }
}
