using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Neon signs (#1232): a venue's name in tube lettering beside a generic icon of what it is (a pin
/// and a ball, a film reel, a joystick, a cocktail glass...), never a real logo. The icon is tubes:
/// polylines drawn as flat ribbons with a white-hot core (<c>shaders/neon_tube.gdshader</c>, colour in
/// the vertex colour), one mesh and one draw. The lettering is a <see cref="Label3D"/> in Saira, a
/// bright core inside an outline of the gas colour. Built once per sign, nothing per frame.
/// </summary>
public static class NeonSign
{
    /// <summary>A sign is drawn to here: past it a door sign is a few pixels.</summary>
    public const float DrawnM = 250f;

    /// <summary>The tube's width, m, at an icon one metre tall.</summary>
    private const float Tube = 0.07f;

    /// <summary>
    /// How far in front of the tube before it each tube of an icon stands, at an icon one metre tall: over
    /// the 8 mm a z-fight is judged within (`zfight`) at the smallest icon a sign draws (0.8 m).
    /// </summary>
    private const float StrokeStep = 0.02f;

    /// <summary>Longest name lettered in full; a longer one is cut at a word.</summary>
    private const int MaxChars = 26;

    private static ShaderMaterial? _tube;
    private static FontVariation? _font;

    /// <summary>The lettering's gas colour and the icon's tubes, per kind.</summary>
    public static Color TextColor(VenueType kind, string cuisine = "") => kind switch
    {
        VenueType.Bowling => new Color(1.0f, 0.16f, 0.22f),
        VenueType.Cinema => new Color(1.0f, 0.78f, 0.22f),
        VenueType.Arcade => new Color(1.0f, 0.22f, 0.86f),
        VenueType.Cafe => new Color(1.0f, 0.58f, 0.24f),
        VenueType.FastFood => new Color(1.0f, 0.86f, 0.18f),
        VenueType.Bar => new Color(0.24f, 0.62f, 1.0f),
        VenueType.Pub => new Color(0.22f, 1.0f, 0.42f),
        VenueType.Nightclub => new Color(0.68f, 0.30f, 1.0f),
        VenueType.Biergarten => new Color(1.0f, 0.70f, 0.20f),
        _ => IsPizza(cuisine) ? new Color(1.0f, 0.24f, 0.18f) : new Color(1.0f, 0.86f, 0.62f),
    };

    private static bool IsPizza(string cuisine) => cuisine is "pizza" or "italian";

    /// <summary>The name as lettered: cut at a word past <see cref="MaxChars"/>.</summary>
    public static string Lettering(string name)
    {
        name = name.Trim();
        if (name.Length <= MaxChars) return name;
        int cut = name.LastIndexOf(' ', MaxChars);
        return (cut > 8 ? name[..cut] : name[..MaxChars]).TrimEnd(' ', ',', '-', '&');
    }

    /// <summary>
    /// A sign: the icon at the left, the name to its right, both centred on the origin, +Z facing
    /// the street, letters <paramref name="letterHeight"/> tall and the icon a little taller.
    /// <paramref name="maxWidth"/> caps the whole: a long name is lettered smaller.
    /// </summary>
    public static Node3D Make(string name, VenueType kind, string cuisine = "", float letterHeight = 0.42f, float maxWidth = 5f,
        bool flicker = false)
    {
        string text = Lettering(name);
        var color = TextColor(kind, cuisine);
        // Saira's capitals are about 0.7 of the em and an average glyph about 0.55 of it wide
        float em = letterHeight / 0.7f;
        float icon = letterHeight * 1.9f, gap = letterHeight * 0.45f;
        float textW = text.Length * em * 0.55f;
        float scale = Math.Min(1f, maxWidth / (icon + gap + textW));
        em *= scale;
        icon *= scale;
        gap *= scale;
        textW *= scale;
        float total = icon + gap + textW;

        var sign = new Node3D { Name = "NeonSign" };
        var mesh = IconMesh(kind, cuisine);
        if (mesh != null)
        {
            var tubes = new MeshInstance3D
            {
                Name = "Icon",
                Mesh = mesh,
                Scale = Vector3.One * icon,
                Position = new Vector3(-total / 2 + icon / 2, 0, 0.02f),
                VisibilityRangeEnd = DrawnM,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            tubes.SetSurfaceOverrideMaterial(0, TubeMaterial(flicker));
            sign.AddChild(tubes);
            sign.AddChild(new MeshInstance3D
            {
                Name = "IconWash",
                Mesh = new QuadMesh { Size = Vector2.One * icon * 1.9f },
                MaterialOverride = Wash(IconColor(kind, cuisine)),
                Position = new Vector3(-total / 2 + icon / 2, 0, -0.09f),
                VisibilityRangeEnd = DrawnM,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        // the light the sign throws on the wall behind it: what makes it read as lit at night in a style with no bloom
        sign.AddChild(new MeshInstance3D
        {
            Name = "Wash",
            Mesh = new QuadMesh { Size = new Vector2(total + letterHeight * 2.4f * scale, letterHeight * 4.2f * scale) },
            MaterialOverride = Wash(color),
            Position = new Vector3(0, 0, -0.07f),
            VisibilityRangeEnd = DrawnM,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        // the lettering on both faces: whichever way the facade's outward runs, one reads from the street
        const int fontSize = 96;
        foreach (float side in new[] { 1f, -1f })
            sign.AddChild(new Label3D
            {
                Text = text,
                Font = SignFont(),
                FontSize = fontSize,
                PixelSize = em / fontSize,
                // a tube is the letter in its gas colour with a whiter core, haloed by a thin translucent
                // outline; HDR on purpose, brighter than white, so the lit styles' glow blooms it at night
                OutlineSize = 12,
                Modulate = color * 1.45f,
                OutlineModulate = new Color(color.R, color.G, color.B, 0.45f),
                Shaded = false,
                DoubleSided = false,
                RenderPriority = 1,
                OutlineRenderPriority = 0,
                VisibilityRangeEnd = DrawnM,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Transform = new Transform3D(new Basis(Vector3.Up, side > 0 ? 0 : Mathf.Pi),
                    new Vector3(total / 2 - textW / 2, 0, 0.03f * side)),
            });
        return sign;
    }

    /// <summary>Saira at a medium weight: the variable font's default is a hairline, too thin to read from the street.</summary>
    public static Font? SignFont()
    {
        if (_font != null) return _font;
        var file = ResourceLoader.Load<FontFile>("res://assets/fonts/Saira-Variable.ttf");
        if (file == null) return null;
        return _font = new FontVariation
        {
            BaseFont = file,
            VariationOpentype = new Godot.Collections.Dictionary { { TextServerManager.GetPrimaryInterface().NameToTag("weight"), 560 } },
        };
    }

    private static readonly Dictionary<Color, ShaderMaterial> Washes = new();

    private static ShaderMaterial Wash(Color color)
    {
        if (!Washes.TryGetValue(color, out var m)) Washes[color] = m = HaloMaterial(color);
        return m;
    }

    /// <summary>A wall wash of one colour: the light a sign throws on the wall behind it.</summary>
    private static ShaderMaterial HaloMaterial(Color wash)
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/neon_halo.gdshader") };
        m.SetShaderParameter("tint", wash);
        m.SetShaderParameter("strength", 0.4f);
        return m;
    }

    private static ShaderMaterial TubeMaterial(bool flicker)
    {
        if (!flicker) return _tube ??= New(0f);
        return New(0.85f);

        static ShaderMaterial New(float f)
        {
            var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/neon_tube.gdshader") };
            m.SetShaderParameter("flicker", f);
            return m;
        }
    }

    // ---- icons: polylines in a unit box, y up, x right, centred on the origin ---------------------

    /// <summary>One tube: its points and its gas colour.</summary>
    public readonly record struct Stroke(Vector2[] Points, Color Color, bool Closed = false);

    /// <summary>The icon for a kind of venue as tubes, or none.</summary>
    public static List<Stroke> Icon(VenueType kind, string cuisine = "")
    {
        var s = new List<Stroke>();
        Color white = new(0.95f, 0.95f, 1f), red = new(1f, 0.18f, 0.2f), blue = new(0.25f, 0.55f, 1f),
            gold = new(1f, 0.78f, 0.25f), cyan = new(0.2f, 0.95f, 1f), magenta = new(1f, 0.25f, 0.85f),
            amber = new(1f, 0.62f, 0.15f), green = new(0.3f, 1f, 0.4f), pink = new(1f, 0.4f, 0.65f);
        switch (kind)
        {
            case VenueType.Bowling:
            {
                // a pin, a ball in front of it with its three holes
                var pin = new List<Vector2>();
                (float Y, float W)[] profile = { (-0.48f, 0.07f), (-0.30f, 0.13f), (-0.12f, 0.12f), (0.04f, 0.055f), (0.12f, 0.05f),
                    (0.24f, 0.08f), (0.36f, 0.075f), (0.45f, 0.03f) };
                foreach (var (y, w) in profile) pin.Add(new Vector2(-0.16f + w, y));
                for (int i = profile.Length - 1; i >= 0; i--) pin.Add(new Vector2(-0.16f - profile[i].W, profile[i].Y));
                s.Add(new Stroke(pin.ToArray(), white, true));
                s.Add(new Stroke(new[] { new Vector2(-0.215f, 0.10f), new Vector2(-0.105f, 0.10f) }, red));
                s.Add(new Stroke(new[] { new Vector2(-0.22f, 0.16f), new Vector2(-0.10f, 0.16f) }, red));
                s.Add(new Stroke(Circle(new Vector2(0.18f, -0.24f), 0.22f, 20), blue, true));
                foreach (var h in new[] { new Vector2(0.12f, -0.15f), new Vector2(0.22f, -0.13f), new Vector2(0.20f, -0.24f) })
                    s.Add(new Stroke(Circle(h, 0.028f, 8), blue, true));
                break;
            }
            case VenueType.Cinema:
            {
                // a film reel, and a strip of film running off it
                s.Add(new Stroke(Circle(new Vector2(-0.08f, 0.08f), 0.36f, 24), gold, true));
                for (int i = 0; i < 5; i++)
                {
                    float a = Mathf.Tau * i / 5 + 0.3f;
                    s.Add(new Stroke(Circle(new Vector2(-0.08f, 0.08f) + 0.2f * new Vector2(Mathf.Cos(a), Mathf.Sin(a)), 0.075f, 10), gold, true));
                }
                s.Add(new Stroke(Circle(new Vector2(-0.08f, 0.08f), 0.035f, 8), gold, true));
                s.Add(new Stroke(new[] { new Vector2(0.10f, -0.24f), new Vector2(0.48f, -0.44f) }, white));
                s.Add(new Stroke(new[] { new Vector2(0.20f, -0.10f), new Vector2(0.50f, -0.26f) }, white));
                break;
            }
            case VenueType.Arcade:
            {
                // a joystick on its base, two buttons
                s.Add(new Stroke(new[] { new Vector2(-0.42f, -0.48f), new Vector2(0.42f, -0.48f), new Vector2(0.36f, -0.24f),
                    new Vector2(-0.36f, -0.24f) }, magenta, true));
                s.Add(new Stroke(new[] { new Vector2(-0.12f, -0.24f), new Vector2(0.02f, 0.18f) }, cyan));
                s.Add(new Stroke(Circle(new Vector2(0.05f, 0.30f), 0.14f, 14), red, true));
                s.Add(new Stroke(Circle(new Vector2(0.18f, -0.36f), 0.05f, 8), cyan, true));
                s.Add(new Stroke(Circle(new Vector2(0.30f, -0.36f), 0.05f, 8), gold, true));
                break;
            }
            case VenueType.Cafe:
            {
                // a cup on its saucer, steam rising
                s.Add(new Stroke(new[] { new Vector2(-0.30f, 0.05f), new Vector2(-0.26f, -0.22f), new Vector2(-0.16f, -0.32f),
                    new Vector2(0.10f, -0.32f), new Vector2(0.20f, -0.22f), new Vector2(0.24f, 0.05f) }, amber, true));
                s.Add(new Stroke(Arc(new Vector2(0.25f, -0.10f), 0.10f, -1.4f, 1.4f, 8), amber));
                s.Add(new Stroke(new[] { new Vector2(-0.42f, -0.40f), new Vector2(0.38f, -0.40f) }, white));
                foreach (float x in new[] { -0.14f, 0.06f })
                    s.Add(new Stroke(new[] { new Vector2(x, 0.14f), new Vector2(x + 0.05f, 0.24f), new Vector2(x - 0.02f, 0.34f),
                        new Vector2(x + 0.04f, 0.45f) }, white));
                break;
            }
            case VenueType.Bar:
            case VenueType.Nightclub:
            {
                // a cocktail glass with an olive on a stick
                s.Add(new Stroke(new[] { new Vector2(-0.36f, 0.38f), new Vector2(0.36f, 0.38f), new Vector2(0f, 0f) }, kind == VenueType.Bar ? cyan : magenta, true));
                s.Add(new Stroke(new[] { new Vector2(0f, 0f), new Vector2(0f, -0.36f) }, white));
                s.Add(new Stroke(new[] { new Vector2(-0.18f, -0.44f), new Vector2(0.18f, -0.44f) }, white));
                s.Add(new Stroke(new[] { new Vector2(-0.05f, 0.18f), new Vector2(0.26f, 0.52f) }, white));
                s.Add(new Stroke(Circle(new Vector2(0.06f, 0.28f), 0.06f, 8), green, true));
                break;
            }
            case VenueType.Pub:
            case VenueType.Biergarten:
            {
                // a beer mug, a head of foam, a handle
                s.Add(new Stroke(new[] { new Vector2(-0.30f, 0.22f), new Vector2(-0.30f, -0.44f), new Vector2(0.18f, -0.44f),
                    new Vector2(0.18f, 0.22f) }, amber));
                s.Add(new Stroke(new[] { new Vector2(-0.34f, 0.22f), new Vector2(-0.30f, 0.34f), new Vector2(-0.18f, 0.40f),
                    new Vector2(-0.06f, 0.34f), new Vector2(0.06f, 0.40f), new Vector2(0.18f, 0.34f), new Vector2(0.22f, 0.22f) }, white));
                s.Add(new Stroke(Arc(new Vector2(0.18f, -0.10f), 0.16f, -1.5f, 1.5f, 10), amber));
                s.Add(new Stroke(new[] { new Vector2(-0.18f, 0.10f), new Vector2(-0.18f, -0.32f) }, white));
                s.Add(new Stroke(new[] { new Vector2(0.02f, 0.10f), new Vector2(0.02f, -0.32f) }, white));
                break;
            }
            case VenueType.FastFood:
            {
                // a burger
                s.Add(new Stroke(Arc(new Vector2(0f, 0.02f), 0.42f, 0f, Mathf.Pi, 14), amber, true));
                s.Add(new Stroke(new[] { new Vector2(-0.44f, -0.10f), new Vector2(-0.30f, -0.04f), new Vector2(-0.15f, -0.12f),
                    new Vector2(0f, -0.04f), new Vector2(0.15f, -0.12f), new Vector2(0.30f, -0.04f), new Vector2(0.44f, -0.10f) }, green));
                s.Add(new Stroke(new[] { new Vector2(-0.42f, -0.20f), new Vector2(0.42f, -0.20f) }, red));
                s.Add(new Stroke(new[] { new Vector2(-0.40f, -0.30f), new Vector2(0.40f, -0.30f), new Vector2(0.32f, -0.44f),
                    new Vector2(-0.32f, -0.44f) }, amber, true));
                break;
            }
            default:
                if (IsPizza(cuisine))
                {
                    // a slice of pizza, crust at the top
                    s.Add(new Stroke(new[] { new Vector2(-0.38f, 0.34f), new Vector2(0.38f, 0.34f), new Vector2(0f, -0.48f) }, amber, true));
                    s.Add(new Stroke(Arc(new Vector2(0f, 0.30f), 0.40f, 0.35f, Mathf.Pi - 0.35f, 10), gold));
                    foreach (var p in new[] { new Vector2(-0.12f, 0.16f), new Vector2(0.12f, 0.12f), new Vector2(0f, -0.14f) })
                        s.Add(new Stroke(Circle(p, 0.06f, 8), red, true));
                }
                else
                {
                    // a fork and a knife
                    s.Add(new Stroke(new[] { new Vector2(-0.18f, -0.48f), new Vector2(-0.18f, 0.10f) }, white));
                    s.Add(new Stroke(new[] { new Vector2(-0.32f, 0.44f), new Vector2(-0.32f, 0.16f), new Vector2(-0.24f, 0.08f),
                        new Vector2(-0.12f, 0.08f), new Vector2(-0.04f, 0.16f), new Vector2(-0.04f, 0.44f) }, white));
                    s.Add(new Stroke(new[] { new Vector2(-0.18f, 0.44f), new Vector2(-0.18f, 0.18f) }, white));
                    s.Add(new Stroke(new[] { new Vector2(0.20f, -0.48f), new Vector2(0.20f, 0.44f), new Vector2(0.32f, 0.30f),
                        new Vector2(0.32f, -0.02f), new Vector2(0.20f, -0.06f) }, gold));
                }
                break;
        }
        return s;
    }

    private static Vector2[] Circle(Vector2 c, float r, int n) => Arc(c, r, 0, Mathf.Tau * (n - 1) / n, n);

    private static Vector2[] Arc(Vector2 c, float r, float a0, float a1, int n)
    {
        var p = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float a = a0 + (a1 - a0) * i / (n - 1);
            p[i] = c + r * new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }
        return p;
    }

    /// <summary>
    /// The icon's tubes as one mesh in the XY plane (a metre tall: scale it): each segment two strips
    /// either side of its core (UV.y 0 at the edges, 1 on the core) and a disc at every joint so a
    /// bend has no notch. Null for a kind with no icon.
    /// </summary>
    public static ArrayMesh? IconMesh(VenueType kind, string cuisine = "")
    {
        var strokes = Icon(kind, cuisine);
        if (strokes.Count == 0) return null;
        var mesh = new ArrayMesh();
        Surface(mesh, strokes, Tube / 2, 0f, joints: true);
        return mesh;
    }

    /// <summary>The mean gas colour of a kind's icon, for the glow behind it.</summary>
    public static Color IconColor(VenueType kind, string cuisine = "")
    {
        var strokes = Icon(kind, cuisine);
        if (strokes.Count == 0) return TextColor(kind, cuisine);
        float r = 0, g = 0, b = 0;
        foreach (var st in strokes) { r += st.Color.R; g += st.Color.G; b += st.Color.B; }
        return new Color(r / strokes.Count, g / strokes.Count, b / strokes.Count);
    }

    private static void Surface(ArrayMesh mesh, List<Stroke> strokes, float h, float z, bool joints)
    {
        var v = new List<Vector3>();
        var uv = new List<Vector2>();
        var col = new List<Color>();
        // each tube a layer in front of the one before, so tubes of two colours crossing never share a plane
        // (a pin's stripes over its outline, a ball's holes); a tube's joint discs a hair behind its strips:
        // drawn level, their radial shading beaded every bend
        float layer = z, depth = z;
        void Tri(Vector2 a, float ua, Vector2 b, float ub, Vector2 c, float uc, Color k)
        {
            v.Add(new Vector3(a.X, a.Y, depth)); uv.Add(new Vector2(0, ua)); col.Add(k);
            v.Add(new Vector3(b.X, b.Y, depth)); uv.Add(new Vector2(0, ub)); col.Add(k);
            v.Add(new Vector3(c.X, c.Y, depth)); uv.Add(new Vector2(0, uc)); col.Add(k);
        }
        foreach (var st in strokes)
        {
            depth = layer;
            var p = st.Points;
            int segs = st.Closed ? p.Length : p.Length - 1;
            for (int i = 0; i < segs; i++)
            {
                Vector2 a = p[i], b = p[(i + 1) % p.Length];
                var d = b - a;
                if (d.LengthSquared() < 1e-8f) continue;
                var n = new Vector2(-d.Y, d.X).Normalized() * h;
                // core to one edge, core to the other
                Tri(a, 1, a + n, 0, b + n, 0, st.Color);
                Tri(a, 1, b + n, 0, b, 1, st.Color);
                Tri(a, 1, b, 1, b - n, 0, st.Color);
                Tri(a, 1, b - n, 0, a - n, 0, st.Color);
            }
            layer += StrokeStep;
            if (!joints) continue;
            depth -= 0.004f;
            foreach (var c in p)
                for (int k = 0; k < 8; k++)
                {
                    float a0 = Mathf.Tau * k / 8, a1 = Mathf.Tau * (k + 1) / 8;
                    Tri(c, 1, c + h * new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)), 0, c + h * new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)), 0, st.Color);
                }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = col.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    /// <summary>A neon sign for every kind of venue in the model viewer, with a name of the right length.</summary>
    [Core.Showcase("Interiors", "Neon sign")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> ShowcaseSigns()
    {
        (VenueType Kind, string Cuisine, string Name)[] rows =
        {
            (VenueType.Bowling, "", "Bowling de Vidy"), (VenueType.Cinema, "", "Cinéma Rex"), (VenueType.Arcade, "", "Pixel Arcade"),
            (VenueType.Restaurant, "regional", "Restaurant du Commerce"), (VenueType.Restaurant, "pizza", "Pizzeria Da Mario"),
            (VenueType.Cafe, "", "Café de la Gare"), (VenueType.FastFood, "burger", "Burger"), (VenueType.Bar, "", "Bar Le Lounge"),
            (VenueType.Pub, "", "The Old Pub"), (VenueType.Nightclub, "", "Club"), (VenueType.Biergarten, "", "Biergarten"),
        };
        foreach (var (kind, cuisine, name) in rows)
            yield return (cuisine.Length > 0 ? $"{kind} ({cuisine})" : kind.ToString(), () =>
            {
                var root = new Node3D();
                var sign = Make(name, kind, cuisine);
                sign.Position = new Vector3(0, 0.6f, 0);
                root.AddChild(sign);
                return root;
            });
    }
}
