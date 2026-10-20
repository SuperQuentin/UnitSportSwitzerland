using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// One 3D file's triangles (#958): positions in METRES in MVR's frame (Z up, right-handed), an index
/// list (three per triangle), and the colour most of its triangles have (sRGB, as a person picks it).
/// </summary>
public sealed record MvrMesh(Vector3[] Positions, int[] Indices, Color Color)
{
    public int Triangles => Indices.Length / 3;
}

/// <summary>
/// The 3D files an MVR (and a GDTF) carries, read in pure .NET (#958): 3DS (binary chunks, 1 unit =
/// 1 mm, Z up, as MVR says) and glTF 2.0 (<c>.glb</c>, or <c>.gltf</c> with its buffers as data URIs
/// or files beside it in the archive; Y up, metres, as glTF says: its (x, y, z) is MVR's (x, -z, y)).
/// Triangles only (points and lines are dropped, strips and fans unrolled), one colour per file: the
/// material that covers most triangles. Untrusted data: every count and offset is checked against the
/// bytes, and anything wrong is an error, never an exception.
/// </summary>
public static class MvrGeometry
{
    /// <summary>More than this is not a stage object: refused before it is unrolled.</summary>
    public const int MaxTriangles = 2_000_000;
    /// <summary>A 3DS or glTF without a material: a light grey.</summary>
    public static readonly Color DefaultColor = new(0.7f, 0.7f, 0.7f);

    /// <summary>Reads a 3D file by its extension (none = 3DS, as MVR says); null with why when it cannot.</summary>
    public static MvrMesh? Read(string fileName, byte[] bytes, out string? error) => Read(fileName, bytes, null, out error);

