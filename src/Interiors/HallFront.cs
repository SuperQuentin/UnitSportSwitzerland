using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Occasions;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// The street front of a hall venue that takes its whole building (#1250, <see cref="DoorSpot.WholeHall"/>):
/// the blind box's one glazed face round its main door, aluminium mullions, a flat canopy over it with
/// downlights under, and at a cinema the film titles along the canopy's fascia and lit poster cases on
/// the wall beside the glass [S: Cine Rex Bern, Plaza Zürich, Cineplexx Innsbruck: a glass front of 1 m
/// bays 2.4-3.4 m tall, a canopy 2-3 m deep at 3-3.5 m, posters 0.9 x 1.28 m (Weltformat) from 0.6 m].
/// Built in the door's frame (origin the door's foot, +Z the street, +X along the wall), once per tile.
/// </summary>
public static class HallFront
{
    /// <summary>The canopy's depth and thickness, m.</summary>
    public const float CanopyDepth = 2.2f, CanopyThick = 0.55f;

    /// <summary>A poster case's outer size and its foot above the pavement, m.</summary>
    private const float CaseW = 1.04f, CaseH = 1.42f, CaseFoot = 0.62f;

    private const float Sink = 0.012f;

    private static readonly Color Alu = PropColors.Matte(0.30f, 0.31f, 0.33f), Frame = PropColors.Matte(0.10f, 0.10f, 0.11f),
        Glass = PropColors.Matte(0.17f, 0.22f, 0.28f), White = PropColors.Matte(0.93f, 0.93f, 0.90f),
        Downlight = PropColors.Lamp(1.0f, 0.92f, 0.70f);

    /// <summary>What a front is: its glass's width and top, whether it has a canopy, all in the door's frame.</summary>
    public readonly record struct Shape(float Width, float GlassTop, bool Canopy)
    {
        /// <summary>Where a name sign over the front stands, its centre's height.</summary>
        public float NameY => Canopy ? GlassTop + CanopyThick + 0.65f : GlassTop + 0.55f;
    }

    /// <summary>The front a door gets, or null when its wall is too short or its building too low for one.</summary>
    public static Shape? ShapeOf(DoorSpot d)
    {
        if (!d.WholeHall || d.Run < d.Width + 3f) return null;
        float top = d.Top - d.Position.Y, doorTop = Math.Max(2.1f, d.Height);
        float glassTop = Math.Min(top - 1.6f, 3.4f);
        if (glassTop < doorTop + 0.3f) return null;
        float w = Math.Min(d.Run - 0.8f, Math.Clamp(d.Width + 6f, 6f, 12f));
        return new Shape(w, glassTop, top >= glassTop + CanopyThick + 1.6f);
    }

