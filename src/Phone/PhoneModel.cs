using Godot;
using UnitSport.Avatar;

namespace UnitSport.Phone;

/// <summary>
/// The foldable phone (#889) as drawn: a book fold with its hinge down the holder's left edge.
/// Folded, the narrow cover screen faces the holder; opened, the front half has swung across to
/// the left and the inner screen, square, spans both halves. Docs: <c>docs/notes/ui/phone.md</c>.
///
/// <para>
/// The node's frame: +Y up the phone, +Z out of the screens toward the holder, origin at the centre
/// of the back half (the one in the right hand). The front half turns about the hinge, so the open
/// inner screen's centre is <see cref="InnerScreenCentre"/>, left of the origin.
/// </para>
/// </summary>
public partial class PhoneModel : Node3D
{
    /// <summary>One half's size: the folded phone, 7.5 by 15 cm, 6 mm thick a half.</summary>
    public const float HalfWidth = 0.075f, Height = 0.15f, HalfThickness = 0.006f;
    /// <summary>The inner screen is square: <see cref="InnerSize"/> a side, over both halves.</summary>
    public const float InnerSize = 0.142f;
    /// <summary>The cover screen on the front half's outside, tall and narrow.</summary>
    public static readonly Vector2 CoverSize = new(0.066f, 0.140f);
    /// <summary>How long the hinge takes to open or close all the way.</summary>
    public const float UnfoldSeconds = 0.42f;

    /// <summary>The middle of the open inner screen, in this node's frame: on the hinge line.</summary>
    public static readonly Vector3 InnerScreenCentre = new(-HalfWidth * 0.5f, 0f, 0.0004f);
    /// <summary>The middle of the cover screen while folded, in this node's frame.</summary>
    public static readonly Vector3 CoverScreenCentre = new(0f, 0f, HalfThickness + 0.0004f);

    private static readonly Color Shell = new(0.16f, 0.17f, 0.20f), Frame = new(0.32f, 0.33f, 0.36f),
        Lens = new(0.05f, 0.06f, 0.09f);
    private static ArrayMesh? _backMesh, _frontMesh;

    private Node3D _hinge = null!;
    private MeshInstance3D _cover = null!, _innerLeft = null!, _innerRight = null!;
    private StandardMaterial3D _coverMat = null!, _innerLeftMat = null!, _innerRightMat = null!;
    private float _fold = -1f;

    /// <summary>0 folded, 1 open flat; eased in between (smoothstep), so a linear ramp opens like a hinge with a detent.</summary>
    public float Fold
    {
        get => Mathf.Max(0f, _fold);
        set
        {
            float f = Mathf.Clamp(value, 0f, 1f);
            if (f == _fold) return;
            _fold = f;
            if (_hinge != null) Turn();
        }
    }

    /// <summary>True once live screens are set (the owner's); a remote copy shows the wallpaper.</summary>
    public bool HasLiveScreens { get; private set; }

    public override void _Ready()
    {
        Name = "Phone";
        var back = new MeshInstance3D { Name = "Back", Mesh = _backMesh ??= BackMesh(), MaterialOverride = HumanMeshBuilder.Material() };
        AddChild(back);
        _innerRightMat = ScreenMaterial(new Vector2(0.5f, 1f), new Vector2(0.5f, 0f));
        _innerRight = Screen("InnerRight", new Vector2(InnerSize * 0.5f, InnerSize), _innerRightMat);
        _innerRight.Position = new Vector3(-HalfWidth * 0.5f + InnerSize * 0.25f, 0f, 0.0004f);
        AddChild(_innerRight);

        // the front half hangs off the hinge line (the left edge, at the halves' meeting plane)
        _hinge = new Node3D { Name = "Hinge", Position = new Vector3(-HalfWidth * 0.5f, 0f, 0f) };
        AddChild(_hinge);
        var front = new MeshInstance3D
        {
            Name = "Front", Mesh = _frontMesh ??= FrontMesh(), MaterialOverride = HumanMeshBuilder.Material(),
            Position = new Vector3(HalfWidth * 0.5f, 0f, 0f),
        };
        _hinge.AddChild(front);
        _coverMat = ScreenMaterial(Vector2.One, Vector2.Zero);
        _cover = Screen("Cover", CoverSize, _coverMat);
        _cover.Position = new Vector3(HalfWidth * 0.5f, 0f, HalfThickness + 0.0004f);
        _hinge.AddChild(_cover);
        // the inner face looks back at the other half while folded: turned half round, so once the
        // hinge has turned it the other half round it reads the right way
        _innerLeftMat = ScreenMaterial(new Vector2(0.5f, 1f), Vector2.Zero);
        _innerLeft = Screen("InnerLeft", new Vector2(InnerSize * 0.5f, InnerSize), _innerLeftMat);
        _innerLeft.Position = new Vector3(InnerSize * 0.25f, 0f, -0.0004f);
        _innerLeft.Rotation = new Vector3(0f, Mathf.Pi, 0f);
        _hinge.AddChild(_innerLeft);

        SetScreens(Wallpaper.Cover, Wallpaper.Inner);
        HasLiveScreens = false;
        if (_fold < 0f) _fold = 0f;
        Turn();
    }

