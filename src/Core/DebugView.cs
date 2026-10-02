using Godot;
using UnitSport.Styles;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>How the debug menu draws the world (#339).</summary>
public enum DebugViewMode
{
    /// <summary>The style as it is.</summary>
    Normal,

    /// <summary>Triangle edges only (Godot's wireframe debug draw).</summary>
    Wireframe,

    /// <summary>Plain grey, flat shaded from a fixed light: the shapes, with no colour, pattern or texture.</summary>
    Clay,

    /// <summary>The meshes' vertex colours (the land cover classes), flat shaded, without patterns, photos or fog.</summary>
    VertexColours,

    /// <summary>How many times each pixel is drawn (Godot's overdraw debug draw).</summary>
    Overdraw,
}

/// <summary>
/// Puts the world in a <see cref="DebugViewMode"/> and takes it out again.
///
/// <para>
/// The clay and vertex-colour views swap the shader of every world material through
/// <see cref="StyleKit.OverrideShader"/>: the five tile materials are shared by every tile, the
/// horizon and interiors have theirs in the kit too, so one swap reaches the whole streamed world
/// and whatever loads after. Figures (players, vehicles) keep their own look. Every world shader
/// is <c>unshaded</c>, which is why Godot's own "unshaded" and "lighting" debug draws would show
/// nothing new; these two replace them.
/// </para>
///
/// <para>
/// Wireframe needs the meshes to carry wireframe indices, which Godot only makes for meshes
/// created after <see cref="RenderingServer.SetDebugGenerateWireframes"/>: switching it on
/// rebuilds the tiles. Figures made before stay invisible in it.
/// </para>
/// </summary>
public static class DebugView
{
    // a fixed light and a flat normal from the screen-space derivatives, as the terrain does it;
    // no vertex(): the PS1 snap, wind and billboards are gone, which is the point of the view
    private const string Body = @"
render_mode unshaded, cull_disabled;

void fragment() {
    vec3 n = normalize(cross(dFdy(VERTEX), dFdx(VERTEX)));
    if (dot(n, VERTEX) > 0.0) {
        n = -n;
    }
    vec3 world_n = (INV_VIEW_MATRIX * vec4(n, 0.0)).xyz;
    float lit = 0.25 + 0.75 * max(dot(world_n, normalize(vec3(0.6, 0.55, 0.35))), 0.0);
    ALBEDO = BASE * lit;
}
";

    private static Shader? _clay, _colours;

    internal static Shader Clay => _clay ??= new Shader
    {
        Code = "shader_type spatial;\n#define BASE vec3(0.42, 0.41, 0.39)\n" + Body,
    };

    private static Shader Colours => _colours ??= new Shader
    {
        Code = "shader_type spatial;\n#define BASE COLOR.rgb\n" + Body,
    };

    /// <summary>
    /// Leaves <paramref name="was"/> for <paramref name="mode"/>. Main thread; <paramref name="chunks"/>
    /// may be null where there is no streamed world to rebuild.
    /// </summary>
    public static void Apply(Viewport viewport, ChunkManager? chunks, DebugViewMode mode, DebugViewMode was)
    {
        bool wire = mode == DebugViewMode.Wireframe;
        if (wire != (was == DebugViewMode.Wireframe))
        {
            RenderingServer.SetDebugGenerateWireframes(wire);
            if (wire) chunks?.RebuildVisuals();
        }
        viewport.DebugDraw = mode switch
        {
            DebugViewMode.Wireframe => Viewport.DebugDrawEnum.Wireframe,
            DebugViewMode.Overdraw => Viewport.DebugDrawEnum.Overdraw,
            _ => Viewport.DebugDrawEnum.Disabled,
        };
        StyleKit.OverrideShader(mode switch
        {
            DebugViewMode.Clay => Clay,
            DebugViewMode.VertexColours => Colours,
            _ => null,
        });
    }
}
