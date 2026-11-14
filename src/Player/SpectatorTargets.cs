using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Player;

/// <summary>
/// The spectator's target (#1850, docs/plans/planets.md sections 8 and 11), under <c>--frame earth</c>: a tap of
/// <see cref="PlayerInput.SpectatorTarget"/> steps to the next body (<see cref="TargetList"/>, wrapping through none), a
/// hold turns the view to it (<see cref="SpectatorCamera.TurnToward"/>), and the cue shows where it is: a ring on it, or
/// an arrow at the screen's edge when it is off screen, with its name, the distance to its surface and the time to go at
/// the speed flown. Fly toward the ring and the spectator's own speed rule takes you there.
///
/// <para>
/// Three devices (<c>general/new-action-three-devices</c>): ] on a keyboard, a pad's D-pad right, VR's right stick down
/// (<c>XrPad</c> sends it as D-pad right while spectating). Nothing on screen without a target (<c>ui/style-guide</c>);
/// the text is set twice a second, the ring drawn each frame without an allocation.
/// </para>
/// </summary>
public partial class SpectatorTargets : CanvasLayer
{
    /// <summary>A press held this long turns the view to the target; shorter, it steps to the next one, s.</summary>
    public const double HoldSeconds = 0.35;

    /// <summary>How fast the hold turns the view, rad/s.</summary>
    public const float TurnRate = Mathf.Pi / 2;

    /// <summary>The live one, for the checks.</summary>
    public static SpectatorTargets? Instance { get; private set; }

    /// <summary>The body aimed at, or none.</summary>
    public BodyId? Target { get; private set; }

    /// <summary>The hold is turning the view to the target now.</summary>
    public bool Aligning { get; private set; }

    /// <summary>The target's direction from the camera in world axes, and the distance to its surface, metres (when one is set).</summary>
    public (Vector3 Direction, double Distance)? Toward { get; private set; }

    private readonly WorldOrigin _origin;
    private SpectatorCamera? _cam;
    private Cue _cue = null!;
    private Label _name = null!, _detail = null!;
    private Control _labels = null!;
    private double _heldFor = -1, _textAt;

    public SpectatorTargets(WorldOrigin origin)
    {
        _origin = origin;
        Name = "SpectatorTargets";
        Layer = 8;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        var root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        _cue = new Cue { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _cue.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_cue);
        _labels = UiKit.VBox(0);
        _labels.MouseFilter = Control.MouseFilterEnum.Ignore;
        _labels.Visible = false;
        _labels.AddChild(_name = Floating(UiKit.Text("", UiTheme.FontBody, UiTheme.Amber, bold: true, align: HorizontalAlignment.Center)));
        _labels.AddChild(_detail = Floating(UiKit.Text("", UiTheme.FontSmall, UiTheme.Text, align: HorizontalAlignment.Center)));
        root.AddChild(_labels);
    }

