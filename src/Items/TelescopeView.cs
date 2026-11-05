using Godot;
using UnitSport.Core;

namespace UnitSport.Items;

/// <summary>
/// Looking through a set-up telescope (#1440, <c>items/telescope</c>): the view from its tube, a focal
/// length from <see cref="TelescopeOptics.FocalMin"/> to <see cref="TelescopeOptics.FocalMax"/> mm,
/// the tube turned slowly (the look slowed with the zoom, <see cref="TelescopeOptics.LookScale"/>), and
/// a lock that keeps the moon in the middle as the sky turns. The world stays where it is; only the
/// tube node turns and a camera rides it (nothing rebuilt).
///
/// <para>
/// Mouse and right stick turn it; the wheel, + / -, or the D-pad up / down (right) zoom; Use (left
/// button, RT) locks on the moon or lets go; E or Esc / B steps back. In VR the head stays the
/// camera: the eyepiece's picture floats before the eyes, the tube follows where the head looks,
/// the right stick up / down zooms (<c>XrPad</c>'s D-pad up / right) and the trigger locks.
/// </para>
/// </summary>
public partial class TelescopeView : CanvasLayer
{
    public static TelescopeView? Current { get; private set; }

    private static readonly StringName ActUse = PlayerInput.UseItem, ActLeave = PlayerInput.InteractMount, ActCancel = "ui_cancel",
        ActLookLeft = PlayerInput.LookLeft, ActLookRight = PlayerInput.LookRight, ActLookUp = PlayerInput.LookUp, ActLookDown = PlayerInput.LookDown,
        ActNext = PlayerInput.TelescopeNext, ActPrev = PlayerInput.TelescopePrev, ActInfo = PlayerInput.TelescopeInfo;

    /// <summary>The placed telescope's id.</summary>
    public long Id { get; private set; }

    /// <summary>The focal length now, mm (35 mm equivalent).</summary>
    public float Focal { get; private set; } = TelescopeOptics.FocalStart;

    /// <summary>Following the moon.</summary>
    public bool Locked { get; private set; }

