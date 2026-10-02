using System.Text.Json.Serialization;

namespace UnitSport.Core;

/// <summary>
/// One analog control on the wheel: which SDL axis, and the raw values (−1..1) at rest and at full
/// travel. A pedal that rests at +1 and a pedal that rests at −1 are both just a From and a To, and
/// so are the two halves of a combined gas/brake axis (0 → −1 and 0 → +1).
/// </summary>
public sealed class WheelAxis
{
    /// <summary>SDL axis index; −1 unbound.</summary>
    public int Axis { get; set; } = -1;
    public float From { get; set; } = 1f;
    public float To { get; set; } = -1f;

    [JsonIgnore]
    public bool Bound => Axis >= 0;

    /// <summary>0 at rest .. 1 at full travel, with a little travel ignored at each end.</summary>
    public float Read(float raw)
    {
        float span = To - From;
        if (MathF.Abs(span) < 0.05f) return 0f;
        float v = (raw - From) / span;
        return Math.Clamp((v - 0.03f) / 0.94f, 0f, 1f);
    }

    public WheelAxis Copy() => new() { Axis = Axis, From = From, To = To };
}

/// <summary>
/// The steering wheel and its pedals (<see cref="SteeringWheel"/>, issue #68), saved inside
/// <see cref="GameSettings"/>. Bindings are raw SDL indices of one device: a preset fills them for a
/// known wheel (<see cref="WheelPresets"/>), Settings → Steering wheel reassigns them by moving the
/// control.
/// </summary>
public sealed class WheelSettings
{
    /// <summary>Read a wheel at all. Off leaves every wheel to Godot as a plain joypad.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The device these bindings belong to (SDL name); empty picks the first wheel found.</summary>
    public string Device { get; set; } = "";

    /// <summary>
    /// The rotation the wheel is set to in its driver, degrees lock to lock. The game maps the
    /// steering axis to this, so the car's wheel turns as far as the real one.
    /// </summary>
    public float RangeDeg { get; set; } = 900f;
    public const float MinRangeDeg = 270f, MaxRangeDeg = 1800f;

    public int SteerAxis { get; set; } = 0;
    public bool SteerInvert { get; set; }

    public WheelAxis Throttle { get; set; } = new();
    public WheelAxis Brake { get; set; } = new();
    public WheelAxis Clutch { get; set; } = new();
    public WheelAxis Handbrake { get; set; } = new();

    /// <summary>
    /// Wheel button → <see cref="PlayerInput"/> action. Button indices past
    /// <see cref="SteeringWheel.HatBase"/> are hat directions (the D-pad on a G29).
    /// <see cref="HandbrakeButton"/> is the one target that is not an action.
    /// </summary>
    public Dictionary<int, string> Buttons { get; set; } = new();

    /// <summary>A button bound to this pulls the handbrake fully, like a handbrake lever.</summary>
    public const string HandbrakeButton = "handbrake";

    // --- force feedback ---
    /// <summary>Forces through the rim, on a wheel that has them (<c>SteeringWheel.Force.cs</c>).</summary>
    public bool ForceFeedback { get; set; } = true;
    /// <summary>Everything at once, 0..1: a strong direct-drive base wants it lower than a gear-driven wheel.</summary>
    public float FfbStrength { get; set; } = 0.7f;
    /// <summary>Self-aligning torque and the soft lock past the vehicle's lock, 0..1.5.</summary>
    public float FfbAligning { get; set; } = 1f;
    /// <summary>The road's rumble off tarmac, 0..1.</summary>
    public float FfbRoad { get; set; } = 0.35f;
    /// <summary>The engine's shake through the column, following the rpm, 0..1.</summary>
    public float FfbEngine { get; set; } = 0.5f;
    /// <summary>Crashes and hard landings, 0..1.</summary>
    public float FfbKnocks { get; set; } = 0.8f;
    /// <summary>Damping, and the steering's weight when parked, 0..1.</summary>
    public float FfbWeight { get; set; } = 0.6f;
    /// <summary>The device pushes the other way for a positive force: flips every force.</summary>
    public bool FfbInvert { get; set; }

    /// <summary>The preset these bindings started from, so a new device of the same kind is not re-preset.</summary>
    public string Preset { get; set; } = "";

    public void Clamp()
    {
        RangeDeg = Math.Clamp(RangeDeg, MinRangeDeg, MaxRangeDeg);
        FfbStrength = Math.Clamp(FfbStrength, 0f, 1f);
        FfbAligning = Math.Clamp(FfbAligning, 0f, 1.5f);
        FfbRoad = Math.Clamp(FfbRoad, 0f, 1f);
        FfbEngine = Math.Clamp(FfbEngine, 0f, 1f);
        FfbKnocks = Math.Clamp(FfbKnocks, 0f, 1f);
        FfbWeight = Math.Clamp(FfbWeight, 0f, 1f);
        Device ??= "";
        Preset ??= "";
        Throttle ??= new();
        Brake ??= new();
        Clutch ??= new();
        Handbrake ??= new();
        Buttons ??= new();
    }
}
