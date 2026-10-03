using Godot;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// The thin border around the world item the local player points at, and which item that is.
///
/// <para>
/// A stencil silhouette (#401), two passes laid over every mesh of the target as its
/// <see cref="GeometryInstance3D.MaterialOverlay"/>, so the object's own materials stay untouched:
/// <list type="number">
/// <item>The mark draws nothing but writes <see cref="StencilRef"/> where the object covers the
/// screen (depth test off: the whole silhouette, even behind the player's arm).</item>
/// <item>The edge: the same meshes drawn eight more times, each nudged two <b>pixels</b> on screen in
/// one direction, only where the mark is not (and marking what they draw, so nothing is painted
/// twice): what is left is the outer edge, the same width near and far, with no lines between the
/// object's own parts and no cracks at flat-shaded corners (no normals involved). Pulled 15 cm
/// toward the eye, it shows over the body a flush car door sits in, so that needs no glow of its
/// own, and still hides behind anything really in front.</item>
/// </list>
/// The stencil is read in the transparent pass only, hence both are transparent and ordered by
/// <see cref="Material.RenderPriority"/>. The old inverted hull, pushed from each mesh's centre,
/// was lumpy on long objects and outlined every sub-mesh.
/// </para>
/// </summary>
public static class Highlight
{
    /// <summary>The stencil value the target is marked with. Other stencil users must pick another.</summary>
    private const int StencilRef = 77;

    private const string MarkShader = """
        shader_type spatial;
        render_mode unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled, fog_disabled;
        stencil_mode write, compare_always, 77;
        void fragment() {
            ALBEDO = vec3(0.0);
            ALPHA = 1.0;
        }
        """;

    private const string EdgeShader = """
        shader_type spatial;
        render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
        // drawn only off the mark, and marks what it draws: the passes never paint a pixel twice
        stencil_mode read, write, compare_not_equal, 77;
        uniform vec4 color : source_color = vec4(1.0, 0.86, 0.36, 0.9);
        // this pass's nudge on screen, in pixels at 1080 lines (scaled with the screen)
        uniform vec2 offset = vec2(2.0, 0.0);
        void vertex() {
            vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
            // a little toward the eye along its own ray (same place on screen): it wins over the
            // body a flush door sits in, and still loses to anything really in front (a hat)
            view.xyz -= normalize(view.xyz) * min(0.15, 0.5 * length(view.xyz));
            vec4 clip = PROJECTION_MATRIX * view;
            clip.xy += offset * (VIEWPORT_SIZE.y / 1080.0) * 2.0 / VIEWPORT_SIZE * clip.w;
            POSITION = clip;
        }
        void fragment() {
            ALBEDO = color.rgb;
            ALPHA = color.a;
        }
        """;

    /// <summary>The edge's width in pixels at 1080 lines.</summary>
    private const float EdgeWidth = 2f;

    private static ShaderMaterial? _mark;

    /// <summary>
    /// The overlay put on the target's meshes: the stencil mark, then the silhouette drawn again
    /// nudged in eight directions as its next passes; their union less the mark is the edge.
    /// </summary>
    public static ShaderMaterial Material => _mark ??= Build();

    private static ShaderMaterial Build()
    {
        var mark = new ShaderMaterial { Shader = new Shader { Code = MarkShader }, RenderPriority = 100 };
        var edge = new Shader { Code = EdgeShader };
        var last = mark;
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.Pi / 4;
            var pass = new ShaderMaterial { Shader = edge, RenderPriority = 101 };
            pass.SetShaderParameter("offset", new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * EdgeWidth);
            last.NextPass = pass;
            last = pass;
        }
        return mark;
    }

    /// <summary>Draws (or removes) the border on every mesh under <paramref name="root"/>.</summary>
    public static void Set(Node? root, bool on)
    {
        if (root == null || !GodotObject.IsInstanceValid(root)) return;
        if (root is MeshInstance3D mi && mi.Mesh != null)
        {
            if (on) mi.MaterialOverlay = Material;
            else if (mi.MaterialOverlay == Material) mi.MaterialOverlay = null;
        }
        foreach (var child in root.GetChildren())
            if (child is not CpuParticles3D) Set(child, on);
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
