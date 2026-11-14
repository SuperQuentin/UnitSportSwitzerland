using Godot;

namespace UnitSport.Player;

/// <summary>
/// Free fly camera: WASD / left stick (level by default: <see cref="Core.GameSettings.LevelFlight"/>),
/// Space / E / RT up, Shift / Q / LT down, mouse or right
/// stick look, Ctrl / L3 for boost, the wheel, = / - or RB / LB to change speed (shown by
/// <see cref="SpectatorSpeedHud"/>), up to 100 km/s high up (<see cref="SpectatorSpeed"/>, #1796). Click to take
/// mouse capture back after a menu.
/// </summary>
public partial class SpectatorCamera : Camera3D, Core.IOriginShiftAware
{
    /// <summary>Held, a key or button repeats its notch after <see cref="RampDelay"/>, every <see cref="RampEvery"/>, s.</summary>
    private const double RampDelay = 0.35, RampEvery = 0.08;

    /// <summary>The speed asked for, m/s; what is flown is no more than <see cref="SpectatorSpeed.AltitudeCap"/> (<see cref="FlownSpeed"/>).</summary>
    [Export] public float Speed { get; set; } = 150f;
    [Export] public float BoostMultiplier { get; set; } = 6f;
    [Export] public float MouseSensitivity { get; set; } = 0.0025f;

    /// <summary>The camera's altitude above the ellipsoid, through the frame (in the Space regime Y is not the altitude, #1430 H3).</summary>
    private double Altitude => Core.OriginShifter.Instance?.Origin.AltitudeOf(Position) ?? Position.Y;

    /// <summary>The speed flown now, without boost: <see cref="Speed"/> held to <see cref="Cap"/>.</summary>
    public float FlownSpeed => Mathf.Min(Speed, Cap);

    /// <summary>
    /// The fastest the wheel's speed is flown here: <see cref="SpectatorSpeed.AltitudeCap"/> by the altitude; between
    /// bodies (#1850, <see cref="Core.OriginShifter.CrossBodies"/>) <see cref="SpectatorSpeed.SurfaceCap"/> by the
    /// nearest surface of any body. Worked out once a frame (the frame tree's few microseconds).
    /// </summary>
    public float Cap => _capFrame == Engine.GetProcessFrames() ? _cap : UpdateCap();

    private float _cap;
    private ulong _capFrame = ulong.MaxValue;

    private float UpdateCap()
    {
        _capFrame = Engine.GetProcessFrames();
        _cap = Core.OriginShifter.CrossBodies ? SpectatorSpeed.SurfaceCap(SurfaceDistance()) : SpectatorSpeed.AltitudeCap(Altitude);
        return _cap;
    }

    /// <summary>How far the nearest surface of any body is (#1850, <see cref="SurfaceReach"/>).</summary>
    private double SurfaceDistance() => Core.OriginShifter.Instance?.Origin.Frame is { } frame
        ? SurfaceReach.Of(frame, Position, Altitude)
        : Altitude;

    private SpectatorSpeedHud? _hud;
    private bool _boosted;
    private double _heldFor, _nextRamp;

    private float _yaw;

    /// <summary>
    /// A turning shift (#1430 H3, the earth world's re-anchors #1512) keeps the view as it was against the local
    /// horizon: its pitch, its heading turned by the shift's yaw, no roll (#1800). The shifter turned the camera rigidly
    /// with the world, tilt and all: after a long flight with the look untouched (every 3 km on the ground, every 20 km
    /// in Space, ~50° of the Earth's turn from Geneva to Asia) the view stood rolled and pitched, and the next look
    /// input rebuilt it from the angles kept here, a snap. The angles are read off the camera's own placement before
    /// the shift, so a tool that turned the camera itself keeps its view too.
    /// </summary>
    public void OnOriginShifted(Core.OriginShift shift)
    {
        if (!shift.Turns) return;
        var before = (shift.Transform.Basis.Inverse() * GlobalTransform.Basis).GetEuler();
        _pitch = Mathf.Clamp(before.X, -Mathf.Pi / 2 + 0.01f, Mathf.Pi / 2 - 0.01f);
        _yaw = before.Y + shift.Yaw;
        Rotation = new Vector3(_pitch, _yaw, 0);
    }

    /// <summary>The look's angles from the camera as it is (a tool or the spawn may have turned it): a look input turns that view, never a stale one.</summary>
    private void SyncLook()
    {
        var r = Rotation;
        _pitch = Mathf.Clamp(r.X, -Mathf.Pi / 2 + 0.01f, Mathf.Pi / 2 - 0.01f);
        _yaw = r.Y;
    }
    private float _pitch;

    public override void _Ready()
    {
        Near = 1f;
        Far = Core.GameSettings.Current.CameraFar;
        Core.MouseCapture.Capture();
    }

    /// <summary>A mouse look by <paramref name="relative"/> pixels (also a check's, <c>--spectatorturncheck</c>).</summary>
    public void Look(Vector2 relative)
    {
        SyncLook();
        _yaw -= relative.X * MouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - relative.Y * MouseSensitivity, -Mathf.Pi / 2 + 0.01f, Mathf.Pi / 2 - 0.01f);
        Rotation = new Vector3(_pitch, _yaw, 0);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (UnitSport.Core.UiFocus.TextEntryActive) return;

