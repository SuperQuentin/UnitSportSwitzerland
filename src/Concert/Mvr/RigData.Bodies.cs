using System.Text.Json.Serialization;
using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// Fixture types' own bodies (#961): a GDTF's base, yoke and head, each a mesh row of
/// <see cref="Bodies"/> (the same quantised rows as the scene objects', in the fixture's GDTF frame),
/// shared by every type that has the same part; the yoke's and head's pivots and the beam's point.
/// Capped in <see cref="Check"/> on the server and every client: <see cref="MaxBodyTriangles"/> a part,
/// <see cref="MaxBodyBytes"/> in all; the import keeps to them (a body past the budget is left off,
/// and the type draws as the generic head). Its faces' colours and textures as the objects' (#973),
/// its textures in the rig's shared <see cref="Textures"/> budget (past it: colours only).
/// </summary>
public sealed partial class RigData
{
    public const int MaxBodyMeshes = 3 * MaxTypes, MaxBodyTriangles = GdtfBody.MaxPartTriangles, MaxBodyBytes = 300_000;
    /// <summary>How far a body's mesh and pivots may reach from the fixture's origin (m).</summary>
    public const float MaxBodyReach = 5f;

    public sealed class BodyRow
    {
        /// <summary>Indices into <see cref="Bodies"/>; -1 for a part it has not.</summary>
        [JsonPropertyName("b")] public int Base { get; set; } = -1;
        [JsonPropertyName("y")] public int Yoke { get; set; } = -1;
        [JsonPropertyName("h")] public int Head { get; set; } = -1;
        [JsonPropertyName("yp")] public float[] YokePivot { get; set; } = new float[3];
        [JsonPropertyName("hp")] public float[] HeadPivot { get; set; } = new float[3];
        [JsonPropertyName("at")] public float[] BeamAt { get; set; } = new float[3];
    }

    [JsonPropertyName("bm")] public List<MeshRow> Bodies { get; set; } = new();

    /// <summary>
    /// Gives a type row its body: each part quantised and shared with an equal one already here. False
    /// (no body) when it has none or the rig's body budget is spent.
    /// </summary>
    public bool AddBody(TypeRow row, GdtfBody? body)
    {
        if (body == null) return false;
        // its textures while the shared budget lasts (#973); a body refused below leaves them unused: dropped again
        var parts = new[] { body.Base, body.Yoke, body.Head }
            .Select(m => m == null ? null : Quantise(m, AddTexture)).Select(r => r is { TriangleCount: > 0 } ? r : null).ToArray();
        if (parts.All(p => p == null)) { CompactTextures(); return false; }
        int bytes = Bodies.Sum(b => b.Bytes);
        var index = new int[3];
        int added = 0;
        for (int k = 0; k < 3; k++)
        {
            index[k] = -1;
            if (parts[k] is not { } p) continue;
            index[k] = Bodies.FindIndex(b => b.Same(p));
            if (index[k] < 0) { bytes += p.Bytes; added++; }
        }
        if (bytes > MaxBodyBytes || Bodies.Count + added > MaxBodyMeshes) { CompactTextures(); return false; }
        for (int k = 0; k < 3; k++)
            if (parts[k] is { } p && index[k] < 0)
            {
                index[k] = Bodies.Count;
                Bodies.Add(p);
            }
        row.Body = new BodyRow
        {
            Base = index[0], Yoke = index[1], Head = index[2],
            YokePivot = V(body.YokePivot), HeadPivot = V(body.HeadPivot), BeamAt = V(body.BeamAt),
        };
        return true;
    }

    /// <summary>The row, given its body when it has one and the budget allows.</summary>
    public TypeRow WithBody(TypeRow row, GdtfBody? body)
    {
        AddBody(row, body);
        return row;
    }

    /// <summary>The bodies' share of <see cref="Check"/>: counts, every index inside its mesh, the caps, small and near the fixture.</summary>
    private string? CheckBodies()
    {
        if (Bodies.Count > MaxBodyMeshes) return $"{Bodies.Count} body meshes (0..{MaxBodyMeshes})";
        long bytes = 0;
        for (int m = 0; m < Bodies.Count; m++)
        {
            var mesh = Bodies[m];
            if (mesh.Vertices.Length % 6 != 0 || mesh.Indices.Length % 6 != 0) return $"body mesh {m}: broken arrays";
            if (mesh.TriangleCount is < 1 or > MaxBodyTriangles) return $"body mesh {m}: {mesh.TriangleCount} triangles (1..{MaxBodyTriangles})";
            if (mesh.Min is not { Length: 3 } || mesh.Size is not { Length: 3 }) return $"body mesh {m}: no box";
            var lo = V(mesh.Min);
            var size = V(mesh.Size);
            if (!Finite(lo) || !Finite(size) || size.X < 0 || size.Y < 0 || size.Z < 0 || Reach(lo) > MaxBodyReach || Reach(lo + size) > MaxBodyReach)
                return $"body mesh {m}: its box";
            int verts = mesh.VertexCount;
            for (int i = 0; i < mesh.Indices.Length; i += 2)
                if (BitConverter.ToUInt16(mesh.Indices, i) >= verts) return $"body mesh {m}: an index past its vertices";
            if (CheckLooks(mesh) is { } look) return $"body mesh {m}: {look}";
            bytes += mesh.Bytes;
        }
        if (bytes > MaxBodyBytes) return $"{bytes} bytes of bodies (0..{MaxBodyBytes})";
        foreach (var t in Types)
        {
            if (t.Body is not { } b) continue;
            foreach (int i in new[] { b.Base, b.Yoke, b.Head })
                if (i < -1 || i >= Bodies.Count) return $"type {t.Name}: a body part of no mesh";
            foreach (var p in new[] { b.YokePivot, b.HeadPivot, b.BeamAt })
                if (p is not { Length: 3 } || !Finite(V(p)) || Reach(V(p)) > MaxBodyReach) return $"type {t.Name}: a body's pivot";
        }
        return null;
    }
}
