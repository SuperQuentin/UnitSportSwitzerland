using Godot;

namespace UnitSport.Items;

/// <summary>
/// How a Polaroid print looks in 3D: the card mesh (textured front, white back), its materials,
/// the "developing" shader and the placed-photo factory for <see cref="PlacedObjects"/>.
/// </summary>
public static class PhotoVisuals
{
    private static ArrayMesh? _card, _held;
    private static ImageTexture? _blank;
    private static Shader? _develop3D, _develop2D;
    private static readonly Dictionary<string, StandardMaterial3D> Materials = new();
    private static StandardMaterial3D? _placeholder;

    /// <summary>
    /// The card, centred on the origin (offset by <paramref name="centre"/>), facing +Z, 1 mm thick.
    /// The front is UV-mapped to the whole print; the back and the edges point at a corner of the
    /// white frame, so one textured material draws all of it.
    /// </summary>
    public static ArrayMesh BuildCard(Vector3 centre)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float hw = PhotoStore.CardSize.X * 0.5f, hh = PhotoStore.CardSize.Y * 0.5f, t = 0.0005f;
        var white = new Vector2(0.01f, 0.99f);

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            // a b c d counter-clockwise seen from the side n points to; Godot's front faces are clockwise
            foreach (var (p, uv) in new[] { (a, ua), (c, uc), (b, ub), (a, ua), (d, ud), (c, uc) })
            {
                st.SetNormal(n);
                st.SetUV(uv);
                st.AddVertex(centre + p);
            }
        }

        // front (+Z): the print; image v runs top to bottom
        Quad(new(-hw, -hh, t), new(hw, -hh, t), new(hw, hh, t), new(-hw, hh, t), Vector3.Back,
            new(0, 1), new(1, 1), new(1, 0), new(0, 0));
        // back (-Z): white
        Quad(new(hw, -hh, -t), new(-hw, -hh, -t), new(-hw, hh, -t), new(hw, hh, -t), Vector3.Forward,
            white, white, white, white);
        // edges
        Quad(new(-hw, hh, t), new(hw, hh, t), new(hw, hh, -t), new(-hw, hh, -t), Vector3.Up, white, white, white, white);
        Quad(new(hw, -hh, t), new(-hw, -hh, t), new(-hw, -hh, -t), new(hw, -hh, -t), Vector3.Down, white, white, white, white);
        Quad(new(hw, hh, t), new(hw, -hh, t), new(hw, -hh, -t), new(hw, hh, -t), Vector3.Right, white, white, white, white);
        Quad(new(-hw, -hh, t), new(-hw, hh, t), new(-hw, hh, -t), new(-hw, -hh, -t), Vector3.Left, white, white, white, white);
        return st.Commit();
    }

    /// <summary>A stuck photo: centred on its transform, 1 mm proud of the surface.</summary>
    public static ArrayMesh Card => _card ??= BuildCard(new Vector3(0, 0, 0.0015f));

    /// <summary>The photo in the hand: its grip at the bottom edge, like the icon card items.</summary>
    public static ArrayMesh HeldCard => _held ??= BuildCard(new Vector3(0, 0.06f, 0.02f));

    /// <summary>An undeveloped print: the frame round a dark grey-blue square. Stands in until the image is here.</summary>
    public static ImageTexture Blank
    {
        get
        {
            if (_blank != null) return _blank;
            var img = Image.CreateEmpty(PhotoStore.CardW, PhotoStore.CardH, false, Image.Format.Rgb8);
            img.Fill(PhotoStore.Paper);
            img.FillRect(new Rect2I(PhotoStore.Border, PhotoStore.Border, PhotoStore.ImageSize, PhotoStore.ImageSize),
                new Color(0.17f, 0.19f, 0.25f));
            return _blank = ImageTexture.CreateFromImage(img);
        }
    }

    /// <summary>Lit, textured material of a print (cached per id); the blank card when the image is not here.</summary>
    public static StandardMaterial3D Material(string? id)
    {
        if (id != null && Materials.TryGetValue(id, out var m)) return m;
        var tex = PhotoStore.Texture(id);
        if (tex == null) return _placeholder ??= Make(Blank);
        return Materials[id!] = Make(tex);

        static StandardMaterial3D Make(Texture2D t) => new()
        {
            AlbedoTexture = t,
            Roughness = 0.55f,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        };
    }

    // ---- developing -----------------------------------------------------------------------------

    private const string Develop3DCode = @"