    /// <summary>Over the bright limb of a planet as over space: the floating text's outline and shadow (<c>ui/style-guide</c>).</summary>
    private static Label Floating(Label label)
    {
        label.AddThemeConstantOverride("outline_size", 4);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.55f));
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.45f));
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeConstantOverride("shadow_outline_size", 6);
        return label;
    }

    /// <summary>Steps to the next target (the tap; a check's too).</summary>
    public void Cycle()
    {
        Target = TargetList.Next(Target, _origin.Body);
        _textAt = 0;
        GD.Print($"[target] {(Target is { } t ? CelestialBody.Of(t).Name : "none")}");
    }

    public override void _Process(double delta)
    {
        if (_cam == null || !IsInstanceValid(_cam))
            _cam = GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false) as SpectatorCamera;
        var cam = _cam;
        bool spectating = cam != null && (GetViewport()?.GetCamera3D() == cam || XR.XrSession.Anchor == cam);
        if (!spectating || UiFocus.TextEntryActive)
        {
            _heldFor = -1;
            Aligning = false;
            Hide();
            return;
        }

        // a tap steps, a hold turns: decided on the release, or once the hold is long enough
        bool down = Input.IsActionPressed(PlayerInput.SpectatorTarget);
        if (down) _heldFor = _heldFor < 0 ? 0 : _heldFor + delta;
        else if (_heldFor >= 0)
        {
            if (_heldFor < HoldSeconds) Cycle();
            _heldFor = -1;
        }
        Aligning = down && _heldFor >= HoldSeconds && Target != null;

        Toward = Target is { } target && _origin.Frame.Axes is { } axes ? Measure(target, axes, cam!) : null;
        if (Toward is not { } toward)
        {
            Hide();
            return;
        }
        if (Aligning) cam!.TurnToward(toward.Direction, TurnRate * (float)delta);
        Show(cam!, toward, delta);
    }

    /// <summary>The target's direction from the camera (world axes) and the distance to its surface, in doubles.</summary>
    private static (Vector3, double) Measure(BodyId target, FrameAxes axes, Camera3D cam)
    {
        var sky = SkyClock.Now;
        var centre = Frames.Convert(target, DVec3.Zero, axes.Body, sky);
        var c = cam.GlobalPosition;
        var e = axes.ToEcef(c.X, c.Y, c.Z);
        var to = centre - new DVec3(e.X, e.Y, e.Z);
        double length = to.Length;
        var dir = length > 0 ? to / length : new DVec3(0, 0, -1);
        var world = new Vector3((float)FrameAxes.Dot(dir.AsEcef, axes.X), (float)FrameAxes.Dot(dir.AsEcef, axes.Y), (float)FrameAxes.Dot(dir.AsEcef, axes.Z));
        return (world.Normalized(), Math.Max(0, length - CelestialBody.Of(target).Shape.A));
    }

    private void Show(SpectatorCamera cam, (Vector3 Direction, double Distance) toward, double delta)
    {
        var rect = GetViewport().GetVisibleRect().Size;
        var ahead = cam.GlobalPosition + toward.Direction * 100f;
        bool behind = cam.IsPositionBehind(ahead);
        var at = behind ? Vector2.Zero : cam.UnprojectPosition(ahead);
        const float inset = 56f;
        bool onScreen = !behind && at.X >= inset && at.Y >= inset && at.X <= rect.X - inset && at.Y <= rect.Y - inset;
        float angle = 0f;
        if (!onScreen)
        {
            // off screen: an arrow at the edge toward it, the direction in the view's plane
            var local = cam.GlobalBasis.Inverse() * toward.Direction;
            var flat = new Vector2(local.X, -local.Y);
            if (flat.LengthSquared() < 1e-8f) flat = Vector2.Down;
            flat = flat.Normalized();
            var half = rect / 2 - new Vector2(inset, inset);
            float scale = Mathf.Min(Mathf.Abs(half.X / Mathf.Max(Mathf.Abs(flat.X), 1e-6f)), Mathf.Abs(half.Y / Mathf.Max(Mathf.Abs(flat.Y), 1e-6f)));
            at = rect / 2 + flat * scale;
            angle = flat.Angle();
        }
        _cue.Set(at, onScreen, angle, Aligning);
        _cue.Visible = true;

        _textAt -= delta;
        if (_textAt <= 0 && Target is { } target)
        {
            _textAt = 0.5;
            _name.Text = CelestialBody.Of(target).Name;
            // the trip at full speed by the spectator's own rule, from the height over the body left
            double up = Math.Max(0, _origin.AltitudeOf(cam.GlobalPosition));
            _detail.Text = TargetList.Distance(toward.Distance) + " · ~" + TargetList.Duration(TargetList.TripSeconds(toward.Distance + up, up)) + " at full speed";
        }
        _labels.Visible = true;
        var size = _labels.GetCombinedMinimumSize();
        // under the ring; at the bottom edge, over the arrow
        float y = at.Y + Cue.Radius + 6f;
        if (y + size.Y > rect.Y - 8f) y = at.Y - Cue.Radius - 6f - size.Y;
        _labels.Position = new Vector2(Mathf.Clamp(at.X - size.X / 2, 8f, rect.X - size.X - 8f), y);
    }

    private void Hide()
    {
        if (_cue.Visible) _cue.Visible = false;
        if (_labels.Visible) _labels.Visible = false;
    }

    /// <summary>The ring on the target, or the arrow at the edge toward it: drawn, no nodes, no allocation.</summary>
    private sealed partial class Cue : Control
    {
        public const float Radius = 16f;
        private Vector2 _at;
        private bool _onScreen, _aligning;
        private float _angle;

        public void Set(Vector2 at, bool onScreen, float angle, bool aligning)
        {
            if (at == _at && onScreen == _onScreen && angle == _angle && aligning == _aligning) return;
            (_at, _onScreen, _angle, _aligning) = (at, onScreen, angle, aligning);
            QueueRedraw();
        }

        public override void _Draw()
        {
            var amber = UiTheme.Amber;
            var shade = new Color(0, 0, 0, 0.5f);
            if (_onScreen)
            {
                DrawArc(_at, Radius + 1.5f, 0, Mathf.Tau, 48, shade, 4f, true);
                DrawArc(_at, Radius, 0, Mathf.Tau, 48, amber, _aligning ? 3f : 2f, true);
                return;
            }
            // an arrow pointing out of the screen toward it (its points in reused arrays: drawn every frame it moves)
            var d = Vector2.FromAngle(_angle);
            var side = new Vector2(-d.Y, d.X);
            _tri[0] = _at + d * Radius;
            _tri[1] = _at - d * Radius * 0.6f + side * Radius * 0.8f;
            _tri[2] = _at - d * Radius * 0.6f - side * Radius * 0.8f;
            _back[0] = _tri[0] + d * 1.5f;
            _back[1] = _tri[1] + side * 1.5f;
            _back[2] = _tri[2] - side * 1.5f;
            DrawColoredPolygon(_back, shade);
            DrawColoredPolygon(_tri, amber);
        }

        private readonly Vector2[] _tri = new Vector2[3], _back = new Vector2[3];
    }
}
