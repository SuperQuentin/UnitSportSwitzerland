using Godot;
using UnitSport.Avatar;

namespace UnitSport.Phone;

/// <summary>
/// The foldable phone (#889) as drawn, after the iPhone Duo's lines: a book fold with its hinge down
/// the holder's left edge, rounded corners, a silver rim, a white back with a pill-shaped camera
/// island (two lenses, a flash). Folded it is a squat portrait slab whose outside is the cover
/// screen; opened, the front half has swung across to the left and the inner screen, wider than tall
/// (about 1.4 : 1), spans both halves. Docs: <c>docs/notes/ui/phone.md</c>.
///
/// <para>
/// The node's frame: +Y up the phone, +Z out of the screens toward the holder, origin at the centre
/// of the back half (the one in the right hand). The front half turns about the hinge, so the open
/// inner screen's centre is <see cref="InnerScreenCentre"/>, on the hinge line. Fully open, the inner
/// screen is one surface (<c>InnerFull</c>): no seam down the fold; the two halves' parts show only
/// while it moves.
/// </para>
/// </summary>
public partial class PhoneModel : Node3D
{
    /// <summary>One half: 8.4 by 12.2 cm, 5.5 mm thick; folded the phone is 11 mm.</summary>
    public const float HalfWidth = 0.084f, Height = 0.122f, HalfThickness = 0.0055f;
    /// <summary>The corner radius of the outer corners; the hinge's are square, so open there is no notch at the fold.</summary>
    private const float Corner = 0.012f, HingeCorner = 0f;
    /// <summary>The glass's border round the screens.</summary>
    private const float Bezel = 0.003f;
    /// <summary>The open inner screen, across both halves: 16.2 by 11.6 cm.</summary>
    public const float InnerW = 2f * HalfWidth - 2f * Bezel, InnerH = Height - 2f * Bezel;
    /// <summary>The cover screen on the front half's outside.</summary>
    public static readonly Vector2 CoverSize = new(HalfWidth - 2f * 0.0025f, Height - 2f * 0.0025f);
    /// <summary>The whole open phone, rim to rim: what the camera keeps in view.</summary>
    public static readonly Vector2 OpenSize = new(2f * HalfWidth, Height);
    /// <summary>How long the hinge takes to open or close all the way.</summary>
    public const float UnfoldSeconds = 0.42f;
    private const float ScreenLift = 0.0003f;

    private const float IslandX = HalfWidth * 0.5f - 0.005f - 0.029f, IslandY = Height * 0.5f - 0.006f - 0.011f;

    /// <summary>The middle of the open inner screen, in this node's frame: on the hinge line.</summary>
    public static readonly Vector3 InnerScreenCentre = new(-HalfWidth * 0.5f, 0f, ScreenLift);
    /// <summary>The middle of the cover screen while folded, in this node's frame.</summary>
    public static readonly Vector3 CoverScreenCentre = new(0f, 0f, HalfThickness + ScreenLift);
    /// <summary>The back camera's main lens, in this node's frame: it looks out along −Z.</summary>
    public static readonly Vector3 LensPoint = new(IslandX + 0.015f, IslandY, -HalfThickness - 0.0045f);

    private static readonly Color Rim = new(0.80f, 0.80f, 0.82f), BackWhite = new(0.93f, 0.93f, 0.91f),
        IslandWhite = new(0.86f, 0.86f, 0.85f), Black = new(0.03f, 0.03f, 0.035f), LensRing = new(0.42f, 0.43f, 0.46f),
        LensGlass = new(0.04f, 0.05f, 0.08f), Flash = new(0.95f, 0.88f, 0.70f);
    private static ArrayMesh? _backMesh, _frontMesh, _coverMesh, _innerLeftMesh, _innerRightMesh, _innerFullMesh;

    private Node3D _hinge = null!;
    private MeshInstance3D _cover = null!, _innerLeft = null!, _innerRight = null!, _innerFull = null!;
    private StandardMaterial3D _coverMat = null!, _innerMat = null!;
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
        var body = HumanMeshBuilder.Material();
        AddChild(new MeshInstance3D { Name = "Back", Mesh = _backMesh ??= BackMesh(), MaterialOverride = body });
        _innerMat = ScreenMaterial();
        _innerRight = Screen("InnerRight", _innerRightMesh ??= InnerRightMesh(), _innerMat);
        AddChild(_innerRight);
        _innerFull = Screen("InnerFull", _innerFullMesh ??= InnerFullMesh(), _innerMat);
        AddChild(_innerFull);

