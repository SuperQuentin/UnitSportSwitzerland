using Godot;
using UnitSport.Core;

namespace UnitSport.Movie;

/// <summary>
/// The studio's view (#638, #669), in one of three modes:
/// <list type="bullet">
/// <item><b>Orbit</b>: circles the selected actor, so scrubbing never loses them. Right-drag (or
/// the right stick) orbits, the wheel (or LB / RB) zooms. Tab or picking an actor comes back here.</item>
/// <item><b>Free</b>: a camera of its own, tied to nobody, where the studio opens. <see cref="Flying"/>
/// (F, L3) captures the mouse and flies it as the game's fly camera does: W A S D / left stick,
/// Space E / A up, Shift Q / B down, Ctrl boost, the wheel or LB / RB its speed. Without flying,
/// hold the right mouse button on the view with W A S D, Q E. Its place is kept in LV95, so an
/// origin shift never moves it.</item>
/// <item><b>Track</b>: shows the camera track's pose (<see cref="Show"/>); any move takes the view
/// over in Free mode from there, to set a key where it ends up.</item>
/// </list>
/// The <see cref="Lens"/> is in millimetres, as the camera track keeps it.
/// </summary>
public partial class StudioCamera : Camera3D
{
    public enum ViewMode { Orbit, Free, Track }

    private static readonly StringName Left = PlayerInput.LookLeft, Right = PlayerInput.LookRight,
        Up = PlayerInput.LookUp, Down = PlayerInput.LookDown, Closer = PlayerInput.MapZoomIn, Further = PlayerInput.MapZoomOut,
        MoveL = PlayerInput.MoveLeft, MoveR = PlayerInput.MoveRight, MoveF = PlayerInput.MoveForward, MoveB = PlayerInput.MoveBack,
        Rise = PlayerInput.FlyUp, Sink = PlayerInput.FlyDown, Boost = PlayerInput.FlyBoost;

    /// <summary>Flying with the mouse captured (#669): the studio hands it every key and button but its own few.</summary>
    public bool Flying { get; private set; }
    public event Action? FlyingChanged;
    private float _flySpeed = 15f;

    private readonly WorldOrigin _origin;
    private float _yaw = 0.6f, _pitch = -0.3f, _distance = 9f;
    private Vector3 _focus;
    private bool _hasFocus;
    private GlobalPos _freeAt;
    private float _lens = 24;

    /// <summary>Where to look in Orbit: the selected actor's world position, or null to stay where it is.</summary>
    public Func<Vector3?>? Target { get; set; }
    /// <summary>The timeline has the pad: LB / RB zoom it, not the view.</summary>
    public Func<bool>? PadBusy { get; set; }
    /// <summary>The right mouse button went down on the view (not on a panel): it may fly.</summary>
    public bool Steering { get; set; }

    public ViewMode Mode { get; private set; } = ViewMode.Orbit;
    /// <summary>The view left Track by being moved: the studio turns its "look through" off.</summary>
    public event Action? TookOver;

    public float Lens
    {
        get => _lens;
        set { _lens = Mathf.Clamp(value, CameraTrack.MinLens, CameraTrack.MaxLens); Fov = CameraTrack.Fov(_lens); }
    }

    public StudioCamera(WorldOrigin origin)
    {
        _origin = origin;
        Name = "StudioCamera";
        Near = 0.1f;
        Far = GameSettings.Current.CameraFar;
        Lens = 24;
    }

    /// <summary>Right-drag: orbits in Orbit, looks round in Free (a Track view becomes Free first).</summary>
    public void Turn(Vector2 by)
    {
        if (Mode == ViewMode.Track) TakeOver();
        _yaw -= by.X;
        _pitch = Mode == ViewMode.Free ? Mathf.Clamp(_pitch - by.Y, -1.5f, 1.5f) : Mathf.Clamp(_pitch - by.Y, -1.45f, 1.2f);
    }