        switch (@event)
        {
            case InputEventMouseMotion motion when Core.PlayerInput.IsLookMotion(motion):
                Look(motion.Relative);
                break;

            case InputEventMouseButton { Pressed: true } button
                when button.ButtonIndex is not (MouseButton.WheelUp or MouseButton.WheelDown):
                if (Core.MouseCapture.Mode == Input.MouseModeEnum.Visible)
                    Core.MouseCapture.Capture();
                break;
        }
        // the speed, only while this is the camera: on foot the wheel zooms the body's camera
        if (!Current) return;
        if (@event.IsActionPressed(Core.PlayerInput.SpectatorFaster)) Step(+1);
        else if (@event.IsActionPressed(Core.PlayerInput.SpectatorSlower)) Step(-1);
        // Esc belongs to the mode menu (ClientWorld); clicking in the viewport is what
        // takes mouse capture back, handled by the button case above.
    }

    /// <summary>A notch faster (+1) or slower (-1), and the speed shown.</summary>
    private void Step(int way)
    {
        Speed = way > 0
            ? Core.OriginShifter.CrossBodies ? SpectatorSpeed.Faster(Speed, Cap) : SpectatorSpeed.Faster(Speed)
            : SpectatorSpeed.Slower(Speed, Cap);
        ShowSpeed();
    }

    /// <summary>The speed flown now in the corner (<see cref="SpectatorSpeedHud"/>), made on its first use.</summary>
    private void ShowSpeed()
    {
        if (_hud == null) AddChild(_hud = new SpectatorSpeedHud());
        float cap = Cap;
        _hud.Show(Mathf.Min(Speed, cap) * (_boosted ? BoostMultiplier : 1f), _boosted, limited: Speed > cap);
    }

    /// <summary>A held key or button repeats its notch: a pad has no wheel (2 m/s to 100 km/s in about 4 s).</summary>
    private void Ramp(double delta)
    {
        int way = Core.PlayerInput.HeldButton(Core.PlayerInput.SpectatorFaster) ? 1
            : Core.PlayerInput.HeldButton(Core.PlayerInput.SpectatorSlower) ? -1 : 0;
        if (way == 0)
        {
            _heldFor = 0;
            _nextRamp = RampDelay;
            return;
        }
        _heldFor += delta;
        if (_heldFor < _nextRamp) return;
        _nextRamp += RampEvery;
        Step(way);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        if (Current)
        {
            Ramp(delta);
            // boost pressed or let go changes the speed too: shown once per change
            bool boosted = UnitSport.Core.PlayerInput.Held(UnitSport.Core.PlayerInput.FlyBoost);
            if (boosted != _boosted)
            {
                _boosted = boosted;
                ShowSpeed();
            }
        }

        // right stick: a rate, integrated here; the mouse arrives as events above
        var look = UnitSport.Core.PlayerInput.LookRate;
        if (look != Vector2.Zero)
        {
            SyncLook();
            _yaw -= look.X * dt;
            _pitch = Mathf.Clamp(_pitch - look.Y * dt, -Mathf.Pi / 2 + 0.01f, Mathf.Pi / 2 - 0.01f);
            Rotation = new Vector3(_pitch, _yaw, 0);
        }

        // PlayerInput returns neutral while a text field has the keyboard, so typing in chat
        // does not fly the camera. Space/Shift match the on-foot and mounted controls, so the
        // vertical axis is on the same keys whatever you are; Q/E stay for the other hand, and
        // the triggers do it on a pad.
        // Level flight (the default, #1002) is Minecraft's: the stick moves on the ground plane
        // whatever the pitch, so looking down while flying forward does not dive; off, forward
        // follows the look.
        var stick = UnitSport.Core.PlayerInput.Move;
        var dir = Core.GameSettings.Current.LevelFlight
            ? new Basis(Vector3.Up, Rotation.Y) * new Vector3(stick.X, 0, stick.Y)
            : Basis * new Vector3(stick.X, 0, stick.Y);
        dir += Vector3.Up * (UnitSport.Core.PlayerInput.Strength(UnitSport.Core.PlayerInput.FlyUp)
            - UnitSport.Core.PlayerInput.Strength(UnitSport.Core.PlayerInput.FlyDown));

        if (dir.LengthSquared() > 1e-6f)
        {
            // an analog stick asks for part of the speed; a key asks for all of it
            float amount = Mathf.Min(dir.Length(), 1f);
            float speed = FlownSpeed * amount
                * (UnitSport.Core.PlayerInput.Held(UnitSport.Core.PlayerInput.FlyBoost) ? BoostMultiplier : 1f);
            Position += dir.Normalized() * speed * dt;
            // no higher than the ceiling, unless there are other bodies to fly to (#1850)
            double altitude = Altitude;
            if (!Core.OriginShifter.CrossBodies && altitude > Core.Curvature.SpectatorCeilingM)
                Position -= Vector3.Up * (float)(altitude - Core.Curvature.SpectatorCeilingM);
        }
    }
}
