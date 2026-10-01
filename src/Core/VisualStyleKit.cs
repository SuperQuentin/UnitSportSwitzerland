using Godot;

namespace UnitSport.Core;

/// <summary>How the world looks. Prototype, issue #181 (docs/plans/visual-styles.md).</summary>
public enum VisualStyle
{
    Ps1,
    Cartoon,
    /// <summary>Textures and lit materials on the Mobile renderer.</summary>
    RealisticLow,
    /// <summary>Realistic− plus Forward+ effects (GI, SSAO, SSR); needs --rendering-method forward_plus.</summary>
    RealisticHigh,
}

/// <summary>
/// PROTOTYPE (issue #181, phase 0): hands out the world materials, the environment and the sun
/// for the current <see cref="VisualStyle"/>.
///
/// <para>
/// Shaders resolve through the fallback chain Realistic+ → Realistic− → Cartoon → PS1: a role
/// with no shader of its own in a style borrows the next one's (shaders/&lt;prefix&gt;_&lt;role&gt;.gdshader).
/// Only the five tile materials have lit variants; everything else (interiors, props, paths,
/// snowfall) stays PS1 in every style, which is the fallback working as designed.
/// </para>
/// </summary>
public static class VisualStyleKit
{
    public static VisualStyle Style => GameSettings.Current.VisualStyle;

    /// <summary>The world origin, set by ClientWorld before any material is made.</summary>
    public static WorldOrigin? Origin { get; set; }

    public static bool Lit => Style != VisualStyle.Ps1;
    public static bool Realistic => Style is VisualStyle.RealisticLow or VisualStyle.RealisticHigh;

    /// <summary>Forward+ effects asked for and actually available.</summary>
    public static bool HighEffects =>
        Style == VisualStyle.RealisticHigh && RenderingServer.GetCurrentRenderingMethod() == "forward_plus";

    /// <summary>
    /// Whether the terrain casts sun shadows. Off by default: the full-resolution tiles under
    /// the shadow cascades are millions of triangles, and N·L already shades the slopes.
    /// "--ground-shadows on" to measure the difference.
    /// </summary>
    public static GeometryInstance3D.ShadowCastingSetting GroundShadows =>
        System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--ground-shadows") >= 0
            ? GeometryInstance3D.ShadowCastingSetting.On
            : GeometryInstance3D.ShadowCastingSetting.Off;

    /// <summary>Billboard trees, beyond the 3D trees' range (lit styles).</summary>
    public static ShaderMaterial TreeFarMaterial => _treeFar ??= WorldMaterial("treefar");