    /// <summary>The wheel: closer or further in Orbit, forward or back in Free.</summary>
    public void Zoom(float factor)
    {
        if (Mode == ViewMode.Orbit) { _distance = Mathf.Clamp(_distance * factor, 1.5f, 3000f); return; }
        if (Mode == ViewMode.Track) TakeOver();
        Fly(-GlobalTransform.Basis.Z * (1 - factor) * 40f);
    }

    /// <summary>Starts or stops flying: the mouse captured to look, or the cursor back for the timeline.</summary>
    public void SetFlying(bool on)
    {
        if (on == Flying) return;
        Flying = on;
        if (on)
        {
            if (Mode != ViewMode.Free) TakeOver();
            MouseCapture.Capture();   // never in a probe run (--nocapture): it flies with the cursor showing
        }
        else Input.MouseMode = Input.MouseModeEnum.Visible;
        FlyingChanged?.Invoke();
    }

    /// <summary>
    /// A free camera placed <paramref name="distance"/> metres behind and above <paramref name="target"/>,
    /// looking at it: where the studio opens, tied to nothing afterwards.
    /// </summary>
    public void PlaceNear(Vector3 target, float distance)
    {
        var eye = target + new Vector3(0, 1.2f, 0);
        var offset = new Basis(Vector3.Up, _yaw) * new Basis(Vector3.Right, -0.3f) * new Vector3(0, 0, distance);
        var look = Basis.LookingAt(-offset, Vector3.Up).GetEuler();
        _pitch = look.X;
        _yaw = look.Y;
        _freeAt = _origin.ToGlobal(eye + offset);
        Mode = ViewMode.Free;
    }

