using Godot;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// The glowing border on the world item the local player points at, and which item that is.
///
/// <para>
/// The border is an inverted hull drawn as a <see cref="GeometryInstance3D.MaterialOverlay"/>: the
/// same mesh again, pushed outward and drawn back faces only, so the object itself hides all of it
/// but a rim. Pushed away from the mesh's own centre rather than along its normals: the items are
/// flat-shaded, and a hull split at every hard edge would show cracks. The push grows with distance,
/// so the rim stays about the same width on screen, and it breathes.
/// </para>
/// </summary>
public static class Highlight
{
    private const string Shader = """
        shader_type spatial;
        render_mode unshaded, cull_front, shadows_disabled, fog_disabled;
        instance uniform vec3 center = vec3(0.0);
        uniform vec4 color : source_color = vec4(1.0, 0.86, 0.32, 1.0);
        uniform float width = 0.008;
        void vertex() {
            vec3 dir = VERTEX - center;
            dir = length(dir) > 0.0001 ? normalize(dir) : NORMAL;
            float d = length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz);
            float pulse = 1.0 + 0.3 * sin(TIME * 6.5);
            // in the mesh's own units: a blown-up floating item keeps the same rim
            VERTEX += dir * width * clamp(d, 0.6, 14.0) * pulse / max(length(MODEL_MATRIX[0].xyz), 0.001);
        }
        void fragment() {
            float pulse = 0.75 + 0.25 * sin(TIME * 6.5);
            ALBEDO = color.rgb * (0.8 + 0.4 * pulse);
        }
        """;

    private static ShaderMaterial? _material;
    public static ShaderMaterial Material => _material ??= new ShaderMaterial { Shader = new Shader { Code = Shader } };

    /// <summary>
    /// A warm pulsing glow laid over the surface itself, then the border: for parts set flush in
    /// something bigger (a car door in its body), whose rim the body around them hides (#261).
    /// </summary>
    private const string TintShader = """
        shader_type spatial;
        render_mode unshaded, blend_add, depth_draw_never, cull_back, shadows_disabled, fog_disabled;
        uniform vec4 color : source_color = vec4(1.0, 0.82, 0.3, 1.0);
        void vertex() {
            VERTEX += NORMAL * 0.004;
        }
        void fragment() {
            float pulse = 0.5 + 0.5 * sin(TIME * 6.5);
            ALBEDO = color.rgb * (0.12 + 0.16 * pulse);
        }
        """;

    private static ShaderMaterial? _tint;
    private static ShaderMaterial Tint => _tint ??= new ShaderMaterial { Shader = new Shader { Code = TintShader }, NextPass = Material };

    /// <summary>
    /// Draws (or removes) the border on every mesh under <paramref name="root"/>; with
    /// <paramref name="tint"/> the surfaces glow too (a flush part, whose rim does not show).
    /// </summary>
    public static void Set(Node? root, bool on, bool tint = false)
    {
        if (root == null || !GodotObject.IsInstanceValid(root)) return;
        if (root is MeshInstance3D mi && mi.Mesh != null)
        {
            if (on)
            {
                mi.SetInstanceShaderParameter("center", mi.Mesh.GetAabb().GetCenter());
                // a car door's window: the hull would show through the glass as a solid sheet, so
                // the border goes on the opaque surfaces only, as a second pass of each (#261)
                if (tint || HasGlass(mi.Mesh)) OutlineSurfaces(mi, tint ? Tint : Material);
                else mi.MaterialOverlay = Material;
            }
            else
            {
                if (mi.MaterialOverlay == Material) mi.MaterialOverlay = null;
                if (mi.HasMeta(SurfacesMeta)) RestoreSurfaces(mi);
            }
        }
        foreach (var child in root.GetChildren())
            if (child is not CpuParticles3D) Set(child, on, tint);
    }

    private const string SurfacesMeta = "highlight_surfaces";

    private static bool HasGlass(Mesh mesh)
    {
        if (mesh is not ArrayMesh am) return false;
        for (int i = 0; i < am.GetSurfaceCount(); i++)
            if (am.SurfaceGetName(i) == Avatar.MeshScratch.GlassSurface) return true;
        return false;
    }

