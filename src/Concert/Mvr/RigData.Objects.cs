using System.Text.Json.Serialization;
using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// What an MVR holds besides its fixtures (#958): trusses, supports, scene objects, screens' and
/// projectors' bodies, focus points. Each distinct 3D file is one simplified mesh (positions quantised
/// to 16 bits over its box, 16-bit indices, one colour), each object an instance of one by its
/// transform, so two hundred identical truss pieces cost one mesh. In the rig's frame (metres, Y up),
/// moved with the fixtures by <see cref="StagePlan.FromStage"/>. Hard caps, checked by the server and
/// every client (<see cref="Check"/>): the import decimates or drops to fit them, and says so. Faces'
/// own looks (#973): a colour per vertex, and runs of triangles drawing one of <see cref="Textures"/>
/// (small PNGs, shared by the objects and the bodies) through u16 texture coordinates.
/// </summary>
public sealed partial class RigData
{
    /// <summary>The limits on a rig's objects: meshes, instances, triangles per mesh, in all, drawn, and the meshes' bytes.</summary>
    public const int MaxMeshes = 4096, MaxObjects = 20_000, MaxMeshTriangles = 20_000, MaxSceneTriangles = 150_000,
        MaxDrawnTriangles = 500_000, MaxSceneBytes = 1_200_000;
    /// <summary>How far from the centre spot an object may stand, and its mesh reach from its origin (m).</summary>
    public const float MaxObjectReach = 450f, MaxMeshReach = 1000f;
    /// <summary>
    /// The limits on textures (#973): how many, a side (pixels), one's PNG bytes and all of theirs. A
    /// 64 x 64 RGB PNG is 12 kB at worst, so the budget holds twenty noisy ones or many plain ones.
    /// </summary>
    public const int MaxTextures = 64, MaxTextureSize = MvrImages.MaxSize, MaxTextureFileBytes = 16_384, MaxTextureBytes = 256_000;
    /// <summary>How far a texture coordinate may reach (tiling a few thousand times is junk).</summary>
    public const float MaxUvReach = 4096f;

    public sealed class MeshRow
    {
        /// <summary>sRGB, 0xRRGGBBAA.</summary>
        [JsonPropertyName("c")] public uint Color { get; set; }
        [JsonPropertyName("lo")] public float[] Min { get; set; } = new float[3];
        [JsonPropertyName("sz")] public float[] Size { get; set; } = new float[3];
        /// <summary>Three little-endian u16 per vertex, 0..65535 across the box.</summary>
        [JsonPropertyName("v")] public byte[] Vertices { get; set; } = Array.Empty<byte>();
        /// <summary>A little-endian u16 per corner, three per triangle.</summary>
        [JsonPropertyName("i")] public byte[] Indices { get; set; } = Array.Empty<byte>();

