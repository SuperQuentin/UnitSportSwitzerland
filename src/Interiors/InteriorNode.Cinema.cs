using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A cinema's screens and beams (#1238): on every <see cref="FurnitureType.MovieScreen"/> a picture quad
/// (the cat, <see cref="CatPicture"/>), and from its hall's porthole a projector beam to it, both built
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
                MaterialOverride = BeamMaterial(),
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            node.AddChild(beam);
            node._cinemaShow.Add(beam);
        }
    }

    /// <summary>The booth's state on this cinema: the house lights' level, the cat and the beams on or off.</summary>
    public void ShowCinema(float houseLights, bool screen)
    {
        _lit?.SetShaderParameter(GHouse, houseLights);
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
        AlbedoTexture = CatPicture(),
        TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
    };

    private static ShaderMaterial BeamMaterial() => _beamMaterial ??= new ShaderMaterial
    {
        Shader = GD.Load<Shader>("res://shaders/projector_beam.gdshader"),
    };

    private static ImageTexture? _cat;

    /// <summary>
    /// The picture on the screen until films come (a follow-up streams a video or a shared screen): a
    /// cartoon cat drawn by code, 2.39:1 like the screen, so there is no image file and no licence to it.
    /// A ginger head with its ears, green eyes with slit pupils, a pink nose, whiskers, on a sunset.
    /// </summary>
    public static ImageTexture CatPicture()
    {
        if (_cat != null) return _cat;
        const int w = 478, h = 200;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        static float Tri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float Edge(Vector2 x, Vector2 y, Vector2 q) => (y.X - x.X) * (q.Y - x.Y) - (y.Y - x.Y) * (q.X - x.X);
            float e0 = Edge(a, b, p), e1 = Edge(b, c, p), e2 = Edge(c, a, p);
            return e0 >= 0 && e1 >= 0 && e2 >= 0 || e0 <= 0 && e1 <= 0 && e2 <= 0 ? 1 : 0;
        }
        static float Segment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Math.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
            return (a + ab * t).DistanceTo(p);
        }
        var head = new Vector2(239, 118);
        var ginger = new Color(0.95f, 0.55f, 0.18f);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = new Vector2(x, y);
                // a sunset behind
                var col = new Color(0.98f, 0.70f, 0.40f).Lerp(new Color(0.42f, 0.22f, 0.50f), y / (float)h);
                var d = (p - head) / new Vector2(78, 66);
                bool inHead = d.LengthSquared() <= 1;
                bool ear = Tri(p, new(170, 82), new(196, 20), new(222, 66)) > 0 || Tri(p, new(256, 66), new(282, 20), new(308, 82)) > 0;
                bool innerEar = Tri(p, new(184, 74), new(198, 38), new(214, 68)) > 0 || Tri(p, new(264, 68), new(280, 38), new(294, 74)) > 0;
                if (ear) col = ginger * 0.92f;
                if (innerEar) col = new Color(0.98f, 0.62f, 0.66f);
                if (inHead)
                {
                    col = ginger;
                    // stripes on the forehead
                    if (Math.Abs(x - 239) < 4 && y < 90 && y > 62 || Math.Abs(x - 224) < 3 && y < 84 && y > 66 || Math.Abs(x - 254) < 3 && y < 84 && y > 66)
                        col = ginger * 0.7f;
                    // the muzzle, paler
                    if (((p - new Vector2(239, 140)) / new Vector2(34, 22)).LengthSquared() <= 1) col = new Color(1f, 0.90f, 0.78f);
                }
                foreach (float ex in new[] { 212f, 266f })
                {
                    var e = (p - new Vector2(ex, 108)) / new Vector2(15, 18);
                    if (e.LengthSquared() <= 1) col = new Color(0.40f, 0.80f, 0.30f);
                    if (Math.Abs(x - ex) < 3 && e.LengthSquared() <= 0.9f) col = new Color(0.05f, 0.05f, 0.05f);
                    if (((p - new Vector2(ex + 4, 102)) / 3.5f).LengthSquared() <= 1) col = Colors.White;
                }
                if (Tri(p, new(231, 128), new(247, 128), new(239, 138)) > 0) col = new Color(0.95f, 0.45f, 0.55f);
                if (Segment(p, new(239, 138), new(239, 146)) < 1.4f || Segment(p, new(239, 146), new(230, 152)) < 1.4f
                    || Segment(p, new(239, 146), new(248, 152)) < 1.4f) col = new Color(0.25f, 0.12f, 0.08f);
                foreach (var (a, b) in new (Vector2, Vector2)[]
                         {
                             (new(210, 138), new(150, 128)), (new(210, 144), new(152, 148)),
                             (new(268, 138), new(328, 128)), (new(268, 144), new(326, 148)),
                         })
                    if (Segment(p, a, b) < 1.2f) col = new Color(0.98f, 0.96f, 0.92f);
                img.SetPixel(x, y, col);
            }
        return _cat = ImageTexture.CreateFromImage(img);
    }
}
