using Godot;

namespace UnitSport.Player;

/// <summary>
/// Free fly camera: WASD / left stick, Space / E / RT up, Shift / Q / LT down, mouse or right
/// stick look, Ctrl / L3 for boost, mouse wheel to change speed. Click to take mouse capture back
/// after a menu.
/// </summary>
public partial class SpectatorCamera : Camera3D
{
    [Export] public float Speed { get; set; } = 150f;
    [Export] public float BoostMultiplier { get; set; } = 6f;
    [Export] public float MouseSensitivity { get; set; } = 0.0025f;

    private float _yaw;
    private float _pitch;

    public override void _Ready()
    {
        Near = 1f;
        Far = Core.GameSettings.Current.CameraFar;
        Core.MouseCapture.Capture();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (UnitSport.Core.UiFocus.TextEntryActive) return;

        switch (@event)
        {
            case InputEventMouseMotion motion when Core.PlayerInput.IsLookMotion(motion):
                _yaw -= motion.Relative.X * MouseSensitivity;
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity,
                    -Mathf.Pi / 2 + 0.01f, Mathf.Pi / 2 - 0.01f);
                Rotation = new Vector3(_pitch, _yaw, 0);
                break;

            case InputEventMouseButton { Pressed: true } button:
                if (button.ButtonIndex == MouseButton.WheelUp)
                    Speed = Mathf.Min(Speed * 1.25f, 3000f);
                else if (button.ButtonIndex == MouseButton.WheelDown)
                    Speed = Mathf.Max(Speed / 1.25f, 2f);
                else if (Input.MouseMode == Input.MouseModeEnum.Visible)
                    Core.MouseCapture.Capture();
                break;

        }
        // Esc belongs to the mode menu (ClientWorld); clicking in the viewport is what
        // takes mouse capture back, handled by the button case above.
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // right stick: a rate, integrated here; the mouse arrives as events above
        var look = UnitSport.Core.PlayerInput.LookRate;
        if (look != Vector2.Zero)
        {
            _yaw -= look.X * dt;
            _pitch = Mathf.Clamp(_pitch - look.Y * dt, -Mathf.Pi / 2 + 0.01f, Mathf.Pi / 2 - 0.01f);
            Rotation = new Vector3(_pitch, _yaw, 0);
        }

        // PlayerInput returns neutral while a text field has the keyboard, so typing in chat
        // does not fly the camera. Space/Shift match the on-foot and mounted controls, so the
        // vertical axis is on the same keys whatever you are; Q/E stay for the other hand, and
        // the triggers do it on a pad.
        var stick = UnitSport.Core.PlayerInput.Move;
        var dir = Basis * new Vector3(stick.X, 0, stick.Y);
        dir += Vector3.Up * (UnitSport.Core.PlayerInput.Strength(UnitSport.Core.PlayerInput.FlyUp)
            - UnitSport.Core.PlayerInput.Strength(UnitSport.Core.PlayerInput.FlyDown));

        if (dir.LengthSquared() > 1e-6f)
        {
            // an analog stick asks for part of the speed; a key asks for all of it
            float amount = Mathf.Min(dir.Length(), 1f);
            float speed = Speed * amount
                * (UnitSport.Core.PlayerInput.Held(UnitSport.Core.PlayerInput.FlyBoost) ? BoostMultiplier : 1f);
            Position += dir.Normalized() * speed * dt;
        }
    }
}