    public override void _Input(InputEvent e)
    {
        if (!Flying) return;
        switch (e)
        {
            case InputEventMouseMotion m:
                Turn(m.Relative * 0.0025f);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _flySpeed = Mathf.Min(_flySpeed * 1.25f, 600f);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _flySpeed = Mathf.Max(_flySpeed / 1.25f, 1f);
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    /// <summary>Back to orbiting the target, <paramref name="distance"/> metres off: a new selection.</summary>
    public void Snap(float distance)
    {
        Mode = ViewMode.Orbit;
        _hasFocus = false;
        _distance = distance;
        _pitch = Mathf.Clamp(_pitch, -1.45f, 1.2f);
    }

    /// <summary>Draws the camera track's pose: the view is the movie's camera until something moves it.</summary>
    public void Show(Vector3 at, Basis basis, float lens)
    {
        Mode = ViewMode.Track;
        GlobalTransform = new Transform3D(basis.Orthonormalized(), at);
        Lens = lens;
    }

    /// <summary>
    /// Draws the camera track's <paramref name="pose"/>: placed in this world, turned as keyed, or aimed
    /// at the head of the actor it names (<paramref name="actorAt"/> gives a lane's puppet position).
    /// </summary>
    public void ShowPose(in CameraPose pose, Func<int, Vector3?> actorAt)
    {
        var xf = PoseTransform(_origin, pose, actorAt);
        Show(xf.Origin, xf.Basis, pose.Lens);
    }

    /// <summary>
    /// Where a camera track's pose is in this world and which way it looks: as keyed, or at the head
    /// of the actor it aims at. Shared by the view and the cameras' gizmos (#675).
    /// </summary>
    public static Transform3D PoseTransform(WorldOrigin origin, in CameraPose pose, Func<int, Vector3?> actorAt)
    {
        var at = origin.ToWorld(new GlobalPos(pose.E, pose.N, pose.Alt));
        var basis = new Basis(new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw).Normalized());
        if (pose.LookAt >= 0 && actorAt(pose.LookAt) is { } target)
        {
            var to = target + new Vector3(0, 1.2f, 0) - at;
            if (to.LengthSquared() > 0.01f) basis = Basis.LookingAt(to, Vector3.Up);
        }
        return new Transform3D(basis, at);
    }

    /// <summary>Leaves Track (or Orbit) for Free where the view is now.</summary>
    private void TakeOver()
    {
        bool wasTrack = Mode == ViewMode.Track;
        var euler = GlobalTransform.Basis.GetEuler();
        _pitch = euler.X;
        _yaw = euler.Y;
        _freeAt = _origin.ToGlobal(GlobalPosition);
        Mode = ViewMode.Free;
        if (wasTrack) TookOver?.Invoke();
    }

    private void Fly(Vector3 by)
    {
        if (Mode != ViewMode.Free) TakeOver();
        var at = _origin.ToWorld(_freeAt) + by;
        _freeAt = _origin.ToGlobal(at);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        bool padFree = PadBusy?.Invoke() != true && !Flying;
        if (Flying)
        {
            // the game's fly camera's controls, keyboard and pad alike, through the input map
            var stickMove = Input.GetVector(MoveL, MoveR, MoveF, MoveB);
            float vertical = Input.GetActionStrength(Rise) - Input.GetActionStrength(Sink);
            if (Input.IsActionPressed(Closer)) _flySpeed = Mathf.Min(_flySpeed * Mathf.Exp(1.2f * dt), 600f);
            if (Input.IsActionPressed(Further)) _flySpeed = Mathf.Max(_flySpeed * Mathf.Exp(-1.2f * dt), 1f);
            var dir = GlobalTransform.Basis * new Vector3(stickMove.X, 0, stickMove.Y) + Vector3.Up * vertical;
            if (dir.LengthSquared() > 1e-4f)
            {
                float amount = Mathf.Min(dir.Length(), 1f);
                float speed = _flySpeed * amount * (Input.IsActionPressed(Boost) ? 5f : 1f);
                Fly(dir.Normalized() * speed * dt);
            }
        }
        // a pad (and VR, whose controllers play a pad on another device): the right stick turns, the
        // shoulders zoom or dolly. Through the input map, never a device's raw axes (vr-action-map, finding 1).
        var stick = Input.GetVector(Left, Right, Up, Down);
        if (stick.Length() > 0.2f) Turn(stick * 2.2f * dt);
        if (padFree && Input.IsActionPressed(Closer)) Zoom(Mathf.Exp(-1.5f * dt));
        if (padFree && Input.IsActionPressed(Further)) Zoom(Mathf.Exp(1.5f * dt));

        // the right mouse button held on the view: W A S D, Q E fly, as in an editor's viewport. The keys
        // are read raw: the studio holds the movement actions for its menus.
        if (Steering && !Input.IsMouseButtonPressed(MouseButton.Right)) Steering = false;
        if (Steering && !UiFocus.TextEntryActive)
        {
            var move = new Vector3(
                Key(Godot.Key.D) - Key(Godot.Key.A),
                Key(Godot.Key.E) - Key(Godot.Key.Q),
                Key(Godot.Key.S) - Key(Godot.Key.W));
            if (move != Vector3.Zero)
            {
                if (Mode != ViewMode.Free) TakeOver();
                float speed = Input.IsPhysicalKeyPressed(Godot.Key.Shift) ? 60f : 10f;
                var b = GlobalTransform.Basis;
                Fly((b.X * move.X + Vector3.Up * move.Y + b.Z * move.Z) * speed * dt);
            }
        }

        switch (Mode)
        {
            case ViewMode.Orbit:
                if (Target?.Invoke() is { } target)
                {
                    // eased, so a plane's bank and a rider's bob do not shake the whole view; snapped when far
                    if (!_hasFocus || _focus.DistanceTo(target) > 200f) _focus = target;
                    else _focus = _focus.Lerp(target, 1f - Mathf.Exp(-12f * dt));
                    _hasFocus = true;
                }
                var offset = new Basis(Vector3.Up, _yaw) * new Basis(Vector3.Right, _pitch) * new Vector3(0, 0, _distance);
                var eye = _focus + new Vector3(0, 1.2f, 0);
                GlobalTransform = new Transform3D(Basis.LookingAt(-offset, Vector3.Up), eye + offset);
                break;
            case ViewMode.Free:
                GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(_pitch, _yaw, 0)), _origin.ToWorld(_freeAt));
                break;
        }
    }

    private static float Key(Key k) => Input.IsPhysicalKeyPressed(k) ? 1f : 0f;
}
