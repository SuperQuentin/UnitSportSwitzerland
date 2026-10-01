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

/// <summary>
/// How finely the mesh builders work for a style: rounder tubes, more segments, overhangs. Tile
/// builds carry it to their builders, which only implement <see cref="Low"/> so far.
/// </summary>
public enum MeshDetail
{
    /// <summary>Every builder as it is: PS1's.</summary>
    Low,
    High,
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
    /// <summary>
    /// The style the player chose: the setting, or <c>/style</c>'s choice for this session. The
    /// world shows it from the next <see cref="Restyle"/>.
    /// </summary>
    public static VisualStyle Style => _session ?? GameSettings.Current.VisualStyle;

    private static VisualStyle? _session;

    /// <summary>
    /// Switches style for this session only, without saving it (<c>/style</c>): the saved setting
    /// is the settings menu's, and a test run's command-line overrides must not be written over it.
    /// </summary>
    public static void Choose(VisualStyle style)
    {
        _session = style;
        Chosen?.Invoke();
    }

    /// <summary>
    /// The settings menu's choice: saved with the settings (the menu commits them, and
    /// <c>GameSettings.Changed</c> restyles the world), and it ends any <c>/style</c> choice.
    /// </summary>
    public static void ChooseSetting(VisualStyle style)
    {
        _session = null;
        GameSettings.Current.VisualStyle = style;
    }

    /// <summary>The styles the settings menu offers: those with a look of their own so far.</summary>
    public static readonly VisualStyle[] MenuStyles = [VisualStyle.Ps1, VisualStyle.Cartoon];

    /// <summary>"PS1", "Cartoon", "Realistic−", "Realistic+": for menus.</summary>
    public static string Label(VisualStyle style) => style switch
    {
        VisualStyle.Ps1 => "PS1",
        VisualStyle.Cartoon => "Cartoon",
        VisualStyle.RealisticLow => "Realistic−",
        _ => "Realistic+",
    };

    /// <summary>Raised by <see cref="Choose"/>: the client world restyles itself. Main thread.</summary>
    public static event System.Action? Chosen;

    /// <summary>Whether a client world is listening for <see cref="Chosen"/>.</summary>
    public static bool HasWorld => Chosen != null;

    /// <summary>
    /// The style the kit hands out and the world shows: <see cref="Style"/> as of the last
    /// <see cref="Restyle"/>, so a material made between a settings change and the restyle still
    /// matches the others.
    /// </summary>
    public static VisualStyle Applied => _applied ??= Style;

    private static VisualStyle? _applied;

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
        // interiors and snowfall stay PS1's, unshaded on purpose: rooms float in the dark under
        // the terrain, out of the sun
        [VisualStyle.Cartoon] = new()
        {
            [MaterialRole.Terrain] = "res://shaders/cartoon_terrain.gdshader",
            [MaterialRole.Road] = "res://shaders/cartoon_road.gdshader",
            [MaterialRole.Building] = "res://shaders/cartoon_building.gdshader",
            [MaterialRole.Tree] = "res://shaders/cartoon_tree.gdshader",
            [MaterialRole.TreeFar] = "res://shaders/cartoon_treefar.gdshader",
            [MaterialRole.Water] = "res://shaders/cartoon_water.gdshader",
            [MaterialRole.Prop] = "res://shaders/cartoon_prop.gdshader",
            [MaterialRole.Path] = "res://shaders/cartoon_path.gdshader",
        },
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

    /// <summary>
    /// A new material for <paramref name="role"/> in the applied style, with the style's own
    /// settings for that role already applied. The kit keeps a weak hold on it, so
    /// <see cref="Restyle"/> can move it to another style in place. Main thread.
    /// </summary>
    public static ShaderMaterial Material(MaterialRole role)
    {
        var m = new ShaderMaterial();
        Configure(m, role, Applied);
        lock (Live)
        {
            Live.RemoveAll(l => !l.Material.TryGetTarget(out _));
            Live.Add((new System.WeakReference<ShaderMaterial>(m), role));
        }
        return m;
    }

    /// <summary>Every material handed out that is still alive, and its role.</summary>
    private static readonly List<(System.WeakReference<ShaderMaterial> Material, MaterialRole Role)> Live = new();

