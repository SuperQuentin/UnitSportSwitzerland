using Godot;
using UnitSport.Player;
using UnitSport.Core;

namespace UnitSport.XR;

/// <summary>
/// The headset and the two controllers, carried by whatever camera the game wants to look
/// through.
///
/// <para>
/// The game keeps placing its cameras exactly as it does flat: a player's first-person eye, a
/// cockpit eye on the car body, the spectator. Whenever one of them is made current this takes
/// it as the <see cref="Anchor"/> and makes its own XR camera current again. Every frame, after
/// the game has placed the anchor, the tracking space is put so that the head, at the pose it
/// had when last recentred, sits exactly on the anchor; moving your real head moves you from
/// there. In a vehicle the anchor carries the body's pitch and roll, so the cockpit stays put
/// around you; on foot it only yaws (the player's VR eyes are level, <see cref="FootPlayer"/>).
/// </para>
///
/// <para>
/// The head is then written back onto the player's own camera, so everything that aims along
/// <see cref="FootPlayer.Camera"/> (guns, the camera item, birds being looked at) aims where
/// you look.
/// </para>
/// </summary>
public partial class XrRig : Node3D, Core.IOriginShiftAware
{
    /// <summary>On foot, the right stick snaps the body round by this much.</summary>
    private const float SnapTurn = Mathf.Pi / 6f;
    /// <summary>Hold the right stick in this long to recentre on where the head is now.</summary>
    private const float RecentreHold = 0.8f;

    public Camera3D? Anchor { get; private set; }

    /// <summary>The headset camera's near plane (a doorway shortens it, Interiors/DoorPortals).</summary>
    public const float HeadNear = 0.05f;

    /// <summary>The headset camera and tracking origin, for the monitor's eye views.</summary>
    internal XRCamera3D Head => _camera;
    internal XROrigin3D Origin => _origin;

    private SubViewport _view = null!;
    private XrMonitor _monitor = null!;
    private XROrigin3D _origin = null!;
    private XRCamera3D _camera = null!;
    private XRController3D _left = null!, _right = null!;
    private XrPad _pad = null!;
    private XrWristMenu _wrist = null!;

    /// <summary>Following a camera in the world: not the title's backdrop, not before any camera.</summary>
    public bool InWorld => Anchor != null && !_anchorIsBackdrop;
    private XrHands _hands = null!;
    private XrUi _ui = null!;
    private MeshInstance3D _vignette = null!;
    private ShaderMaterial _vignetteMat = null!;

    /// <summary>Tracking space → anchor: the inverse of the (yaw-only) head pose at the last recentre.</summary>
    private Transform3D _calib = Transform3D.Identity;
    private bool _calibrated;
    private float _trackedFor;

    // the anchor as the game left it, and the head we wrote back over it, to tell them apart
    private Transform3D _lastAnchor = Transform3D.Identity, _lastHead = Transform3D.Identity;
    private Vector3 _prevAnchorPos;
    private float _prevAnchorYaw;
    private float _vignetteLevel;
    /// <summary>All black, fading back to clear: the cover for a cut (<see cref="Blink"/>).</summary>
    private float _blink;
    private MeshInstance3D _leftMarker = null!, _rightMarker = null!;
    /// <summary>The heading kept for an anchor that is not a player's, taken when it was adopted.</summary>
    private float _heldYaw;
    private bool _anchorIsBackdrop, _holdPending;

    private bool _snapArmed = true;
    private float _recentreHeld;
    private bool _recentreDone;

    // skiing
    private Vector3 _prevLeft, _prevRight;

    /// <summary>
    /// The origin moved (#185): where the rig was and where the hands were are somewhere else in
    /// world space, or the comfort vignette and the hand velocities read the shift as motion.
    /// </summary>
    public void OnOriginShifted(Core.OriginShift shift)
    {
        _lastAnchor = shift.Apply(_lastAnchor);
        _lastHead = shift.Apply(_lastHead);
        _prevAnchorPos = shift.Point(_prevAnchorPos);
        _prevLeft = shift.Point(_prevLeft);
        _prevRight = shift.Point(_prevRight);
    }
    private float _pole;
    private float _snowBuzz;