    /// <param name="resolve">A glTF's external buffers, by their URI (relative to the archive's root).</param>
    public static MvrMesh? Read(string fileName, byte[] bytes, Func<string, byte[]?>? resolve, out string? error)
    {
        error = null;
        try
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            MvrMesh? mesh = ext switch
            {
                ".3ds" or "" => Read3ds(bytes, out error),
                ".glb" => ReadGlb(bytes, resolve, out error),
                ".gltf" => ReadGltf(bytes, null, resolve, out error),
                _ => Fail($"{ext} is not a 3D format MVR allows (3ds, glb, gltf)", out error),
            };
            if (mesh != null && mesh.Indices.Length == 0) return Fail("no triangles", out error);
            return mesh;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or IndexOutOfRangeException or InvalidOperationException
            or FormatException or OverflowException or KeyNotFoundException or InvalidCastException or DecoderFallbackException)
        {
            error = e.Message;
            return null;
        }
    }

    private static MvrMesh? Fail(string why, out string? error)
    {
        error = why;
        return null;
    }

    // ---- 3DS ----------------------------------------------------------------------------------------

    private const ushort Main3ds = 0x4D4D, Editor = 0x3D3D, Object = 0x4000, TriMesh = 0x4100, VertexList = 0x4110,
        FaceList = 0x4120, FaceMaterial = 0x4130, Material = 0xAFFF, MaterialName = 0xA000, Diffuse = 0xA020,
        ColorF = 0x0010, Color24 = 0x0011, LinColor24 = 0x0012, LinColorF = 0x0013;

    /// <summary>
    /// A 3DS: every object's vertex and face lists (vertices are stored placed, so each object's local
    /// matrix is not applied), each face's material's diffuse colour. Millimetres to metres.
    /// </summary>
    private static MvrMesh? Read3ds(byte[] b, out string? error)
    {
        error = null;
        if (b.Length < 6 || BitConverter.ToUInt16(b, 0) is not (Main3ds or Editor)) return Fail("not a 3DS file", out error);
        var positions = new List<Vector3>();
        var indices = new List<int>();
        var colours = new Dictionary<string, Color>();
        var weight = new Dictionary<string, int>();
        int objectStart = 0, objectVerts = 0, unmaterialled = 0;
        string material = "";
        Walk(0, b.Length, 0);
        if (indices.Count / 3 > MaxTriangles) return Fail($"over {MaxTriangles} triangles", out error);
        // the colour of the most triangles
        var colour = DefaultColor;
        int best = unmaterialled;
        foreach (var (name, n) in weight)
            if (n > best && colours.TryGetValue(name, out var c)) { best = n; colour = c; }
        return new MvrMesh(positions.ToArray(), indices.ToArray(), colour);

        void Walk(int off, int end, int depth)
        {
            if (depth > 8) return;
            while (off + 6 <= end)
            {
                ushort id = BitConverter.ToUInt16(b, off);
                int len = BitConverter.ToInt32(b, off + 2);
                if (len < 6 || len > end - off) return;   // a broken chunk ends its parent
                int body = off + 6, stop = off + len;
                switch (id)
                {
                    case Main3ds or Editor or TriMesh or Material:
                        Walk(body, stop, depth + 1);
                        break;
                    case Object:
                        int nul = Array.IndexOf(b, (byte)0, body, stop - body);
                        if (nul < 0) break;
                        objectStart = positions.Count;
                        objectVerts = 0;
                        Walk(nul + 1, stop, depth + 1);
                        break;
                    case VertexList when body + 2 <= stop:
                    {
                        int n = BitConverter.ToUInt16(b, body);
                        if (body + 2 + n * 12 > stop) break;
                        // a second list in one object would shift the faces: only the first counts
                        if (objectVerts > 0) break;
                        for (int i = 0; i < n; i++)
                        {
                            int p = body + 2 + i * 12;
                            positions.Add(new Vector3(BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4), BitConverter.ToSingle(b, p + 8)) / 1000f);
                        }
                        objectVerts = n;
                        break;
                    }
                    case FaceList when body + 2 <= stop:
                    {
                        int n = BitConverter.ToUInt16(b, body);
                        int faces = body + 2;
                        if (faces + n * 8 > stop) break;
                        int k = 0;
                        for (int i = 0; i < n; i++)
                        {
                            int p = faces + i * 8;
                            int a = BitConverter.ToUInt16(b, p), c1 = BitConverter.ToUInt16(b, p + 2), c2 = BitConverter.ToUInt16(b, p + 4);
                            if (a >= objectVerts || c1 >= objectVerts || c2 >= objectVerts) continue;
                            k++;
                            indices.Add(objectStart + a); indices.Add(objectStart + c1); indices.Add(objectStart + c2);
                        }
                        // the faces' materials follow the list
                        int counted = CountMaterials(faces + n * 8, stop, n);
                        unmaterialled += Math.Max(0, k - counted);
                        break;
                    }
                    case MaterialName:
                        material = CString(body, stop);
                        break;
                    case Diffuse:
                        if (ReadColour(body, stop) is { } col) colours[material] = col;
                        break;
                }
                off = stop;
            }
        }

        // 0x4130: a material's name, then the faces it covers; returns how many faces were named
        int CountMaterials(int off, int end, int faces)
        {
            int counted = 0;
            while (off + 6 <= end)
            {
                ushort id = BitConverter.ToUInt16(b, off);
                int len = BitConverter.ToInt32(b, off + 2);
                if (len < 6 || len > end - off) break;
                if (id == FaceMaterial)
                {
                    string name = CString(off + 6, off + len);
                    int at = off + 6 + Encoding.Latin1.GetByteCount(name) + 1;
                    if (at + 2 <= off + len)
                    {
                        int n = Math.Min(BitConverter.ToUInt16(b, at), faces);
                        weight[name] = weight.GetValueOrDefault(name) + n;
                        counted += n;
                    }
                }
                off += len;
            }
            return counted;
        }

        string CString(int from, int end)
        {
            int nul = Array.IndexOf(b, (byte)0, from, end - from);
            return Encoding.Latin1.GetString(b, from, (nul < 0 ? end : nul) - from);
        }

        // a colour chunk inside the diffuse chunk: gamma-corrected first, as 3DS writers mean it
        Color? ReadColour(int off, int end)
        {
            Color? found = null;
            while (off + 6 <= end)
            {
                ushort id = BitConverter.ToUInt16(b, off);
                int len = BitConverter.ToInt32(b, off + 2);
                if (len < 6 || len > end - off) break;
                int p = off + 6;
                if (id is Color24 or LinColor24 && p + 3 <= off + len)
                    found ??= Color.Color8(b[p], b[p + 1], b[p + 2]);
                else if (id is ColorF or LinColorF && p + 12 <= off + len)
                {
                    var c = new Color(BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4), BitConverter.ToSingle(b, p + 8));
                    if (float.IsFinite(c.R) && float.IsFinite(c.G) && float.IsFinite(c.B)) found ??= c.Clamp();
                }
                off += len;
            }
            return found;
        }
    }

    // ---- glTF ---------------------------------------------------------------------------------------

    private static MvrMesh? ReadGlb(byte[] b, Func<string, byte[]?>? resolve, out string? error)
    {
        error = null;
        if (b.Length < 20 || BitConverter.ToUInt32(b, 0) != 0x46546C67) return Fail("not a GLB", out error);
        if (BitConverter.ToUInt32(b, 4) != 2) return Fail($"glTF {BitConverter.ToUInt32(b, 4)}, not 2", out error);
        int total = (int)Math.Min(BitConverter.ToUInt32(b, 8), (uint)b.Length);
        byte[]? json = null, bin = null;
        int off = 12;
        while (off + 8 <= total)
        {
            uint len = BitConverter.ToUInt32(b, off), type = BitConverter.ToUInt32(b, off + 4);
            if (len > total - off - 8) return Fail("a GLB chunk runs past the file", out error);
            var chunk = b.AsSpan(off + 8, (int)len).ToArray();
            if (type == 0x4E4F534A) json ??= chunk;
            else if (type == 0x004E4942) bin ??= chunk;
            off += 8 + (int)((len + 3) & ~3u);
        }
        if (json == null) return Fail("a GLB without JSON", out error);
        return ReadGltf(json, bin, resolve, out error);
    }

    private static MvrMesh? ReadGltf(byte[] jsonBytes, byte[]? glbBin, Func<string, byte[]?>? resolve, out string? error)
    {
        error = null;
        using var doc = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions { MaxDepth = 64 });
        var root = doc.RootElement;
        if (root.TryGetProperty("extensionsRequired", out var req) && req.ValueKind == JsonValueKind.Array && req.GetArrayLength() > 0)
            return Fail("needs glTF extensions MVR does not allow: " + string.Join(", ", req.EnumerateArray().Select(e => e.ToString())), out error);

        var buffers = new List<byte[]?>();
        foreach (var buf in Items(root, "buffers"))
        {
            if (!buf.TryGetProperty("uri", out var uri)) { buffers.Add(glbBin); continue; }
            string u = uri.GetString() ?? "";
            if (u.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = u.IndexOf(',');
                buffers.Add(comma > 0 && u[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ? Convert.FromBase64String(u[(comma + 1)..]) : null);
            }
            else buffers.Add(resolve?.Invoke(Uri.UnescapeDataString(u)));
        }
        var views = Items(root, "bufferViews").ToArray();
        var accessors = Items(root, "accessors").ToArray();
        var meshes = Items(root, "meshes").ToArray();
        var nodes = Items(root, "nodes").ToArray();
        var materials = Items(root, "materials").ToArray();

        var positions = new List<Vector3>();
        var indices = new List<int>();
        var weight = new Dictionary<int, int>();
        string? why = null;

        // the default scene's node trees; with none, every mesh where it lies
        var roots = new List<int>();
        var scenes = Items(root, "scenes").ToArray();
        int scene = root.TryGetProperty("scene", out var sc) && sc.TryGetInt32(out int si) ? si : 0;
        if (scene >= 0 && scene < scenes.Length) roots.AddRange(Items(scenes[scene], "nodes").Select(n => n.GetInt32()));
        else if (scenes.Length == 0)
        {
            // no scene: every node no other holds, or every mesh where it lies when there are no nodes
            var held = nodes.SelectMany(n => Items(n, "children")).Select(c => c.GetInt32()).ToHashSet();
            roots.AddRange(Enumerable.Range(0, nodes.Length).Where(n => !held.Contains(n)));
            if (nodes.Length == 0)
                for (int m = 0; m < meshes.Length; m++) AddMesh(m, Transform3D.Identity);
        }
        var visited = new HashSet<int>();
        foreach (int r in roots) Visit(r, Transform3D.Identity, 0);
        if (why != null && indices.Count == 0) return Fail(why, out error);
        if (indices.Count / 3 > MaxTriangles) return Fail($"over {MaxTriangles} triangles", out error);

        var colour = DefaultColor;
        int best = -1;
        foreach (var (mat, n) in weight)
            if (n > best) { best = n; colour = MaterialColour(mat); }
        // glTF is Y up: its (x, y, z) is MVR's (x, -z, y)
        var mvr = new Vector3[positions.Count];
        for (int i = 0; i < mvr.Length; i++) mvr[i] = new Vector3(positions[i].X, -positions[i].Z, positions[i].Y);
        return new MvrMesh(mvr, indices.ToArray(), colour);

        void Visit(int n, Transform3D parent, int depth)
        {
            if (n < 0 || n >= nodes.Length || depth > 64 || !visited.Add(n)) return;   // a cycle or a shared node is drawn once
            var node = nodes[n];
            var xf = parent * NodeTransform(node);
            if (node.TryGetProperty("mesh", out var m) && m.TryGetInt32(out int mi)) AddMesh(mi, xf);
            foreach (var c in Items(node, "children")) Visit(c.GetInt32(), xf, depth + 1);
        }

        void AddMesh(int m, Transform3D xf)
        {
            if (m < 0 || m >= meshes.Length) return;
            foreach (var prim in Items(meshes[m], "primitives"))
            {
                int mode = prim.TryGetProperty("mode", out var md) ? md.GetInt32() : 4;
                if (mode is not (4 or 5 or 6)) continue;
                if (!prim.TryGetProperty("attributes", out var attrs) || !attrs.TryGetProperty("POSITION", out var pa)) continue;
                var pos = ReadPositions(pa.GetInt32());
                if (pos == null) continue;
                int[] idx;
                if (prim.TryGetProperty("indices", out var ia))
                {
                    var read = ReadIndices(ia.GetInt32());
                    if (read == null) continue;
                    idx = read;
                }
                else idx = Enumerable.Range(0, pos.Length).ToArray();
                int baseVertex = positions.Count;
                foreach (var p in pos) positions.Add(xf * p);
                int before = indices.Count;
                void Tri(int a, int c, int d)
                {
                    if (a < 0 || c < 0 || d < 0 || a >= pos.Length || c >= pos.Length || d >= pos.Length) return;
                    indices.Add(baseVertex + a); indices.Add(baseVertex + c); indices.Add(baseVertex + d);
                }
                if (mode == 4) for (int i = 0; i + 2 < idx.Length; i += 3) Tri(idx[i], idx[i + 1], idx[i + 2]);
                else if (mode == 5) for (int i = 0; i + 2 < idx.Length; i++) { if (i % 2 == 0) Tri(idx[i], idx[i + 1], idx[i + 2]); else Tri(idx[i + 1], idx[i], idx[i + 2]); }
                else for (int i = 1; i + 1 < idx.Length; i++) Tri(idx[0], idx[i], idx[i + 1]);
                if (indices.Count / 3 > MaxTriangles) throw new InvalidOperationException($"over {MaxTriangles} triangles");
                int mat = prim.TryGetProperty("material", out var mt) && mt.TryGetInt32(out int mv) ? mv : -1;
                weight[mat] = weight.GetValueOrDefault(mat) + (indices.Count - before) / 3;
            }
        }

        // an accessor's bytes: its view's slice, its stride; null with why when it does not fit
        bool Slice(int a, out JsonElement acc, out byte[] data, out int start, out int stride, int size)
        {
            acc = default; data = Array.Empty<byte>(); start = 0; stride = size;
            if (a < 0 || a >= accessors.Length) { why = "an accessor out of range"; return false; }
            acc = accessors[a];
            if (!acc.TryGetProperty("bufferView", out var bv)) { why = "an accessor without data (sparse)"; return false; }
            int v = bv.GetInt32();
            if (v < 0 || v >= views.Length) { why = "a buffer view out of range"; return false; }
            var view = views[v];
            int buffer = view.GetProperty("buffer").GetInt32();
            if (buffer < 0 || buffer >= buffers.Count || buffers[buffer] is not { } bytes) { why = "a buffer is not in the file"; return false; }
            long viewOff = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt64() : 0;
            long viewLen = view.GetProperty("byteLength").GetInt64();
            long accOff = acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt64() : 0;
            stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : size;
            if (stride < size) stride = size;
            long count = acc.GetProperty("count").GetInt64();
            if (viewOff < 0 || viewLen < 0 || accOff < 0 || count < 0 || viewOff + viewLen > bytes.Length
                || count > 0 && accOff + (count - 1) * stride + size > viewLen)
            { why = "an accessor runs past its buffer"; return false; }
            data = bytes;
            start = (int)(viewOff + accOff);
            return true;
        }

        Vector3[]? ReadPositions(int a)
        {
            if (!Slice(a, out var acc, out var data, out int start, out int stride, 12)) return null;
            if (acc.GetProperty("componentType").GetInt32() != 5126 || acc.GetProperty("type").GetString() != "VEC3") { why = "positions not float VEC3"; return null; }
            int count = acc.GetProperty("count").GetInt32();
            if (count > 3 * MaxTriangles) { why = $"over {3 * MaxTriangles} vertices"; return null; }
            var r = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int p = start + i * stride;
                r[i] = new Vector3(BitConverter.ToSingle(data, p), BitConverter.ToSingle(data, p + 4), BitConverter.ToSingle(data, p + 8));
                if (!float.IsFinite(r[i].X) || !float.IsFinite(r[i].Y) || !float.IsFinite(r[i].Z)) r[i] = Vector3.Zero;
            }
            return r;
        }

        int[]? ReadIndices(int a)
        {
            if (a < 0 || a >= accessors.Length) { why = "an accessor out of range"; return null; }
            int type = accessors[a].GetProperty("componentType").GetInt32();
            int size = type switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => 0 };
            if (size == 0) { why = "indices of an unknown type"; return null; }
            if (!Slice(a, out var acc, out var data, out int start, out int stride, size)) return null;
            int count = acc.GetProperty("count").GetInt32();
            if (count > 3 * MaxTriangles) { why = $"over {MaxTriangles} triangles"; return null; }
            var r = new int[count];
            for (int i = 0; i < count; i++)
            {
                int p = start + i * stride;
                r[i] = size switch { 1 => data[p], 2 => BitConverter.ToUInt16(data, p), _ => (int)Math.Min(BitConverter.ToUInt32(data, p), int.MaxValue) };
            }
            return r;
        }

        // glTF colours are linear: back to sRGB, as the rest of the game's colours are given
        Color MaterialColour(int m)
        {
            if (m < 0 || m >= materials.Length) return DefaultColor;
            if (!materials[m].TryGetProperty("pbrMetallicRoughness", out var pbr) || !pbr.TryGetProperty("baseColorFactor", out var f)
                || f.ValueKind != JsonValueKind.Array || f.GetArrayLength() < 3)
                return Colors.White;
            var c = new Color(f[0].GetSingle(), f[1].GetSingle(), f[2].GetSingle());
            return float.IsFinite(c.R) && float.IsFinite(c.G) && float.IsFinite(c.B) ? c.Clamp().LinearToSrgb() : DefaultColor;
        }
    }

    /// <summary>A node's matrix (column-major) or its translation, rotation and scale.</summary>
    private static Transform3D NodeTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var m) && m.ValueKind == JsonValueKind.Array && m.GetArrayLength() == 16)
        {
            float M(int i) => m[i].GetSingle();
            var xf = new Transform3D(new Vector3(M(0), M(1), M(2)), new Vector3(M(4), M(5), M(6)), new Vector3(M(8), M(9), M(10)), new Vector3(M(12), M(13), M(14)));
            return Finite(xf) ? xf : Transform3D.Identity;
        }
        var t = Vec(node, "translation", 3, Vector3.Zero);
        var s = Vec(node, "scale", 3, Vector3.One);
        var q = Quaternion.Identity;
        if (node.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() == 4)
        {
            q = new Quaternion(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle());
            q = q.LengthSquared() > 1e-12f && float.IsFinite(q.LengthSquared()) ? q.Normalized() : Quaternion.Identity;
        }
        var trs = new Transform3D(new Basis(q) * Basis.FromScale(s), t);
        return Finite(trs) ? trs : Transform3D.Identity;
    }

    private static bool Finite(Transform3D x) =>
        float.IsFinite(x.Basis.X.LengthSquared()) && float.IsFinite(x.Basis.Y.LengthSquared()) && float.IsFinite(x.Basis.Z.LengthSquared()) && float.IsFinite(x.Origin.LengthSquared());

    private static Vector3 Vec(JsonElement e, string name, int n, Vector3 fallback) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() == n
            ? new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle()) : fallback;

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();
}