    /// <summary>
    /// Moves the world to <see cref="Style"/>: every live material gets that style's shader for
    /// its role and its settings, in place, so every mesh using it changes at once. Godot keeps a
    /// material's parameters across a shader change, so what the game pushed into it (fog, the
    /// sightline cut, occupancy, open doors) carries over. The rest of the world follows from
    /// <c>ClientWorld.ApplyStyle</c>: environment, sun, and the terrain's rings and mesh detail.
    /// False when the world already shows <see cref="Style"/>. Main thread.
    /// </summary>
    public static bool Restyle()
    {
        if (_applied == Style) return false;
        _applied = Style;
        lock (Live)
        {
            Live.RemoveAll(l => !l.Material.TryGetTarget(out _));
            foreach (var (weak, role) in Live)
                if (weak.TryGetTarget(out var m))
                    Configure(m, role, Applied);
        }
        lock (Figures)
        {
            Figures.RemoveAll(f => !f.Material.TryGetTarget(out _));
            foreach (var (weak, plain) in Figures)
                if (weak.TryGetTarget(out var m))
                    Shade(m, plain);
        }
        return true;
    }

    // --- avatars, vehicles, birds and items ----------------------------------------------------

    /// <summary>What a figure material was built with, for PS1 and for going back to it.</summary>
    private readonly record struct PlainFigure(BaseMaterial3D.DiffuseModeEnum Diffuse,
        BaseMaterial3D.SpecularModeEnum Specular, float Roughness, bool Rim, float RimAmount, float RimTint);

    private static readonly List<(System.WeakReference<StandardMaterial3D> Material, PlainFigure Plain)> Figures = new();

    /// <summary>
    /// The vertex-coloured standard material of avatars, vehicles, birds and items
    /// (<c>Avatar/HumanMeshBuilder.Material</c>), shaded for the applied style: PS1's as built,
    /// Cartoon's with a toon diffuse and a rim. Kept like the shader materials, so a restyle
    /// reaches it. Any thread (mesh builders make figures on workers).
    /// </summary>
    public static StandardMaterial3D Figure(StandardMaterial3D m)
    {
        var plain = new PlainFigure(m.DiffuseMode, m.SpecularMode, m.Roughness, m.RimEnabled, m.Rim, m.RimTint);
        lock (Figures)
        {
            Figures.RemoveAll(f => !f.Material.TryGetTarget(out _));
            Figures.Add((new System.WeakReference<StandardMaterial3D>(m), plain));
        }
        Shade(m, plain);
        return m;
    }

    private static void Shade(StandardMaterial3D m, PlainFigure plain)
    {
        if (StyleFinish == Finish.Cartoon)
        {
            m.DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Toon;
            m.SpecularMode = BaseMaterial3D.SpecularModeEnum.Toon;
            m.Roughness = 0.9f;
            m.RimEnabled = true;
            m.Rim = 0.35f;
            m.RimTint = 0.6f;
            return;
        }
        m.DiffuseMode = plain.Diffuse;
        m.SpecularMode = plain.Specular;
        m.Roughness = plain.Roughness;
        m.RimEnabled = plain.Rim;
        m.Rim = plain.RimAmount;
        m.RimTint = plain.RimTint;
    }

    private static void Configure(ShaderMaterial m, MaterialRole role, VisualStyle style)
    {
        var shader = GD.Load<Shader>(Resolve(style, role).Path);
        if (m.Shader != shader) m.Shader = shader;
        // PS1's finish (shaders/common/retro.gdshaderinc) belongs to PS1: off wherever another
        // style draws with a PS1 body, borrowed or wrapped
        if (HasUniform(shader, "retro")) m.SetShaderParameter("retro", style == VisualStyle.Ps1);
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
    }

    private static bool HasUniform(Shader shader, string name)
    {
        foreach (var u in shader.GetShaderUniformList())
            if (u.AsGodotDictionary()["name"].AsString() == name)
                return true;
        return false;
    }

    // --- the rest of a style's look ------------------------------------------------------------
    // Beyond its shaders, a style decides its environment, whether there is a sun, and the
    // terrain's geometry. Each item walks the fallback chain on its own, like the roles.

    /// <summary>A style's look beyond its shaders; null borrows the parent's.</summary>
    private sealed record Look(MeshDetail? Detail = null, int? FinestStride = null, bool? Sun = null, Finish? Finish = null);

