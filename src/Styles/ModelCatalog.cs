using System.Collections.Generic;
using Godot;

namespace UnitSport.Styles;

/// <summary>
/// Imported models by semantic id, per visual style (docs/notes/styles/assets.md): a style that
/// has a model for an id gets it, a style that has none borrows its parent's through the same
/// fallback chain as the materials, and where no style down the chain has one the caller builds
/// its own procedural mesh. Gameplay never reads these meshes: an imported model is fitted to
/// the canonical dimensions (the unit tree, a car's catalogue size), never the other way round.
/// </summary>
public static class ModelCatalog
{
    /// <summary>The realistic conifer: an EZ-Tree pine, unit size (tools/trees).</summary>
    public const string TreeConifer = "tree:conifer";

    /// <summary>The realistic broadleaf: an EZ-Tree ash, unit size.</summary>
    public const string TreeBroadleaf = "tree:broadleaf";

    private static readonly Dictionary<VisualStyle, Dictionary<string, string>> Models = new()
    {
        [VisualStyle.Ps1] = new(),
        [VisualStyle.Cartoon] = new(),
        [VisualStyle.RealisticLow] = new()
        {
            [TreeConifer] = "res://assets/realistic/trees/conifer.glb",
            [TreeBroadleaf] = "res://assets/realistic/trees/broadleaf.glb",
        },
        [VisualStyle.RealisticHigh] = new(),
    };

    /// <summary>Every id any style has, for <c>--style-report</c>.</summary>
    public static IEnumerable<string> Ids
    {
        get
        {
            var ids = new SortedSet<string>();
            foreach (var models in Models.Values) ids.UnionWith(models.Keys);
            return ids;
        }
    }

    /// <summary>Which style's model an id resolves to in <paramref name="style"/>, and its path; null when none has one.</summary>
    public static (VisualStyle From, string Path)? Resolve(VisualStyle style, string id)
    {
        for (VisualStyle? s = style; s is { } at; s = StyleKit.Parent(at))
            if (Models[at].TryGetValue(id, out var path))
                return (at, path);
        return null;
    }

    /// <summary>The paths the catalogue lists, for <c>--style-report</c>'s existence check.</summary>
    public static IEnumerable<(VisualStyle Style, string Id, string Path)> All()
    {
        foreach (var (style, models) in Models)
            foreach (var (id, path) in models)
                yield return (style, id, path);
    }

    private static readonly Dictionary<string, ArrayMesh> Loaded = new();

    /// <summary>
    /// The mesh of the model <paramref name="id"/> resolves to in the applied style, or null when
    /// no style down the chain has one. Loaded once and shared: callers set surface materials on
    /// a copy (<see cref="Resource.Duplicate"/>), never on this. Main thread.
    /// </summary>
    public static ArrayMesh? Mesh(string id)
    {
        if (Resolve(StyleKit.Applied, id) is not { } found) return null;
        if (Loaded.TryGetValue(found.Path, out var cached)) return cached;
        var scene = GD.Load<PackedScene>(found.Path).Instantiate();
        ArrayMesh? mesh = null;
        foreach (var node in scene.FindChildren("*", "MeshInstance3D", true, false))
            if (((MeshInstance3D)node).Mesh is ArrayMesh m) { mesh = m; break; }
        if (mesh == null && scene is MeshInstance3D { Mesh: ArrayMesh root }) mesh = root;
        scene.Free();
        if (mesh == null)
        {
            GD.PushError($"[models] {found.Path} holds no mesh");
            return null;
        }
        Loaded[found.Path] = mesh;
        return mesh;
    }
}
