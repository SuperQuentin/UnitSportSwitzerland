using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Xml.Linq;
using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// An MVR's scene objects (#958): every Truss, Support, SceneObject, VideoScreen, Projector and
/// FocusPoint, wherever it nests (layers, GroupObjects, other objects' child lists, matrices composed
/// down as for fixtures), drawn from its <c>Geometries</c>: <c>Geometry3D</c> files and <c>Symbol</c>
/// instances of the <c>AUXData</c> Symdefs (whose child lists hold more of both, each with its own
/// matrix). Each distinct file (by name, then by content) is read once into a mesh of
/// <see cref="RigData"/>, then decimated or dropped to its caps, with a note saying how much.
/// </summary>
public static partial class MvrFile
{
    /// <summary>The nodes that are drawn from their geometries; a GroupObject only holds others.</summary>
    private static readonly HashSet<string> ObjectNodes = new() { "SceneObject", "Truss", "Support", "VideoScreen", "Projector", "FocusPoint" };

    /// <summary>Further than this from the file's origin (m), an object is not part of a stage.</summary>
    private const float ObjectReach = 300f;

    /// <summary>
    /// A piece longer than this across (m) is the venue, not the show: a room's walls, floor or ceiling,
    /// a hall's roof (Capture's demo, Vectorworks' theatre). The stadium is the venue, so it is not drawn.
    /// </summary>
    public const float VenueSpan = 28f;

    /// <summary>Layer, group and class names that say "the venue" (Vectorworks' Site- classes, Capture's Venue layer).</summary>
    private static readonly string[] VenueWords = { "site", "venue", "architect", "building", "floorplan", "floor plan", "ceiling", "cieling" };

    public static bool IsVenueName(string? name) =>
        name != null && VenueWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the scene's objects into <paramref name="rig"/> (our frame: metres, Y up, the file's
    /// origin). <paramref name="ours"/>: a file we wrote, whose stage object (<c>stage.glb</c>) is not
    /// drawn, as we build our own stage.
    /// </summary>
    internal static void ReadObjects(XElement root, Func<string, byte[]?> files, RigData rig, List<string> notes, bool ours)
    {
        var scene = root.Element("Scene");
        var symdefs = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in scene?.Element("AUXData")?.Elements("Symdef") ?? Enumerable.Empty<XElement>())
            if ((string?)s.Attribute("uuid") is { } id) symdefs.TryAdd(id, s);

