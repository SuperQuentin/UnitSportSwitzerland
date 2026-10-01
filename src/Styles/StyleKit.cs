using System.Collections.Generic;
using Godot;
using UnitSport.Core;

namespace UnitSport.Styles;

/// <summary>How the world looks (docs/plans/visual-styles.md). Client-only: never on the wire.</summary>
public enum VisualStyle
{
    Ps1,
    Cartoon,
    /// <summary>Textures and lit materials on the Mobile renderer.</summary>
    RealisticLow,
    /// <summary>Realistic− plus Forward+ effects; needs the Forward+ renderer.</summary>
    RealisticHigh,
}

/// <summary>What a world material is for. Each style answers with its own shader, or borrows one.</summary>
public enum MaterialRole
{
    Terrain,
    Road,
    Building,
    /// <summary>The 3D trees near the camera.</summary>
    Tree,
    /// <summary>The camera-facing billboard trees beyond <see cref="StyleKit.TreeNear"/>.</summary>
    TreeFar,
    Water,
    /// <summary>Occasion decor and creatures.</summary>
    Prop,
    Interior,
    /// <summary>The GPX track ribbon.</summary>
    Path,
    /// <summary>Falling snow.</summary>
    Precip,
}

/// <summary>
/// Hands out the world materials for the current <see cref="VisualStyle"/>.
///
/// <para>
/// Each style names a parent, and a role a style has no shader for resolves through the chain
/// Realistic+ → Realistic− → Cartoon → PS1. PS1 is the base: the only complete style, with every
/// role. A new visual is built once, in the base, and the other styles borrow it until they get
/// their own. <c>--style-report</c> lists what each style borrows.
/// </para>
/// </summary>
public static class StyleKit
{
    public static VisualStyle Style => GameSettings.Current.VisualStyle;

    /// <summary>The style a style borrows from; null for the base.</summary>
    public static VisualStyle? Parent(VisualStyle style) => style switch
    {
        VisualStyle.RealisticHigh => VisualStyle.RealisticLow,
        VisualStyle.RealisticLow => VisualStyle.Cartoon,
        VisualStyle.Cartoon => VisualStyle.Ps1,
        _ => null,
    };

    private const VisualStyle Base = VisualStyle.Ps1;

    /// <summary>Each style's own shaders. The base must have every role (<see cref="Report"/> checks).</summary>
    private static readonly Dictionary<VisualStyle, Dictionary<MaterialRole, string>> Shaders = new()
    {
        [VisualStyle.Ps1] = new()
        {
            [MaterialRole.Terrain] = "res://shaders/ps1_terrain.gdshader",
            [MaterialRole.Road] = "res://shaders/ps1_road.gdshader",
            [MaterialRole.Building] = "res://shaders/ps1_building.gdshader",
            [MaterialRole.Tree] = "res://shaders/ps1_tree.gdshader",
            [MaterialRole.TreeFar] = "res://shaders/ps1_treefar.gdshader",
            [MaterialRole.Water] = "res://shaders/ps1_water.gdshader",
            [MaterialRole.Prop] = "res://shaders/ps1_prop.gdshader",
            [MaterialRole.Interior] = "res://shaders/ps1_interior.gdshader",
            [MaterialRole.Path] = "res://shaders/ps1_path.gdshader",
            [MaterialRole.Precip] = "res://shaders/ps1_snowfall.gdshader",
        },
        [VisualStyle.Cartoon] = new(),
        [VisualStyle.RealisticLow] = new(),
        [VisualStyle.RealisticHigh] = new(),
    };

    /// <summary>Which style's shader a role resolves to in <paramref name="style"/>, and its path.</summary>
    public static (VisualStyle From, string Path) Resolve(VisualStyle style, MaterialRole role)
    {
        for (VisualStyle? s = style; s is { } at; s = Parent(at))
            if (Shaders[at].TryGetValue(role, out var path))
                return (at, path);
        throw new KeyNotFoundException($"the base style has no {role} shader");
    }

