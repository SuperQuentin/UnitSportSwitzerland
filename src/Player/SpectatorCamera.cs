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
    public float FlownSpeed => SpectatorSpeed.Flown(Speed, Cap);   // the wheel's top: full speed, the cap (#2026)

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
    /// A turning shift (#1430 H3, the earth world's re-anchors #1512, a body's handover #1850) keeps the view exactly
    /// (#1988): the shifter turned the camera rigidly with the world, and the angles kept here are read off it, roll
    /// included, so what is looked at stays where it is on screen. Before, the view was rebuilt level against the new
    /// anchor's horizon at each shift (#1800): 0.03° a re-anchor on the ground, 0.66° near the Moon in Space, and at the
    /// handover between the Moon and the Earth, whose ups are opposite there, a view looking down at the Moon was rebuilt
    /// looking down at the Earth. The view is kept level continuously instead (<see cref="Level"/>).
    /// </summary>
    public void OnOriginShifted(Core.OriginShift shift)
    {
        if (!shift.Turns) return;
        SyncLook();
    }

    /// <summary>The look's angles from the camera as it is (a tool, the spawn or a shift may have turned it): a look input turns that view, never a stale one.</summary>
    private void SyncLook()
    {
        var r = Basis.GetEuler();
        _pitch = r.X;
        _yaw = r.Y;
        _roll = r.Z;
    }
    private float _pitch;

    /// <summary>The roll left by a handover (#1988), eased out about the view's own axis by <see cref="Level"/>.</summary>
    private float _roll;

    /// <summary>The roll is eased out at least this fast, rad/s, and a third of what is left each tenth of a second.</summary>
    private const float RollRate = 0.5f, RollEase = 3f;

    /// <summary>A step of the local up larger than this is a handover's (another body's up), not the camera's travel, rad.</summary>
    private const double TravelTurnMax = 0.2;

    // the local up under the camera last frame, body-fixed (shifts never move it), and its body
    private Terrain.Format.EcefPos _upBefore;
    private Core.BodyId _upBody;
    private bool _hasUp;

    private static Terrain.Format.EcefPos Normalized(Terrain.Format.EcefPos p)
    {
        double l = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        return new(p.X / l, p.Y / l, p.Z / l);
    }

    /// <summary>
    /// Keeps the view level against the ground under it, continuously (#1988): each frame the camera is turned by the
    /// turn of the local up between last frame's place and this one (a few hundredths of a degree a frame flying low;
    /// nothing flying straight up or down, so a body looked at straight back or ahead stays put), and a roll (left by a
    /// handover, where the new body's up is not the old one's) is eased out about the view's own axis: the middle of the
    /// picture never moves. At a handover nothing is turned that frame: the anchor counts from the other body, the view
    /// stays exactly as it was.
    /// </summary>
    private void Level(float dt)
    {
        var origin = Core.OriginShifter.Instance?.Origin;
        var up = origin?.UpAt(Position) ?? Vector3.Up;
        if (origin?.Frame.Axes is { } axes)
        {
            // the ellipsoid's up under the camera, body-fixed, in doubles
            var shape = axes.Shape;
            var upEcef = new Terrain.Format.EnuFrame(shape.ToGeodetic(axes.ToEcef(Position.X, Position.Y, Position.Z)), shape).Up;
            if (_hasUp && _upBody == axes.Body)
            {
                // in doubles, body-fixed: a frame's turn is a few microradians on the ground (Godot's arc quaternion
                // gives none under ~0.3°), the axis from the two ups' cross product, turned into world axes
                var a = _upBefore;
                var b = Normalized(upEcef);
                double cx = a.Y * b.Z - a.Z * b.Y, cy = a.Z * b.X - a.X * b.Z, cz = a.X * b.Y - a.Y * b.X;
                double sin = Math.Sqrt(cx * cx + cy * cy + cz * cz), cos = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
                double turn = Math.Atan2(sin, cos);
                if (turn > 1e-9 && turn < TravelTurnMax)
                {
                    var k = new Terrain.Format.EcefPos(cx / sin, cy / sin, cz / sin);
                    var axis = new Vector3((float)Core.FrameAxes.Dot(k, axes.X), (float)Core.FrameAxes.Dot(k, axes.Y), (float)Core.FrameAxes.Dot(k, axes.Z));
                    Basis = (new Basis(axis.Normalized(), (float)turn) * Basis).Orthonormalized();
                    SyncLook();
                }
            }
            _upBefore = Normalized(upEcef);
            _upBody = axes.Body;
            _hasUp = true;
        }
        else _hasUp = false;

        // the roll against the local up: the right vector brought square to it, about the view's own axis
        var forward = -Basis.Z;
        var want = forward.Cross(up);
        if (want.LengthSquared() < 1e-8f) return;   // looking straight up or down: no horizon to level to
        want = want.Normalized();
        float roll = Basis.X.SignedAngleTo(want, forward);
        if (Mathf.Abs(roll) < 1e-5f) return;
        float step = Mathf.Max(RollRate, Mathf.Abs(roll) * RollEase) * dt;
        Basis = (new Basis(forward, Mathf.Clamp(roll, -step, step)) * Basis).Orthonormalized();
        SyncLook();
    }

    public override void _Ready()
    {
        Near = 1f;
        Far = Core.GameSettings.Current.CameraFar;
        Core.MouseCapture.Capture();
    }

    /// <summary>A mouse look by <paramref name="relative"/> pixels (also a check's, <c>--spectatorturncheck</c>).</summary>
    /// <summary>
    /// Turns the view toward a world direction by at most <paramref name="maxStep"/> radians (the target's align hold,
    /// #1850): yaw and pitch eased as a mouse look would move them, never a roll.
    /// </summary>
    public void TurnToward(Vector3 direction, float maxStep)
    {
        if (direction.LengthSquared() < 1e-12f) return;
        SyncLook();
        var d = direction.Normalized();
        float yaw = Mathf.Atan2(-d.X, -d.Z), pitch = Mathf.Asin(Mathf.Clamp(d.Y, -1f, 1f));
        float dy = Mathf.Wrap(yaw - _yaw, -Mathf.Pi, Mathf.Pi), dp = pitch - _pitch;
        _yaw += Mathf.Clamp(dy, -maxStep, maxStep);
        _pitch = Mathf.Clamp(_pitch + Mathf.Clamp(dp, -maxStep, maxStep), -Mathf.Pi / 2 + 0.01f, Mathf.Pi / 2 - 0.01f);
        Rotation = new Vector3(_pitch, _yaw, _roll);
    }

    public void Look(Vector2 relative)
    {
        SyncLook();
        _yaw -= relative.X * MouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - relative.Y * MouseSensitivity, -Mathf.Pi / 2 + 0.01f, Mathf.Pi / 2 - 0.01f);
        Rotation = new Vector3(_pitch, _yaw, _roll);
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
        _hud.Show(SpectatorSpeed.Flown(Speed, cap) * (_boosted ? BoostMultiplier : 1f), _boosted, limited: Speed > cap && Speed < SpectatorSpeed.MaxSpeed);
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
            Rotation = new Vector3(_pitch, _yaw, _roll);
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
        KeepOverFloor();
        Level(dt);
    }

    /// <summary>
    /// Over a body one cannot stand on (#1854: a giant, the Sun) the camera stays above its floor
    /// (<see cref="Core.CelestialBody.FloorM"/>), pushed back out along the vertical: no flying into Jupiter's gas or the
    /// Sun. Near a giant the anchor is the giant (its sphere of influence is millions of km), so its altitude is the one.
    /// </summary>
    private void KeepOverFloor()
    {
        if (Core.OriginShifter.Instance?.Origin is not { } origin) return;
        var body = Core.CelestialBody.Of(origin.Body);
        if (body.Landable) return;
        double altitude = origin.AltitudeOf(Position);
        if (altitude < body.FloorM) Position += origin.UpAt(Position) * (float)(body.FloorM - altitude);
    }
}