    /// <summary>The front of door <paramref name="d"/>, in its frame.</summary>
    public static Node3D Make(DoorSpot d, VenueRecord v, Shape shape, Material prop)
    {
        var root = new Node3D { Name = "HallFront" };
        root.AddChild(new MeshInstance3D
        {
            Name = "Front",
            Mesh = Build(d, v.Type, shape),
            MaterialOverride = prop,
            // MeshScratch hands its meshes back turned half round: turned back, so +Z is the street
            Rotation = new Vector3(0, Mathf.Pi, 0),
            VisibilityRangeEnd = 400f,
        });
        if (v.Type != VenueType.Cinema) return root;

        // the films on the canopy's fascia, black capitals on white [S: Cine Rex]
        if (shape.Canopy)
        {
            float y = shape.GlassTop + CanopyThick / 2;
            root.AddChild(new Label3D
            {
                Name = "Titles",
                Text = Marquee(Films(v.Osm, 6), shape.Width),
                Font = NeonSign.SignFont(),
                FontSize = 64,
                PixelSize = 0.3f / 0.7f / 64,
                Modulate = new Color(0.06f, 0.06f, 0.07f),
                OutlineSize = 0,
                Shaded = false,
                DoubleSided = false,
                VisibilityRangeEnd = NeonSign.DrawnM,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(0, y, CanopyDepth + 2 * Sink + 0.012f),
            });
        }
        // the posters: in cases on the wall beside the glass, else stuck inside the glass beside the door
        int n = 0;
        var films = Films(v.Osm, 8);
        foreach (float x in PosterSpots(d, shape))
        {
            var film = films[n++ % films.Length];
            root.AddChild(new MeshInstance3D
            {
                Name = "Poster",
                Mesh = new QuadMesh { Size = new Vector2(CaseW - 0.14f, CaseH - 0.14f) },
                MaterialOverride = PosterMaterial(film.Art),
                Position = new Vector3(x, CaseFoot + CaseH / 2, 0.1f + 0.024f),
                VisibilityRangeEnd = NeonSign.DrawnM,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
            root.AddChild(new Label3D
            {
                Name = "PosterTitle",
                Text = film.Title,
                Font = NeonSign.SignFont(),
                FontSize = 48,
                PixelSize = 0.09f / 0.7f / 48,
                Modulate = Colors.White,
                OutlineSize = 8,
                OutlineModulate = new Color(0, 0, 0, 0.7f),
                Shaded = false,
                DoubleSided = false,
                Width = (CaseW - 0.2f) / (0.09f / 0.7f / 48),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                VisibilityRangeEnd = 60f,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(x, CaseFoot + 0.3f, 0.1f + 0.036f),
            });
        }
        return root;
    }

    /// <summary>As many of the films as fit along a canopy <paramref name="width"/> wide in 0.3 m capitals, three spaces apart.</summary>
    public static string Marquee((string Title, int Art)[] films, float width)
    {
        // Saira's capitals: about 0.62 of the em wide, the em 0.3 / 0.7 m
        float per = 0.3f / 0.7f * 0.62f, room = width - 0.3f;
        var text = "";
        foreach (var f in films)
        {
            string next = text.Length == 0 ? f.Title : text + "   " + f.Title;
            if (next.Length * per > room) break;
            text = next;
        }
        return text;
    }

    /// <summary>The poster cases' centres along the wall: two each side outside the glass where the run has room, else inside it.</summary>
    public static List<float> PosterSpots(DoorSpot d, Shape shape)
    {
        var xs = new List<float>();
        float half = shape.Width / 2, room = d.Run / 2 - 0.3f;
        foreach (int side in new[] { -1, 1 })
            for (int k = 0; k < 2; k++)
            {
                float x = half + 0.35f + CaseW / 2 + k * (CaseW + 0.25f);
                if (x + CaseW / 2 <= room) xs.Add(side * x);
            }
        if (xs.Count > 0) return xs;
        // inside the glass: from beside the door's frame outwards
        foreach (int side in new[] { -1, 1 })
            for (int k = 0; k < 2; k++)
            {
                float x = d.Width / 2 + 0.35f + CaseW / 2 + k * (CaseW + 0.2f);
                if (x + CaseW / 2 <= half - 0.15f) xs.Add(side * x);
            }
        return xs;
    }

    private static ArrayMesh Build(DoorSpot d, VenueType kind, Shape shape)
    {
        var s = new MeshScratch();
        float half = shape.Width / 2, dw = d.Width / 2 + 0.06f, doorTop = Math.Max(2.1f, d.Height) + 0.06f, gt = shape.GlassTop;
        // the glass, round the door's hole: a pane each side and the transom over it, 1.5 cm proud of the wall
        void Pane(float x0, float x1, float y0, float y1)
        {
            if (x1 - x0 < 0.05f || y1 - y0 < 0.05f) return;
            s.Box(new Vector3((x0 + x1) / 2, (y0 + y1) / 2, 0), new Vector3(x1 - x0, y1 - y0, 0.03f), Glass);
        }
        Pane(-half, -dw, 0.05f, gt);
        Pane(dw, half, 0.05f, gt);
        Pane(-dw, dw, doorTop, gt);
        // the mullions: a frame round it all, one every metre or so, the transom bar at the door's head
        const float m = 0.07f, md = 0.07f, mz = 0.035f;
        s.Box(new Vector3(0, gt + m / 2 - Sink, mz), new Vector3(shape.Width + m, m, md), Alu);
        s.Box(new Vector3(0, 0.05f, mz), new Vector3(shape.Width + m, 0.1f, md), Alu);
        int bays = Math.Max(2, (int)MathF.Round((half - dw) / 1.1f));
        foreach (int side in new[] { -1, 1 })
        {
            for (int k = 0; k <= bays; k++)
            {
                float x = side * (dw + (half - dw) * k / bays);
                s.Box(new Vector3(x, gt / 2 + 0.05f, mz), new Vector3(m, gt - 0.1f, md), Alu);
            }
            s.Box(new Vector3(side * (dw + half) / 2, doorTop + m / 2, mz + Sink), new Vector3(half - dw, m, md), Alu);
        }
        s.Box(new Vector3(0, doorTop + m / 2, mz + Sink), new Vector3(2 * dw + m, m, md), Alu);

        if (shape.Canopy)
        {
            var body = kind switch
            {
                VenueType.Bowling => PropColors.Matte(0.62f, 0.10f, 0.12f),
                VenueType.Arcade => PropColors.Matte(0.30f, 0.12f, 0.42f),
                _ => PropColors.Matte(0.16f, 0.16f, 0.18f),
            };
            float cw = shape.Width + 0.6f, cy = gt + m + CanopyThick / 2;
            s.Box(new Vector3(0, cy, CanopyDepth / 2), new Vector3(cw, CanopyThick, CanopyDepth), body);
            // a cinema's fascia, white for the titles; the others a band of their colour, lighter
            var band = kind == VenueType.Cinema ? White : PropColors.Matte(body.R * 1.6f, body.G * 1.6f, body.B * 1.6f);
            s.Box(new Vector3(0, cy, CanopyDepth + Sink), new Vector3(cw - 0.1f, CanopyThick - 0.12f, 2 * Sink), band);
            // downlights in rows under it
            for (float x = -cw / 2 + 0.6f; x <= cw / 2 - 0.5f; x += 1.1f)
                for (float z = 0.6f; z < CanopyDepth - 0.3f; z += 0.8f)
                    s.Box(new Vector3(x, cy - CanopyThick / 2 - 0.01f + Sink / 2, z), new Vector3(0.14f, 0.02f + Sink, 0.14f), Downlight);
        }
        if (kind == VenueType.Cinema)
            foreach (float x in PosterSpots(d, shape))
            {
                // a case: its dark frame and the lit back the poster stands on
                s.Box(new Vector3(x, CaseFoot + CaseH / 2, 0.05f), new Vector3(CaseW, CaseH, 0.1f), Frame);
                s.Box(new Vector3(x, CaseFoot + CaseH / 2, 0.1f), new Vector3(CaseW - 0.1f, CaseH - 0.1f, 2 * Sink), Downlight);
            }
        return s.Build();
    }

    // ---- the films: made up, with their posters drawn by code ------------------------------------

    /// <summary>Made-up films (no real title, no real poster): a title and which of the drawn posters it has.</summary>
    private static readonly (string Title, int Art)[] Programme =
    {
        ("COL DU DIABLE", 0), ("NUIT À GENÈVE", 1), ("STERNENFAHRT", 2), ("MINOU", 3), ("DERNIER TRAIN", 4),
        ("AMOUR À OUCHY", 5), ("LA GRANDE FONDUE", 0), ("ORBIT ZÜRI", 2), ("LE CHAT BOTTÉ II", 3), ("ALPENGLÜHEN", 0),
        ("ROUTE DU SIMPLON", 4), ("DREI TAGE REGEN", 1),
    };

    /// <summary><paramref name="n"/> films for one cinema, from its OSM id: what it shows this week.</summary>
    public static (string Title, int Art)[] Films(string osm, int n)
    {
        int start = (int)(Fnv.Unit(osm + "|films") * Programme.Length);
        return Enumerable.Range(0, n).Select(i => Programme[(start + i * 5) % Programme.Length]).ToArray();
    }

    private static readonly Dictionary<int, StandardMaterial3D> Posters = new();

    private static StandardMaterial3D PosterMaterial(int art)
    {
        if (Posters.TryGetValue(art, out var m)) return m;
        return Posters[art] = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = ImageTexture.CreateFromImage(PosterArt(art)),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        };
    }

    /// <summary>
    /// A poster drawn by code, 90 x 128 px: a sky, one big motif (the Alps under a sun, a figure in a
    /// lit street, a planet in the stars, a cat's head, rails to the horizon, a heart over the lake),
    /// and a dark band at the foot for the title (lettered over it).
    /// </summary>
    public static Image PosterArt(int art)
    {
        const int w = 90, h = 128;
        var img = Image.CreateEmpty(w, h, true, Image.Format.Rgb8);
        var (top, bottom, ink) = (art % 6) switch
        {
            0 => (new Color(0.98f, 0.62f, 0.30f), new Color(0.55f, 0.20f, 0.42f), new Color(0.16f, 0.10f, 0.22f)),
            1 => (new Color(0.08f, 0.10f, 0.24f), new Color(0.35f, 0.12f, 0.30f), new Color(1.0f, 0.82f, 0.36f)),
            2 => (new Color(0.02f, 0.02f, 0.08f), new Color(0.10f, 0.14f, 0.34f), new Color(0.40f, 0.80f, 0.95f)),
            3 => (new Color(0.98f, 0.86f, 0.30f), new Color(0.95f, 0.50f, 0.20f), new Color(0.10f, 0.08f, 0.08f)),
            4 => (new Color(0.62f, 0.78f, 0.92f), new Color(0.88f, 0.90f, 0.86f), new Color(0.22f, 0.24f, 0.28f)),
            _ => (new Color(0.95f, 0.70f, 0.78f), new Color(0.40f, 0.62f, 0.86f), new Color(0.86f, 0.12f, 0.24f)),
        };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float fy = y / (float)h;
                var c = top.Lerp(bottom, fy);
                var p = new Vector2(x, y);
                switch (art % 6)
                {
                    case 0: // a sun behind three peaks
                        if (p.DistanceTo(new Vector2(58, 40)) < 16) c = new Color(1f, 0.92f, 0.62f);
                        if (y > 92 - 40 * Math.Max(0, 1 - Math.Abs(x - 30) / 30f) || y > 96 - 46 * Math.Max(0, 1 - Math.Abs(x - 64) / 34f)) c = ink;
                        break;
                    case 1: // a figure under a street lamp
                        if (Math.Abs(x - 26) < 2 && y > 20 && y < 104) c = ink * 0.8f;
                        if (p.DistanceTo(new Vector2(30, 22)) < 6) c = ink;
                        if (p.DistanceTo(new Vector2(56, 52)) < 7 || Math.Abs(x - 56) < 9 - (y - 60) * 0.02f && y > 58 && y < 104) c = new Color(0.02f, 0.02f, 0.04f);
                        break;
                    case 2: // a ringed planet in the stars
                        if (Fnv.Unit($"{x},{y}") < 0.02) c = Colors.White;
                        if (p.DistanceTo(new Vector2(45, 54)) < 22) c = ink.Lerp(new Color(0.1f, 0.3f, 0.6f), (y - 32) / 44f);
                        if (Math.Abs((p - new Vector2(45, 54)).Dot(new Vector2(0.3f, 1f).Normalized())) < 2.5f && p.DistanceTo(new Vector2(45, 54)) < 36 && p.DistanceTo(new Vector2(45, 54)) > 22) c = new Color(0.95f, 0.85f, 0.6f);
                        break;
                    case 3: // a cat's head and ears
                        bool head = (p - new Vector2(45, 62)).LengthSquared() < 26 * 26;
                        bool ear = Tri(p, new(22, 50), new(28, 22), new(42, 40)) || Tri(p, new(48, 40), new(62, 22), new(68, 50));
                        if (head || ear) c = ink;
                        if ((p - new Vector2(35, 58)).Length() < 4 || (p - new Vector2(55, 58)).Length() < 4) c = new Color(0.4f, 0.9f, 0.3f);
                        break;
                    case 4: // rails to the horizon
                        if (y > 60 && (Math.Abs(x - 45 - (y - 60) * 0.55f) < 1.5f || Math.Abs(x - 45 + (y - 60) * 0.55f) < 1.5f)) c = ink;
                        if (y > 60 && y % 7 == 0 && Math.Abs(x - 45) < (y - 60) * 0.6f) c = ink * 1.3f;
                        if (y < 60 && y > 56) c = ink * 1.6f;
                        break;
                    default: // a heart over the lake
                        var q = (p - new Vector2(45, 50)) / 18f;
                        float hx = q.X, hy = -q.Y;
                        if (Math.Pow(hx * hx + hy * hy - 1, 3) - hx * hx * hy * hy * hy < 0) c = ink;
                        if (y > 84) c = new Color(0.20f, 0.42f, 0.70f).Lerp(new Color(0.10f, 0.25f, 0.50f), (y - 84) / 44f);
                        break;
                }
                if (y > 100) c = c.Lerp(new Color(0.03f, 0.03f, 0.04f), 0.75f);
                img.SetPixel(x, y, c);
            }
        img.GenerateMipmaps();
        return img;
    }