    public static Shader Shader(MaterialRole role) => GD.Load<Shader>(Resolve(Style, role).Path);

    /// <summary>
    /// A new material for <paramref name="role"/> in the current style, with the style's own
    /// settings for that role already applied.
    /// </summary>
    public static ShaderMaterial Material(MaterialRole role)
    {
        var m = new ShaderMaterial { Shader = Shader(role) };
        switch (role)
        {
            case MaterialRole.Tree:
                m.SetShaderParameter("tree_lod", TreeLod);
                m.SetShaderParameter("tree_near", TreeNear);
                break;
            case MaterialRole.TreeFar:
                m.SetShaderParameter("tree_near", TreeNear);
                break;
        }
        return m;
    }

    // --- trees -------------------------------------------------------------------------------
    // Forest tiles hold up to 60k trees. Beyond TreeNear a tree is a camera-facing billboard,
    // inside it the 3D tree, crossfaded per tree with a dither.

    /// <summary>Whether far trees are billboards. "--tree-lod off" keeps every tree 3D, as before.</summary>
    public static bool TreeLod { get; } = ArgValue("--tree-lod") is not ("off" or "0" or "false");

    /// <summary>Where the 3D trees hand over to billboards, in metres. "--tree-near m" overrides it.</summary>
    public static float TreeNear { get; } =
        float.TryParse(ArgValue("--tree-near"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float m) ? m : 220f;

    /// <summary>
    /// Visibility range for a whole tile's 3D tree MultiMesh, whose trees lie in
    /// <paramref name="bounds"/>: Godot measures it to the AABB's centre, so this is the per-tree
    /// range, its crossfade and the half-diagonal. The height counts: a mountain tile's bounds
    /// reach from the valley floor to the summit, and its centre is far above a valley camera.
    /// </summary>
    public static float TreeNearRange(Aabb bounds) => TreeNear + TreeFade + bounds.Size.Length() * 0.5f;

    /// <summary>The crossfade's width, as in the tree shaders' <c>tree_fade</c>.</summary>
    private const float TreeFade = 40f;

    /// <summary>
    /// The billboard material the tile builds put their far trees in; null leaves every tree 3D.
    /// Set on the main thread before the first tile builds, read by the build workers.
    /// </summary>
    public static ShaderMaterial? TreeFarMaterial { get; set; }

    // --- --style-report ------------------------------------------------------------------------

    public static bool ReportRequested => System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--style-report") >= 0;

    /// <summary>
    /// <c>--style-report</c>: every role each style borrows, and from which style. Fails if the
    /// base misses a role or a listed shader does not exist. Returns the exit code.
    /// </summary>
    public static int Report()
    {
        int failures = 0;
        foreach (var role in System.Enum.GetValues<MaterialRole>())
        {
            if (!Shaders[Base].ContainsKey(role))
            {
                GD.PrintErr($"[style-report] FAIL: the base style {Base} has no {role} shader");
                failures++;
            }
        }
        foreach (var (style, roles) in Shaders)
            foreach (var (role, path) in roles)
                if (!ResourceLoader.Exists(path))
                {
                    GD.PrintErr($"[style-report] FAIL: {style} {role}: {path} does not exist");
                    failures++;
                }
        if (failures == 0)
        {
            foreach (var style in System.Enum.GetValues<VisualStyle>())
            {
                var borrowed = new List<string>();
                foreach (var role in System.Enum.GetValues<MaterialRole>())
                {
                    var (from, _) = Resolve(style, role);
                    if (from != style) borrowed.Add($"{role}<-{from}");
                }
                GD.Print($"[style-report] {style}: {(borrowed.Count == 0 ? "complete" : $"borrows {borrowed.Count}: {string.Join(" ", borrowed)}")}");
            }
        }
        GD.Print($"RESULT {(failures == 0 ? "PASS" : $"FAIL ({failures})")}");
        return failures == 0 ? 0 : 1;
    }

    private static string? ArgValue(string flag)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
