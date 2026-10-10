using Godot;

namespace UnitSport.Movie;

/// <summary>
/// The recorded world's meshes on disk (#775): each <see cref="WorldLook"/> a project uses is saved
/// once as a tiny packed scene, a lone <see cref="MeshInstance3D"/> wearing it, in
/// <c>user://movies/looks/</c>, with its generated mesh and materials inside and the game's own
/// shaders referenced by path. Projects share the files: a look saved once is never saved again.
/// </summary>
public static class WorldLookStore
{
    public const string Folder = "user://movies/looks";
    private static readonly Dictionary<string, WorldLook> Loaded = new();

    /// <summary>Writes every look of <paramref name="p"/> not on disk yet. The number written.</summary>
    public static int Store(MovieProject p)
    {
        int written = 0;
        foreach (var track in p.Worlds)
            foreach (var look in track.Looks)
            {
                if (look.Asset != null || look.Mesh == null) continue;
                DirAccess.MakeDirRecursiveAbsolute(Folder);
                string name = $"{Guid.NewGuid():N}.scn";
                var node = new MeshInstance3D();
                Dress(node, look);
                var scene = new PackedScene();
                var packed = scene.Pack(node);
                node.Free();
                if (packed != Error.Ok || ResourceSaver.Save(scene, $"{Folder}/{name}", ResourceSaver.SaverFlags.Compress) != Error.Ok)
                {
                    GD.PushWarning($"[movie] could not save a world look ({packed})");
                    continue;
                }
                look.Asset = name;
                Loaded[name] = look;
                written++;
            }
        return written;
    }

    /// <summary>Loads the meshes of every look of <paramref name="p"/> from its file. The number missing.</summary>
    public static int Resolve(MovieProject p)
    {
        int missing = 0;
        foreach (var track in p.Worlds)
            for (int i = 0; i < track.Looks.Length; i++)
            {
                var look = track.Looks[i];
                if (look.Mesh != null || string.IsNullOrEmpty(look.Asset)) continue;
                if (Loaded.TryGetValue(look.Asset, out var known)) { track.Looks[i] = known; continue; }
                string path = $"{Folder}/{look.Asset}";
                if (!ResourceLoader.Exists(path) || ResourceLoader.Load<PackedScene>(path)?.Instantiate() is not MeshInstance3D node)
                {
                    missing++;
                    continue;
                }
                look.Mesh = node.Mesh;
                look.Override = node.MaterialOverride;
                int surfaces = node.Mesh?.GetSurfaceCount() ?? 0;
                var overrides = new Material?[surfaces];
                bool any = false;
                for (int s = 0; s < surfaces; s++) any |= (overrides[s] = node.GetSurfaceOverrideMaterial(s)) != null;
                look.Surfaces = any ? overrides : Array.Empty<Material?>();
                look.RangeEnd = node.VisibilityRangeEnd;
                look.Shadow = node.CastShadow;
                node.Free();
                Loaded[look.Asset] = look;
            }
        return missing;
    }

    internal static void Dress(MeshInstance3D m, WorldLook look)
    {
        m.Mesh = look.Mesh;
        m.MaterialOverride = look.Override;
        for (int s = 0; s < look.Surfaces.Length; s++) m.SetSurfaceOverrideMaterial(s, look.Surfaces[s]);
        m.VisibilityRangeEnd = look.RangeEnd;
        m.CastShadow = look.Shadow;
    }
}