    /// <summary>"--tree-near m": where the 3D trees hand over to billboards (default 220 m).</summary>
    public static float TreeNear => float.TryParse(ArgValue("--tree-near"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out float m) ? m : Realistic ? 80f : 220f;
    private static ShaderMaterial? _treeFar;
    private static ShaderMaterial? _treeMaterial;

    /// <summary>
    /// The 3D trees NearTrees culls per tree (lit styles); null = PS1, whose 20-triangle trees
    /// stay per tile. Tile builds check this to hand over their buffers instead.
    /// </summary>
    public static (Mesh Conifer, Mesh Broadleaf)? NearTreeMeshes { get; private set; }

    /// <summary>
    /// Tile-level cut for the 3D tree MultiMeshes: Godot measures visibility range to the tile's
    /// AABB centre, so this is the per-tree near range plus most of a tile's half-diagonal.
    /// </summary>
    public static float TreeNearTileRange => TreeNear + 40f + 720f;

    /// <summary>The sun, in lit styles only: DayNight steers it.</summary>
    public static DirectionalLight3D? Sun { get; private set; }

    private static readonly string[] Chain = { "real", "real", "cartoon", "ps1" };

    private static int ChainStart => Style switch
    {
        VisualStyle.RealisticHigh => 0,
        VisualStyle.RealisticLow => 1,
        VisualStyle.Cartoon => 2,
        _ => 3,
    };

    /// <summary>The shader for a world role ("terrain", "road", ...), through the fallback chain.</summary>
    public static Shader WorldShader(string role)
    {
        for (int i = ChainStart; i < Chain.Length; i++)
        {
            string path = $"res://shaders/{Chain[i]}_{role}.gdshader";
            if (ResourceLoader.Exists(path))
            {
                if (i != ChainStart) GD.Print($"[style] {Style}: {role} borrowed from {Chain[i]}");
                return GD.Load<Shader>(path);
            }
        }
        return GD.Load<Shader>($"res://shaders/ps1_{role}.gdshader");
    }

    public static ShaderMaterial WorldMaterial(string role)
    {
        var m = new ShaderMaterial { Shader = WorldShader(role) };
        if (role == "tree")
        {
            _treeMaterial = m;
            m.SetShaderParameter("tree_lod", true);
            m.SetShaderParameter("tree_near", TreeNear);
        }
        if (!Realistic) return m;
        switch (role)
        {
            case "terrain":
                m.SetShaderParameter("tex_grass", Tex("Grass004"));
                m.SetShaderParameter("tex_forest", Tex("Ground078"));
                m.SetShaderParameter("tex_rock", Tex("Rock058"));
                m.SetShaderParameter("tex_gravel", Tex("Gravel023"));
                // "--photo off|far|all": the SWISSIMAGE drape (default far: beyond ~150 m)
                string photo = ArgValue("--photo") ?? "far";
                if (photo != "off")
                {
                    m.SetShaderParameter("tex_photo", DiskTexture("swissimage/riddes_2m_2582000_1114000.jpg"));
                    // the mosaic's NW corner, LV95 2582000/1114000, in this run's world coordinates
                    var nw = Origin?.ToWorld(2582000, 1114000, 0) ?? Vector3.Zero;
                    m.SetShaderParameter("photo_origin", new Vector2(nw.X, nw.Z));
                    m.SetShaderParameter("photo_extent", new Vector2(2000, 2000));
                    if (photo == "all") { m.SetShaderParameter("photo_near", 0f); m.SetShaderParameter("photo_far", 1f); }
                }
                break;
            case "road":
                m.SetShaderParameter("tex_asphalt", Tex("Asphalt031"));
                m.SetShaderParameter("tex_gravel", Tex("Gravel023"));
                break;
            case "building":
                m.SetShaderParameter("tex_plaster", Tex("Plaster001"));
                m.SetShaderParameter("tex_roof", Tex("RoofingTiles006"));
                break;
        }
        return m;
    }

    private static readonly System.Collections.Generic.Dictionary<string, Texture2D> _textures = new();

    /// <summary>
    /// A CC0 ambientCG colour map from assets/proto/textures, loaded from disk with mipmaps
    /// (that folder has a .gdignore: the prototype skips the import pipeline).
    /// </summary>
    private static Texture2D Tex(string id) => DiskTexture($"textures/{id}_1K-JPG_Color.jpg");

    private static Texture2D DiskTexture(string file)
    {
        if (_textures.TryGetValue(file, out var t)) return t;
        string path = ProjectSettings.GlobalizePath($"res://assets/proto/{file}");
        var img = Image.LoadFromFile(path);
        img.GenerateMipmaps();
        t = ImageTexture.CreateFromImage(img);
        _textures[file] = t;
        return t;
    }

    /// <summary>Avatars and vehicles: the vertex-coloured standard material, restyled.</summary>
    public static StandardMaterial3D Adapt(StandardMaterial3D m)
    {
        switch (Style)
        {
            case VisualStyle.Cartoon:
                m.DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Toon;
                m.SpecularMode = BaseMaterial3D.SpecularModeEnum.Toon;
                m.Roughness = 0.9f;
                m.RimEnabled = true;
                m.Rim = 0.35f;
                m.RimTint = 0.6f;
                break;
            case VisualStyle.RealisticLow or VisualStyle.RealisticHigh:
                // one material for paint, cloth and skin alike: a compromise until they split
                m.SpecularMode = BaseMaterial3D.SpecularModeEnum.SchlickGgx;
                m.Roughness = 0.55f;
                break;
        }
        return m;
    }

    private static string? ArgValue(string flag)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>World environment and sun for the style. PS1 keeps its flat-colour background and no light.</summary>
    public static void Setup(Node world, Godot.Environment env)
    {
        GD.Print($"[style] {Style} on {RenderingServer.GetCurrentRenderingMethod()}"
            + (Style == VisualStyle.RealisticHigh && !HighEffects ? " (Forward+ effects unavailable: run with --rendering-method forward_plus)" : ""));
        // created here: tile workers build the tree MultiMeshes with it
        TreeFarMaterial.SetShaderParameter("tree_near", TreeNear);
        FogUniforms.Apply(TreeFarMaterial);
        if (!Lit) return;
        if (Realistic)
        {
            RealTrees.Load(TreeNear);
            NearTreeMeshes = (RealTrees.Conifer!, RealTrees.Broadleaf!);
            _ = RealTrees.BakeImpostors(world, TreeFarMaterial);
        }
        else if (_treeMaterial != null)
        {
            NearTreeMeshes = Terrain.ChunkNode.HighDetailTrees(_treeMaterial);
        }
        if (NearTreeMeshes is var (conifer, broadleaf))
            world.AddChild(new Terrain.NearTrees(conifer, broadleaf, TreeNear + (Realistic ? 20f : 40f)));

        Sun = new DirectionalLight3D
        {
            Name = "StyleSun",
            ShadowEnabled = !(ArgValue("--nofx") ?? "").Contains("shadows"),
            DirectionalShadowMode = HighEffects
                ? DirectionalLight3D.ShadowMode.Parallel4Splits
                : DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = Realistic ? 900f : 600f,
            ShadowBlur = Style == VisualStyle.Cartoon ? 0.6f : 1.2f,
            LightAngularDistance = 0f,  // 0.6 (Forward+ PCSS) washed the shadows out entirely
        };
        world.AddChild(Sun);

        var sky = new ProceduralSkyMaterial();
        env.BackgroundMode = Godot.Environment.BGMode.Sky;
        env.Sky = new Sky { SkyMaterial = sky };

        if (Style == VisualStyle.Cartoon)
        {
            env.TonemapMode = Godot.Environment.ToneMapper.Filmic;
            env.TonemapExposure = 1.05f;
            env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            env.ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled;
            // the aerial haze is half the look: ridges go blue, then pale, with distance
            env.FogEnabled = true;
            env.FogMode = Godot.Environment.FogModeEnum.Exponential;
            env.FogDensity = 0.00011f;
            env.FogAerialPerspective = 0.35f;
            env.FogSkyAffect = 0f;
            sky.SunAngleMax = 20f;
            sky.SkyCurve = 0.12f;
        }
        else
        {
            env.TonemapMode = Godot.Environment.ToneMapper.Aces;
            env.TonemapExposure = 0.8f;
            env.AdjustmentEnabled = true;
            env.AdjustmentSaturation = 1.0f;
            env.AdjustmentContrast = 1.05f;
            env.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
            env.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;
            env.FogEnabled = true;
            env.FogMode = Godot.Environment.FogModeEnum.Exponential;
            env.FogDensity = 0.00004f;
            env.FogAerialPerspective = 0.8f;
            env.FogSkyAffect = 0f;
            env.GlowEnabled = true;
            env.GlowIntensity = 0.4f;
            env.GlowHdrThreshold = 1.2f;
            if (HighEffects)
            {
                // "--nofx sdfgi,ssr" switches single effects off, to measure each one's cost
                string nofx = ArgValue("--nofx") ?? "";
                bool Fx(string name) => !nofx.Contains(name);
                env.SsaoEnabled = Fx("ssao");
                env.SsaoRadius = 1.5f;
                env.SsaoIntensity = 1.6f;
                env.SsrEnabled = Fx("ssr");
                env.SdfgiEnabled = Fx("sdfgi");
                env.SdfgiUseOcclusion = true;
                env.SdfgiCascades = 6;
                env.SdfgiMinCellSize = 0.4f;
            }
        }
    }

    /// <summary>
    /// Per frame from DayNight: the sun (or the moon below the horizon) as the light, the
    /// palette's tint as its colour, the palette's sky in the sky material.
    /// </summary>
    public static void UpdateLighting(Vector3 shadeDir, Color tint, Color sky, float night, float sunElevationDeg, Godot.Environment? env)
    {
        if ((Engine.GetMainLoop() as SceneTree)?.Root.GetCamera3D() is { } cam)
            RenderingServer.GlobalShaderParameterSet("world_cam_pos", cam.GlobalPosition);
        if (Sun == null || env == null) return;
        var dir = shadeDir.Normalized();
        var up = Mathf.Abs(dir.Y) > 0.98f ? Vector3.Right : Vector3.Up;
        Sun.GlobalBasis = Basis.LookingAt(-dir, up);
        Sun.LightColor = tint;
        Sun.LightEnergy = Mathf.Lerp(Realistic ? 1.6f : 1.0f, Realistic ? 0.12f : 0.3f, night);

        if (env.Sky?.SkyMaterial is ProceduralSkyMaterial toon)
        {
            // a clear day sky, sliding into the palette's own at dusk and night
            float dusk = Mathf.Clamp(1f - (sunElevationDeg - 2f) / 18f, 0f, 1f);
            var top = Realistic ? new Color(0.20f, 0.38f, 0.70f) : new Color(0.24f, 0.47f, 0.85f);
            var horizon = Realistic ? new Color(0.66f, 0.76f, 0.88f) : new Color(0.72f, 0.84f, 0.95f);
            toon.SkyTopColor = top.Lerp(sky.Darkened(0.2f), dusk);
            toon.SkyHorizonColor = horizon.Lerp(sky.Lightened(0.2f), dusk);
            toon.SkyEnergyMultiplier = Realistic ? Mathf.Lerp(1.0f, 0.25f, night) : 1f;
            toon.GroundHorizonColor = toon.SkyHorizonColor;
            toon.GroundBottomColor = toon.SkyHorizonColor.Darkened(0.3f);
            env.FogLightColor = new Color(0.68f, 0.79f, 0.93f).Lerp(sky.Lightened(0.15f), dusk);
        }

        if (Style == VisualStyle.Cartoon)
        {
            // BotW's shade is bright and cool: the shadow side is the sky's blue, not black
            var shade = new Color(0.50f, 0.60f, 0.92f).Lerp(sky, 0.2f);
            env.AmbientLightColor = shade;
            env.AmbientLightEnergy = Mathf.Lerp(0.8f, 0.4f, night);
            env.BackgroundColor = sky;
        }
        else
        {
            env.AmbientLightSkyContribution = 1f;
            env.AmbientLightEnergy = Mathf.Lerp(1.0f, 0.35f, night);
        }
    }
}
