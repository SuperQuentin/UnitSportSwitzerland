using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A cinema's screens and beams (#1238): on every <see cref="FurnitureType.MovieScreen"/> a picture quad
/// (cat photos, a slide every few seconds: <see cref="Slide"/>), and from its hall's porthole a projector beam to it, both built
/// hidden with the interior and shown by <see cref="CinemaBooths"/> with the house lights, on change only.
/// The picture sits 2 cm in front of the screen's white, which nothing else does there (no z-fight).
/// </summary>
public partial class InteriorNode
{
    private readonly List<Node3D> _cinemaShow = new();
    private const string GHouse = "house_lights";

    private static void AddScreens(InteriorNode node)
    {
        var layout = node.Layout;
        if (layout.Type != BuildingType.Cinema) return;
        var ports = layout.Furniture.Where(f => f.Type == FurnitureType.Porthole).ToList();
        foreach (var p in layout.Furniture.Where(f => f.Type == FurnitureType.MovieScreen))
        {
            var basis = new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2);
            var origin = new Vector3(p.X, layout.FloorY(p.Floor) + p.Lift, p.Z);
            var (c, half) = InteriorMeshBuilder.ScreenPicture(p);
            var centre = origin + basis * (c + new Vector3(0, 0, 0.02f));
            var picture = new MeshInstance3D
            {
                Name = "CatScreen",
                Mesh = new QuadMesh { Size = half * 2 },
                MaterialOverride = CatMaterial(),
                Transform = new Transform3D(basis, centre),
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            node.AddChild(picture);
            node._cinemaShow.Add(picture);

            // the beam from this hall's porthole: the nearest one to the screen
            var port = ports.MinBy(q => (q.X - p.X) * (q.X - p.X) + (q.Z - p.Z) * (q.Z - p.Z));
            if (port == null) continue;
            var from = new Vector3(port.X, layout.FloorY(port.Floor) + port.Lift + port.H / 2, port.Z);
            var right = basis * new Vector3(half.X, 0, 0);
            var up = new Vector3(0, half.Y, 0);
            var beam = new MeshInstance3D
            {
                Name = "ProjectorBeam",
                Mesh = Beam(from, centre, right, up),
                MaterialOverride = BeamFor(from, centre, right, up),
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            node.AddChild(beam);
            node._cinemaShow.Add(beam);
        }
    }

    /// <summary>The booth's state on this cinema: the house lights' level, the cat slides and the beams on or off.</summary>
    public void ShowCinema(float houseLights, bool screen)
    {
        _lit?.SetShaderParameter(GHouse, houseLights);
        ShowGlow(screen);
        foreach (var n in _cinemaShow)
            if (IsInstanceValid(n)) n.Visible = screen;
    }

    /// <summary>A light pyramid from a point to a rectangle: four faces, bright at the lens, faint at the screen.</summary>
    private static ArrayMesh Beam(Vector3 from, Vector3 centre, Vector3 right, Vector3 up)
    {
        var corners = new[] { centre - right - up, centre + right - up, centre + right + up, centre - right + up };
        var v = new List<Vector3>();
        var col = new List<Color>();
        var at = new Color(1f, 0.96f, 0.86f, 0.10f);
        var screen = new Color(1f, 0.96f, 0.86f, 0.02f);
        for (int i = 0; i < 4; i++)
        {
            v.Add(from); col.Add(at);
            v.Add(corners[i]); col.Add(screen);
            v.Add(corners[(i + 1) % 4]); col.Add(screen);
        }
        // the cone closed at the screen, 3 cm short of the picture (#1250): looking at the screen from inside
        // the beam, its haze is drawn from there back to the eye, over the picture
        var near = corners.Select(c => c + (from - c).Normalized() * 0.03f).ToArray();
        foreach (var (a, b, c) in new[] { (0, 1, 2), (0, 2, 3) })
        {
            v.Add(near[a]); col.Add(screen);
            v.Add(near[b]); col.Add(screen);
            v.Add(near[c]); col.Add(screen);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = col.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static StandardMaterial3D? _catMaterial;
    private static ShaderMaterial? _beamMaterial;

    private static StandardMaterial3D CatMaterial() => _catMaterial ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoTexture = Slide(0),
        TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
    };

    /// <summary>Every beam's material alive, to give each the new picture: one per beam, its cone in its uniforms.</summary>
    private static readonly List<WeakReference<ShaderMaterial>> Beams = new();

    /// <summary>A beam's material: the cone in the shader's terms (projector_beam), the lens, the picture's centre and half extents.</summary>
    private static ShaderMaterial BeamFor(Vector3 apex, Vector3 centre, Vector3 right, Vector3 up)
    {
        var m = (ShaderMaterial)BeamMaterial().Duplicate();
        m.SetShaderParameter("apex", apex);
        m.SetShaderParameter("centre", centre);
        m.SetShaderParameter("half_right", right);
        m.SetShaderParameter("half_up", up);
        m.SetShaderParameter(GPicture, Slide(_slide));
        Beams.RemoveAll(w => !w.TryGetTarget(out _));
        Beams.Add(new WeakReference<ShaderMaterial>(m));
        return m;
    }

    private static ShaderMaterial BeamMaterial() => _beamMaterial ??= new ShaderMaterial
    {
        Shader = GD.Load<Shader>("res://shaders/projector_beam.gdshader"),
    };

    /// <summary>
    /// The pictures on the screen until films come (a follow-up streams a video or a shared screen): a
    /// slideshow of six cat photos, CC0 from Wikimedia Commons (<c>assets/cinema/</c>, sources in
    /// <c>assets/ASSETS.md</c>), cropped to the screen's 2.39:1. One every <see cref="SlideSeconds"/> on
    /// the shared clock, so everyone in the hall sees the same cat (<see cref="SlideAt"/>).
    /// </summary>
    public const int SlideCount = 6;
    public const double SlideSeconds = 8;

    private static Texture2D[]? _slides;

    public static Texture2D Slide(int i)
    {
        _slides ??= Enumerable.Range(1, SlideCount)
            .Select(k => GD.Load<Texture2D>($"res://assets/cinema/cat_{k}.jpg")).ToArray();
        return _slides[((i % SlideCount) + SlideCount) % SlideCount];
    }

    /// <summary>The slide showing at a shared-clock time, s.</summary>
    public static int SlideAt(double now) => (int)(Math.Floor(now / SlideSeconds) % SlideCount + SlideCount) % SlideCount;

    private static readonly StringName GGlow = "screen_glow", GPicture = "picture";

    /// <summary>How much of the picture's colour the hall takes back from its screen: a wash, under the house lights.</summary>
    private const float GlowGain = 0.45f;

    private static int _slide;
    private static Color[]? _tints;

    /// <summary>A slide's mean colour, from its picture shrunk to a pixel: what its beam and its glow are tinted.</summary>
    public static Color SlideTint(int i)
    {
        _tints ??= new Color[SlideCount];
        i = ((i % SlideCount) + SlideCount) % SlideCount;
        if (_tints[i].A > 0) return _tints[i];
        var img = Slide(i).GetImage();
        if (img == null) return _tints[i] = Colors.White;
        if (img.IsCompressed()) img.Decompress();
        img.Resize(1, 1, Image.Interpolation.Bilinear);
        var c = img.GetPixel(0, 0);
        return _tints[i] = new Color(c.R, c.G, c.B, 1f);
    }

    /// <summary>Every screen's picture (one material for all) and every beam's colour: to slide <paramref name="i"/>, on change only.</summary>
    public static void ShowSlide(int i)
    {
        _slide = i;
        CatMaterial().AlbedoTexture = Slide(i);
        // the beams carry the picture itself, blurred: what each part of the haze lights (projector_beam)
        foreach (var w in Beams)
            if (w.TryGetTarget(out var m)) m.SetShaderParameter(GPicture, Slide(i));
    }

    /// <summary>This hall's wash of the screen's colour: the current slide's while the screen is on, none off.</summary>
    public void ShowGlow(bool screen)
    {
        var t = SlideTint(_slide);
        _lit?.SetShaderParameter(GGlow, screen ? new Vector3(t.R, t.G, t.B) * GlowGain : Vector3.Zero);
    }
}
