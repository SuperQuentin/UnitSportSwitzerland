using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Adventure.Alcohol;

/// <summary>
/// The alcohol plan's screen furniture (#1271): the breathalyzer's reading (a small device screen low in the view for a few
/// seconds, or the tube's colour), a faint glass icon by the stamina ring while above 0.3 per mille (never a number: you
/// measure it), and the edge blur over the 3D view while drunk. All of it is built once and changes only on a result or when
/// the level's tier changes; nothing per frame but a float compare.
/// </summary>
public sealed partial class AlcoholHud : CanvasLayer
{
    /// <summary>The prompt over a player when a breathalyzer is in hand (E, pad Y, a VR grip offer).</summary>
    public const string OfferPrompt = "Offer a breath test";
    /// <summary>The reading stays this long, s.</summary>
    public const double ShowSeconds = 8;

    private ColorRect _blur = null!;
    private ShaderMaterial _blurMat = null!;
    private Control _icon = null!;
    private PanelContainer _panel = null!;
    private Label _title = null!, _main = null!, _sub = null!;
    private ColorRect _tube = null!;
    private AlcoholService? _service;
    private float _blurShown = -1f;
    private double _hideAt;

    private static readonly StringName Strength = "strength";

    /// <summary>The reading's main line while it shows ("" when hidden): the checks read it.</summary>
    public string Shown => _panel.Visible ? _main.Text : "";
    /// <summary>The second line (the limit, the tube's words).</summary>
    public string ShownSub => _panel.Visible ? _sub.Text : "";
    /// <summary>The tube's colour while a tube reading shows.</summary>
    public Color TubeShown => _tube.Visible ? _tube.Color : default;
    public bool IconShown => _icon.Visible;
    public float BlurStrength => _blurShown;

    public override void _Ready()
    {
        Name = "AlcoholHud";
        Layer = 8;

        // the edge blur: under every other layer's widgets, over the 3D view
        _blurMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/alcohol_edge.gdshader") };
        _blur = new ColorRect { Name = "EdgeBlur", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, Material = _blurMat, Color = Colors.White };
        _blur.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_blur);

        // the glass icon, left of where the stamina ring sits (the together chip is on its right)
        _icon = new GlassIcon { Name = "TipsyIcon", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(22, 26), TooltipText = "You have been drinking" };
        _icon.SetAnchorsPreset(Control.LayoutPreset.Center);
        _icon.Position = new Vector2(-118f, 52f);
        AddChild(_icon);

        // the device screen
        _panel = new PanelContainer { Name = "BreathScreen", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, Theme = UiTheme.Get() };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.9f, 10, 14));
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 4);
        _title = Line(UiTheme.FontSmall, UiTheme.TextDim);
        _main = Line(UiTheme.FontHeading, UiTheme.Good);
        _sub = Line(UiTheme.FontBody, UiTheme.Text);
        _tube = new ColorRect { Name = "Tube", Visible = false, CustomMinimumSize = new Vector2(14, 56), SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_title);
        box.AddChild(_tube);
        box.AddChild(_main);
        box.AddChild(_sub);
        _panel.AddChild(box);
        _panel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.GrowVertical = Control.GrowDirection.Begin;
        _panel.Position = new Vector2(-150f, -250f);
        _panel.CustomMinimumSize = new Vector2(300f, 0f);
        AddChild(_panel);
    }

    private static Label Line(int size, Color colour)
    {
        var l = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", colour);
        return l;
    }

    public override void _Process(double delta)
    {
        var s = AlcoholService.Instance;
        if (s != _service)
        {
            if (_service != null && IsInstanceValid(_service)) { _service.Result -= OnResult; _service.LevelChanged -= OnLevel; }
            _service = s;
            if (s != null) { s.Result += OnResult; s.LevelChanged += OnLevel; OnLevel(); }
        }
        if (s != null)
        {
            // the blur follows the body's tier: a float compare per frame, the uniform set on change only
            float blur = s.Body.Fx.Blur > 0f && !XR.XrSession.Active ? s.Body.Blur : 0f;
            if (!Mathf.IsEqualApprox(blur, _blurShown))
            {
                _blurShown = blur;
                _blur.Visible = blur > 0f;
                _blurMat.SetShaderParameter(Strength, blur);
            }
        }
        if (_panel.Visible && GameClock.Now >= _hideAt) _panel.Visible = false;
    }

    private void OnLevel()
    {
        bool tipsy = _service is { } s && s.Mode != AlcoholMode.Off && s.Level > AlcoholRules.Tipsy;
        _icon.Visible = tipsy;
        _icon.QueueRedraw();
    }

    private void OnResult(BreathResult r)
    {
        _title.Text = r.Kind switch
        {
            BreathKind.Tube => "BREATH TEST",
            BreathKind.Friend when r.Subject != _service!.Self => "BREATHALYZER · " + (_service.NameOf?.Invoke(r.Subject) ?? "friend"),
            _ => "BREATHALYZER",
        };
        if (r.Kind == BreathKind.Tube)
        {
            var colour = AlcoholRules.Tube(r.Level);
            _tube.Color = colour == TubeColour.Green ? new Color(0.20f, 0.72f, 0.28f) : new Color(0.95f, 0.62f, 0.10f);
            _tube.Visible = true;
            _main.Text = colour == TubeColour.Green ? "Green" : "Amber";
            _main.AddThemeColorOverride("font_color", colour == TubeColour.Green ? UiTheme.Good : UiTheme.Warn);
            _sub.Text = AlcoholRules.TubeText(colour, r.Waiting);
        }
        else
        {
            var e = AlcoholRules.Electronic(r.Level, r.WaitSeconds);
            _tube.Visible = false;
            _main.Text = e.Main;
            _main.AddThemeColorOverride("font_color", e.Waiting ? UiTheme.Warn : e.Permille > AlcoholRules.Limit ? UiTheme.Bad : UiTheme.Good);
            _sub.Text = e.Limit;
        }
        _panel.Visible = true;
        _hideAt = GameClock.Now + ShowSeconds;
    }

    /// <summary>A little beer glass: a trapezoid with a foam cap, drawn when it shows.</summary>
    private sealed partial class GlassIcon : Control
    {
        public override void _Draw()
        {
            var glass = new Color(1f, 0.82f, 0.3f, 0.75f);
            var foam = new Color(1f, 1f, 1f, 0.8f);
            DrawColoredPolygon(new[] { new Vector2(3, 7), new Vector2(17, 7), new Vector2(15, 24), new Vector2(5, 24) }, glass);
            DrawRect(new Rect2(2, 3, 16, 5), foam);
            DrawArc(new Vector2(18, 15), 4, -1.2f, 1.2f, 8, glass, 2f);
        }
    }
}
