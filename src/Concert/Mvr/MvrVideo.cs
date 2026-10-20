using System.Security.Cryptography;
using System.Xml.Linq;
using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>What an imported screen plays (#975): an MVR VideoScreen's first <c>Sources/Source</c>, by its <c>type</c>.</summary>
public enum VideoSource
{
    /// <summary>No source: the show's own content (the media-server clips on its DMX, the IMAG cameras).</summary>
    Show,
    /// <summary>A video file shipped in the MVR archive.</summary>
    File,
    /// <summary>An NDI source on the network, by name.</summary>
    Ndi,
    /// <summary>CITP and a capture device: kept for the record, not shown (the show's content instead).</summary>
    Citp,
    CaptureDevice,
}

/// <summary>
/// An MVR VideoScreen's size from its geometry and its video source (#975). Pure: the file's bytes are
/// only hashed (the importing machine caches them, ConcertMvrTool), never put in the rig.
/// </summary>
public static partial class MvrFile
{
    /// <summary>How far in front of a VideoScreen's drawn geometry its picture sits (m): over the depth buffer's fight (docs/notes/general/zfight.md).</summary>
    public const float ScreenLift = 0.01f;

    /// <summary>
    /// A VideoScreen's place and size from its geometry's bounding box (#975): the box's front face (its
    /// -Y side, the audience's) lifted <see cref="ScreenLift"/>, X across, Z up, as its matrix scales it.
    /// Null when it has no geometry the game reads (then <see cref="ScreenSize"/> and its origin).
    /// </summary>
    internal static (Vector3 Centre, Vector3 Size)? ScreenFrame(XElement screen, Transform3D xf, XElement root, Func<string, byte[]?> files)
    {
        var symdefs = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in root.Element("Scene")?.Element("AUXData")?.Elements("Symdef") ?? Enumerable.Empty<XElement>())
            if ((string?)s.Attribute("uuid") is { } id) symdefs.TryAdd(id, s);
        var meshes = new Dictionary<string, MvrMesh?>(StringComparer.OrdinalIgnoreCase);
        Vector3 lo = Vector3.Inf, hi = -Vector3.Inf;
        Geometries(screen.Element("Geometries"), Transform3D.Identity, 0);
        // a flat panel has no depth; under 5 cm across or high it is not a screen's face
        if (!(hi.X - lo.X >= 0.05f) || !(hi.Z - lo.Z >= 0.05f)) return null;
        float sx = xf.Basis.X.Length(), sy = xf.Basis.Y.Length(), sz = xf.Basis.Z.Length();
        if (!(sx > 1e-4f) || !(sy > 1e-4f) || !(sz > 1e-4f)) return null;
        var size = new Vector3(Math.Clamp((hi.X - lo.X) * sx, 0.5f, 200f), Math.Clamp((hi.Z - lo.Z) * sz, 0.5f, 200f), 0f);
        // the front face's centre (metres in the screen's own frame) to the file's (mm), then ours
        var front = new Vector3((lo.X + hi.X) / 2, lo.Y - ScreenLift / sy, (lo.Z + hi.Z) / 2) * 1000f;
        return (FromMvr(xf * front) / 1000f, size);

        void Geometries(XElement? list, Transform3D parent, int depth)
        {
            if (list == null || depth > 8) return;
            foreach (var g in list.Elements())
            {
                var gx = parent * ParseMatrix((string?)g.Element("Matrix")?.Value);
                if (g.Name.LocalName == "Geometry3D") Box(((string?)g.Attribute("fileName") ?? "").Trim(), gx);
                else if (g.Name.LocalName == "Symbol" && (string?)g.Attribute("symdef") is { } id && symdefs.TryGetValue(id, out var def))
                    Geometries(def.Element("ChildList"), gx, depth + 1);
            }
        }

        void Box(string file, Transform3D mm)
        {
            if (file.Length == 0) return;
            if (!meshes.TryGetValue(file, out var mesh))
            {
                var bytes = files(file) ?? (System.IO.Path.HasExtension(file) ? null : files(file + ".3ds"));
                meshes[file] = mesh = bytes == null ? null : MvrGeometry.Read(file, bytes, files, out _);
            }
            if (mesh == null) return;
            // mesh metres, matrix millimetres: the piece's points in the screen's frame (metres)
            foreach (int i in mesh.Indices)
            {
                var p = mm.Basis * mesh.Positions[i] + mm.Origin / 1000f;
                lo = lo.Min(p);
                hi = hi.Max(p);
            }
        }
    }

    /// <summary>A VideoScreen's row from its geometry (its place and size, when it has one) and its sources (#975).</summary>
    internal static void Video(RigData.FixtureRow row, XElement screen, Transform3D xf, XElement root, Func<string, byte[]?> files, List<string> notes)
    {
        if (ScreenFrame(screen, xf, root, files) is { } frame)
        {
            row.Pos = RigData.V(frame.Centre);
            row.Screen = RigData.V(frame.Size);
        }
        ReadSources(screen, row, files, notes.Add);
    }

    /// <summary>
    /// A VideoScreen's video (#975): its first source the game can show (a Theora file in the archive, an
    /// NDI source), else its first source, kept for the record with a note; none leaves the show's content.
    /// </summary>
    internal static void ReadSources(XElement screen, RigData.FixtureRow row, Func<string, byte[]?> files, Action<string> note)
    {
        var sources = screen.Element("Sources")?.Elements("Source").ToList();
        if (sources == null || sources.Count == 0) return;
        string name = row.Name;
        (VideoSource Kind, string Value, string? Hash)? first = null;
        var why = new List<string>();
        foreach (var s in sources)
        {
            string value = Clean(s.Value);
            var kind = SourceKind((string?)s.Attribute("type"));
            if (kind == null) { why.Add($"source type \"{(string?)s.Attribute("type")}\" unknown"); continue; }
            if (value.Length == 0) { why.Add($"{kind} source with no name"); continue; }
            string? hash = null;
            if (kind == VideoSource.File)
            {
                var bytes = files(value);
                if (bytes == null) why.Add($"video {value} is not in the file (or over 64 MB)");
                else if (!IsTheora(bytes)) why.Add($"video {value} is not Ogg Theora, the one video format this build plays (ffmpeg -i in -c:v libtheora out.ogv)");
                else hash = Convert.ToHexString(SHA256.HashData(bytes));
            }
            first ??= (kind.Value, value, hash);
            if (kind == VideoSource.Ndi || hash != null)
            {
                Set(kind.Value, value, hash);
                note(kind == VideoSource.Ndi ? $"{name}: NDI source \"{value}\", shown where the NDI runtime is installed"
                    : $"{name}: plays {value} on this machine (other players: '/concert videos <file.mvr>')");
                return;
            }
            if (kind is VideoSource.Citp or VideoSource.CaptureDevice) why.Add($"{(kind == VideoSource.Citp ? "CITP" : "capture device")} source \"{value}\" is not read");
        }
        if (first is { } f) Set(f.Kind, f.Value, null);
        note($"{name}: {string.Join("; ", why)}: the show's content instead");

        void Set(VideoSource kind, string value, string? hash)
        {
            row.VideoSource = (int)kind;
            row.VideoName = value;
            row.VideoHash = hash;
        }
    }

    private static VideoSource? SourceKind(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "file" => VideoSource.File,
        "ndi" => VideoSource.Ndi,
        "citp" => VideoSource.Citp,
        "capturedevice" => VideoSource.CaptureDevice,
        _ => null,
    };

    /// <summary>A source's name as the rig carries it: trimmed, no control characters, at most 256 characters.</summary>
    private static string Clean(string s)
    {
        var c = string.Concat(s.Trim().Where(ch => !char.IsControl(ch)));
        return c.Length > RigData.MaxVideoName ? c[..RigData.MaxVideoName] : c;
    }

    /// <summary>Whether a file is Ogg Theora (its first page an Ogg page holding Theora's identification header): what Godot plays.</summary>
    public static bool IsTheora(byte[] b)
    {
        if (b.Length < 64 || b[0] != 'O' || b[1] != 'g' || b[2] != 'g' || b[3] != 'S') return false;
        // the identification header: 0x80 "theora", in the first page (a few pages in when another stream leads)
        int end = Math.Min(b.Length - 7, 64 << 10);
        for (int i = 4; i < end; i++)
            if (b[i] == 0x80 && b[i + 1] == 't' && b[i + 2] == 'h' && b[i + 3] == 'e' && b[i + 4] == 'o' && b[i + 5] == 'r' && b[i + 6] == 'a') return true;
        return false;
    }

    /// <summary>The playable video files an MVR holds for a rig's screens, by their hash (#975): what the importing machine caches.</summary>
    public static Dictionary<string, byte[]> Videos(byte[] mvr, RigData rig)
    {
        var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var ms = new System.IO.MemoryStream(mvr);
            using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
            foreach (var f in rig.Fixtures)
                if (f is { VideoSource: (int)VideoSource.File, VideoHash: { } h, VideoName: { } n } && !found.ContainsKey(h)
                    && Entry(zip, n) is { } bytes && Convert.ToHexString(SHA256.HashData(bytes)) == h)
                    found[h] = bytes;
        }
        catch (Exception e) when (e is System.IO.InvalidDataException or System.IO.IOException) { }
        return found;
    }

    /// <summary>The projectors whose projection the game does not show (#975): a note, their bodies are drawn (#958).</summary>
    internal static void NoteProjectors(XElement root, List<string> notes)
    {
        int n = root.Descendants("Projector").Count(p => p.Element("Projections")?.Elements("Projection").Any() == true);
        if (n > 0) notes.Add($"{n} projector projection(s) not shown (no target in MVR to project on): their bodies are drawn");
    }
}