    /// <summary>
    /// A style's finish outside its shaders: how its sky, ambient light and haze are made and
    /// driven through the day, and how avatars and vehicles are shaded (<see cref="Figure"/>).
    /// </summary>
    private enum Finish
    {
        /// <summary>PS1: a flat background colour, ambient light for the avatars only, plain figures.</summary>
        Flat,
        /// <summary>A gradient sky, filmic tonemap, cool blue shade, strong aerial haze; toon figures.</summary>
        Cartoon,
    }

    private static readonly Dictionary<VisualStyle, Look> Looks = new()
    {
        [VisualStyle.Ps1] = new(MeshDetail.Low, FinestStride: 1, Sun: false, Finish: Finish.Flat),
        [VisualStyle.Cartoon] = new(MeshDetail.High, Sun: true, Finish: Finish.Cartoon),
        // Textures carry the surface detail, so 2 m quads underfoot rather than 1 m: the
        // prototype's biggest geometry lever (Realistic-, Riddes, M1 Pro: ~25 -> 11-20 ms)
        [VisualStyle.RealisticLow] = new(FinestStride: 2),
        [VisualStyle.RealisticHigh] = new(),
    };

    /// <summary>An item of <paramref name="style"/>'s look, and the style it comes from.</summary>
    private static (T Value, VisualStyle From) Pick<T>(VisualStyle style, System.Func<Look, T?> item) where T : struct
    {
        for (VisualStyle? s = style; s is { } at; s = Parent(at))
            if (item(Looks[at]) is { } value)
                return (value, at);
        throw new KeyNotFoundException($"the base style has no {typeof(T).Name} in its look");
    }

    /// <summary>How finely the applied style's tile meshes are built.</summary>
    public static MeshDetail Detail => Pick(Applied, l => l.Detail).Value;

    /// <summary>
    /// The finest terrain stride the applied style draws, in metres: no LOD ring is finer
    /// (<see cref="Terrain.LodPolicy.Create"/>).
    /// </summary>
    public static int FinestStride => Pick(Applied, l => l.FinestStride).Value;

    /// <summary>Whether the applied style lights the world with a real sun (a shadowed <see cref="DirectionalLight3D"/>).</summary>
    public static bool HasSun => Pick(Applied, l => l.Sun).Value;

    private static Finish StyleFinish => Pick(Applied, l => l.Finish).Value;