    private static void OutlineSurfaces(MeshInstance3D mi, Material pass)
    {
        if (mi.HasMeta(SurfacesMeta) || mi.Mesh is not ArrayMesh am) return;
        var originals = new Godot.Collections.Array();
        for (int i = 0; i < am.GetSurfaceCount(); i++)
        {
            var original = mi.GetSurfaceOverrideMaterial(i);
            originals.Add(original);
            if (am.SurfaceGetName(i) == Avatar.MeshScratch.GlassSurface) continue;
            if ((original ?? am.SurfaceGetMaterial(i)) is not { } shown) continue;
            var outlined = (Material)shown.Duplicate();
            outlined.NextPass = pass;
            mi.SetSurfaceOverrideMaterial(i, outlined);
        }
        mi.SetMeta(SurfacesMeta, originals);
    }

    private static void RestoreSurfaces(MeshInstance3D mi)
    {
        var originals = mi.GetMeta(SurfacesMeta).AsGodotArray();
        for (int i = 0; i < originals.Count && i < mi.GetSurfaceOverrideMaterialCount(); i++)
            mi.SetSurfaceOverrideMaterial(i, originals[i].As<Material>());
        mi.RemoveMeta(SurfacesMeta);
    }

    // ---- what is pointed at ----------------------------------------------------------------------

    /// <summary>The world item the local player points at (a <see cref="DroppedItem"/> or a <see cref="RadioBody"/>), refreshed every frame by <see cref="ItemController"/>.</summary>
    public static Node3D? Pointed { get; private set; }

    /// <summary>The view's ray, every frame: one query, reused (#221).</summary>
    private static readonly Core.RayQuery Ray = new();

    /// <summary>
    /// What the view points at within reach of the body: a ray along the view first (it may hit the
    /// item's collider), else the item closest to the view's centre inside a narrow cone, else one
    /// right at the player's feet — standing on something and not seeing it outlined would be odd.
    /// </summary>
    public static Node3D? Find(FootPlayer player)
    {
        var camera = player.Camera;
        var from = camera.GlobalPosition;
        var forward = -camera.GlobalTransform.Basis.Z;
        var chest = player.GlobalPosition + Vector3.Up * 1.0f;
        float reach = DroppedItems.Reach;

        float ray = reach + from.DistanceTo(chest) + 0.5f;
        var hit = Ray.Cast(player.GetWorld3D().DirectSpaceState, from, from + forward * ray, uint.MaxValue, player.SelfExclude);
        if (hit.Count > 0 && Candidate(hit["collider"].AsGodotObject() as Node) is { } struck
            && struck.GlobalPosition.DistanceTo(chest) < reach + 0.4f)
            return struck;

        Node3D? best = null, nearest = null;
        float bestAngle = 0.32f, nearestDist = 1.1f;
        foreach (var item in Candidates())
        {
            float d = item.GlobalPosition.DistanceTo(chest);
            if (d > reach) continue;
            float angle = forward.AngleTo(item.GlobalPosition - from);
            // nearer things get a little more slack: they cover more of the screen
            float slack = angle - 0.08f * (reach - d) / reach;
            if (slack < bestAngle) { bestAngle = slack; best = item; }
            float flat = new Vector2(item.GlobalPosition.X - player.GlobalPosition.X, item.GlobalPosition.Z - player.GlobalPosition.Z).Length();
            if (flat < nearestDist) { nearestDist = flat; nearest = item; }
        }
        return best ?? nearest;
    }

    private static IEnumerable<Node3D> Candidates()
    {
        if (DroppedItems.Instance is { } dropped)
            foreach (var item in dropped.Items)
                if (!dropped.IsClaimed(item)) yield return item;
        if (RadioManager.Instance is { } radios)
            foreach (var node in radios.GetChildren())
                if (node is RadioBody r) yield return r;
    }

    private static Node3D? Candidate(Node? node)
    {
        for (; node != null; node = node.GetParent())
            if (node is DroppedItem d) return DroppedItems.Instance?.IsClaimed(d) == true ? null : d;
            else if (node is RadioBody r) return r;
        return null;
    }

    /// <summary>Points at <paramref name="target"/> (null for nothing): moves the border over.</summary>
    internal static void Point(Node3D? target)
    {
        if (Pointed == target && (target == null || GodotObject.IsInstanceValid(target))) return;
        if (Pointed != null && GodotObject.IsInstanceValid(Pointed)) Set(Pointed, false);
        Pointed = target;
        if (target != null) Set(target, true);
    }
}
