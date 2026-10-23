using System.Text;
using RideLink.Parsing;

namespace RideLink.Ble.Parsers;

/// <summary>Fitness Machine Status op codes. FTMS v1.0, §4.17, Table 4.25.</summary>
public enum FitnessMachineStatusOpCode : byte
{
    Reserved = 0x00,
    Reset = 0x01,
    StoppedOrPausedByUser = 0x02,
    StoppedBySafetyKey = 0x03,
    StartedOrResumedByUser = 0x04,
    TargetSpeedChanged = 0x05,
    TargetInclineChanged = 0x06,
    TargetResistanceLevelChanged = 0x07,
    TargetPowerChanged = 0x08,
    TargetHeartRateChanged = 0x09,
    TargetedExpendedEnergyChanged = 0x0A,
    TargetedNumberOfStepsChanged = 0x0B,
    TargetedNumberOfStridesChanged = 0x0C,
    TargetedDistanceChanged = 0x0D,
    TargetedTrainingTimeChanged = 0x0E,
    TargetedTimeInTwoHeartRateZonesChanged = 0x0F,
    TargetedTimeInThreeHeartRateZonesChanged = 0x10,
    TargetedTimeInFiveHeartRateZonesChanged = 0x11,
    IndoorBikeSimulationParametersChanged = 0x12,
    WheelCircumferenceChanged = 0x13,
    SpinDownStatus = 0x14,
    TargetedCadenceChanged = 0x15,

    /// <summary>
    /// The machine has taken control back — a user pressed a button on it, or the connection
    /// was idle. Any pending target is void and Request Control must be repeated.
    /// </summary>
    ControlPermissionLost = 0xFF,
}

/// <summary>Decoded Fitness Machine Status notification (<c>0x2ADA</c>).</summary>
/// <param name="OpCode">What changed.</param>
/// <param name="Parameters">Raw parameter octets, meaning depends on the op code.</param>
public readonly record struct FitnessMachineStatus(FitnessMachineStatusOpCode OpCode, byte[] Parameters);

/// <summary>
/// Fitness Machine Status parser (<c>0x2ADA</c>, notify). FTMS v1.0, §4.17.
/// <para>
/// This is how the machine reports changes it made itself — including
/// <see cref="FitnessMachineStatusOpCode.ControlPermissionLost"/>, which the control layer
/// must react to by re-running the Request Control handshake rather than silently continuing
/// to write targets that will be rejected.
/// </para>
/// </summary>
public static class FitnessMachineStatusParser
{
    public static FitnessMachineStatus Parse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        var opCode = (FitnessMachineStatusOpCode)reader.ReadUInt8("Status Op Code");
        return new FitnessMachineStatus(opCode, reader.PeekRest().ToArray());
    }
}

/// <summary>Training Status values. FTMS v1.0, §4.10.1.1, Table 4.9.</summary>
public enum TrainingStatusValue : byte
{
    Other = 0x00,
    Idle = 0x01,
    WarmingUp = 0x02,
    LowIntensityInterval = 0x03,
    HighIntensityInterval = 0x04,
    RecoveryInterval = 0x05,
    Isometric = 0x06,
    Heartrate = 0x07,
    FitnessTest = 0x08,
    SpeedOutsideControlRegionLow = 0x09,
    SpeedOutsideControlRegionHigh = 0x0A,
    CoolDown = 0x0B,
    WattControl = 0x0C,
    ManualMode = 0x0D,
    PreWorkout = 0x0E,
    PostWorkout = 0x0F,
}

/// <summary>Decoded Training Status (<c>0x2AD3</c>).</summary>
public readonly record struct TrainingStatus(TrainingStatusValue Status, string? StatusString);

/// <summary>
/// Training Status parser (<c>0x2AD3</c>). FTMS v1.0, §4.10.
/// <para>
/// Flags (uint8): bit 0 — Training Status String present; bit 1 — Extended String present.
/// Then Training Status (uint8), then the optional UTF-8 string filling the rest of the PDU.
/// </para>
/// </summary>
public static class TrainingStatusParser
{
    private const byte FlagTrainingStatusStringPresent = 1 << 0;

    public static TrainingStatus Parse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        byte flags = reader.ReadUInt8("Training Status flags");
        var status = (TrainingStatusValue)reader.ReadUInt8("Training Status");

        string? text = null;
        if ((flags & FlagTrainingStatusStringPresent) != 0 && reader.Remaining > 0)
            text = Encoding.UTF8.GetString(reader.PeekRest()).TrimEnd('\0');

        return new TrainingStatus(status, text);
    }
}