    public override void _Ready()
    {
        // after every game node has placed its camera this frame
        ProcessPriority = 1000;
        ProcessMode = ProcessModeEnum.Always;
        TopLevel = true;

        // The headset renders its own viewport (the documented way to keep the desktop window for
        // something else): the window keeps showing the game's camera, which XrMonitor turns into
        // the monitor view. Same World3D as the game: a SubViewport finds its parent's.
        _view = new SubViewport
        {
            Name = "Headset",
            UseXR = !XrSession.Simulated,
            RenderTargetUpdateMode = XrSession.Simulated ? SubViewport.UpdateMode.Disabled : SubViewport.UpdateMode.Always,
            GuiDisableInput = true,
            Size = new Vector2I(1832, 1920),   // the runtime overrides it with the eye size
        };
        AddChild(_view);
        _origin = new XROrigin3D { Name = "Origin", Current = true };
        _view.AddChild(_origin);
        _camera = new XRCamera3D
        {
            Name = "Head",
            Near = HeadNear,
            Far = Core.GameSettings.Current.CameraFar,
            // the player's own body is for the monitor: from inside the head it fills the view
            CullMask = 0xFFFFFu & ~XrSession.SpectatorOnlyLayer,
        };
        _origin.AddChild(_camera);
        _left = new XRController3D { Name = "Left", Tracker = "left_hand", Pose = "aim" };
        _right = new XRController3D { Name = "Right", Tracker = "right_hand", Pose = "aim" };
        _origin.AddChild(_left);
        _origin.AddChild(_right);
        _leftMarker = HandMarker();
        _rightMarker = HandMarker();
        _left.AddChild(_leftMarker);
        _right.AddChild(_rightMarker);

        _vignetteMat = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/xr_vignette.gdshader"),
            RenderPriority = (int)Material.RenderPriorityMax,   // over everything, the UI panel included
        };
        _vignette = new MeshInstance3D
        {
            Name = "Vignette",
            Mesh = new QuadMesh { Size = new Vector2(2f, 2f) },
            MaterialOverride = _vignetteMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // the shader draws it over the whole view; it must never be culled
            ExtraCullMargin = 16384f,
            Visible = false,
            Layers = XrSession.HeadsetOnlyLayer,
        };
        _camera.AddChild(_vignette);

        _pad = new XrPad(_left, _right);
        XrProfile.Watch();
        _hands = new XrHands(_left, _leftMarker, _right, _rightMarker);
        _ui = new XrUi(_camera, _right);
        AddChild(_ui);
        _wrist = new XrWristMenu(this);
        AddChild(_wrist);
        Notice = new XrNotice();
        AddChild(Notice);
        if (XrSession.Simulated) Notice.CallDeferred(XrNotice.MethodName.Show, "VR", "simulated");
        _monitor = new XrMonitor(this) { Name = "Monitor" };
        AddChild(_monitor);

        _camera.Current = true;

