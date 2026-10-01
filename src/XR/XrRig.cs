using Godot;
using UnitSport.Player;

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
public partial class XrRig : Node3D
{
    /// <summary>On foot, the right stick snaps the body round by this much.</summary>
    private const float SnapTurn = Mathf.Pi / 6f;
    /// <summary>Hold the right stick in this long to recentre on where the head is now.</summary>
    private const float RecentreHold = 0.8f;

    public Camera3D? Anchor { get; private set; }

    private XROrigin3D _origin = null!;
    private XRCamera3D _camera = null!;
    private XRController3D _left = null!, _right = null!;
    private XrPad _pad = null!;
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

    private bool _snapArmed = true;
    private float _recentreHeld;
    private bool _recentreDone;

    // skiing
    private Vector3 _prevLeft, _prevRight;
    private float _pole;
    private float _snowBuzz;

    public override void _Ready()
    {
        // after every game node has placed its camera this frame
        ProcessPriority = 1000;
        ProcessMode = ProcessModeEnum.Always;
        TopLevel = true;

        _origin = new XROrigin3D { Name = "Origin", Current = true };
        AddChild(_origin);
        _camera = new XRCamera3D { Name = "Head", Near = 0.05f, Far = Core.GameSettings.Current.CameraFar };
        _origin.AddChild(_camera);
        _left = new XRController3D { Name = "Left", Tracker = "left_hand", Pose = "aim" };
        _right = new XRController3D { Name = "Right", Tracker = "right_hand", Pose = "aim" };
        _origin.AddChild(_left);
        _origin.AddChild(_right);
        _left.AddChild(HandMarker());
        _right.AddChild(HandMarker());

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
        };
        _camera.AddChild(_vignette);

        _pad = new XrPad(_left, _right);
        _ui = new XrUi(_camera, _right);
        AddChild(_ui);

        _camera.MakeCurrent();
        Core.GameSettings.Changed += OnSettings;
    }

    public override void _ExitTree() => Core.GameSettings.Changed -= OnSettings;

    private void OnSettings() => _camera.Far = Core.GameSettings.Current.CameraFar;

    /// <summary>A small box in each hand, until the avatar's own hands are driven (#186 phase 2).</summary>
    private static MeshInstance3D HandMarker() => new()
    {
        Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.04f, 0.12f) },
        MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.75f, 0.62f) },
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var viewport = GetViewport();

        // someone made their camera current: that is now the view to carry, and ours goes back on
        if (viewport.GetCamera3D() is { } current && current != _camera)
        {
            Anchor = current;
            _camera.MakeCurrent();
        }
        if (Anchor != null && !IsInstanceValid(Anchor)) Anchor = null;

        var head = _camera.Transform;
        bool tracking = head.Origin != Vector3.Zero;
        _trackedFor = tracking ? _trackedFor + dt : 0f;
        // the first good head pose a moment after start is where you sit or stand: centre on it
        if (!_calibrated && _trackedFor > 0.5f) Recentre();

        var player = Anchor?.GetParent() as FootPlayer;
        UpdateAnchor(player);
        var calibrated = _calib * head;

        HandleSticks(player, calibrated, dt);
        _pad.Update(player, calibrated, uiActive: _ui.Pointing, dt);
        _ui.UpdatePanel(dt);
        UpdateSki(player, calibrated, dt);
        UpdateVignette(player, dt);

        // the player aims and is heard from where its head really is
        if (player != null && Anchor != null)
        {
            _lastHead = _camera.GlobalTransform.Orthonormalized();
            Anchor.GlobalTransform = _lastHead;
        }
    }

    /// <summary>Puts the tracking space so the calibrated head sits on the anchor.</summary>
    private void UpdateAnchor(FootPlayer? player)
    {
        if (Anchor == null) return;
        var at = Anchor.GlobalTransform.Orthonormalized();
        // A frame where the game did not place its camera again still holds the head we wrote
        // back: carrying on from that would walk the origin away by the head's offset each frame.
        if (player != null && at.IsEqualApprox(_lastHead)) at = _lastAnchor;
        _lastAnchor = at;

        // A player's camera is VR-aware: level on foot, the body's frame in a vehicle. Anything
        // else (the spectator, a filmed shot) is only followed in heading, never in pitch or roll.
        var basis = player != null ? at.Basis : YawOnly(at.Basis);
        _origin.GlobalTransform = new Transform3D(basis, at.Origin) * _calib;
    }

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

        // snap turn, on foot only: in a vehicle the head is the free look
        bool onFoot = player != null && player.Ride == RideKind.OnFoot && player.RidingWith == 0;
        if (onFoot && Mathf.Abs(stick.X) > 0.7f && _snapArmed && !_ui.Pointing)
        {
            player!.LookYaw -= Mathf.Sign(stick.X) * SnapTurn;
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
                // sitting in a cockpit gives the eye a frame that moves with it: half as much
                float frame = player != null && player.Ride != RideKind.OnFoot && player.IsFirstPerson ? 0.5f : 1f;
                target = (Mathf.Clamp((speed - 1.5f) / 14f, 0f, 0.55f) + Mathf.Clamp((turn - 0.4f) / 2.5f, 0f, 0.45f)) * frame;
            }
        }
        _vignetteLevel = Mathf.Lerp(_vignetteLevel, target, 1f - Mathf.Exp(-6f * dt));
        _vignette.Visible = _vignetteLevel > 0.02f;
        _vignetteMat.SetShaderParameter("strength", _vignetteLevel);
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
