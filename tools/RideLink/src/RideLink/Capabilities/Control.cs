using RideLink.Ble.Parsers;

namespace RideLink.Capabilities;

/// <summary>Which control strategy a trainer is currently following.</summary>
public enum ControlMode
{
    /// <summary>No target has been set, or control was lost.</summary>
    None,

    /// <summary>Resistance follows a simulated road: grade, wind, Crr, Cw.</summary>
    Simulation,

    /// <summary>Resistance is servoed to hold a fixed wattage.</summary>
    Erg,

    /// <summary>A raw resistance level, no power feedback.</summary>
    Resistance,
}

/// <summary>
/// Parameters of SIM mode.
/// <para>
/// Grade is a fraction throughout the engine — 0.05 is a 5% climb — because that is what the
/// physics wants; the FTMS and FE-C encoders convert to their own scaled integers at the wire.
/// </para>
/// </summary>
/// <param name="GradeFraction">Road gradient as a fraction. Positive uphill.</param>
/// <param name="WindSpeedMetersPerSecond">Head wind positive, tail wind negative.</param>
/// <param name="RollingResistanceCoefficient">Crr. ~0.004 on tarmac, higher off-road.</param>
/// <param name="WindResistanceCoefficient">Cw in kg/m, roughly frontal area × drag × air density.</param>
public readonly record struct SimulationParameters(
    double GradeFraction,
    double WindSpeedMetersPerSecond = 0.0,
    double RollingResistanceCoefficient = 0.004,
    double WindResistanceCoefficient = 0.51)
{
    /// <summary>Grade expressed as a percentage, for display.</summary>
    public double GradePercent => GradeFraction * 100.0;

    /// <summary>Flat road with default coefficients.</summary>
    public static SimulationParameters Flat { get; } = new(0.0);
}

/// <summary>Why a control command did not take effect.</summary>
public enum ControlFailure
{
    None = 0,

    /// <summary>The device told us, or its feature bits told us, that it cannot do this.</summary>
    NotSupported,

    /// <summary>The target fell outside the device's advertised range and was refused.</summary>
    OutOfRange,

    /// <summary>No indication came back within the transaction timeout.</summary>
    Timeout,

    /// <summary>The device answered with a failure result code.</summary>
    Rejected,

    /// <summary>Control was never granted, or was revoked.</summary>
    ControlNotPermitted,

    /// <summary>The link is down.</summary>
    NotConnected,
}

/// <summary>Outcome of a control command.</summary>
/// <param name="Success">Whether the device confirmed the command.</param>
/// <param name="Failure">Why not, when it did not.</param>
/// <param name="Message">Human-readable detail for logs and the UI.</param>
/// <param name="AppliedValue">
/// What was actually sent after clamping, so the consumer can show "asked 600 W, sent 400 W".
/// </param>
public readonly record struct ControlResult(
    bool Success,
    ControlFailure Failure,
    string? Message,
    double? AppliedValue)
{
    public static ControlResult Ok(double? applied = null, string? message = null) =>
        new(true, ControlFailure.None, message, applied);

    public static ControlResult Fail(ControlFailure reason, string message) =>
        new(false, reason, message, null);

    public override string ToString() =>
        Success ? $"ok{(AppliedValue is { } v ? $" ({v})" : "")}" : $"{Failure}: {Message}";
}

/// <summary>
/// What a trainer will accept, discovered from the device rather than assumed.
/// <para>
/// Read from Fitness Machine Feature plus the two supported-range characteristics on BLE, and
/// from the FE-C capabilities bits on ANT+. Targets are clamped to these before any write, so
/// hardware never sees a value it advertised as impossible.
/// </para>
/// </summary>
/// <param name="SupportsSimulation">SIM mode available.</param>
/// <param name="SupportsErg">ERG mode available.</param>
/// <param name="SupportsResistance">Raw resistance level available.</param>
/// <param name="PowerRange">Accepted ERG target range, when the device reports one.</param>
/// <param name="ResistanceRange">Accepted resistance level range, when the device reports one.</param>
public readonly record struct TrainerControlCapabilities(
    bool SupportsSimulation,
    bool SupportsErg,
    bool SupportsResistance,
    PowerRange? PowerRange,
    ResistanceLevelRange? ResistanceRange)
{
    /// <summary>Nothing controllable.</summary>
    public static TrainerControlCapabilities None { get; } = new(false, false, false, null, null);

    /// <summary>
    /// Clamps an ERG target into the advertised range.
    /// </summary>
    /// <param name="watts">Requested target.</param>
    /// <param name="clamped">Value that should actually be written.</param>
    /// <returns>True when the request was already inside the range.</returns>
    public bool TryClampPower(int watts, out int clamped)
    {
        if (PowerRange is not { } range)
        {
            // No advertised range. Refuse absurd values rather than trusting the caller with
            // hardware: a mis-scaled target is a common bug and a 30 kW write helps nobody.
            clamped = Math.Clamp(watts, 0, 2500);
            return clamped == watts;
        }

        clamped = range.Clamp(watts);
        return clamped == watts;
    }

    /// <summary>Clamps a resistance level into the advertised range.</summary>
    public bool TryClampResistance(double level, out double clamped)
    {
        if (ResistanceRange is not { } range)
        {
            clamped = Math.Clamp(level, 0, 100);
            return Math.Abs(clamped - level) < 1e-9;
        }

        clamped = range.Clamp(level);
        return Math.Abs(clamped - level) < 1e-9;
    }
}
