using Godot;
using UnitSport.Interiors;

namespace UnitSport.Items;

public enum OutlineKind : byte { None, Node, Piece, Box }

/// <summary>Which of the other pointers' targets E takes a target before (<see cref="InteractOutline.Outranks"/>).</summary>
public enum Outranking : byte { None, Vehicle, ItemAndVehicle }

/// <summary>
/// The thing E (the pad's interact, a VR grip) would act on, as what to outline (#1286):
/// <list type="bullet">
/// <item><see cref="OutlineKind.Node"/>: a node of its own (a door leaf, a crate, a chest), outlined as it is.</item>
/// <item><see cref="OutlineKind.Piece"/>: a piece of an interior's furniture (furniture index
/// <see cref="Index"/>, one <see cref="Half"/> of it or all), baked into the interior's one mesh: built
/// alone into a proxy (<see cref="InteriorMeshBuilder.Piece"/>).</item>
/// <item><see cref="OutlineKind.Box"/>: a box <see cref="Size"/> at <see cref="At"/> in <see cref="Node"/>'s
/// frame (the world's when null): a lift's call button, a door shut in a facade.</item>
/// </list>
/// A value, compared field by field: the proxy is built only when it changes.
/// </summary>
public readonly record struct Outline(OutlineKind Kind, Node3D? Node, int Index = -1, int Half = 0,
    Transform3D At = default, Vector3 Size = default)
{
    public static Outline Of(Node3D? node) => node != null ? new(OutlineKind.Node, node) : default;

    public static Outline Piece(InteriorNode node, int furniture, int half = 0) => new(OutlineKind.Piece, node, furniture, half);

    /// <summary>An axis-aligned box from <paramref name="min"/> to <paramref name="max"/> in <paramref name="frame"/>.</summary>
    public static Outline Box(Node3D? frame, Vector3 min, Vector3 max) =>
        new(OutlineKind.Box, frame, At: new Transform3D(Basis.Identity, (min + max) / 2), Size: max - min);

    public static Outline Box(Node3D? frame, Transform3D at, Vector3 size) => new(OutlineKind.Box, frame, At: at, Size: size);

    /// <summary>
    /// The box round a mesh drawn at <paramref name="at"/> in <paramref name="frame"/>: one instance of a
    /// MultiMesh (a trial's post, a summit cross), which has no node of its own.
    /// </summary>
    public static Outline Around(Node3D frame, Mesh mesh, Transform3D at)
    {
        var box = mesh.GetAabb();
        return Box(frame, at * new Transform3D(Basis.Identity, box.GetCenter()), box.Size);
    }

    public bool IsSome => Kind != OutlineKind.None;
}

/// <summary>
/// What E would do and to what (#1286): the prompt's words and the outline, picked by one call so the
/// two never disagree. Every prompt source of a thing returns one (<c>InteractOutlineTests</c> checks it).
/// <see cref="InBar"/>: the words are a prompt bar row (no key tag), not the floating prompt's line.
/// </summary>
public readonly record struct Interaction(string Text, Outline Target, bool InBar = false);

/// <summary>
/// The outline over what E would act on, for everything that is not a world item
/// (<see cref="Highlight.Pointed"/>) or a vehicle's door (<see cref="Vehicles.VehicleReach"/>): one
/// at a time, moved by <see cref="Point"/> where the prompt is picked (<c>InteriorManager.UpdatePrompt</c>,
/// <c>InteriorManager.Interacting</c>).
///
/// <para>
/// A node is outlined as <see cref="Highlight.Set"/> does a dropped item. A piece of furniture or a box
/// gets a hidden proxy: a mesh at its place whose only material is <see cref="Highlight.Material"/>
/// (the stencil mark draws nothing, the nudged passes the edge), so only the border shows, over the
/// interior's own baked piece. Built when the target changes, never per frame
/// (docs/notes/general/no-mesh-rebuild-per-frame.md).
/// </para>
/// </summary>
public static class InteractOutline
{
    /// <summary>What is outlined now.</summary>
    public static Outline Current { get; private set; }

    /// <summary>
    /// What E takes <see cref="Current"/> before: a crate at the feet comes before a pointed world item and
    /// a vehicle's door, a crane's ladder or a farm stand before the vehicle only. Their borders stay off
    /// (<see cref="ItemController"/>), so only one ever shows.
    /// </summary>
    public static Outranking Outranks { get; set; }

    /// <summary>The proxy of a piece or a box, while one is outlined.</summary>
    public static MeshInstance3D? Proxy { get; private set; }

    private static readonly StringName ProxyName = "InteractOutline";

    /// <summary>Outlines <paramref name="target"/> (default for nothing): the border moves over on change only.</summary>
    public static void Point(Outline target)
    {
        if (target.Node != null && !GodotObject.IsInstanceValid(target.Node)) target = default;
        if (target == Current && Live()) return;
        Clear();
        Current = target;
        switch (target.Kind)
        {
            case OutlineKind.Node:
                Highlight.Set(target.Node, true);
                break;
            case OutlineKind.Piece when target.Node is InteriorNode interior
                                        && target.Index >= 0 && target.Index < interior.Layout.Furniture.Count:
                Show(interior, Transform3D.Identity, MeshOf(InteriorMeshBuilder.Piece(interior.Layout, target.Index, target.Half)));
                break;
            case OutlineKind.Box:
                Node? frame = target.Node != null ? target.Node : (Engine.GetMainLoop() as SceneTree)?.Root;
                if (frame != null) Show(frame, target.At, new BoxMesh { Size = target.Size });
                break;
        }
    }

    /// <summary>Whether what <see cref="Current"/> drew is still there (an interior left, a leaf freed).</summary>
    private static bool Live() => Current.Kind switch
    {
        OutlineKind.None => true,
        OutlineKind.Node => GodotObject.IsInstanceValid(Current.Node),
        _ => Proxy != null && GodotObject.IsInstanceValid(Proxy) && Proxy.IsInsideTree(),
    };

    private static void Clear()
    {
        if (Current.Kind == OutlineKind.Node && GodotObject.IsInstanceValid(Current.Node)) Highlight.Set(Current.Node, false);
        if (Proxy != null && GodotObject.IsInstanceValid(Proxy)) Proxy.QueueFree();
        Proxy = null;
        Current = default;
    }

    private static void Show(Node parent, Transform3D at, Mesh mesh)
    {
        Proxy = new MeshInstance3D
        {
            Name = ProxyName, Mesh = mesh, Transform = at, MaterialOverride = Highlight.Material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(Proxy);
    }

    private static ArrayMesh MeshOf(Vector3[] vertices)
    {
        var mesh = new ArrayMesh();
        if (vertices.Length == 0) return mesh;
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
