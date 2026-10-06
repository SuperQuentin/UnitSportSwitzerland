using Godot;
using UnitSport.Core;

namespace UnitSport.Movie;

/// <summary>
/// The studio's working view (#638): orbits whichever actor is selected, so scrubbing never loses
/// them. Right-drag (or the right stick) orbits, the wheel (or LB / RB) zooms. The camera that
/// films comes with the virtual cameras (#637, milestone 2); this one is for editing.
/// </summary>
public partial class StudioCamera : Camera3D
{
    private float _yaw = 0.6f, _pitch = -0.3f, _distance = 9f;
    private Vector3 _focus;
    private bool _hasFocus;

    /// <summary>Where to look: the selected actor's world position, or null to stay where it is.</summary>
    public Func<Vector3?>? Target { get; set; }

    public StudioCamera()
    {
        Name = "StudioCamera";
        Near = 0.1f;
        Far = GameSettings.Current.CameraFar;
        Fov = 60f;
    }

    public void Orbit(Vector2 by)
    {
        _yaw -= by.X;
        _pitch = Mathf.Clamp(_pitch - by.Y, -1.45f, 1.2f);
    }

    public void Zoom(float factor) => _distance = Mathf.Clamp(_distance * factor, 1.5f, 3000f);

    /// <summary>Jumps to the target instead of easing there, <paramref name="distance"/> metres off: a new selection.</summary>
    public void Snap(float distance)
    {
        _hasFocus = false;
        _distance = distance;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        // a pad (and VR, whose controllers play a pad): the right stick orbits, the shoulders zoom
        var stick = new Vector2(Input.GetJoyAxis(0, JoyAxis.RightX), Input.GetJoyAxis(0, JoyAxis.RightY));
        if (stick.Length() > 0.2f) Orbit(stick * 2.2f * dt);
        if (Input.IsJoyButtonPressed(0, JoyButton.RightShoulder)) Zoom(Mathf.Exp(-1.5f * dt));
        if (Input.IsJoyButtonPressed(0, JoyButton.LeftShoulder)) Zoom(Mathf.Exp(1.5f * dt));

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
    }
}
