using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// The tracked target drawn in the world (#1226, navigation plan 3), on the HUD: an amber diamond 2.5 m over the
/// target with "Piz Palü · 3.4 km ▲1,210 m" under it, its size easing from 28 px far to 18 px near; off screen or
/// behind, an arrow on an inset ellipse round the screen's edge (<see cref="NavMath.EdgePoint"/>) with the distance.
/// Within max(radius, 15 m) it pulses once, and a place or pin tracked from the map is let go. Each part is a child
/// node: per frame only positions and a rotation move, the text is set when the rounded distance changes, the shapes
/// redraw only when their size does. In the headset the same as a world-space billboard (<see cref="Label3D"/>, no
/// depth test) and a haptic tick on the left hand when the target first comes within 100 m.
/// </summary>
public partial class TargetMarker : CanvasLayer
{
    private const float HeightM = 2.5f, ArriveM = 15f, HapticM = 100f;

    public static TargetMarker? Instance { get; private set; }

    /// <summary>The world's origin (LV95 to the scene), set by the client world.</summary>
    public static Func<WorldOrigin?>? Origin { get; set; }

    private Diamond _diamond = null!;
    private Arrow _arrow = null!;
    private Label _label = null!, _edgeLabel = null!;
    private Label3D _vr = null!;
    private int _step = int.MinValue, _climb = int.MinValue, _version = -1;
    private bool _arrived, _near;
    private double _pulse;