        /// <summary>sRGB, three bytes a vertex; null: <see cref="Color"/> everywhere (#973).</summary>
        [JsonPropertyName("vc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public byte[]? VertexColors { get; set; }
        /// <summary>Two little-endian u16 per vertex, 0..65535 across <see cref="UvMin"/>, <see cref="UvSize"/>; null when nothing is textured.</summary>
        [JsonPropertyName("uv"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public byte[]? Uvs { get; set; }
        [JsonPropertyName("ul"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public float[]? UvMin { get; set; }
        [JsonPropertyName("us"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public float[]? UvSize { get; set; }
        /// <summary>
        /// The triangles in runs, each { texture (into <see cref="Textures"/>, -1 none), triangles }, in
        /// order; null: one untextured run.
        /// </summary>
        [JsonPropertyName("ts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int[]? Runs { get; set; }

        [JsonIgnore] public int VertexCount => Vertices.Length / 6;
        [JsonIgnore] public int TriangleCount => Indices.Length / 6;
        [JsonIgnore] public int Bytes => Vertices.Length + Indices.Length + (VertexColors?.Length ?? 0) + (Uvs?.Length ?? 0) + 4 * (Runs?.Length ?? 0);

        /// <summary>The same mesh, looks and all (bodies share equal parts).</summary>
        public bool Same(MeshRow o) => Color == o.Color && Vertices.AsSpan().SequenceEqual(o.Vertices) && Indices.AsSpan().SequenceEqual(o.Indices)
            && Min.SequenceEqual(o.Min) && Size.SequenceEqual(o.Size) && Eq(VertexColors, o.VertexColors) && Eq(Uvs, o.Uvs)
            && Eq(UvMin, o.UvMin) && Eq(UvSize, o.UvSize) && Eq(Runs, o.Runs);

        private static bool Eq<T>(T[]? a, T[]? b) where T : IEquatable<T> => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);
    }

    public sealed class ObjectRow
    {
        [JsonPropertyName("m")] public int Mesh { get; set; }
        /// <summary>The basis' three columns, then the origin.</summary>
        [JsonPropertyName("x")] public float[] Xf { get; set; } = new float[12];
    }

    [JsonPropertyName("meshes")] public List<MeshRow> Meshes { get; set; } = new();
    [JsonPropertyName("obj")] public List<ObjectRow> Objects { get; set; } = new();
    /// <summary>The meshes' and bodies' textures (#973): RGB PNGs at most <see cref="MaxTextureSize"/> a side.</summary>
    [JsonPropertyName("tx")] public List<byte[]> Textures { get; set; } = new();

    public static float[] X(Transform3D t) => new[]
    {
        t.Basis.Column0.X, t.Basis.Column0.Y, t.Basis.Column0.Z, t.Basis.Column1.X, t.Basis.Column1.Y, t.Basis.Column1.Z,
        t.Basis.Column2.X, t.Basis.Column2.Y, t.Basis.Column2.Z, t.Origin.X, t.Origin.Y, t.Origin.Z,
    };

    public static Transform3D X(float[]? a) => a is { Length: 12 }
        ? new Transform3D(new Vector3(a[0], a[1], a[2]), new Vector3(a[3], a[4], a[5]), new Vector3(a[6], a[7], a[8]), new Vector3(a[9], a[10], a[11]))
        : Transform3D.Identity;

    /// <summary>
    /// A mesh row from triangles (our frame, metres): quantised over their box, equal vertices welded,
    /// triangles that collapse dropped. At most 65535 vertices survive: the caller keeps to
    /// <see cref="MaxMeshTriangles"/>, which cannot reach that.
    /// </summary>
    public static MeshRow Quantise(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, Color srgb) =>
        Quantise(new MvrMesh(positions.ToArray(), indices.ToArray(), srgb));

    /// <summary>
    /// A mesh row from a mesh with its faces' looks (#973): each image to a texture of the rig by
    /// <paramref name="textureOf"/> (-1: left off, its faces take its mean colour), the triangles in a
    /// run per texture, a vertex welded only with one of the same colour, texture and coordinate.
    /// </summary>
    public static MeshRow Quantise(MvrMesh mesh, Func<MvrImage, int>? textureOf = null)
    {
        var positions = mesh.Positions;
        var indices = mesh.Indices;
        int tris = indices.Length / 3;
        // each triangle's texture, and the order that groups them (untextured first)
        var tex = new int[tris];
        var texOf = new Dictionary<int, int>();
        for (int t = 0; t < tris; t++)
        {
            int img = mesh.ImageOf(t);
            if (img < 0 || mesh.Images == null || img >= mesh.Images.Length) { tex[t] = -1; continue; }
            if (!texOf.TryGetValue(img, out int x)) texOf[img] = x = textureOf?.Invoke(mesh.Images[img]) ?? -1;
            tex[t] = x;
        }
        var order = Enumerable.Range(0, tris).OrderBy(t => tex[t]).ToArray();   // stable

        var lo = Vector3.Inf;
        var hi = -Vector3.Inf;
        var uvLo = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var uvHi = -uvLo;
        for (int t = 0; t < tris; t++)
            for (int k = 0; k < 3; k++)
            {
                int i = indices[t * 3 + k];
                lo = lo.Min(positions[i]);
                hi = hi.Max(positions[i]);
                if (tex[t] >= 0 && mesh.Uvs is { } uv) { uvLo = uvLo.Min(uv[i]); uvHi = uvHi.Max(uv[i]); }
            }
        if (tris == 0) lo = hi = Vector3.Zero;
        if (!(uvLo.X <= uvHi.X)) uvLo = uvHi = Vector2.Zero;
        var size = hi - lo;
        var uvSize = uvHi - uvLo;
        uint mainRgb = Rgb(mesh.Color.Clamp());

        var weld = new Dictionary<(long, long), int>();
        var verts = new List<ushort>();
        var uvs = new List<ushort>();
        var cols = new List<byte>();
        var idx = new List<ushort>();
        var runs = new List<int>();
        bool coloured = false;
        Span<int> corner = stackalloc int[3];
        foreach (int t in order)
        {
            int img = mesh.ImageOf(t);
            // a texture left off: its faces take its mean colour
            var shade = tex[t] < 0 && img >= 0 && mesh.Images is { } ims && img < ims.Length ? ims[img].Average : Colors.White;
            for (int k = 0; k < 3; k++)
            {
                int i = indices[t * 3 + k];
                var p = positions[i];
                ushort x = Q(p.X, lo.X, size.X), y = Q(p.Y, lo.Y, size.Y), z = Q(p.Z, lo.Z, size.Z);
                uint rgb = Rgb((mesh.ColorAt(i) * shade).Clamp());
                ushort u = 0, v = 0;
                if (tex[t] >= 0 && mesh.Uvs is { } uv) { u = Q(uv[i].X, uvLo.X, uvSize.X); v = Q(uv[i].Y, uvLo.Y, uvSize.Y); }
                var key = ((long)x << 32 | (long)y << 16 | z, (long)rgb << 40 | (long)u << 24 | (long)v << 8 | (byte)(tex[t] + 1));
                if (!weld.TryGetValue(key, out int w))
                {
                    if (weld.Count >= 65535) throw new ArgumentException("over 65535 vertices in one mesh");
                    weld[key] = w = weld.Count;
                    verts.Add(x); verts.Add(y); verts.Add(z);
                    uvs.Add(u); uvs.Add(v);
                    cols.Add((byte)(rgb >> 16)); cols.Add((byte)(rgb >> 8)); cols.Add((byte)rgb);
                    coloured |= rgb != mainRgb;
                }
                corner[k] = w;
            }
            if (corner[0] == corner[1] || corner[1] == corner[2] || corner[0] == corner[2]) continue;
            idx.Add((ushort)corner[0]); idx.Add((ushort)corner[1]); idx.Add((ushort)corner[2]);
            if (runs.Count > 0 && runs[^2] == tex[t]) runs[^1]++;
            else { runs.Add(tex[t]); runs.Add(1); }
        }
        bool textured = false;
        for (int i = 0; i < runs.Count; i += 2) textured |= runs[i] >= 0;
        return new MeshRow
        {
            Color = mainRgb << 8 | 0xFF,
            Min = V(lo), Size = V(size),
            Vertices = U16(verts), Indices = U16(idx),
            VertexColors = coloured ? cols.ToArray() : null,
            Uvs = textured ? U16(uvs) : null,
            UvMin = textured ? new[] { uvLo.X, uvLo.Y } : null,
            UvSize = textured ? new[] { uvSize.X, uvSize.Y } : null,
            Runs = textured ? runs.ToArray() : null,
        };

        static ushort Q(float v, float l, float s) => s > 0 ? (ushort)Math.Clamp(MathF.Round((v - l) / s * 65535f), 0f, 65535f) : (ushort)0;
    }

    private static uint Rgb(Color c) => (uint)c.R8 << 16 | (uint)c.G8 << 8 | (uint)c.B8;

    private static byte[] U16(List<ushort> values)
    {
        var b = new byte[values.Count * 2];
        for (int i = 0; i < values.Count; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), values[i]);
        return b;
    }

    /// <summary>A mesh row's vertices back in metres (its own frame).</summary>
    public static Vector3[] Positions(MeshRow m)
    {
        var lo = V(m.Min);
        var size = V(m.Size) / 65535f;
        var r = new Vector3[m.VertexCount];
        for (int i = 0; i < r.Length; i++)
            r[i] = lo + new Vector3(BitConverter.ToUInt16(m.Vertices, i * 6), BitConverter.ToUInt16(m.Vertices, i * 6 + 2), BitConverter.ToUInt16(m.Vertices, i * 6 + 4)) * size;
        return r;
    }

    public static int[] Indices(MeshRow m)
    {
        var r = new int[m.Indices.Length / 2];
        for (int i = 0; i < r.Length; i++) r[i] = BitConverter.ToUInt16(m.Indices, i * 2);
        return r;
    }

    public static Color ColorOf(MeshRow m) => Color.Color8((byte)(m.Color >> 24), (byte)(m.Color >> 16), (byte)(m.Color >> 8));

    /// <summary>A mesh row's colour per vertex (sRGB): its own, or its one colour everywhere.</summary>
    public static Color[] VertexColorsOf(MeshRow m)
    {
        var r = new Color[m.VertexCount];
        if (m.VertexColors is not { } c) { Array.Fill(r, ColorOf(m)); return r; }
        for (int i = 0; i < r.Length; i++) r[i] = Color.Color8(c[i * 3], c[i * 3 + 1], c[i * 3 + 2]);
        return r;
    }

    /// <summary>A mesh row's texture coordinates (Godot's, v down); null when nothing in it is textured.</summary>
    public static Vector2[]? UvsOf(MeshRow m)
    {
        if (m.Uvs is not { } b || m.UvMin is not { Length: 2 } lo || m.UvSize is not { Length: 2 } size) return null;
        var r = new Vector2[m.VertexCount];
        for (int i = 0; i < r.Length; i++)
            r[i] = new Vector2(lo[0] + BitConverter.ToUInt16(b, i * 4) * size[0] / 65535f, lo[1] + BitConverter.ToUInt16(b, i * 4 + 2) * size[1] / 65535f);
        return r;
    }

    /// <summary>A mesh row's runs: each texture (-1 none), its first triangle and how many.</summary>
    public static (int Texture, int First, int Count)[] RunsOf(MeshRow m)
    {
        if (m.Runs is not { } runs) return new[] { (-1, 0, m.TriangleCount) };
        var r = new (int, int, int)[runs.Length / 2];
        for (int i = 0, at = 0; i < r.Length; at += runs[i * 2 + 1], i++) r[i] = (runs[i * 2], at, runs[i * 2 + 1]);
        return r;
    }

    /// <summary>
    /// A texture of the rig for an image (#973): the same PNG already here, else added while the caps
    /// allow; -1 when they do not (its faces keep their colours).
    /// </summary>
    public int AddTexture(MvrImage image)
    {
        for (int i = 0; i < Textures.Count; i++)
            if (Textures[i].AsSpan().SequenceEqual(image.Png)) return i;
        if (Textures.Count >= MaxTextures || image.Png.Length > MaxTextureFileBytes || Textures.Sum(t => t.Length) + image.Png.Length > MaxTextureBytes
            || image.Width > MaxTextureSize || image.Height > MaxTextureSize)
            return -1;
        Textures.Add(image.Png);
        return Textures.Count - 1;
    }

    /// <summary>Drops the textures no mesh or body draws (their meshes were dropped to fit), renumbering the runs.</summary>
    public void CompactTextures()
    {
        var used = new bool[Textures.Count];
        var rows = Meshes.Concat(Bodies).ToList();
        foreach (var r in rows)
            for (int i = 0; r.Runs != null && i < r.Runs.Length; i += 2)
                if (r.Runs[i] >= 0 && r.Runs[i] < used.Length) used[r.Runs[i]] = true;
        if (used.All(u => u)) return;
        var map = new int[used.Length];
        var kept = new List<byte[]>();
        for (int i = 0; i < used.Length; i++)
        {
            map[i] = used[i] ? kept.Count : -1;
            if (used[i]) kept.Add(Textures[i]);
        }
        Textures = kept;
        foreach (var r in rows)
            for (int i = 0; r.Runs != null && i < r.Runs.Length; i += 2)
                if (r.Runs[i] >= 0 && r.Runs[i] < map.Length) r.Runs[i] = map[r.Runs[i]];
    }

    /// <summary>Every object moved by <paramref name="by"/> (someone else's file into the stadium frame).</summary>
    public void MoveObjects(Transform3D by)
    {
        foreach (var o in Objects) o.Xf = X(by * X(o.Xf));
    }

    /// <summary>The objects' share of <see cref="Check"/>: counts, every index inside its mesh, the caps, places near the pitch.</summary>
    private string? CheckObjects()
    {
        if (CheckTextures() is { } bad) return bad;
        if (Meshes.Count > MaxMeshes) return $"{Meshes.Count} meshes (0..{MaxMeshes})";
        if (Objects.Count > MaxObjects) return $"{Objects.Count} objects (0..{MaxObjects})";
        long tris = 0, bytes = 0, drawn = 0;
        for (int m = 0; m < Meshes.Count; m++)
        {
            var mesh = Meshes[m];
            if (mesh.Vertices.Length % 6 != 0 || mesh.Indices.Length % 6 != 0) return $"mesh {m}: broken arrays";
            if (mesh.TriangleCount is < 1 or > MaxMeshTriangles) return $"mesh {m}: {mesh.TriangleCount} triangles (1..{MaxMeshTriangles})";
            if (mesh.Min is not { Length: 3 } || mesh.Size is not { Length: 3 }) return $"mesh {m}: no box";
            var lo = V(mesh.Min);
            var size = V(mesh.Size);
            if (!Finite(lo) || !Finite(size) || size.X < 0 || size.Y < 0 || size.Z < 0 || Reach(lo) > MaxMeshReach || Reach(lo + size) > MaxMeshReach)
                return $"mesh {m}: its box";
            int verts = mesh.VertexCount;
            for (int i = 0; i < mesh.Indices.Length; i += 2)
                if (BitConverter.ToUInt16(mesh.Indices, i) >= verts) return $"mesh {m}: an index past its vertices";
            if (CheckLooks(mesh) is { } look) return $"mesh {m}: {look}";
            tris += mesh.TriangleCount;
            bytes += mesh.Bytes;
        }
        if (tris > MaxSceneTriangles) return $"{tris} triangles in the objects' meshes (0..{MaxSceneTriangles})";
        if (bytes > MaxSceneBytes) return $"{bytes} bytes of meshes (0..{MaxSceneBytes})";
        foreach (var o in Objects)
        {
            if (o.Mesh < 0 || o.Mesh >= Meshes.Count) return "an object of no mesh";
            if (o.Xf is not { Length: 12 } || o.Xf.Any(f => !float.IsFinite(f))) return "an object's transform";
            var x = X(o.Xf);
            if (MathF.Abs(x.Origin.X) > MaxObjectReach || MathF.Abs(x.Origin.Z) > MaxObjectReach || x.Origin.Y is < -50 or > 300)
                return $"an object placed {x.Origin} (within {MaxObjectReach} m of the centre spot)";
            if (x.Basis.Column0.Length() > 100 || x.Basis.Column1.Length() > 100 || x.Basis.Column2.Length() > 100) return "an object scaled over 100 times";
            drawn += Meshes[o.Mesh].TriangleCount;
        }
        if (drawn > MaxDrawnTriangles) return $"{drawn} triangles drawn (0..{MaxDrawnTriangles})";
        return null;
    }

    /// <summary>The textures' share of <see cref="Check"/>: how many, PNGs of their size, the bytes. Clients decode them; the server never does.</summary>
    private string? CheckTextures()
    {
        if (Textures.Count > MaxTextures) return $"{Textures.Count} textures (0..{MaxTextures})";
        long bytes = 0;
        for (int i = 0; i < Textures.Count; i++)
        {
            var t = Textures[i];
            if (t == null || t.Length > MaxTextureFileBytes) return $"texture {i}: over {MaxTextureFileBytes} bytes";
            if (MvrImages.PngSize(t) is not { W: <= MaxTextureSize, H: <= MaxTextureSize }) return $"texture {i}: not a PNG of at most {MaxTextureSize} pixels a side";
            bytes += t.Length;
        }
        return bytes > MaxTextureBytes ? $"{bytes} bytes of textures (0..{MaxTextureBytes})" : null;
    }

    /// <summary>A mesh row's looks (#973): arrays of its vertex count, runs covering its triangles, textures that exist, coordinates for them.</summary>
    private string? CheckLooks(MeshRow mesh)
    {
        int verts = mesh.VertexCount;
        if (mesh.VertexColors != null && mesh.VertexColors.Length != verts * 3) return "vertex colours not one a vertex";
        if (mesh.Runs is { } runs)
        {
            if (runs.Length == 0 || runs.Length % 2 != 0) return "broken runs";
            long sum = 0;
            for (int i = 0; i < runs.Length; i += 2)
            {
                if (runs[i] < -1 || runs[i] >= Textures.Count) return "a run of no texture";
                if (runs[i + 1] < 1) return "an empty run";
                sum += runs[i + 1];
            }
            if (sum != mesh.TriangleCount) return "runs not covering its triangles";
        }
        if (mesh.Uvs == null != (mesh.Runs == null) || mesh.Uvs == null != (mesh.UvMin == null) || mesh.Uvs == null != (mesh.UvSize == null))
            return "texture coordinates without runs, or runs without them";
        if (mesh.Uvs != null)
        {
            if (mesh.Uvs.Length != verts * 4) return "texture coordinates not one a vertex";
            if (mesh.UvMin is not { Length: 2 } lo || mesh.UvSize is not { Length: 2 } size || !float.IsFinite(lo[0]) || !float.IsFinite(lo[1])
                || !float.IsFinite(size[0]) || !float.IsFinite(size[1]) || size[0] < 0 || size[1] < 0
                || MathF.Abs(lo[0]) > MaxUvReach || MathF.Abs(lo[1]) > MaxUvReach || MathF.Abs(lo[0] + size[0]) > MaxUvReach || MathF.Abs(lo[1] + size[1]) > MaxUvReach)
                return "texture coordinates' box";
        }
        return null;
    }

    private static float Reach(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
