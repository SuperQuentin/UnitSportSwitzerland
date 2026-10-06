using Godot;
using UnitSport.Player;

namespace UnitSport.Vehicles;

/// <summary>
/// One look a dormant vehicle can have (#552): the merged mesh it is drawn with and the boxes it is
/// solid with, shared by every slot of that kind, train and load.
/// </summary>
public sealed class DormantLook
{
    /// <summary>The parked model as one mesh, a surface per material; null where nothing is drawn (a server).</summary>
    public ArrayMesh? Mesh { get; init; }

    /// <summary>The parked hull and each further section's box, in the vehicle's own frame, with shared shapes.</summary>
    public required (Transform3D Pose, Shape3D Shape)[] Boxes { get; init; }

    public int Triangles { get; init; }
}

/// <summary>
/// What a dormant vehicle looks like: <b>the very model the woken vehicle is drawn with</b>,
/// <see cref="Rideable.BuildParkedVisual"/> as <see cref="VehicleBody"/> builds it, so nothing but
/// the detail of being alive changes when one wakes (it used to be a low-poly traffic car in one of
/// eight paints, or for a lorry a full cab and dashboard built afresh per slot and per redraw).
///
/// <para>
/// Built once per look and kept for the session: the node tree is walked, every visible
/// <see cref="MeshInstance3D"/> surface appended with its pose into one <see cref="SurfaceTool"/>
/// per (material, primitive, format), and the tree freed. A car park is then one
/// <see cref="MultiMesh"/> per look, and a look costs its meshes once however many slots share it.
/// </para>
/// </summary>
public static class DormantLooks
{
    public readonly record struct Key(int Kind, int Train, int Load);

    private static readonly Dictionary<Key, DormantLook?> _looks = new();

    /// <summary>A slot's look: the load to a twentieth, which no eye tells apart and keeps the set small.</summary>
    public static Key KeyOf(VehicleSlot s) => new(s.KindId, s.Train, Mathf.RoundToInt(s.Load * 20f));

    /// <summary>
    /// The look of <paramref name="key"/>, built the first time from <paramref name="create"/>'s ride.
    /// Null when the ride cannot be made. Main thread: it builds Godot nodes.
    /// </summary>
    public static DormantLook? For(Key key, Func<Rideable?> create, bool drawn)
    {
        if (_looks.TryGetValue(key, out var known) && (known == null || known.Mesh != null || !drawn)) return known;
        if (create() is not { } ride) return _looks[key] = null;

        var boxes = new List<(Transform3D, Shape3D)>();
        var parked = ride.ParkedBox;
        boxes.Add((new Transform3D(Basis.Identity, parked.Centre), new BoxShape3D { Size = parked.Size }));
        foreach (var (pose, centre, size) in ride.ExtraBoxes())
            boxes.Add((pose * new Transform3D(Basis.Identity, centre), new BoxShape3D { Size = size }));

        ArrayMesh? mesh = null;
        int triangles = 0;
        if (drawn)
        {
            var visual = ride.BuildParkedVisual(1);   // VehicleBody: Math.Max(1, Owner), and a slot is the server's
            (mesh, triangles) = Merge(visual);
            visual.Free();
            GD.Print($"[dormant] look {(RideKind)key.Kind} train {key.Train}: {triangles} triangles, {mesh.GetSurfaceCount()} surfaces");
        }
        return _looks[key] = new DormantLook { Mesh = mesh, Boxes = boxes.ToArray(), Triangles = triangles };
    }

    /// <summary>Forgets every look (a world torn down): the meshes go with the last instance using them.</summary>
    public static void Reset() => _looks.Clear();

    /// <summary>
    /// The visible meshes under <paramref name="root"/> as one mesh in its frame. Surfaces are grouped
    /// by material, primitive and vertex format: merging a surface without colours into one with them
    /// would paint it black.
    /// </summary>
    public static (ArrayMesh Mesh, int Triangles) Merge(Node3D root)
    {
        var tools = new Dictionary<(Material?, Mesh.PrimitiveType, ulong), SurfaceTool>();
        var order = new List<(Material?, Mesh.PrimitiveType, ulong)>();
        Walk(root, Transform3D.Identity, true, tools, order);

        var merged = new ArrayMesh();
        int triangles = 0;
        foreach (var key in order)
        {
            var st = tools[key];
            st.Commit(merged);
            int surface = merged.GetSurfaceCount() - 1;
            if (surface < 0) continue;
            merged.SurfaceSetMaterial(surface, key.Item1);
            if (key.Item2 == Godot.Mesh.PrimitiveType.Triangles)
                triangles += merged.SurfaceGetArrayIndexLen(surface) is > 0 and var n ? n / 3 : merged.SurfaceGetArrayLen(surface) / 3;
        }
        return (merged, triangles);
    }

    private static void Walk(Node node, Transform3D parent, bool isRoot,
        Dictionary<(Material?, Mesh.PrimitiveType, ulong), SurfaceTool> tools, List<(Material?, Mesh.PrimitiveType, ulong)> order)
    {
        var pose = parent;
        if (node is Node3D n3)
        {
            if (!n3.Visible) return;
            if (!isRoot) pose = parent * n3.Transform;
        }
        if (node is MeshInstance3D { Mesh: { } mesh } mi && mi.Skin == null)
            for (int i = 0; i < mesh.GetSurfaceCount(); i++)
            {
                var material = mi.GetActiveMaterial(i);
                var primitive = mesh is ArrayMesh am ? am.SurfaceGetPrimitiveType(i) : Godot.Mesh.PrimitiveType.Triangles;
                ulong raw = mesh is ArrayMesh fm ? (ulong)fm.SurfaceGetFormat(i) : 0;
                // a skinned surface (a rider) is posed by its skeleton, not its node: not part of a parked vehicle
                if ((raw & (ulong)Godot.Mesh.ArrayFormat.FormatBones) != 0) continue;
                ulong format = raw & AttributeMask;
                var key = (material, primitive, format);
                if (!tools.TryGetValue(key, out var st))
                {
                    tools[key] = st = new SurfaceTool();
                    st.Begin(primitive);
                    order.Add(key);
                }
                st.AppendFrom(mesh, i, pose);
            }
        foreach (var child in node.GetChildren())
            Walk(child, pose, false, tools, order);
    }

    /// <summary>The vertex attributes of a surface format, without the compression and stride flags.</summary>
    private const ulong AttributeMask = (ulong)(Godot.Mesh.ArrayFormat.FormatNormal | Godot.Mesh.ArrayFormat.FormatTangent
        | Godot.Mesh.ArrayFormat.FormatColor | Godot.Mesh.ArrayFormat.FormatTexUV | Godot.Mesh.ArrayFormat.FormatTexUV2
        | Godot.Mesh.ArrayFormat.FormatCustom0 | Godot.Mesh.ArrayFormat.FormatCustom1);
}