    private static bool Tri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        static float Edge(Vector2 x, Vector2 y, Vector2 q) => (y.X - x.X) * (q.Y - x.Y) - (y.Y - x.Y) * (q.X - x.X);
        float e0 = Edge(a, b, p), e1 = Edge(b, c, p), e2 = Edge(c, a, p);
        return e0 >= 0 && e1 >= 0 && e2 >= 0 || e0 <= 0 && e1 <= 0 && e2 <= 0;
    }

    /// <summary>Hall fronts in the model viewer: a cinema's, a bowling centre's and an arcade's on a 9 m box.</summary>
    [Showcase("Interiors", "Hall front")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> ShowcaseFronts()
    {
        foreach (var kind in new[] { VenueType.Cinema, VenueType.Bowling, VenueType.Arcade })
            yield return (kind.ToString(), () =>
            {
                var d = new DoorSpot(0, Vector3.Zero, Vector3.Back, 1.8f, 2.4f) { WholeHall = true, Top = 9f, Run = 20f };
                var v = new VenueRecord("n" + kind, kind, 0, 0, VenueFix.Inside, VenueFlags.None, 0, "", kind.ToString());
                var root = new Node3D();
                root.AddChild(VenueSigns.Dress(d, v, Styles.StyleKit.Material(Styles.MaterialRole.Prop)));
                // the blind wall behind it
                root.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(20f, 9f, 0.2f) },
                    Position = new Vector3(0, 4.5f, -0.13f),
                });
                return root;
            });
    }
}
