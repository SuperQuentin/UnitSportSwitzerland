namespace UnitSport.Core;

/// <summary>
/// Starting bindings for wheels we know, picked by SDL device name. Anything else starts from the
/// generic layout (steering on axis 0, nothing else) and is set up in Settings → Steering wheel.
///
/// <para>
/// Pedal rest ends are only a first guess: <see cref="SteeringWheel"/> flips From/To when an axis's
/// initial state says it rests at the other end, so a preset never reads a released pedal as floored.
/// </para>
/// </summary>
public static class WheelPresets
{
    public sealed record Preset(string Name, Func<string, bool> Matches, Action<WheelSettings> Apply);

    public static readonly Preset[] All =
    {
        // Logitech G29 / G923 (PlayStation), as DirectInput and SDL's lg4ff driver number them:
        // X steering, then throttle, brake, clutch, all resting at +1. Buttons: 0 cross, 1 square,
        // 2 circle, 3 triangle, 4/5 right/left paddle, 6/7 R2/L2, 8 share, 9 options, 10/11 R3/L3,
        // 12-18 the H-shifter's 1-6 and R, 23 enter, 24 PS; the D-pad is hat 0.
        new("Logitech G29", n => Has(n, "G29") || Has(n, "G923") || Has(n, "Driving Force"), s =>
        {
            s.RangeDeg = 900f;
            s.SteerAxis = 0;
            s.Throttle = Pedal(1);
            s.Brake = Pedal(2);
            s.Clutch = Pedal(3);
            s.Handbrake = new();
            s.Buttons = new()
            {
                [0] = WheelSettings.HandbrakeButton,
                [1] = PlayerInput.LookBehind,
                [2] = PlayerInput.CameraToggle,
                [3] = PlayerInput.InteractMount,
                [6] = PlayerInput.LightsToggle,
                [7] = PlayerInput.RoofToggle,
                [8] = PlayerInput.EngineToggle,
                [9] = PlayerInput.Menu,
            };
        }),
        // HORI Force Feedback Truck Control System ("HORI TRUCK CONTROL SYSTEM WHEEL", 0f0d:017a),
        // recorded on the device: 8 axes, 54 buttons, 1 hat. Steering on axis 0 (left negative);
        // clutch, brake and gas on axes 4, 5, 6, each resting at −1 and reading +1 floored. 1800° of rotation.
        new("HORI Truck Control", n => Has(n, "HORI") && (Has(n, "Truck") || Has(n, "Force Feedback")), s =>
        {
            s.RangeDeg = 1800f;
            s.SteerAxis = 0;
            s.Throttle = Pedal(6, rest: -1f);
            s.Brake = Pedal(5, rest: -1f);
            s.Clutch = Pedal(4, rest: -1f);
            s.Handbrake = new();
            s.Buttons = new()
            {
                [45] = WheelSettings.HandbrakeButton,
                [8] = PlayerInput.LookBehind,
                [7] = PlayerInput.CameraToggle,
                [0] = PlayerInput.LightsToggle,
                [5] = PlayerInput.InteractMount,
            };
        }),
    };

    /// <summary>The preset for a device name, or null.</summary>
    public static Preset? For(string deviceName) => All.FirstOrDefault(p => p.Matches(deviceName));

    /// <summary>Fresh bindings for a device: its preset, else steering on axis 0 and nothing else.</summary>
    public static void Reset(WheelSettings s, string deviceName)
    {
        s.Device = deviceName;
        s.SteerInvert = false;
        var preset = For(deviceName);
        if (preset != null)
        {
            preset.Apply(s);
            s.Preset = preset.Name;
            return;
        }
        s.SteerAxis = 0;
        s.Throttle = new();
        s.Brake = new();
        s.Clutch = new();
        s.Handbrake = new();
        s.Buttons = new();
        s.Preset = "";
    }

    private static WheelAxis Pedal(int axis, float rest = 1f) => new() { Axis = axis, From = rest, To = -rest };

    private static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);
}