    /// <summary>Shows <paramref name="cover"/> and <paramref name="inner"/> (square) on the screens.</summary>
    public void SetScreens(Texture2D cover, Texture2D inner)
    {
        _coverMat.AlbedoTexture = cover;
        _innerLeftMat.AlbedoTexture = inner;
        _innerRightMat.AlbedoTexture = inner;
        HasLiveScreens = true;
    }

    private void Turn()
    {
        float f = Mathf.SmoothStep(0f, 1f, _fold);
        // swinging toward the holder: about +Y by a negative angle takes the free edge out of +Z first
        _hinge.Basis = new Basis(Vector3.Up, -Mathf.Pi * f);
        // the cover looks away once the phone is open; the inner screen is hidden while shut
        _innerLeft.Visible = _innerRight.Visible = f > 0.02f;
        _cover.Visible = f < 0.6f;
    }

    private static MeshInstance3D Screen(string name, Vector2 size, StandardMaterial3D material) => new()
    {
        Name = name,
        Mesh = new QuadMesh { Size = size },
        MaterialOverride = material,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };

    /// <summary>A lit screen: unshaded, no fog, its texture's part <paramref name="scale"/> from <paramref name="offset"/>.</summary>
    private static StandardMaterial3D ScreenMaterial(Vector2 scale, Vector2 offset) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        DisableFog = true,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        Uv1Scale = new Vector3(scale.X, scale.Y, 1f),
        Uv1Offset = new Vector3(offset.X, offset.Y, 0f),
    };

    // MeshScratch builds facing -Z by a half turn about Y: author at (-x, y, -z) to get (x, y, z)
    private static void Box(MeshScratch s, Vector3 centre, Vector3 size, Color colour) =>
        s.Box(new Vector3(-centre.X, centre.Y, -centre.Z), size, colour);

    /// <summary>
    /// The back half: one satin shell (its face under the inner screen), the camera island on the
    /// back. No frame or glass laid over the shell: millimetres apart, they would fight it (#788).
    /// </summary>
    [Core.Showcase("Parts", "Phone back half")]
    private static ArrayMesh BackMesh()
    {
        var s = new MeshScratch();
        float t = HalfThickness;
        Box(s, new Vector3(0f, 0f, -t * 0.5f), new Vector3(HalfWidth, Height, t), Shell);
        // the camera island at the top of the back, three lenses standing proud of it
        Box(s, new Vector3(0.008f, 0.052f, -t - 0.0015f), new Vector3(0.05f, 0.032f, 0.003f), Frame);
        foreach (float x in new[] { -0.008f, 0.006f, 0.020f })
            Box(s, new Vector3(x, 0.052f, -t - 0.0035f), new Vector3(0.011f, 0.011f, 0.001f), Lens);
        return s.Build();
    }

    /// <summary>The front half, in its own frame (centred, its inner face at z = 0): one shell; the screens are quads.</summary>
    [Core.Showcase("Parts", "Phone front half")]
    private static ArrayMesh FrontMesh()
    {
        var s = new MeshScratch();
        float t = HalfThickness;
        Box(s, new Vector3(0f, 0f, t * 0.5f), new Vector3(HalfWidth, Height, t), Shell);
        return s.Build();
    }

    /// <summary>The model viewer (#889): folded, half open, open.</summary>
    [Core.Showcase("Items", "Phone")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> Showcase()
    {
        foreach (var (name, fold) in new[] { ("folded", 0f), ("half open", 0.5f), ("open", 1f) })
            yield return (name, () => new PhoneModel { Fold = fold });
    }
}

/// <summary>What a screen shows when it is not the owner's live one: a gradient, drawn once.</summary>
public static class Wallpaper
{
    private static ImageTexture? _cover, _inner;

    public static readonly Color Top = new(0.16f, 0.32f, 0.62f), Bottom = new(0.62f, 0.18f, 0.36f);

    public static ImageTexture Cover => _cover ??= Gradient(16, 32);
    public static ImageTexture Inner => _inner ??= Gradient(32, 32);

    private static ImageTexture Gradient(int w, int h)
    {
        var image = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                image.SetPixel(x, y, Top.Lerp(Bottom, (y + x * 0.3f) / (h + w * 0.3f)));
        return ImageTexture.CreateFromImage(image);
    }
}