        var meshes = new List<MvrMesh>();
        var byFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byContent = new Dictionary<string, int>();
        var placed = new List<(int Mesh, Transform3D Xf)>();
        var failed = new Dictionary<string, int>();
        var classes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in scene?.Element("AUXData")?.Elements("Class") ?? Enumerable.Empty<XElement>())
            if ((string?)c.Attribute("uuid") is { } id) classes[id] = (string?)c.Attribute("name") ?? "";
        var boxes = new List<Aabb>();
        int objects = 0, far = 0, venue = 0;

        foreach (var layer in scene?.Element("Layers")?.Elements("Layer") ?? Enumerable.Empty<XElement>())
            Walk(layer.Element("ChildList"), ParseMatrix((string?)layer.Element("Matrix")?.Value), 0, IsVenueName((string?)layer.Attribute("name")));
        foreach (var (why, n) in failed) notes.Add($"{n} object geometr{(n == 1 ? "y" : "ies")} not drawn: {why}");
        if (venue > 0) notes.Add($"{venue} venue piece(s) not drawn (walls, floors, ceilings, roofs: on a site or venue layer or class, or over {VenueSpan:0} m across): the stadium is the venue");
        if (far > 0) notes.Add($"{far} object(s) over {ObjectReach} m from the file's origin not drawn");
        if (placed.Count == 0) return;
        Fit(rig, meshes, placed, objects, notes);

        void Walk(XElement? list, Transform3D parent, int depth, bool inVenue)
        {
            if (list == null || depth > 32) return;
            foreach (var child in list.Elements())
            {
                var xf = parent * ParseMatrix((string?)child.Element("Matrix")?.Value);
                bool isVenue = inVenue || child.Name.LocalName == "GroupObject" && IsVenueName((string?)child.Attribute("name"))
                    || classes.TryGetValue(child.Element("Classing")?.Value.Trim() ?? "", out var cls) && IsVenueName(cls);
                if (ObjectNodes.Contains(child.Name.LocalName))
                {
                    objects++;
                    if (isVenue) venue += child.Element("Geometries")?.Elements().Count() ?? 0;
                    else Geometries(child.Element("Geometries"), xf, 0);
                }
                Walk(child.Element("ChildList"), xf, depth + 1, isVenue);
            }
        }

        // a Geometries list or a Symdef's child list: files placed, symbols followed (never round a loop)
        void Geometries(XElement? list, Transform3D parent, int depth)
        {
            if (list == null || depth > 8) return;
            foreach (var g in list.Elements())
            {
                var xf = parent * ParseMatrix((string?)g.Element("Matrix")?.Value);
                if (g.Name.LocalName == "Geometry3D") Place(((string?)g.Attribute("fileName") ?? "").Trim(), xf);
                else if (g.Name.LocalName == "Symbol")
                {
                    if ((string?)g.Attribute("symdef") is { } id && symdefs.TryGetValue(id, out var def)) Geometries(def.Element("ChildList"), xf, depth + 1);
                    else Fail("a symbol of no Symdef");
                }
            }
        }

        void Place(string file, Transform3D mm)
        {
            if (file.Length == 0) { Fail("no file name"); return; }
            if (ours && file.Equals("stage.glb", StringComparison.OrdinalIgnoreCase)) return;   // our own stage: built, not drawn twice
            int mesh = MeshOf(file);
            if (mesh < 0) return;
            var xf = ObjectToOurs(mm);
            if (MathF.Abs(xf.Origin.X) > ObjectReach || MathF.Abs(xf.Origin.Z) > ObjectReach || MathF.Abs(xf.Origin.Y) > ObjectReach) { far++; return; }
            var span = (xf * boxes[mesh]).Size;
            if (span.X > VenueSpan || span.Z > VenueSpan) { venue++; return; }
            placed.Add((mesh, xf));
        }

        int MeshOf(string file)
        {
            if (byFile.TryGetValue(file, out int m)) return m;
            byFile[file] = -1;
            var bytes = files(file) ?? (Path.HasExtension(file) ? null : files(file + ".3ds"));
            if (bytes == null) { Fail("a file not in the archive"); return -1; }
            string key = Path.GetExtension(file).ToLowerInvariant() + Convert.ToHexString(SHA1.HashData(bytes));
            if (byContent.TryGetValue(key, out m)) return byFile[file] = m;
            var mesh = MvrGeometry.Read(file, bytes, files, out string? why);
            if (mesh == null) { Fail($"{Path.GetExtension(file)}: {why}"); return byContent[key] = -1; }
            // MVR's frame to ours, as the objects' transforms
            var pos = new Vector3[mesh.Positions.Length];
            for (int i = 0; i < pos.Length; i++) pos[i] = FromMvr(mesh.Positions[i]);
            meshes.Add(mesh with { Positions = pos });
            var box = new Aabb(pos[mesh.Indices[0]], Vector3.Zero);
            foreach (int i in mesh.Indices) box = box.Expand(pos[i]);
            boxes.Add(box);
            return byFile[file] = byContent[key] = meshes.Count - 1;
        }

        void Fail(string why) => failed[why] = failed.GetValueOrDefault(why) + 1;
    }

    /// <summary>An MVR matrix (mm, Z up, scale in its basis) as an object's transform in ours (m, Y up), for a mesh already in ours.</summary>
    public static Transform3D ObjectToOurs(Transform3D mm)
    {
        Vector3 Col(Vector3 axis) => FromMvr(mm.Basis * ToMvr(axis));
        return new Transform3D(Col(new Vector3(1, 0, 0)), Col(new Vector3(0, 1, 0)), Col(new Vector3(0, 0, 1)), FromMvr(mm.Origin) / 1000f);
    }

    /// <summary>
    /// The meshes and their instances into the rig within its caps: each mesh to
    /// <see cref="RigData.MaxMeshTriangles"/>, all of them to <see cref="RigData.MaxSceneTriangles"/>
    /// (decimated in proportion, by vertex clustering), then the heaviest dropped while the bytes or
    /// triangles still do not fit, then instances past the drawn-triangle and object caps.
    /// </summary>
    private static void Fit(RigData rig, List<MvrMesh> meshes, List<(int Mesh, Transform3D Xf)> placed, int objects, List<string> notes)
    {
        int before = meshes.Sum(m => m.Triangles), decimated = 0;
        var used = new bool[meshes.Count];
        foreach (var p in placed) used[p.Mesh] = true;
        var mesh = meshes.Select((m, i) => used[i] ? m : null).ToArray();
        for (int i = 0; i < mesh.Length; i++)
            if (mesh[i] is { } m && m.Triangles > RigData.MaxMeshTriangles) { mesh[i] = Decimate(m, RigData.MaxMeshTriangles); decimated++; }
        for (int pass = 0; pass < 4; pass++)
        {
            long total = mesh.Sum(m => m?.Triangles ?? 0);
            if (total <= RigData.MaxSceneTriangles) break;
            float keep = RigData.MaxSceneTriangles * 0.97f / total;
            for (int i = 0; i < mesh.Length; i++)
                if (mesh[i] is { Triangles: > 24 } m)
                {
                    int target = Math.Max(12, (int)(m.Triangles * keep));
                    if (target < m.Triangles) { mesh[i] = Decimate(m, target); decimated++; }
                }
        }

        var rows = new RigData.MeshRow?[mesh.Length];
        long tris = 0, bytes = 0;
        for (int i = 0; i < mesh.Length; i++)
            if (mesh[i] is { } m)
            {
                rows[i] = RigData.Quantise(m.Positions, m.Indices, m.Color);
                if (rows[i]!.TriangleCount == 0) { rows[i] = null; continue; }
                tris += rows[i]!.TriangleCount;
                bytes += rows[i]!.Bytes;
            }
        int dropped = 0;
        foreach (int i in Enumerable.Range(0, rows.Length).Where(i => rows[i] != null).OrderByDescending(i => rows[i]!.TriangleCount).ToList())
        {
            if (tris <= RigData.MaxSceneTriangles && bytes <= RigData.MaxSceneBytes) break;
            tris -= rows[i]!.TriangleCount;
            bytes -= rows[i]!.Bytes;
            rows[i] = null;
            dropped++;
        }

        var index = new int[rows.Length];
        System.Array.Fill(index, -1);
        long drawn = 0;
        int cut = 0;
        foreach (var (m, xf) in placed)
        {
            if (rows[m] is not { } row) continue;
            if (rig.Objects.Count >= RigData.MaxObjects || drawn + row.TriangleCount > RigData.MaxDrawnTriangles) { cut++; continue; }
            if (index[m] < 0)
            {
                if (rig.Meshes.Count >= RigData.MaxMeshes) { cut++; continue; }
                index[m] = rig.Meshes.Count;
                rig.Meshes.Add(row);
            }
            drawn += row.TriangleCount;
            rig.Objects.Add(new RigData.ObjectRow { Mesh = index[m], Xf = RigData.X(xf) });
        }

        if (decimated > 0) notes.Add($"{decimated} object mesh(es) simplified to fit: {before} triangles to {rig.Meshes.Sum(r => r.TriangleCount)}");
        if (dropped > 0) notes.Add($"{dropped} heaviest object mesh(es) dropped to fit {RigData.MaxSceneTriangles} triangles and {RigData.MaxSceneBytes / 1000} kB");
        if (cut > 0) notes.Add($"{cut} object piece(s) past the caps ({RigData.MaxObjects} pieces, {RigData.MaxDrawnTriangles} triangles drawn) not drawn");
        if (rig.Objects.Count > 0)
            notes.Add(string.Format(CultureInfo.InvariantCulture, "{0} scene objects: {1} pieces of {2} meshes, {3} triangles ({4:0} kB)",
                objects, rig.Objects.Count, rig.Meshes.Count, rig.Meshes.Sum(r => r.TriangleCount), rig.Meshes.Sum(r => r.Bytes) / 1000f));
    }

    /// <summary>
    /// A mesh to at most <paramref name="target"/> triangles by vertex clustering: vertices snapped to a
    /// grid and merged at their cells' mean, collapsed and repeated triangles dropped; the finest grid
    /// that fits, found by halving. Low-poly by nature, which is the game's look.
    /// </summary>
    public static MvrMesh Decimate(MvrMesh mesh, int target)
    {
        if (mesh.Triangles <= target) return mesh;
        var lo = Vector3.Inf;
        var hi = -Vector3.Inf;
        foreach (var p in mesh.Positions) { lo = lo.Min(p); hi = hi.Max(p); }
        float extent = MathF.Max(hi.X - lo.X, MathF.Max(hi.Y - lo.Y, hi.Z - lo.Z));
        if (!(extent > 0) || !float.IsFinite(extent)) return mesh with { Indices = mesh.Indices[..(Math.Max(0, target) * 3)] };
        MvrMesh? best = null;
        float fine = extent / 65536f, coarse = extent * 1.01f;
        for (int step = 0; step < 18; step++)
        {
            float cell = MathF.Sqrt(fine * coarse);
            var m = Cluster(mesh, lo, cell);
            if (m.Triangles <= target) { best = m; coarse = cell; }
            else fine = cell;
        }
        best ??= Cluster(mesh, lo, coarse);
        return best.Triangles <= target ? best : best with { Indices = best.Indices[..(target * 3)] };
    }

    private static MvrMesh Cluster(MvrMesh mesh, Vector3 lo, float cell)
    {
        var cellOf = new Dictionary<long, int>();
        var sum = new List<Vector3>();
        var count = new List<int>();
        var map = new int[mesh.Positions.Length];
        for (int i = 0; i < map.Length; i++)
        {
            var q = (mesh.Positions[i] - lo) / cell;
            long key = ((long)Math.Min((int)q.X, 0x1FFFFF) << 42) | ((long)Math.Min((int)q.Y, 0x1FFFFF) << 21) | (long)Math.Min((int)q.Z, 0x1FFFFF);
            if (!cellOf.TryGetValue(key, out int c))
            {
                cellOf[key] = c = sum.Count;
                sum.Add(Vector3.Zero);
                count.Add(0);
            }
            sum[c] += mesh.Positions[i];
            count[c]++;
            map[i] = c;
        }
        var seen = new HashSet<(int, int, int)>();
        var indices = new List<int>();
        for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            int a = map[mesh.Indices[t]], b = map[mesh.Indices[t + 1]], c = map[mesh.Indices[t + 2]];
            if (a == b || b == c || a == c) continue;
            // the same three cells once, whichever way round
            int x = Math.Min(a, Math.Min(b, c)), z = Math.Max(a, Math.Max(b, c)), y = a + b + c - x - z;
            if (!seen.Add((x, y, z))) continue;
            indices.Add(a); indices.Add(b); indices.Add(c);
        }
        var positions = new Vector3[sum.Count];
        for (int i = 0; i < positions.Length; i++) positions[i] = sum[i] / count[i];
        return new MvrMesh(positions, indices.ToArray(), mesh.Color);
    }
}
