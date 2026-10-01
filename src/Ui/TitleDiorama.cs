using Godot;

namespace UnitSport.Ui;

/// <summary>
/// The title screen's backdrop: a small low-poly alpine valley — ridged peaks with snow, a lake,
/// fir forests, a chalet with a flag — and a camera orbiting it slowly. Everything is generated
/// here in a few milliseconds; no terrain is loaded and no chunk manager runs, so the menu is up
/// before the world exists, and it is freed the moment a mode is picked.
/// </summary>
public partial class TitleDiorama : Node3D
{
    private const int Cells = 110;
    private const float Cell = 14f;
    private const float Half = Cells * Cell / 2f;
    private const float WaterLevel = 4f;

    private Camera3D _camera = null!;
    private float _angle = 0.9f;
    private FastNoiseLite _noise = null!;
    private FastNoiseLite _detail = null!;

    public static TitleDiorama Create() => new() { Name = "TitleDiorama" };

    public override void _Ready()
    {
        _noise = new FastNoiseLite
        {
            Seed = 1291,   // Swiss Confederation, 1291
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.0021f,
            FractalType = FastNoiseLite.FractalTypeEnum.Ridged,
            FractalOctaves = 5,
            FractalGain = 0.48f,
        };
        _detail = new FastNoiseLite { Seed = 7, Frequency = 0.02f, FractalOctaves = 2 };

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.60f, 0.68f, 0.80f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.55f, 0.62f, 0.75f),
            AmbientLightEnergy = 0.55f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            FogEnabled = true,
            FogLightColor = new Color(0.66f, 0.72f, 0.82f),
            FogDensity = 0.00045f,
            FogAerialPerspective = 0.4f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        var sun = new DirectionalLight3D
        {
            LightColor = new Color(1f, 0.90f, 0.76f),
            LightEnergy = 1.25f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 1600,
        };
        AddChild(sun);
        sun.LookAtFromPosition(Vector3.Zero, new Vector3(-0.55f, -0.42f, -0.62f), Vector3.Up);

        AddChild(Terrain());
        AddChild(Water());
        AddChild(Forest());
        AddChild(Chalet(new Vector3(-60, Height(-60, 110), 110)));