        // the front half hangs off the hinge line (the left edge, at the halves' meeting plane)
        _hinge = new Node3D { Name = "Hinge", Position = new Vector3(-HalfWidth * 0.5f, 0f, 0f) };
        AddChild(_hinge);
        _hinge.AddChild(new MeshInstance3D { Name = "Front", Mesh = _frontMesh ??= FrontMesh(), MaterialOverride = body });
        _coverMat = ScreenMaterial();
        _cover = Screen("Cover", _coverMesh ??= CoverMesh(), _coverMat);
        _hinge.AddChild(_cover);
        // built as it lies open; turned half round here, so once the hinge has turned it the other
        // half round it lies where it was built
        _innerLeft = Screen("InnerLeft", _innerLeftMesh ??= InnerLeftMesh(), _innerMat);
        _innerLeft.Rotation = new Vector3(0f, Mathf.Pi, 0f);
        _hinge.AddChild(_innerLeft);

        SetScreens(Wallpaper.Cover, Wallpaper.Inner);
        HasLiveScreens = false;
        if (_fold < 0f) _fold = 0f;
        Turn();
    }

    /// <summary>Shows <paramref name="cover"/> and <paramref name="inner"/> on the screens.</summary>
    public void SetScreens(Texture2D cover, Texture2D inner)
    {
        _coverMat.AlbedoTexture = cover;
        _innerMat.AlbedoTexture = inner;
        HasLiveScreens = true;
    }

    private void Turn()
    {
        float f = Mathf.SmoothStep(0f, 1f, _fold);
        // swinging toward the holder: about +Y by a negative angle takes the free edge out of +Z first
        _hinge.Basis = new Basis(Vector3.Up, -Mathf.Pi * f);
        // open flat, one screen with no seam; moving, the two halves of it
        bool flat = _fold >= 0.999f;
        _innerFull.Visible = flat;
        _innerLeft.Visible = _innerRight.Visible = !flat && f > 0.02f;
        _cover.Visible = f < 0.6f;
    }

    private static MeshInstance3D Screen(string name, ArrayMesh mesh, StandardMaterial3D material) => new()
    {
        Name = name, Mesh = mesh, MaterialOverride = material,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };

    /// <summary>A lit screen: unshaded, no fog.</summary>
    private static StandardMaterial3D ScreenMaterial() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        DisableFog = true,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
    };

    // ------------------------------------------------------------------ meshes

    /// <summary>The back half: rounded slab (black inside, white back, silver rim) and the camera island.</summary>
    [Core.Showcase("Parts", "Phone back half")]
    private static ArrayMesh BackMesh()
    {
        var m = new Builder();
        float w = HalfWidth, t = HalfThickness;
        m.Slab(RoundRect(w, Height, new Vector4(HingeCorner, Corner, Corner, HingeCorner), Vector2.Zero), -t, 0f, Black, BackWhite, Rim);
        // the pill-shaped island at the top outer corner, two lenses toward the edge, the flash inboard
        m.Slab(RoundRect(0.058f, 0.022f, Vector4.One * 0.011f, new Vector2(IslandX, IslandY)), -t - 0.0018f, -t + 0.0002f, IslandWhite, IslandWhite, IslandWhite);
        foreach (float dx in new[] { 0.015f, -0.006f })
        {
            var at = new Vector2(IslandX + dx, IslandY);
            m.Slab(RoundRect(0.0165f, 0.0165f, Vector4.One * 0.00825f, at), -t - 0.0032f, -t - 0.0016f, LensRing, LensRing, LensRing);
            m.Slab(RoundRect(0.0115f, 0.0115f, Vector4.One * 0.00575f, at), -t - 0.0042f, -t - 0.003f, LensGlass, LensGlass, LensRing);
        }
        m.Slab(RoundRect(0.005f, 0.005f, Vector4.One * 0.0025f, new Vector2(IslandX - 0.021f, IslandY)), -t - 0.0024f, -t - 0.0016f, Flash, Flash, IslandWhite);
        return m.Commit();
    }

    /// <summary>The front half, in the hinge's frame (its hinge edge at x = 0, its inner face at z = 0).</summary>
    [Core.Showcase("Parts", "Phone front half")]
    private static ArrayMesh FrontMesh()
    {
        var m = new Builder();
        m.Slab(RoundRect(HalfWidth, Height, new Vector4(HingeCorner, Corner, Corner, HingeCorner), new Vector2(HalfWidth * 0.5f, 0f)),
            0f, HalfThickness, Black, Black, Rim);
        return m.Commit();
    }

    /// <summary>The cover screen, on the front half's outside (hinge frame).</summary>
    private static ArrayMesh CoverMesh()
    {
        var m = new Builder();
        var at = new Vector2(HalfWidth * 0.5f, 0f);
        m.Screen(RoundRect(CoverSize.X, CoverSize.Y, Vector4.One * (Corner - 0.0025f), at), HalfThickness + ScreenLift,
            new Rect2(at - CoverSize * 0.5f, CoverSize));
        return m.Commit();
    }

    /// <summary>The open inner screen in one piece, its middle on the hinge line (node frame).</summary>
    private static ArrayMesh InnerFullMesh()
    {
        var m = new Builder();
        m.Screen(RoundRect(InnerW, InnerH, Vector4.One * (Corner - Bezel), new Vector2(InnerScreenCentre.X, 0f)), ScreenLift, InnerRect(0f));
        return m.Commit();
    }

    /// <summary>The right part of the inner screen, on the back half (node frame), square at the fold.</summary>
    private static ArrayMesh InnerRightMesh()
    {
        var m = new Builder();
        float w = InnerW * 0.5f;
        m.Screen(RoundRect(w, InnerH, new Vector4(0f, Corner - Bezel, Corner - Bezel, 0f), new Vector2(InnerScreenCentre.X + w * 0.5f, 0f)),
            ScreenLift, InnerRect(0f));
        return m.Commit();
    }

    /// <summary>The left part, as it lies open, in the hinge's frame (x &lt; 0); its node turns it in.</summary>
    private static ArrayMesh InnerLeftMesh()
    {
        var m = new Builder();
        float w = InnerW * 0.5f;
        // the hinge frame's origin is the inner screen's centre line
        m.Screen(RoundRect(w, InnerH, new Vector4(Corner - Bezel, 0f, 0f, Corner - Bezel), new Vector2(-w * 0.5f, 0f)), ScreenLift,
            InnerRect(-InnerScreenCentre.X));
        return m.Commit();
    }

    /// <summary>Where the inner screen's picture lies, shifted by <paramref name="dx"/> for a frame whose origin is elsewhere.</summary>
    private static Rect2 InnerRect(float dx) => new(InnerScreenCentre.X + dx - InnerW * 0.5f, -InnerH * 0.5f, InnerW, InnerH);

    /// <summary>
    /// A rounded rectangle's outline, counter-clockwise seen from +Z, centred on <paramref name="at"/>.
    /// Radii: x top-left, y top-right, z bottom-right, w bottom-left (0 = a square corner).
    /// </summary>
    private static Vector2[] RoundRect(float w, float h, Vector4 radii, Vector2 at)
    {
        const int Steps = 6;
        var points = new List<Vector2>();
        float x0 = at.X - w * 0.5f, x1 = at.X + w * 0.5f, y0 = at.Y - h * 0.5f, y1 = at.Y + h * 0.5f;
        void Arc(float r, Vector2 corner, Vector2 inward, float from)
        {
            if (r <= 0f) { points.Add(corner); return; }
            var centre = corner + inward * r;
            for (int i = 0; i <= Steps; i++)
            {
                float a = from + Mathf.Pi * 0.5f * i / Steps;
                points.Add(centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
            }
        }
        Arc(radii.Z, new Vector2(x1, y0), new Vector2(-1, 1), -Mathf.Pi * 0.5f);   // bottom right
        Arc(radii.Y, new Vector2(x1, y1), new Vector2(-1, -1), 0f);                 // top right
        Arc(radii.X, new Vector2(x0, y1), new Vector2(1, -1), Mathf.Pi * 0.5f);     // top left
        Arc(radii.W, new Vector2(x0, y0), new Vector2(1, 1), Mathf.Pi);             // bottom left
        return points.ToArray();
    }

    /// <summary>Flat-capped, smooth-sided extrusions and flat screens, wound so their outsides face out.</summary>
    private sealed class Builder
    {
        private readonly SurfaceTool _st = new();

        public Builder() => _st.Begin(Mesh.PrimitiveType.Triangles);

        /// <summary>
        /// The outline extruded from <paramref name="z0"/> to <paramref name="z1"/>: the +Z cap in
        /// <paramref name="front"/>, the −Z cap in <paramref name="back"/>, the sides in <paramref name="side"/>.
        /// </summary>
        public void Slab(Vector2[] outline, float z0, float z1, Color front, Color back, Color side)
        {
            var mid = Centre(outline);
            int n = outline.Length;
            for (int i = 0; i < n; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % n];
                Tri(V(mid, z1), V(a, z1), V(b, z1), Vector3.Back, front);
                Tri(V(mid, z0), V(b, z0), V(a, z0), Vector3.Forward, back);
                // the sides smooth round the corners: each point's normal halves its two edges'
                Quad(V(a, z0), V(b, z0), V(b, z1), V(a, z1), Normal(outline, i), Normal(outline, (i + 1) % n), side);
            }
        }

        /// <summary>A flat screen at depth <paramref name="z"/>, facing +Z, its UVs from where it lies in <paramref name="uv"/>.</summary>
        public void Screen(Vector2[] outline, float z, Rect2 uv)
        {
            var mid = Centre(outline);
            int n = outline.Length;
            Vector2 Uv(Vector2 p) => new((p.X - uv.Position.X) / uv.Size.X, 1f - (p.Y - uv.Position.Y) / uv.Size.Y);
            void Put(Vector2 p)
            {
                _st.SetNormal(Vector3.Back);
                _st.SetColor(Colors.White);
                _st.SetUV(Uv(p));
                _st.AddVertex(V(p, z));
            }
            for (int i = 0; i < n; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % n];
                if ((a - b).LengthSquared() < 1e-12f) continue;
                // the outline runs counter-clockwise seen from +Z; Godot's front face is clockwise
                Put(mid); Put(b); Put(a);
            }
        }

        public ArrayMesh Commit()
        {
            Core.ShowcaseTrace.Mark();
            return _st.Commit();
        }

        private static Vector2 Centre(Vector2[] outline)
        {
            var mid = Vector2.Zero;
            foreach (var p in outline) mid += p;
            return mid / outline.Length;
        }

        private static Vector3 V(Vector2 p, float z) => new(p.X, p.Y, z);

        private static Vector3 Normal(Vector2[] outline, int i)
        {
            int n = outline.Length;
            var p = outline[i];
            static Vector2 Out(Vector2 a, Vector2 b)
            {
                var d = b - a;
                return d.LengthSquared() < 1e-12f ? Vector2.Zero : new Vector2(d.Y, -d.X).Normalized();
            }
            var sum = Out(outline[(i + n - 1) % n], p) + Out(p, outline[(i + 1) % n]);
            return sum.LengthSquared() < 1e-12f ? Vector3.Right : new Vector3(sum.X, sum.Y, 0f).Normalized();
        }

        private void Put(Vector3 p, Vector3 normal, Color colour)
        {
            _st.SetNormal(normal);
            _st.SetColor(colour);
            _st.SetUV(Vector2.Zero);
            _st.AddVertex(p);
        }

        private void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Color colour)
        {
            if ((b - a).Cross(c - a).LengthSquared() < 1e-14f) return;
            // Godot draws clockwise faces: the outside is minus (b-a)x(c-a)
            if ((b - a).Cross(c - a).Dot(normal) > 0f) (b, c) = (c, b);
            Put(a, normal, colour); Put(b, normal, colour); Put(c, normal, colour);
        }

        private void Quad(Vector3 a0, Vector3 b0, Vector3 b1, Vector3 a1, Vector3 na, Vector3 nb, Color colour)
        {
            if ((b0 - a0).LengthSquared() < 1e-12f) return;
            var face = (na + nb).Normalized();
            foreach (var (p, q, r, np, nq, nr) in new[] { (a0, b0, b1, na, nb, nb), (a0, b1, a1, na, nb, na) })
            {
                if ((q - p).Cross(r - p).Dot(face) > 0f) { Put(p, np, colour); Put(r, nr, colour); Put(q, nq, colour); }
                else { Put(p, np, colour); Put(q, nq, colour); Put(r, nr, colour); }
            }
        }
    }

    /// <summary>The model viewer (#889): folded, half open, open, and the back with its camera island.</summary>
    [Core.Showcase("Items", "Phone")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> Showcase()
    {
        foreach (var (name, fold) in new[] { ("folded", 0f), ("half open", 0.5f), ("open", 1f) })
            yield return (name, () => new PhoneModel { Fold = fold });
        yield return ("back", () =>
        {
            var holder = new Node3D { Name = "PhoneBack" };
            holder.AddChild(new PhoneModel { Fold = 0f, Rotation = new Vector3(0f, Mathf.Pi, 0f) });
            return holder;
        });
    }
}

/// <summary>What a screen shows when it is not the owner's live one: a gradient, drawn once.</summary>
public static class Wallpaper
{
    private static ImageTexture? _cover, _inner;

    /// <summary>A dusk over the Alps: cold blue above, a warm glow down low.</summary>
    public static readonly Color Top = new(0.36f, 0.52f, 0.72f), Bottom = new(0.86f, 0.72f, 0.58f);

    public static ImageTexture Cover => _cover ??= Gradient(16, 24);
    public static ImageTexture Inner => _inner ??= Gradient(32, 24);

    private static ImageTexture Gradient(int w, int h)
    {
        var image = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                image.SetPixel(x, y, Top.Lerp(Bottom, Mathf.Pow(y / (float)(h - 1), 1.4f)));
        return ImageTexture.CreateFromImage(image);
    }
}
