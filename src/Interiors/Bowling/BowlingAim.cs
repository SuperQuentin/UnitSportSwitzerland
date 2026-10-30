using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// Stepping up to bowl, Wii Sports style (#1323), on keyboard and pad: a camera behind the bowler over
/// their lane, then two steps. <b>Aim</b> (#1336): the move keys (A/D, the left stick) walk the spot along
/// the foul line, the aim keys (the arrows, the right stick) turn the line, the line drawn on the lane
/// (straight: the hook is not shown, it is the player's to judge). <b>Swing</b>: hold to swing, the power
/// swelling and ebbing while held; moving sideways during the swing (A/D, the stick, the mouse, like
/// twisting the remote) puts hook on it; let go to roll. Sprint held looks down the lane at the pins; Cancel steps back.
/// Everything goes out as one <see cref="BowlingBall.Release"/> (<see cref="BowlingLanes.AskRelease"/>).
/// In VR the same steps run through <c>XrPad</c> (the stick and the trigger), or the ball is thrown for
/// real (<c>XrBowling</c>).
/// </summary>
public partial class BowlingAim : CanvasLayer
{
    private enum Step { Aim, Swing }

    public static BowlingAim? Current { get; private set; }

    /// <summary>A full swing's period: up to full power and back down [s].</summary>
    private const float SwingPeriod = 1.6f;
    private const float SpotSpeed = 0.35f, LineSpeed = 0.06f;

    private FootPlayer _player = null!;
    private InteriorNode _node = null!;
    private string _key = "";
    private int _pair, _lane;
    private LaneGeometry _g;
    private Step _step;
    private float _spot, _line, _power, _hook, _held;
    private bool _swinging;
    /// <summary>The roll is watched (<see cref="BowlingWatch"/> has the camera): leaving does not give it back.</summary>
    private bool _watched;
    private Camera3D? _cam, _own;
    private MeshInstance3D _curve = null!;
    private ImmediateMesh _curveMesh = null!;
    private Label _text = null!;
    private ColorRect _bar = null!, _fill = null!, _hookMark = null!;
    private (float, float, float) _drawn = (float.NaN, 0, 0);
    private Step _shownStep = (Step)255;

    private static readonly StringName ActLeft = PlayerInput.MoveLeft, ActRight = PlayerInput.MoveRight,
        ActGo = PlayerInput.Jump, ActZoom = PlayerInput.Sprint, ActCancel = "ui_cancel",
        ActTurnLeft = PlayerInput.BowlAimLeft, ActTurnRight = PlayerInput.BowlAimRight;

    /// <summary>The local player steps up to their lane of pair <paramref name="pair"/>.</summary>
    public static void Begin(FootPlayer p, string key, int pair)
    {
        if (Current != null || BowlingLanes.Instance?.SignedIn(p.Multiplayer.MultiplayerPeer is OfflineMultiplayerPeer or null ? 1 : p.Multiplayer.GetUniqueId()) is not { } me) return;
        if (InteriorManager.Instance?.CurrentNode is not { } node || node.Layout.Key != key) return;
        var aim = new BowlingAim { Name = "BowlingAim", Layer = 11, _player = p, _node = node, _key = key, _pair = pair, _lane = me.Lane };
        aim._g = LaneGeometry.Of(node.Layout.Furniture[pair]);
        p.GetTree().Root.AddChild(aim);
    }