        _camera = new Camera3D { Fov = 52, Far = 6000, Current = true };
        AddChild(_camera);
        Place();
    }

    public override void _Process(double delta)
    {
        _angle += (float)delta * 0.025f;
        Place();
    }

    /// <summary>
    /// The camera circles inside the valley, above the lake, looking out across it at the far
    /// side's peaks — never over the rim, where a hillside would fill the frame.
    /// </summary>
    private void Place()
    {
        var dir = new Vector3(Mathf.Cos(_angle), 0, Mathf.Sin(_angle));
        var eye = dir * 260;
        eye.Y = Mathf.Max(200 + Mathf.Sin(_angle * 0.7f) * 20, Height(eye.X, eye.Z) + 90);
        var target = -dir * 520 + new Vector3(0, 170, 0);
        _camera.LookAtFromPosition(eye, target, Vector3.Up);
    }

    /// <summary>
    /// Height in metres: ridged mountains rising from a bowl, so the camera always looks over a
    /// valley floor and a lake at the peaks behind.
    /// </summary>
    private float Height(float x, float z)
    {
        float r = new Vector2(x, z).Length() / Half;
        float bowl = Mathf.SmoothStep(0.18f, 0.95f, r);           // flat valley in the middle
        float ridge = Mathf.Max(0, _noise.GetNoise2D(x, z) * 0.5f + 0.5f);
        float h = bowl * (80 + ridge * ridge * 520) + _detail.GetNoise2D(x, z) * 6;
        // one tall peak behind the lake, a Matterhorn of sorts
        float peak = Mathf.Exp(-new Vector2(x - 330, z + 260).LengthSquared() / (2 * 150f * 150f));
        return h + peak * 420 - (1 - bowl) * 12;
    }

    private Color Colour(float h, float slope, float jitter)
    {
        var grass = new Color(0.34f, 0.50f, 0.22f).Lerp(new Color(0.42f, 0.56f, 0.26f), jitter);
        var rock = new Color(0.46f, 0.44f, 0.42f).Lerp(new Color(0.38f, 0.37f, 0.36f), jitter);
        var snow = new Color(0.93f, 0.95f, 0.98f);
        var shore = new Color(0.62f, 0.58f, 0.45f);
        if (h < WaterLevel + 2) return shore;
        var c = grass.Lerp(rock, Mathf.SmoothStep(0.45f, 0.75f, slope) + Mathf.SmoothStep(260, 340, h));
        return c.Lerp(snow, Mathf.SmoothStep(380 + jitter * 50, 430 + jitter * 50, h) * (1 - Mathf.SmoothStep(0.8f, 0.95f, slope)));
    }

    private MeshInstance3D Terrain()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetSmoothGroup(uint.MaxValue);   // faceted: every triangle its own normal, the low-poly look
        var heights = new float[Cells + 1, Cells + 1];
        for (int i = 0; i <= Cells; i++)
            for (int j = 0; j <= Cells; j++)
                heights[i, j] = Height(i * Cell - Half, j * Cell - Half);

        for (int i = 0; i < Cells; i++)
            for (int j = 0; j < Cells; j++)
            {
                Vector3 P(int a, int b) => new(a * Cell - Half, heights[a, b], b * Cell - Half);
                var p00 = P(i, j); var p10 = P(i + 1, j); var p01 = P(i, j + 1); var p11 = P(i + 1, j + 1);
                bool flip = ((i + j) & 1) == 0;
                var tris = flip
                    ? new[] { (p00, p10, p11), (p00, p11, p01) }
                    : new[] { (p00, p10, p01), (p10, p11, p01) };
                foreach (var (a, b, c) in tris)
                {
                    var n = (c - a).Cross(b - a).Normalized();
                    float slope = 1 - Mathf.Abs(n.Y);
                    float mid = (a.Y + b.Y + c.Y) / 3;
                    float jitter = _detail.GetNoise2D(a.X * 3, a.Z * 3) * 0.5f + 0.5f;
                    st.SetColor(Colour(mid, slope, jitter));
                    st.AddVertex(a); st.AddVertex(c); st.AddVertex(b);
                }
            }
        st.GenerateNormals();
        var mat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 1,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            // either winding reads right: the back face is lit with the flipped normal
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        return new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = mat };
    }

    private MeshInstance3D Water()
    {
        var mesh = new PlaneMesh { Size = new Vector2(Cells * Cell, Cells * Cell) };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.20f, 0.42f, 0.55f, 0.88f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.15f,
            Metallic = 0.1f,
        };
        return new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = new Vector3(0, WaterLevel, 0) };
    }

    /// <summary>Fir trees, cones on stubs, where the ground is green and not too steep.</summary>
    private MultiMeshInstance3D Forest()
    {
        var cone = new CylinderMesh { TopRadius = 0, BottomRadius = 3.6f, Height = 13, RadialSegments = 6, Rings = 1 };
        cone.Material = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.30f, 0.17f), Roughness = 1 };
        var rng = new RandomNumberGenerator { Seed = 42 };
        var spots = new List<Transform3D>();
        for (int k = 0; k < 4000 && spots.Count < 520; k++)
        {
            float x = rng.RandfRange(-Half * 0.85f, Half * 0.85f), z = rng.RandfRange(-Half * 0.85f, Half * 0.85f);
            float h = Height(x, z);
            if (h < WaterLevel + 6 || h > 250) continue;
            float dx = Height(x + 4, z) - h, dz = Height(x, z + 4) - h;
            if (new Vector2(dx, dz).Length() > 3.2f) continue;
            // forests come in patches
            if (_detail.GetNoise2D(x * 0.15f, z * 0.15f) < -0.05f) continue;
            float s = rng.RandfRange(0.7f, 1.35f);
            var basis = Basis.Identity.Scaled(new Vector3(s, s * rng.RandfRange(0.9f, 1.3f), s)).Rotated(Vector3.Up, rng.Randf() * Mathf.Tau);
            spots.Add(new Transform3D(basis, new Vector3(x, h + 6 * s, z)));
        }
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = cone, InstanceCount = spots.Count };
        for (int i = 0; i < spots.Count; i++) mm.SetInstanceTransform(i, spots[i]);
        return new MultiMeshInstance3D { Multimesh = mm };
    }

    /// <summary>A chalet: a timber box under a wide pitched roof, and a Swiss flag on a pole.</summary>
    private static Node3D Chalet(Vector3 at)
    {
        var root = new Node3D { Position = at };
        var wood = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.28f, 0.16f), Roughness = 1 };
        var roofMat = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.20f, 0.16f), Roughness = 1 };
        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(14, 8, 10), Material = wood }, Position = new Vector3(0, 4, 0) });
        var roof = new PrismMesh { Size = new Vector3(18, 6, 13), Material = roofMat };
        root.AddChild(new MeshInstance3D { Mesh = roof, Position = new Vector3(0, 11, 0), RotationDegrees = new Vector3(0, 90, 0) });

        var pole = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.2f, BottomRadius = 0.2f, Height = 18, Material = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.85f, 0.85f) } },
            Position = new Vector3(12, 9, 0),
        };
        root.AddChild(pole);
        var flag = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(5, 5), Material = new StandardMaterial3D { AlbedoTexture = SwissFlag(), CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 1 } },
            Position = new Vector3(14.7f, 15.5f, 0),
        };
        root.AddChild(flag);
        return root;
    }

    private static ImageTexture SwissFlag()
    {
        const int s = 20;
        var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        img.Fill(new Color(0.84f, 0.17f, 0.12f));
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
                if ((x is >= 8 and < 12 && y is >= 4 and < 16) || (y is >= 8 and < 12 && x is >= 4 and < 16))
                    img.SetPixel(x, y, Colors.White);
        return ImageTexture.CreateFromImage(img);
    }
}