    /// <summary>
    /// A new environment for the applied style; <see cref="DriveEnvironment"/> sets its colours
    /// every frame. PS1: a flat sky colour and no tonemapping, ambient light for the avatars and
    /// vehicles (the world shaders are unshaded). Cartoon: a gradient sky, a filmic tonemap and
    /// the aerial haze that is half the look, ridges going blue and then pale with distance.
    /// </summary>
    public static Godot.Environment NewEnvironment()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.72f, 0.78f, 0.86f),
        };
        if (StyleFinish == Finish.Cartoon)
        {
            env.BackgroundMode = Godot.Environment.BGMode.Sky;
            env.Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial { SunAngleMax = 20f, SkyCurve = 0.12f } };
            env.TonemapMode = Godot.Environment.ToneMapper.Filmic;
            env.TonemapExposure = 1.05f;
            env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            env.ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled;
            env.FogEnabled = true;
            env.FogMode = Godot.Environment.FogModeEnum.Exponential;
            env.FogDensity = 0.00011f;
            env.FogAerialPerspective = 0.35f;
            env.FogSkyAffect = 0f;
        }
        return env;
    }

    /// <summary>
    /// Every frame, from <c>World/DayNight</c>: the environment's colours and the sun (null when
    /// the style has none) for the hour. <paramref name="shade"/> is the direction the shaders
    /// light from (the sun, or the moon at night), <paramref name="tint"/> and
    /// <paramref name="sky"/> the palette's light and sky colours as seen (sRGB).
    /// </summary>
    public static void DriveEnvironment(Godot.Environment env, DirectionalLight3D? sun, Vector3 shade,
        Color tint, Color sky, float night, float sunElevationDeg)
    {
        if (sun != null)
        {
            // it shines along -shade, as the shaders light; the light's own -Z is where it shines
            var down = -shade.Normalized();
            sun.Basis = Basis.LookingAt(down, Mathf.Abs(down.Y) > 0.999f ? Vector3.Forward : Vector3.Up);
            sun.LightColor = tint;
            sun.LightEnergy = Mathf.Lerp(1.0f, 0.3f, night);
        }
        if (StyleFinish == Finish.Cartoon && env.Sky?.SkyMaterial is ProceduralSkyMaterial gradient)
        {
            // a clear day sky, sliding into the palette's own at dusk and night
            float dusk = Mathf.Clamp(1f - (sunElevationDeg - 2f) / 18f, 0f, 1f);
            gradient.SkyTopColor = new Color(0.24f, 0.47f, 0.85f).Lerp(sky.Darkened(0.2f), dusk);
            gradient.SkyHorizonColor = new Color(0.72f, 0.84f, 0.95f).Lerp(sky.Lightened(0.2f), dusk);
            gradient.GroundHorizonColor = gradient.SkyHorizonColor;
            gradient.GroundBottomColor = gradient.SkyHorizonColor.Darkened(0.3f);
            env.FogLightColor = new Color(0.68f, 0.79f, 0.93f).Lerp(sky.Lightened(0.15f), dusk);
            // BotW's shade is bright and cool: the shadow side is the sky's blue, not black
            env.AmbientLightColor = new Color(0.50f, 0.60f, 0.92f).Lerp(sky, 0.2f);
            env.AmbientLightEnergy = Mathf.Lerp(0.8f, 0.4f, night);
            env.BackgroundColor = sky;
            return;
        }
        env.BackgroundColor = sky;
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = sky.Lerp(new Color(tint.R, tint.G, tint.B), 0.5f);
        env.AmbientLightEnergy = Mathf.Lerp(1.0f, 0.55f, night);
    }

    /// <summary>
    /// The applied style's sun, or null when it has none (PS1: the shaders light themselves from
    /// <c>world_sun_dir</c>). <c>World/DayNight</c> points and colours it every frame.
    /// </summary>
    public static DirectionalLight3D? NewSun() => HasSun
        ? new DirectionalLight3D
        {
            Name = "Sun",
            ShadowEnabled = true,
            // about the 3D trees' reach (the billboards beyond cast none), in cascades of 10, 40,
            // 140 and 400 m: the default 40 m first one left a sawtooth on every near shadow
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowMaxDistance = 400f,
            DirectionalShadowSplit1 = 0.025f,
            DirectionalShadowSplit2 = 0.1f,
            DirectionalShadowSplit3 = 0.35f,
            // hard: the soft filter's noise turns into hatching under the cel band
            ShadowBlur = 0f,
            // the cel light reads attenuation as a hard band: any acne becomes stripes on the crowns
            ShadowBias = 0.2f,
            ShadowNormalBias = 4f,
            // an angular size (PCSS) washed the shadows out entirely in the prototype
            LightAngularDistance = 0f,
        }
        : null;

    // --- names ---------------------------------------------------------------------------------

    /// <summary>What <c>--style</c> and <c>/style</c> take: the short names first, then the long ones.</summary>
    public static readonly (string Name, VisualStyle Style)[] Names =
    [
        ("ps1", VisualStyle.Ps1),
        ("cartoon", VisualStyle.Cartoon),
        ("real-", VisualStyle.RealisticLow),
        ("real+", VisualStyle.RealisticHigh),
        ("realistic-", VisualStyle.RealisticLow),
        ("realistic+", VisualStyle.RealisticHigh),
    ];

    public static bool TryParse(string text, out VisualStyle style)
    {
        foreach (var (name, s) in Names)
            if (string.Equals(name, text.Trim(), System.StringComparison.OrdinalIgnoreCase))
            {
                style = s;
                return true;
            }
        style = VisualStyle.Ps1;
        return false;
    }

    /// <summary>"ps1", "cartoon", "real-", "real+".</summary>
    public static string NameOf(VisualStyle style) => System.Array.Find(Names, n => n.Style == style).Name;

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

    /// <summary>How far any 3D tree can show: the handover plus its crossfade (<c>Terrain/NearTrees</c>).</summary>
    public static float TreeReach => TreeNear + TreeFade;

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
        if (Looks[Base] is not { Detail: not null, FinestStride: not null, Sun: not null, Finish: not null })
        {
            GD.PrintErr($"[style-report] FAIL: the base style {Base} has an incomplete look");
            failures++;
        }
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
                void LookItem<T>(string name, System.Func<Look, T?> item) where T : struct
                {
                    var (_, from) = Pick(style, item);
                    if (from != style) borrowed.Add($"{name}<-{from}");
                }
                LookItem("detail", l => l.Detail);
                LookItem("finest-stride", l => l.FinestStride);
                LookItem("sun", l => l.Sun);
                LookItem("finish", l => l.Finish);
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
