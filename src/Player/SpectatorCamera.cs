using Godot;

namespace UnitSport.Player;

/// <summary>
/// Free fly camera: WASD + QE (physical keys, layout-independent), mouse look, Shift for
/// boost, mouse wheel to change speed. Click to take mouse capture back after a menu.
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
        Far = 20000f;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (UnitSport.Core.UiFocus.TextEntryActive) return;

        switch (@event)
        {
            case InputEventMouseMotion motion when Input.MouseMode == Input.MouseModeEnum.Captured:
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
                    Input.MouseMode = Input.MouseModeEnum.Captured;
                break;

        }
        // Esc belongs to the mode menu (ClientWorld); clicking in the viewport is what
        // takes mouse capture back, handled by the button case above.
    }

    public override void _Process(double delta)
    {
        // physical keys are read directly, so a focused text field must be checked here too
        if (UnitSport.Core.UiFocus.TextEntryActive) return;

        var dir = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) dir -= Basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.S)) dir += Basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.A)) dir -= Basis.X;
        if (Input.IsPhysicalKeyPressed(Key.D)) dir += Basis.X;
        if (Input.IsPhysicalKeyPressed(Key.E)) dir += Vector3.Up;
        if (Input.IsPhysicalKeyPressed(Key.Q)) dir -= Vector3.Up;

        if (dir != Vector3.Zero)
        {
            float speed = Speed * (Input.IsPhysicalKeyPressed(Key.Shift) ? BoostMultiplier : 1f);
            Position += dir.Normalized() * speed * (float)delta;
        }
    }
}