shader_type spatial;
uniform sampler2D photo : source_color, filter_linear_mipmap;
uniform float develop = 0.0;
uniform vec4 image_rect;
" + HeldItemVisual.ViewmodelVertex + @"
void fragment() {
    vec3 c = texture(photo, UV).rgb;
    bool inside = UV.x > image_rect.x && UV.x < image_rect.z && UV.y > image_rect.y && UV.y < image_rect.w;
    float d = smoothstep(0.0, 1.0, develop);
    vec3 dark = vec3(0.17, 0.19, 0.25);
    // a faint, cold image first, colour and contrast last
    vec3 early = mix(dark, vec3(dot(c, vec3(0.3, 0.5, 0.2))) * vec3(0.8, 0.9, 1.1), 0.5);
    ALBEDO = inside ? mix(mix(dark, early, clamp(d * 2.0, 0.0, 1.0)), c, clamp(d * 1.6 - 0.6, 0.0, 1.0)) : c;
    ROUGHNESS = 0.55;
}";

    private const string Develop2DCode = @"
shader_type canvas_item;
uniform float develop = 0.0;
uniform vec4 image_rect;
void fragment() {
    vec4 t = texture(TEXTURE, UV);
    vec3 c = t.rgb;
    bool inside = UV.x > image_rect.x && UV.x < image_rect.z && UV.y > image_rect.y && UV.y < image_rect.w;
    float d = smoothstep(0.0, 1.0, develop);
    vec3 dark = vec3(0.17, 0.19, 0.25);
    vec3 early = mix(dark, vec3(dot(c, vec3(0.3, 0.5, 0.2))) * vec3(0.8, 0.9, 1.1), 0.5);
    COLOR = vec4(inside ? mix(mix(dark, early, clamp(d * 2.0, 0.0, 1.0)), c, clamp(d * 1.6 - 0.6, 0.0, 1.0)) : c, t.a) * COLOR;
}";

    /// <summary>A material for the print as it comes out of the viewmodel (drawn over the world like it): set its <c>develop</c> parameter 0..1.</summary>
    public static ShaderMaterial Developing3D(Texture2D photo)
    {
        var m = new ShaderMaterial { Shader = _develop3D ??= new Shader { Code = Develop3DCode } };
        m.SetShaderParameter("photo", photo);
        m.SetShaderParameter("image_rect", PhotoStore.ImageRect);
        m.SetShaderParameter("develop", 0f);
        return m;
    }

    /// <summary>The same for a TextureRect on screen.</summary>
    public static ShaderMaterial Developing2D()
    {
        var m = new ShaderMaterial { Shader = _develop2D ??= new Shader { Code = Develop2DCode } };
        m.SetShaderParameter("image_rect", PhotoStore.ImageRect);
        m.SetShaderParameter("develop", 0f);
        return m;
    }

    // ---- stuck on things ------------------------------------------------------------------------

    /// <summary>
    /// The <see cref="PlacedKind.Photo"/> factory: the card with the print once this machine has it.
    /// Until then the blank card, and the print is asked of the server (<see cref="PhotoTransfer"/>);
    /// it is swapped in when it arrives.
    /// </summary>
    public static Node3D Placed(PlacedObject o)
    {
        var body = new StaticBody3D();
        var mesh = new MeshInstance3D { Name = "Card", Mesh = Card, MaterialOverride = Material(o.Payload) };
        body.AddChild(mesh);
        body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(PhotoStore.CardSize.X, PhotoStore.CardSize.Y, 0.01f) },
            Position = new Vector3(0, 0, 0.004f),
        });

        string id = o.Payload;
        if (PhotoStore.IsValidId(id) && !PhotoStore.Has(id))
        {
            void OnArrived(string got)
            {
                if (got != id || !GodotObject.IsInstanceValid(mesh)) return;
                mesh.MaterialOverride = Material(id);
            }
            PhotoTransfer.Arrived += OnArrived;
            body.TreeExiting += () => PhotoTransfer.Arrived -= OnArrived;
            PhotoTransfer.Instance?.Ensure(id);
        }
        return body;
    }

    /// <summary>
    /// Where a photo would go on the surface a ray hit: flush on a wall, top edge up; laid flat on
    /// the ground or a table, top edge away from the viewer. 1 mm off the surface is in the mesh.
    /// </summary>
    public static Transform3D StickTransform(Vector3 point, Vector3 normal, Vector3 look)
    {
        var z = normal.Normalized();
        var up = Mathf.Abs(z.Y) > 0.7f ? look : Vector3.Up;
        var y = (up - z * up.Dot(z));
        if (y.LengthSquared() < 1e-6f) y = Vector3.Forward - z * Vector3.Forward.Dot(z);
        y = y.Normalized();
        var x = y.Cross(z);
        return new Transform3D(new Basis(x, y, z), point);
    }
}
