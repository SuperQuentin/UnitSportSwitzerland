using System.Text.Json.Serialization;
using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// What an MVR holds besides its fixtures (#958): trusses, supports, scene objects, screens' and
/// projectors' bodies, focus points. Each distinct 3D file is one simplified mesh (positions quantised
/// to 16 bits over its box, 16-bit indices, one colour), each object an instance of one by its
/// transform, so two hundred identical truss pieces cost one mesh. In the rig's frame (metres, Y up),
/// moved with the fixtures by <see cref="StagePlan.FromStage"/>. Hard caps, checked by the server and
/// every client (<see cref="Check"/>): the import decimates or drops to fit them, and says so.
/// </summary>
public sealed partial class RigData
{
    /// <summary>The limits on a rig's objects: meshes, instances, triangles per mesh, in all, drawn, and the meshes' bytes.</summary>
    public const int MaxMeshes = 4096, MaxObjects = 20_000, MaxMeshTriangles = 20_000, MaxSceneTriangles = 150_000,
        MaxDrawnTriangles = 500_000, MaxSceneBytes = 1_200_000;
    /// <summary>How far from the centre spot an object may stand, and its mesh reach from its origin (m).</summary>
    public const float MaxObjectReach = 450f, MaxMeshReach = 1000f;

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

        [JsonIgnore] public int VertexCount => Vertices.Length / 6;
        [JsonIgnore] public int TriangleCount => Indices.Length / 6;
        [JsonIgnore] public int Bytes => Vertices.Length + Indices.Length;
    }

    public sealed class ObjectRow
    {
        [JsonPropertyName("m")] public int Mesh { get; set; }
        /// <summary>The basis' three columns, then the origin.</summary>
        [JsonPropertyName("x")] public float[] Xf { get; set; } = new float[12];
    }

    [JsonPropertyName("meshes")] public List<MeshRow> Meshes { get; set; } = new();
    [JsonPropertyName("obj")] public List<ObjectRow> Objects { get; set; } = new();

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
    public static MeshRow Quantise(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, Color srgb)
    {
        var lo = Vector3.Inf;
        var hi = -Vector3.Inf;
        foreach (int i in indices)
        {
            lo = lo.Min(positions[i]);
            hi = hi.Max(positions[i]);
        }
        if (indices.Count == 0) lo = hi = Vector3.Zero;
        var size = hi - lo;
        var weld = new Dictionary<long, int>();
        var verts = new List<ushort>();
        var idx = new List<ushort>();
        Span<int> tri = stackalloc int[3];
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                var p = positions[indices[t + k]];
                ushort Q(float v, float l, float s) => s > 0 ? (ushort)Math.Clamp(MathF.Round((v - l) / s * 65535f), 0f, 65535f) : (ushort)0;
                ushort x = Q(p.X, lo.X, size.X), y = Q(p.Y, lo.Y, size.Y), z = Q(p.Z, lo.Z, size.Z);
                long key = (long)x << 32 | (long)y << 16 | z;
                if (!weld.TryGetValue(key, out int v))
                {
                    if (weld.Count >= 65535) throw new ArgumentException("over 65535 vertices in one mesh");
                    weld[key] = v = weld.Count;
                    verts.Add(x); verts.Add(y); verts.Add(z);
                }
                tri[k] = v;
            }
            if (tri[0] == tri[1] || tri[1] == tri[2] || tri[0] == tri[2]) continue;
            idx.Add((ushort)tri[0]); idx.Add((ushort)tri[1]); idx.Add((ushort)tri[2]);
        }
        var c = srgb.Clamp();
        return new MeshRow
        {
            Color = (uint)c.R8 << 24 | (uint)c.G8 << 16 | (uint)c.B8 << 8 | 0xFF,
            Min = V(lo), Size = V(size),
            Vertices = U16(verts), Indices = U16(idx),
        };
    }

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

    /// <summary>Every object moved by <paramref name="by"/> (someone else's file into the stadium frame).</summary>
    public void MoveObjects(Transform3D by)
    {
        foreach (var o in Objects) o.Xf = X(by * X(o.Xf));
    }

    /// <summary>The objects' share of <see cref="Check"/>: counts, every index inside its mesh, the caps, places near the pitch.</summary>
    private string? CheckObjects()
    {
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

    private static float Reach(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