        // "--xrheadshot <png> [seconds]": what the headset camera itself renders, saved to a file
        // (with --xrsim the headset viewport is otherwise not drawn); for checks of the panel
        if (CmdArgs.Value("--xrheadshot") is { } path)
        {
            double wait = CmdArgs.Double("--xrheadshot", 2) ?? 2.0;
            if (XrSession.Simulated)
            {
                _view.Size = new Vector2I(1152, 648);
                _view.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            }
            GetTree().CreateTimer(wait).Timeout += () =>
            {
                var err = _view.GetTexture().GetImage().SavePng(path);
                GD.Print($"[xr] headset shot {(err == Error.Ok ? "saved" : "FAILED")}: {path}");
            };
        }
        Core.GameSettings.Changed += OnSettings;
        ApplyQuality();
    }

    public override void _ExitTree()
    {
        Core.GameSettings.Changed -= OnSettings;
        XrProfile.Unwatch();
    }

    private void OnSettings()
    {
        _camera.Far = Core.GameSettings.Current.CameraFar;
        ApplyQuality();
    }

    /// <summary>
    /// The headset's picture, set on its own viewport: the project's render settings are the
    /// window's. Over Link the picture is video-encoded, so what matters most is that it holds
    /// still: MSAA (TAA ghosts and FXAA crawls as the head moves), no debanding noise, and
    /// foveated shading for the frame time (docs/notes/xr/air-link.md).
    /// </summary>
    private void ApplyQuality()
    {
        var s = Core.GameSettings.Current;
        _view.Msaa3D = s.VrMsaa switch
        {
            >= 8 => Viewport.Msaa.Msaa8X,
            >= 4 => Viewport.Msaa.Msaa4X,
            >= 2 => Viewport.Msaa.Msaa2X,
            _ => Viewport.Msaa.Disabled,
        };
        _view.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
        _view.UseTaa = false;
        _view.UseDebanding = false;
        _view.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
        _view.Scaling3DScale = s.VrRenderScale;
        // only on GPUs with variable rate shading; ignored elsewhere
        _view.VrsMode = s.VrFoveation ? Viewport.VrsModeEnum.XR : Viewport.VrsModeEnum.Disabled;
    }

    /// <summary>
    /// A small controller in each hand, until the avatar's own hands are driven (#186 phase 2): the
    /// menus' dark glass, with an amber tip where the pointer leaves it.
    /// </summary>
    private static MeshInstance3D HandMarker()
    {
        var body = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.045f, 0.035f, 0.11f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Ui.UiTheme.Glass with { A = 1f }, Roughness = 0.35f },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        body.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.047f, 0.037f, 0.008f) },
            Position = new Vector3(0, 0, -0.055f),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = Ui.UiTheme.Amber,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return body;
    }

    /// <summary>The one-line notices (recentred, monitor view), on the monitor and the panel.</summary>
    internal XrNotice Notice { get; private set; } = null!;

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        // the window's camera is the game's: the player's eye, a cockpit, the spectator, a shot
        var current = GetViewport().GetCamera3D();
        if (current != Anchor && current != null)
        {
            Anchor = current;
            _heldYaw = YawOf(current.GlobalBasis);
            _anchorIsBackdrop = false;
            for (var n = current.GetParent(); n != null; n = n.GetParent())
                if (n is Ui.TitleDiorama) _anchorIsBackdrop = true;
            // held from where it is on the next frame: when it turns current it may not be placed yet
            _holdPending = _anchorIsBackdrop;
            // the window's copy of the game camera never shows the headset-only parts
            // (with --xrsim the window is the only view, so it keeps them)
            if (!XrSession.Simulated) Anchor.CullMask &= ~XrSession.HeadsetOnlyLayer;
            Anchor.CullMask &= ~XrSession.SpectatorOnlyLayer;
        }
        if (Anchor != null && !IsInstanceValid(Anchor)) Anchor = null;

        UpdateTonemap();

        // a controller that is not tracked sits at the origin, inside the head: not drawn then
        _leftMarker.Visible = _left.GetHasTrackingData();
        _rightMarker.Visible = _right.GetHasTrackingData();

        var head = _camera.Transform;
        bool tracking = head.Origin != Vector3.Zero;
        _trackedFor = tracking ? _trackedFor + dt : 0f;
        // the first good head pose a moment after start is where you sit or stand: centre on it
        if (!_calibrated && _trackedFor > 0.5f) Recentre();

        var player = Anchor?.GetParent() as FootPlayer;
        UpdateAnchor(player);
        var calibrated = _calib * head;

        HandleSticks(player, calibrated, dt);
        // the hands first: a grip that holds the wheel or works a door is not a shoulder press
        _hands.Update(player, _camera.GlobalTransform, dt);
        _pad.LeftGripBusy = _hands.LeftBusy;
        _pad.RightGripBusy = _hands.RightBusy;
        _pad.Update(player, calibrated, uiActive: _ui.Pointing, dt);
        _wrist.Watch(_camera.GlobalTransform, _left, player, InWorld, dt);
        UpdateHandAim();
        _ui.UpdatePanel(dt);
        UpdateSki(player, calibrated, dt);
        UpdateVignette(player, dt);

        // the player aims and is heard from where its head really is
        if (player != null && Anchor != null)
        {
            _lastHead = _camera.GlobalTransform.Orthonormalized();
            Anchor.GlobalTransform = _lastHead;
        }
        // --xrsim shows the window, not a headset: on the title, show it from the held head too,
        // or the backdrop drifts away from the panel the headset would be looking at
        else if (XrSession.Simulated && _anchorIsBackdrop && Anchor != null)
            Anchor.GlobalTransform = _camera.GlobalTransform.Orthonormalized();
    }

    private Vector3 _aimZero;

    /// <summary>The right hand across the view since it was zeroed, as a stick (<see cref="XrSession.HandAim"/>).</summary>
    private void UpdateHandAim()
    {
        var local = _camera.GlobalTransform.AffineInverse() * _right.GlobalPosition;
        if (XrSession.HandAimZeroAsked)
        {
            XrSession.HandAimZeroAsked = false;
            _aimZero = local;
        }
        var d = local - _aimZero;
        XrSession.HandAim = new Vector2(d.X, -d.Y) / 0.15f;
    }

    /// <summary>Puts the tracking space so the calibrated head sits on the anchor.</summary>
    private void UpdateAnchor(FootPlayer? player)
    {
        if (Anchor == null) return;
        var at = Anchor.GlobalTransform.Orthonormalized();
        // A frame where the game did not place its camera again still holds the head we wrote
        // back: carrying on from that would walk the origin away by the head's offset each frame.
        if (player != null && at.IsEqualApprox(_lastHead)) at = _lastAnchor;

        // A player's camera is VR-aware: level on foot, the body's frame in a vehicle. Anything
        // else (the title's turning backdrop, the spectator, a filmed shot) is followed in position
        // only: a camera that orbits or pans on its own would spin the world round the player.
        var basis = player != null ? at.Basis : new Basis(Vector3.Up, _heldYaw);
        // the title's backdrop camera drifts round the valley on its own: in VR you stand still
        // where it started instead of being carried
        if (_anchorIsBackdrop && !_holdPending) at.Origin = _lastAnchor.Origin;
        if (_holdPending)
        {
            _holdPending = false;
            _heldYaw = YawOf(at.Basis);
            basis = new Basis(Vector3.Up, _heldYaw);
        }
        _lastAnchor = new Transform3D(basis, at.Origin);
        _origin.GlobalTransform = _lastAnchor * _calib;
    }

    private const float BlinkSeconds = 0.4f;

    /// <summary>
    /// Goes black at once and fades back in: what hides a cut in a headset. A view that jumps
    /// in plain sight is a lurch; one that jumps behind a blink reads as a teleport.
    /// </summary>
    public void Blink() => _blink = 1f;

    /// <summary>Takes where the head is now as the neutral pose: straight ahead, at the avatar's eye.</summary>
    public void Recentre()
    {
        var head = _camera.Transform;
        _calib = new Transform3D(YawOnly(head.Basis), head.Origin).AffineInverse();
        _calibrated = true;
        GD.Print($"[xr] recentred at head height {head.Origin.Y:F2} m");
    }

    private void HandleSticks(FootPlayer? player, Transform3D calibrated, float dt)
    {
        var stick = _right.GetVector2("primary");

        // snap turn, on foot only: in a vehicle the head is the free look; a pigeon (#217) snaps
        // while perched or walking (PigeonSnap says no in the air)
        bool onFoot = player != null && player.Ride == RideKind.OnFoot && player.RidingWith == 0;
        if ((onFoot || player?.Ride == RideKind.Pigeon) && Mathf.Abs(stick.X) > 0.7f && _snapArmed && !_ui.Pointing)
        {
            float turn = -Mathf.Sign(stick.X) * SnapTurn;
            if (onFoot) player!.LookYaw += turn;
            else player!.PigeonSnap(turn);
            _snapArmed = false;
        }
        else if (Mathf.Abs(stick.X) < 0.35f) _snapArmed = true;

        // hold the right stick in: recentre (a tap is still R3, passed through by the pad)
        if (_right.IsButtonPressed("primary_click"))
        {
            _recentreHeld += dt;
            if (_recentreHeld > RecentreHold && !_recentreDone)
            {
                Recentre();
                Notice.Show("Recentred");
                Rumble(0.4f, 0.1f, both: true);
                _recentreDone = true;
            }
        }
        else
        {
            _recentreHeld = 0f;
            _recentreDone = false;
        }
        _pad.RecentreHeld = _recentreHeld > 0.25f;
    }

    /// <summary>
    /// Skiing with the body (#186): lean the head to carve, crouch to tuck, stab a pole low and
    /// pull it back to push. The sticks and triggers still work alongside.
    /// </summary>
    private void UpdateSki(FootPlayer? player, Transform3D calibrated, float dt)
    {
        var leftPos = _calib * _left.Transform.Origin;
        var rightPos = _calib * _right.Transform.Origin;
        var leftVel = (leftPos - _prevLeft) / Mathf.Max(dt, 1e-3f);
        var rightVel = (rightPos - _prevRight) / Mathf.Max(dt, 1e-3f);
        _prevLeft = leftPos;
        _prevRight = rightPos;

        if (player == null || player.Ride != RideKind.Skis || player.RidingWith != 0 || !_calibrated)
        {
            XrSession.SkiSteer = 0f;
            XrSession.SkiPole = 0f;
            XrSession.SkiTuck = false;
            _pole = 0f;
            return;
        }

        // calibrated space: the head was at the origin, looking down −Z
        var headAt = calibrated.Origin;
        XrSession.SkiSteer = Mathf.Clamp(headAt.X / 0.16f, -1f, 1f);
        XrSession.SkiTuck = headAt.Y < -0.28f;

        // a push: the hand well below the head, sweeping backwards fast
        float push = 0f;
        foreach (var (pos, vel, ctl) in new[] { (leftPos, leftVel, _left), (rightPos, rightVel, _right) })
        {
            if (pos.Y > headAt.Y - 0.55f || vel.Z < 1.1f) continue;
            float p = Mathf.Clamp((vel.Z - 1.1f) / 1.6f, 0.25f, 1f);
            if (p > push) push = p;
            if (_pole < 0.2f) ctl.TriggerHapticPulse("haptic", 0.0, 0.35, 0.05, 0.0);
        }
        // each stab carries on a moment, as the glide after a real push does
        _pole = Mathf.Max(push, _pole - dt * 1.8f);
        XrSession.SkiPole = _pole;

        // the snow under the edges, quicker and harder with speed
        _snowBuzz -= dt;
        if (player.IsOnFloor() && player.RideSpeed > 3f && _snowBuzz <= 0f && Core.GameSettings.Current.Vibration)
        {
            float a = Mathf.Clamp(player.RideSpeed / 30f, 0.05f, 0.35f);
            _left.TriggerHapticPulse("haptic", 0.0, a, 0.04, 0.0);
            _right.TriggerHapticPulse("haptic", 0.0, a, 0.04, 0.0);
            _snowBuzz = 0.11f;
        }
    }

    /// <summary>Narrows the view while the world moves or turns under you and you do not.</summary>
    private void UpdateVignette(FootPlayer? player, float dt)
    {
        float target = 0f;
        if (Anchor != null)
        {
            var at = _lastAnchor;
            float speed = (at.Origin - _prevAnchorPos).Length() / Mathf.Max(dt, 1e-3f);
            float yaw = YawOf(at.Basis);
            float turn = Mathf.Abs(Mathf.AngleDifference(_prevAnchorYaw, yaw)) / Mathf.Max(dt, 1e-3f);
            _prevAnchorPos = at.Origin;
            _prevAnchorYaw = yaw;
            // a teleport or a respawn is not motion
            if (speed < 200f)
            {
                // sitting in a cockpit gives the eye a frame that moves with it: half as much (a pigeon has none)
                float frame = player != null && player.Ride is not (RideKind.OnFoot or RideKind.Pigeon) && player.IsFirstPerson ? 0.5f : 1f;
                target = (Mathf.Clamp((speed - 1.5f) / 14f, 0f, 0.55f) + Mathf.Clamp((turn - 0.4f) / 2.5f, 0f, 0.45f)) * frame;
            }
        }
        _vignetteLevel = Mathf.Lerp(_vignetteLevel, target, MathX.Damp(6f, dt));
        _blink = Mathf.Max(0f, _blink - dt / BlinkSeconds);
        _vignette.Visible = _vignetteLevel > 0.02f || _blink > 0f;
        _vignetteMat.SetShaderParameter("strength", _vignetteLevel);
        // eased out: black long enough to hide the jump, then the world comes back quickly
        _vignetteMat.SetShaderParameter("blackout", _blink * _blink);
    }

    private Godot.Environment? _sceneEnv, _linearEnv;

    /// <summary>
    /// The headset sees the scene's environment with a linear tonemap. The UI panel is a 3D surface,
    /// and the Mobile renderer clamps before the tonemap, so under Filmic (the title's backdrop) its
    /// white could never be more than ~63 % grey. The game world is linear already; this only
    /// changes anything where a scene asks for another tonemapper.
    /// </summary>
    private void UpdateTonemap()
    {
        var env = _camera.GetWorld3D()?.Environment;
        if (env == null || env.TonemapMode == Godot.Environment.ToneMapper.Linear)
        {
            _camera.Environment = null;
            return;
        }
        if (env != _sceneEnv)
        {
            _sceneEnv = env;
            _linearEnv = (Godot.Environment)env.Duplicate();
            _linearEnv.TonemapMode = Godot.Environment.ToneMapper.Linear;
        }
        _camera.Environment = _linearEnv;
    }

    /// <summary>One pulse on one or both hands; strength 0..1.</summary>
    public void Rumble(float strength, float seconds, bool both)
    {
        if (!Core.GameSettings.Current.Vibration || strength <= 0f) return;
        double a = Mathf.Clamp(strength, 0f, 1f);
        _right.TriggerHapticPulse("haptic", 0.0, a, seconds, 0.0);
        if (both) _left.TriggerHapticPulse("haptic", 0.0, a, seconds, 0.0);
    }

    internal static float YawOf(Basis b)
    {
        var f = -b.Z;
        return Mathf.Atan2(-f.X, -f.Z);
    }

    internal static Basis YawOnly(Basis b) => new(Vector3.Up, YawOf(b));
}