    /// <summary>Where the tube points, radians: yaw about up (0 = north, -Z), pitch above the horizon.</summary>
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }

    private Telescopes _service = null!;
    private Player.FootPlayer? _player;
    private Core.TrueNorth.Near _north;
    private Node3D _scope = null!;
    private Node3D? _tube;
    private Camera3D? _cam, _own;
    private bool _vr, _closing;
    private SubViewport? _eyepiece;
    private MeshInstance3D? _panel;
    private Label _readout = null!, _hint = null!, _menu = null!, _info = null!;
    private bool _infoOn;
    private double _infoAt;
    private string _menuShown = "", _infoShown = "";
    private Control _mask = null!;
    private string _hintShown = "";
    private (float Focal, bool Locked, string Target) _readoutShown = (-1f, false, "");

    /// <summary>Opens the view through telescope <paramref name="id"/> (its node <paramref name="scope"/>).</summary>
    public static void Begin(Telescopes service, long id, Node3D scope, Player.FootPlayer? player)
    {
        if (Current != null) return;
        var view = new TelescopeView { Name = "TelescopeView", Layer = 11, _service = service, Id = id, _scope = scope, _player = player };
        (Engine.GetMainLoop() as SceneTree)?.Root.AddChild(view);
    }

    public override void _Ready()
    {
        Current = this;
        UiFocus.Set(this, true);
        _tube = _scope.GetNodeOrNull<Node3D>(TelescopeVisual.TubeName);
        // start where the tripod was set facing, a little up
        var forward = -_scope.GlobalBasis.Z;
        Yaw = Mathf.Atan2(-forward.X, -forward.Z);
        Pitch = Mathf.DegToRad(12f);
        _vr = XR.XrSession.Active;
        var own = GetViewport().GetCamera3D();
        _own = own;
        _cam = new Camera3D
        {
            Name = "TelescopeCamera",
            // nothing within a metre is in the picture: a near plane that far keeps the frustum's
            // size workable at a sixth of a degree (CameraLens)
            Near = 1f,
            Far = own?.Far ?? 20_000f,
            Environment = own?.Environment,
            Attributes = own?.Attributes,
            CullMask = own?.CullMask ?? 0xFFFFF,
        };
        if (_vr) BuildEyepiece(own);
        else
        {
            (_tube ?? _scope).AddChild(_cam);
            _cam.Position = TelescopeVisual.EyeOffset;
            _cam.MakeCurrent();
        }
        BuildHud();
        Apply();
        PlacePlayer();
    }

    /// <summary>The body behind the eyepiece, facing along the tube, so it never stands in the picture.</summary>
    private void PlacePlayer()
    {
        if (_player == null || !IsInstanceValid(_player) || _vr) return;
        var back = new Vector3(Mathf.Sin(Yaw), 0, Mathf.Cos(Yaw));
        _player.GlobalPosition = (_scope.GlobalPosition + back * 0.75f) with { Y = _player.GlobalPosition.Y };
        _player.LookYaw = Yaw;
        _player.Rotation = new Vector3(0, Yaw, 0);
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        UiFocus.Set(this, false);
        if (!_vr && _own != null && IsInstanceValid(_own)) _own.MakeCurrent();
        if (_cam != null && IsInstanceValid(_cam)) _cam.QueueFree();
        if (_panel != null && IsInstanceValid(_panel)) _panel.QueueFree();
        if (IsInstanceValid(_service)) _service.Leave(Id);
    }

    /// <summary>Steps back from the telescope (E, Esc, or it was packed up).</summary>
    public void Close()
    {
        if (_closing) return;
        _closing = true;
        QueueFree();
    }

    /// <summary>Zooms by <paramref name="notches"/> (in when positive).</summary>
    public void Zoom(int notches) => Focal = TelescopeOptics.Zoom(Focal, notches);

    /// <summary>Points the tube at a world direction (a check's, or the lock's).</summary>
    public void Aim(Vector3 direction)
    {
        var d = direction.Normalized();
        Yaw = Mathf.Atan2(-d.X, -d.Z);
        Pitch = Mathf.Asin(Mathf.Clamp(d.Y, -1f, 1f));
    }

    /// <summary>What it follows while <see cref="Locked"/>: the moon, a planet or a big moon (#1444).</summary>
    public SkyBody Target { get; private set; } = SkyBody.Moon;

    /// <summary>Locks on the body nearest the middle of the view, if it is in or near it; true when locked.</summary>
    public bool ToggleLock()
    {
        if (Locked)
        {
            Locked = false;
            return false;
        }
        if (World.DayNight.Instance?.Sky is not { } sky)
        {
            ItemController.Instance?.Toast("Nothing to lock on here.");
            return false;
        }
        var (target, off) = (SkyBody.Moon, Mathf.RadToDeg(Direction.AngleTo(sky.Moon)));
        if (World.SkyBodies.Instance?.Nearest(Direction) is { } planet && planet.OffDeg < off) (target, off) = (planet.Id, planet.OffDeg);
        string? satellite = null;
        if (World.Satellites.Instance?.Nearest(Direction) is { } sat && Mathf.RadToDeg(sat.Direction.AngleTo(Direction)) is var satOff && satOff < off)
            (satellite, off) = (sat.Name, satOff);
        if (!TelescopeOptics.CanLock(off, Focal))
        {
            ItemController.Instance?.Toast("Point it at the moon, a planet or a satellite first.");
            return false;
        }
        Target = target;
        Satellite = satellite;
        Locked = true;
        return true;
    }

    /// <summary>The target menu (#1530): slews to <paramref name="body"/> and keeps it centred, the moon, a planet or a big moon.</summary>
    public bool SelectTarget(SkyBody body)
    {
        Target = body;
        Satellite = null;
        Locked = true;
        if (TargetDirection() is not { } d)
        {
            Locked = false;
            ItemController.Instance?.Toast($"{TelescopeTargets.Name(body)} is not in the sky here.");
            return false;
        }
        Aim(d);
        return true;
    }

    /// <summary>The next (<paramref name="by"/> 1) or previous menu entry, from the one locked on (none: the first).</summary>
    public bool StepTarget(int by) =>
        SelectTarget(TelescopeTargets.Step(Locked && Satellite == null ? Target : null, by));

    /// <summary>The satellite it follows while <see cref="Locked"/> (its name), or null for <see cref="Target"/>.</summary>
    public string? Satellite { get; private set; }

    /// <summary>Where <see cref="Target"/> is this frame, if the sky shows it.</summary>
    private Vector3? TargetDirection() => Satellite != null ? World.Satellites.Instance?.DirectionOf(Satellite)
        : Target == SkyBody.Moon ? World.DayNight.Instance?.Sky?.Moon
        : World.SkyBodies.Instance?.DirectionOf(Target);

    /// <summary>The target's name for the readout.</summary>
    private string TargetName => Satellite ?? (Target == SkyBody.Moon ? "the moon" : Target.ToString());

    /// <summary>The tube's direction in world axes.</summary>
    public Vector3 Direction => new Basis(Vector3.Up, Yaw) * new Basis(Vector3.Right, Pitch) * Vector3.Forward;

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } b && b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            Zoom(b.ButtonIndex == MouseButton.WheelUp ? 1 : -1);
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventKey { Pressed: true, Echo: false } k && k.PhysicalKeycode is Key.Equal or Key.KpAdd or Key.Minus or Key.KpSubtract)
        {
            Zoom(k.PhysicalKeycode is Key.Equal or Key.KpAdd ? 1 : -1);
            GetViewport().SetInputAsHandled();
        }
        // the pad's D-pad, and VR's right stick up / down (XrPad replays it as D-pad up / right)
        else if (e is InputEventJoypadButton { Pressed: true } j && j.ButtonIndex is JoyButton.DpadUp or JoyButton.DpadDown or JoyButton.DpadRight)
        {
            Zoom(j.ButtonIndex == JoyButton.DpadUp ? 1 : -1);
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventMouseMotion m && !_vr && PlayerInput.IsLookMotion(m))
        {
            Turn(m.Relative * 0.0022f * TelescopeOptics.LookScale(Focal));
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (_closing) return;
        if (Input.IsActionJustPressed(ActLeave) || Input.IsActionJustPressed(ActCancel))
        {
            Close();
            return;
        }
        if (Input.IsActionJustPressed(ActUse)) ToggleLock();
        if (Input.IsActionJustPressed(ActNext)) StepTarget(1);
        else if (Input.IsActionJustPressed(ActPrev)) StepTarget(-1);
        if (Input.IsActionJustPressed(ActInfo)) _infoOn = !_infoOn;
        if (!_vr)
        {
            // the right stick, as PlayerInput.LookRate shapes it (that one is blocked while the view has the focus)
            var v = Input.GetVector(ActLookLeft, ActLookRight, ActLookUp, ActLookDown);
            float m = v.Length();
            if (m > 1e-4f)
            {
                var s = GameSettings.Current;
                v = v / m * (m * m) * PlayerInput.StickTurnRate * s.StickSensitivity;
                if (s.InvertY) v.Y = -v.Y;
                Turn(v * (float)delta * TelescopeOptics.LookScale(Focal));
            }
        }
        else if (!Locked && _own is { } head && IsInstanceValid(head))
        {
            // in VR the tube follows the head, eased so the picture does not shake with it
            var want = -head.GlobalBasis.Z;
            Aim(Direction.Slerp(want.Normalized(), Mathf.Min(1f, (float)delta * 6f)));
        }
        if (Locked && TargetDirection() is { } target) Aim(target);
        else if (Locked) Locked = false;
        if (!IsInstanceValid(_scope))
        {
            Close();
            return;
        }
        Apply();
        // the observer walks round the tripod with the tube, never in front of it
        PlacePlayer();
        UpdateHud();
    }

    /// <summary>A turn by the mouse or the stick, radians (x right, y down): it lets go of the moon.</summary>
    private void Turn(Vector2 by)
    {
        if (by.LengthSquared() < 1e-12f) return;
        Locked = false;
        Yaw -= by.X;
        Pitch = Mathf.Clamp(Pitch - by.Y, Mathf.DegToRad(-30f), Mathf.DegToRad(89f));
    }

    private void Apply()
    {
        var basis = new Basis(Vector3.Up, Yaw) * new Basis(Vector3.Right, Pitch);
        if (_tube != null) _tube.GlobalBasis = basis;
        if (_cam == null) return;
        CameraLens.SetFov(_cam, TelescopeOptics.Fov(Focal));
        if (_vr)
        {
            var eye = (_tube ?? _scope).GlobalTransform * TelescopeVisual.EyeOffset;
            _cam.GlobalTransform = new Transform3D(basis, eye);
        }
    }

    // ---- VR: the eyepiece's picture before the eyes ------------------------------------------------

    private void BuildEyepiece(Camera3D? head)
    {
        _eyepiece = new SubViewport { Name = "Eyepiece", Size = new Vector2I(768, 768), RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(_eyepiece);
        _eyepiece.AddChild(_cam);
        _cam!.MakeCurrent();
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = _eyepiece.GetTexture(),
            Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
        };
        _panel = new MeshInstance3D
        {
            Name = "EyepiecePanel",
            Mesh = new QuadMesh { Size = new Vector2(0.34f, 0.34f) },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0, 0, -0.5f),
        };
        head?.AddChild(_panel);
    }

    // ---- the HUD: a round eyepiece, the focal length, the keys ------------------------------------

    private void BuildHud()
    {
        _mask = new ColorRect
        {
            AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = new ShaderMaterial { Shader = new Shader { Code = EyepieceMask } },
            Visible = !_vr,
        };
        AddChild(_mask);
        _readout = HudLabel(20);
        _readout.AnchorLeft = _readout.AnchorRight = 0.5f;
        _readout.AnchorTop = _readout.AnchorBottom = 1;
        _readout.OffsetLeft = -200;
        _readout.OffsetRight = 200;
        _readout.OffsetTop = -96;
        _readout.HorizontalAlignment = HorizontalAlignment.Center;
        AddChild(_readout);
        _hint = HudLabel(17);
        _hint.AnchorTop = _hint.AnchorBottom = 1;
        _hint.OffsetLeft = 24;
        _hint.OffsetTop = -76;
        AddChild(_hint);
        // the target menu down the left, the info of the target down the right: both outside the round picture
        _menu = HudLabel(17);
        _menu.OffsetLeft = 24;
        _menu.OffsetTop = 24;
        AddChild(_menu);
        _info = HudLabel(17);
        _info.AnchorLeft = _info.AnchorRight = 1;
        _info.OffsetLeft = -280;
        _info.OffsetRight = -24;
        _info.OffsetTop = 24;
        _info.HorizontalAlignment = HorizontalAlignment.Right;
        AddChild(_info);
    }

    private static Label HudLabel(int size)
    {
        var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 0.96f));
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 6);
        return l;
    }

    private void UpdateHud()
    {
        if (_readoutShown != (Focal, Locked, TargetName))
        {
            _readoutShown = (Focal, Locked, TargetName);
            _readout.Text = $"{Focal:0} mm   ×{TelescopeOptics.Magnification(Focal):0.#}{(Locked ? $"   locked on {TargetName}" : "")}";
        }
        string hint = Hint();
        if (hint != _hintShown) _hint.Text = _hintShown = hint;
        UpdateMenu();
        UpdateInfo();
    }

    /// <summary>The target list, the locked one marked: text set only when it changes.</summary>
    private void UpdateMenu()
    {
        string text = MenuText(Locked && Satellite == null ? Target : null);
        if (text != _menuShown) _menu.Text = _menuShown = text;
    }

    private static readonly string?[] MenuTexts = new string?[TelescopeTargets.Menu.Length + 1];

    private static string MenuText(SkyBody? marked)
    {
        int slot = marked == null ? 0 : 1 + System.Array.IndexOf(TelescopeTargets.Menu, marked.Value);
        if (MenuTexts[slot] is { } cached) return cached;
        var sb = new System.Text.StringBuilder();
        foreach (var b in TelescopeTargets.Menu)
            sb.Append(b == marked ? "▶ " : "   ").Append(TelescopeTargets.Name(b)).Append('\n');
        return MenuTexts[slot] = sb.ToString().TrimEnd();
    }

    /// <summary>The info HUD: the target's readings, worked out four times a second at most, the label set only on change.</summary>
    private void UpdateInfo()
    {
        _info.Visible = _infoOn;
        if (!_infoOn || RealClock.Now - _infoAt < 0.25) return;
        _infoAt = RealClock.Now;
        if (World.DayNight.Instance?.Sky is not { } sky) return;
        // the satellite (a spacecraft) has no ephemeris reading: it shows its name and place only
        var body = Target;
        string text, nl = "\n";
        (double Azimuth, double Altitude)? horizon = TargetDirection() is { } d ? TelescopeTargets.Horizon(d.X, d.Y, d.Z) : null;
        // the azimuth from true north at the scope, not the world's −Z (#1504)
        if (horizon is { } h && _player?.Origin is { } origin)
            horizon = ((h.Azimuth - Mathf.RadToDeg(_north.World(origin.Frame, _scope.GlobalPosition)) + 360) % 360, h.Altitude);
        if (Satellite != null) text = Satellite + nl + TelescopeTargets.Where(horizon);
        else text = TelescopeTargets.Format(body, TelescopeTargets.Read(body, SwissTime.ToUtc(sky.Date, sky.Hour)), horizon);
        if (text != _infoShown) _info.Text = _infoShown = text;
    }

    /// <summary>The keys while looking through it: zoom, lock, step back, the target menu, the info.</summary>
    private string Hint()
    {
        var device = PlayerInput.HintDevice;
        string zoom = device == InputDevice.Gamepad ? "[D-pad ↑ ↓] zoom" : _vr ? "[right stick ↑ ↓] zoom" : "[wheel] zoom";
        return $"{zoom}   {InputHints.Prompt(PlayerInput.UseItem, Locked ? $"let go of {TargetName}" : "lock on")}   " +
            $"{InputHints.Prompt(PlayerInput.InteractMount, "step back")}\n" +
            $"{InputHints.Prompt(PlayerInput.TelescopePrev, "previous")}  {InputHints.Prompt(PlayerInput.TelescopeNext, "next target")}   " +
            InputHints.Prompt(PlayerInput.TelescopeInfo, _infoOn ? "hide info" : "info");
    }

    /// <summary>Black round the eyepiece's circle, soft at its edge.</summary>
    private const string EyepieceMask = @"shader_type canvas_item;
void fragment() {
	vec2 size = 1.0 / SCREEN_PIXEL_SIZE;
	vec2 p = (SCREEN_UV - 0.5) * size / size.y;
	float r = length(p);
	COLOR = vec4(0.0, 0.0, 0.0, smoothstep(0.47, 0.5, r));
}";
}
