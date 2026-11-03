using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The player's profile picture (#1209): their own figure's face, rendered in a small world of its own and cut to a circle
/// with a ring in the menu skin's accent. <see cref="Portrait"/> false shows a drawn silhouette instead (signed out). The
/// face is rendered once and again only when the figure changes (<see cref="GameSettings.Changed"/>), never per frame.
/// </summary>
public partial class AvatarBadge : Control
{
    private static Shader? _mask;
    private readonly int _size;
    private SubViewport? _viewport;
    private MeshInstance3D? _figure;
    private TextureRect? _face;
    private bool _portrait = true;

    public AvatarBadge(int size)
    {
        _size = size;
        CustomMinimumSize = new Vector2(size, size);
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public AvatarBadge() : this(48) { }

    /// <summary>The face (true) or the silhouette of someone not signed in (false).</summary>
    public bool Portrait
    {
        get => _portrait;
        set
        {
            if (_portrait == value) return;
            _portrait = value;
            if (_face != null) _face.Visible = value;
            QueueRedraw();
        }
    }

    public override void _Ready()
    {
        // twice the drawn size, so the face stays crisp under the mask
        int px = _size * 2;
        _viewport = new SubViewport
        {
            Size = new Vector2I(px, px), OwnWorld3D = true, TransparentBg = true,
            Msaa3D = Viewport.Msaa.Msaa4X, RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
        };
        _viewport.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.ClearColor,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.62f, 0.64f, 0.7f),
                AmbientLightEnergy = 0.95f,
            },
        });
        _viewport.AddChild(new DirectionalLight3D { Rotation = new Vector3(Mathf.DegToRad(-25), Mathf.DegToRad(-35), 0), LightEnergy = 1.15f });
        // head and shoulders, a little from below the eyes
        var camera = new Camera3D { Position = new Vector3(0, 1.69f, 0.9f), Fov = 26, Current = true };
        _viewport.AddChild(camera);
        camera.RotationDegrees = new Vector3(-3, 0, 0);
        _figure = new MeshInstance3D { MaterialOverride = HumanMeshBuilder.FigureMaterial(), Rotation = new Vector3(0, Mathf.Pi, 0) };
        _viewport.AddChild(_figure);
        AddChild(_viewport);

        _face = new TextureRect
        {
            Texture = _viewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            Material = Mask(),
            Visible = _portrait,
        };
        _face.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_face);
        Refresh();
        GameSettings.Changed += Refresh;
    }

    public override void _ExitTree() => GameSettings.Changed -= Refresh;

    /// <summary>Renders the face again from the player's figure (the Character menu's choice).</summary>
    public void Refresh()
    {
        if (_figure == null || _viewport == null || !IsInstanceValid(_figure)) return;
        _figure.Mesh = HumanMeshBuilder.Build(HumanPalette.ForRider(1).With(CharacterPage.Mine(this)));
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        QueueRedraw();
    }

    private ShaderMaterial Mask()
    {
        _mask ??= new Shader
        {
            Code = """
                shader_type canvas_item;
                uniform vec4 ring : source_color;
                uniform vec4 ground : source_color;
                void fragment() {
                    float r = length(UV - 0.5) * 2.0;
                    vec4 face = texture(TEXTURE, UV);
                    vec4 c = vec4(mix(ground.rgb, face.rgb, face.a), 1.0);
                    c = mix(c, ring, smoothstep(0.86, 0.9, r));
                    c.a = 1.0 - smoothstep(0.96, 1.0, r);
                    COLOR = c;
                }
                """,
        };
        var m = new ShaderMaterial { Shader = _mask };
        m.SetShaderParameter("ring", Skins.MenuSkin.Role("accent"));
        m.SetShaderParameter("ground", Skins.MenuSkin.Role("control"));
        return m;
    }

    /// <summary>The silhouette: a disc, a head and shoulders, in the skin's dim ink.</summary>
    public override void _Draw()
    {
        if (_portrait) return;
        float r = _size / 2f;
        var c = new Vector2(r, r);
        DrawCircle(c, r, Skins.MenuSkin.Role("control"));
        var ink = Skins.MenuSkin.Role("inkDim");
        DrawCircle(c + new Vector2(0, -r * 0.2f), r * 0.3f, ink);
        // the shoulders: the top of an ellipse below the head, kept inside the disc
        var shoulders = new Vector2[25];
        var centre = c + new Vector2(0, r * 0.85f);
        for (int i = 0; i < shoulders.Length; i++)
        {
            float a = Mathf.Pi + Mathf.Pi * i / (shoulders.Length - 1);
            var p = centre + new Vector2(Mathf.Cos(a) * r * 0.62f, Mathf.Sin(a) * r * 0.55f);
            var d = p - c;
            shoulders[i] = d.Length() > r - 2 ? c + d.Normalized() * (r - 2) : p;
        }
        DrawColoredPolygon(shoulders, ink);
        DrawArc(c, r - 1.5f, 0, Mathf.Tau, 48, Skins.MenuSkin.Role("inkFaint"), 2f, true);
    }
}