    /// <summary>Where it is drawn now (checks): on screen, or pinned to the edge, and where.</summary>
    public bool OnScreen { get; private set; }
    public bool Showing { get; private set; }
    public Vector2 At { get; private set; }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        Name = "TargetMarker";
        Layer = 4;
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Theme = UiTheme.Get() };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        _diamond = new Diamond { MouseFilter = Control.MouseFilterEnum.Ignore };
        _arrow = new Arrow { MouseFilter = Control.MouseFilterEnum.Ignore, Size = new Vector2(36, 36), PivotOffset = new Vector2(18, 18) };
        _label = Text();
        _edgeLabel = Text();
        root.AddChild(_diamond);
        root.AddChild(_arrow);
        root.AddChild(_label);
        root.AddChild(_edgeLabel);
        _vr = new Label3D
        {
            Name = "TargetVr", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true, NoDepthTest = true,
            PixelSize = 0.0012f, FontSize = 32, OutlineSize = 8, Modulate = UiTheme.Amber, Visible = false, Shaded = false,
        };
        AddChild(_vr);
        Hide(true);
    }

    private static Label Text()
    {
        var l = UiKit.Text("", UiTheme.FontSmall, Colors.White, bold: true, align: HorizontalAlignment.Center);
        l.MouseFilter = Control.MouseFilterEnum.Ignore;
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        l.AddThemeConstantOverride("outline_size", 5);
        return l;
    }

    private void Hide(bool all)
    {
        _diamond.Visible = _arrow.Visible = _label.Visible = _edgeLabel.Visible = false;
        if (all) _vr.Visible = false;
        Showing = false;
    }

    public override void _Process(double delta)
    {
        var target = NavTarget.Current;
        int version = NavTarget.Version;
        if (version != _version) { _version = version; _arrived = false; _near = false; _step = int.MinValue; }
        var cam = GetViewport()?.GetCamera3D();
        var me = AdventureCompass.Me?.Invoke();
        if (target is not { } t || cam == null || me is not { } at || Origin?.Invoke() is not { } origin
            || !AdventureLog.Adventuring || Permissions.InMatch || GameShell.Instance is { Top: not null })
        {
            Hide(true);
            return;
        }
        double de = t.E - at.E, dn = t.N - at.N, d = Math.Sqrt(de * de + dn * dn);
        float alt = NavTarget.Alt ?? (float)at.Alt;
        var world = origin.ToWorld(t.E, t.N, alt + HeightM);
        UpdateText(t.Label, d, alt - at.Alt, t.RadiusM);
        Arrival(t, d);
        if (XR.XrSession.Active)
        {
            Hide(false);
            // far targets would sit beyond the far plane: drawn at most 400 m away, along the way
            var from = cam.GlobalPosition;
            var to = world - from;
            if (to.Length() > 400f) to = to.Normalized() * 400f;
            _vr.Position = from + to;
            _vr.Visible = true;
            return;
        }
        _vr.Visible = false;
        Showing = true;
        var screen = GetViewport()!.GetVisibleRect().Size;
        bool behind = cam.IsPositionBehind(world);
        var p = cam.UnprojectPosition(world);
        float size = NavMath.Size(d);
        bool inside = !behind && p.X >= 24 && p.Y >= 24 && p.X <= screen.X - 24 && p.Y <= screen.Y - 24;
        OnScreen = inside;
        if (inside)
        {
            float pulse = _pulse > 0 ? 1f + 0.4f * Mathf.Sin((float)(1 - _pulse) * Mathf.Pi) : 1f;
            _diamond.SetSize(size * pulse);
            _diamond.Position = p - new Vector2(_diamond.Size.X / 2, _diamond.Size.Y / 2);
            _label.Position = p + new Vector2(-_label.Size.X / 2, size * 0.6f);
            _diamond.Visible = _label.Visible = true;
            _arrow.Visible = _edgeLabel.Visible = false;
            At = p;
        }
        else
        {
            var dir = NavMath.OffscreenDir(p, screen, behind);
            var edge = NavMath.EdgePoint(screen, dir);
            _arrow.Position = edge - _arrow.PivotOffset;
            _arrow.Rotation = dir.Angle();
            _edgeLabel.Position = edge - dir.Normalized() * 34 - new Vector2(_edgeLabel.Size.X / 2, _edgeLabel.Size.Y / 2);
            _arrow.Visible = _edgeLabel.Visible = true;
            _diamond.Visible = _label.Visible = false;
            At = edge;
        }
        if (_pulse > 0) _pulse = Math.Max(0, _pulse - delta * 1.5);
    }

    private void UpdateText(string name, double d, double dz, float radius)
    {
        int step = NavFormat.Step(d), climb = Math.Abs(dz) <= 30 ? 0 : (int)Math.Round(dz / 10);
        if (step == _step && climb == _climb) return;
        _step = step;
        _climb = climb;
        _label.Text = NavFormat.Label(name, d, dz, radius);
        _edgeLabel.Text = radius > 0 && d <= radius ? "in the area" : NavFormat.Distance(d);
        _vr.Text = "◆\n" + _label.Text;
        _label.ResetSize();
        _edgeLabel.ResetSize();
    }

    private void Arrival(in (string Label, double E, double N, float RadiusM) t, double d)
    {
        if (!_near && d <= HapticM)
        {
            _near = true;
            if (XR.XrSession.Active) XR.XrSession.Rig?.PulseLeft(0.3f, 0.05f);
        }
        if (_arrived || d > Math.Max(t.RadiusM, ArriveM)) return;
        _arrived = true;
        _pulse = 1;
        // a place or a pin tracked from the map is done; a quest's step finishes by the quest's own rules
        if (AdventureService.Instance is { TrackedPlace: { } place } svc && Math.Abs(place.E - t.E) < 1 && Math.Abs(place.N - t.N) < 1)
            Callable.From(svc.UntrackPlace).CallDeferred();
    }

    /// <summary>The on-screen diamond: drawn once per size.</summary>
    private sealed partial class Diamond : Control
    {
        private float _size;

        public void SetSize(float size)
        {
            if (Mathf.Abs(size - _size) < 0.5f) return;
            _size = size;
            Size = new Vector2(size, size);
            QueueRedraw();
        }

        public override void _Draw()
        {
            float s = _size, h = s / 2;
            var c = new Vector2(h, h);
            Vector2[] outer = { c + new Vector2(0, -h), c + new Vector2(h, 0), c + new Vector2(0, h), c + new Vector2(-h, 0) };
            DrawColoredPolygon(outer, new Color(0, 0, 0, 0.7f));
            float i = h - 3;
            Vector2[] inner = { c + new Vector2(0, -i), c + new Vector2(i, 0), c + new Vector2(0, i), c + new Vector2(-i, 0) };
            DrawColoredPolygon(inner, UiTheme.Amber);
            DrawCircle(c, Mathf.Max(2, h * 0.22f), new Color(0.15f, 0.1f, 0.02f));
        }
    }

    /// <summary>The edge arrow, pointing +X (rotated toward the target): drawn once.</summary>
    private sealed partial class Arrow : Control
    {
        public override void _Draw()
        {
            var c = Size / 2;
            Vector2[] outer = { c + new Vector2(16, 0), c + new Vector2(-8, -13), c + new Vector2(-3, 0), c + new Vector2(-8, 13) };
            DrawColoredPolygon(outer, new Color(0, 0, 0, 0.75f));
            Vector2[] inner = { c + new Vector2(12, 0), c + new Vector2(-6, -9.5f), c + new Vector2(-2, 0), c + new Vector2(-6, 9.5f) };
            DrawColoredPolygon(inner, UiTheme.Amber);
        }
    }
}