    public override void _Ready()
    {
        Current = this;
        // a right-hander's usual stand: a little right of centre, the line a touch outward
        _spot = -0.15f;
        UiFocus.Set(this, true);
        BuildHud();
        _curveMesh = new ImmediateMesh();
        _curve = new MeshInstance3D
        {
            Name = "BowlingCurve", Mesh = _curveMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = false,
            },
        };
        _node.AddChild(_curve);
        if (!XR.XrSession.Active && GetViewport().GetCamera3D() is { } own)
        {
            _own = own;
            _cam = new Camera3D { Name = "BowlingCamera", Fov = 55f, Near = 0.05f, Far = own.Far, Environment = own.Environment, Attributes = own.Attributes, CullMask = own.CullMask };
            _node.AddChild(_cam);
            _cam.MakeCurrent();
            // the bowler as the others see them, the ball in their hand, not the first person's body (#1336)
            _player.SeenFromOutside = true;
        }
        PlaceBowler();
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        UiFocus.Set(this, false);
        if (_own != null && IsInstanceValid(_own) && !_watched) _own.MakeCurrent();
        if (IsInstanceValid(_player)) _player.SeenFromOutside = false;
        if (_cam != null && IsInstanceValid(_cam)) _cam.QueueFree();
        if (IsInstanceValid(_curve)) _curve.QueueFree();
    }

    /// <summary>Lane-frame point (x across the lane, y up, z from the foul line) to the interior node's frame.</summary>
    private Vector3 ToNode(Vector3 lane) =>
        BowlingLanes.PieceFrame(_node.Layout, _node.Layout.Furniture[_pair]) * new Vector3(_g.LaneX(_lane) + lane.X, LaneGeometry.LaneTop + lane.Y, _g.Foul + lane.Z);

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (Input.IsActionJustPressed(ActCancel))
        {
            if (_step == Step.Aim) { QueueFree(); return; }
            _step--;
            _swinging = false;
        }
        float steer = Input.GetActionStrength(ActRight) - Input.GetActionStrength(ActLeft);
        float turn = Input.GetActionStrength(ActTurnRight) - Input.GetActionStrength(ActTurnLeft);
        switch (_step)
        {
            case Step.Aim:
                // right on screen is the bowler's right: −x in the lane's frame
                _spot = Mathf.Clamp(_spot - steer * SpotSpeed * dt, -BowlingSizes.HalfLane + 0.12f, BowlingSizes.HalfLane - 0.12f);
                _line = Mathf.Clamp(_line - turn * LineSpeed * dt, -0.08f, 0.08f);
                if (steer != 0) PlaceBowler();
                if (Input.IsActionJustPressed(ActGo)) { _step = Step.Swing; _swinging = false; }
                break;
            case Step.Swing:
                if (Input.IsActionJustPressed(ActGo)) { _swinging = true; _held = 0; _hook = 0; }
                if (_swinging)
                {
                    _held += dt;
                    float phase = _held / SwingPeriod % 1f;
                    _power = phase < 0.5f ? phase * 2 : 2 - phase * 2;
                    // the stick (or A/D) sideways during the swing: the twist; the mouse adds to it
                    _hook = Mathf.Clamp(_hook - steer * 1.6f * dt, -1f, 1f);
                    if (!Input.IsActionPressed(ActGo)) Roll();
                }
                break;
        }
        UpdateCamera(dt);
        UpdateHud();
        UpdateCurve();
    }

    public override void _Input(InputEvent e)
    {
        if (_step == Step.Swing && _swinging && e is InputEventMouseMotion m)
            _hook = Mathf.Clamp(_hook - m.Relative.X * 0.004f, -1f, 1f);
    }

    private BowlingBall.Release Release() => new(_spot, _line, 5f + _power * 5f, 16f + Mathf.Abs(_hook) * 26f, _hook * 0.75f);

    private void Roll()
    {
        BowlingLanes.Instance?.AskRelease(_key, Release());
        // the camera stays on the lane, then looks at its screen's replay (#1336)
        if (_cam != null && _own != null && _node.LaneRigOf(_pair) is { } rig)
        {
            BowlingWatch.Begin(_node, _own, rig, _lane, _cam.GlobalTransform);
            _watched = true;
        }
        QueueFree();
    }

    /// <summary>The bowler on the approach, a step behind the foul line at the chosen spot, facing the pins.</summary>
    private void PlaceBowler()
    {
        var at = _node.GlobalTransform * ToNode(new Vector3(_spot, 0, -1.4f));
        var pins = _node.GlobalTransform * ToNode(new Vector3(_spot, 0, 10f));
        _player.GlobalPosition = at with { Y = _player.GlobalPosition.Y };
        var dir = pins - at;
        float yaw = Mathf.Atan2(-dir.X, -dir.Z);
        _player.LookYaw = yaw;
        _player.Rotation = new Vector3(0, yaw, 0);
    }

    private void UpdateCamera(float dt)
    {
        if (_cam == null) return;
        bool zoom = Input.IsActionPressed(ActZoom);
        var eye = zoom ? ToNode(new Vector3(0, 0.9f, BowlingBall.HandOver + 9f)) : ToNode(new Vector3(_spot * 0.5f, 1.9f, -4.2f));
        var look = ToNode(new Vector3(0, 0.1f, 18f));
        var want = new Transform3D(Basis.Identity, eye).LookingAt(look, Vector3.Up);
        _cam.Transform = _cam.Transform.InterpolateWith(want, Mathf.Min(1, dt * 6));
    }

    private void BuildHud()
    {
        var box = new VBoxContainer { Position = new Vector2(40, 40) };
        _text = new Label();
        _text.AddThemeFontSizeOverride("font_size", 22);
        box.AddChild(_text);
        _bar = new ColorRect { CustomMinimumSize = new Vector2(320, 18), Color = new Color(0, 0, 0, 0.55f) };
        _fill = new ColorRect { Size = new Vector2(0, 18), Color = new Color(1f, 0.75f, 0.2f) };
        _hookMark = new ColorRect { Size = new Vector2(4, 26), Position = new Vector2(158, -4), Color = new Color(0.4f, 0.9f, 1f) };
        _bar.AddChild(_fill);
        _bar.AddChild(_hookMark);
        box.AddChild(_bar);
        AddChild(box);
    }

    private void UpdateHud()
    {
        if (_step != _shownStep)
        {
            _shownStep = _step;
            _text.Text = _step switch
            {
                Step.Aim => $"Aim  ·  {InputHints.Prompt(PlayerInput.MoveLeft, "")}{InputHints.Prompt(PlayerInput.MoveRight, "move")}   {InputHints.Prompt(PlayerInput.BowlAimLeft, "")}{InputHints.Prompt(PlayerInput.BowlAimRight, "turn")}   {InputHints.Prompt(PlayerInput.Jump, "set")}   {InputHints.Prompt(PlayerInput.Sprint, "look at the pins")}",
                _ => $"Swing  ·  hold {InputHints.Prompt(PlayerInput.Jump, "")}, move sideways for hook, let go to roll",
            };
            _bar.Visible = _step == Step.Swing;
        }
        if (_step == Step.Swing)
        {
            _fill.Size = new Vector2(320 * _power, 18);
            _hookMark.Position = new Vector2(158 - _hook * 150, -4);
        }
    }

    /// <summary>
    /// The line the ball starts on, from the spot and the line alone (#1336: no hook in it, judging the hook is the game),
    /// redrawn only when they change.
    /// </summary>
    private void UpdateCurve()
    {
        var now = (_spot, _line, 0f);
        if (now == _drawn) return;
        _drawn = now;
        var run = BowlingBall.Roll(_g.Run, new BowlingBall.Release(_spot, _line, 7.5f, 16f, 0f));
        _curveMesh.ClearSurfaces();
        _curveMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        var col = new Color(1f, 1f, 1f, 0.55f);
        int stride = 12;
        for (int i = 0; i + stride < run.Pos.Count; i += stride)
        {
            var a = run.Pos[i] with { Y = 0.006f };
            var b = run.Pos[i + stride] with { Y = 0.006f };
            var side = new Vector3(0.012f, 0, 0);
            Quad(ToNode(a - side), ToNode(a + side), ToNode(b + side), ToNode(b - side), col);
        }
        _curveMesh.SurfaceEnd();
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
    {
        void V(Vector3 v)
        {
            _curveMesh.SurfaceSetColor(col);
            _curveMesh.SurfaceAddVertex(v);
        }
        V(a); V(b); V(c);
        V(a); V(c); V(d);
    }
}
