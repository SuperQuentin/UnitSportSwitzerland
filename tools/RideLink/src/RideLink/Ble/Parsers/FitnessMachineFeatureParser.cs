using RideLink.Parsing;

namespace RideLink.Ble.Parsers;

/// <summary>
/// What the machine can measure. FTMS v1.0, §4.3.1 — first uint32 of Fitness Machine Feature.
/// </summary>
[Flags]
public enum FitnessMachineFeatures : uint
{
    None = 0,
    AverageSpeed = 1u << 0,
    Cadence = 1u << 1,
    TotalDistance = 1u << 2,
    Inclination = 1u << 3,
    ElevationGain = 1u << 4,
    Pace = 1u << 5,
    StepCount = 1u << 6,
    ResistanceLevel = 1u << 7,
    StrideCount = 1u << 8,
    ExpendedEnergy = 1u << 9,
    HeartRateMeasurement = 1u << 10,
    MetabolicEquivalent = 1u << 11,
    ElapsedTime = 1u << 12,
    RemainingTime = 1u << 13,
    PowerMeasurement = 1u << 14,
    ForceOnBeltAndPowerOutput = 1u << 15,
    UserDataRetention = 1u << 16,
}

/// <summary>
/// What the machine will let you set. FTMS v1.0, §4.3.2 — second uint32 of Fitness Machine
/// Feature. This is the authoritative answer to "does this trainer do ERG?"
/// (<see cref="PowerTarget"/>) and "does it do SIM?" (<see cref="IndoorBikeSimulationParameters"/>).
/// </summary>
[Flags]
public enum FitnessMachineTargetFeatures : uint
{
    None = 0,
    SpeedTarget = 1u << 0,
    InclinationTarget = 1u << 1,
    ResistanceTarget = 1u << 2,
    PowerTarget = 1u << 3,
    HeartRateTarget = 1u << 4,
    TargetedExpendedEnergy = 1u << 5,
    TargetedStepNumber = 1u << 6,
    TargetedStrideNumber = 1u << 7,
    TargetedDistance = 1u << 8,
    TargetedTrainingTime = 1u << 9,
    TargetedTimeInTwoHeartRateZones = 1u << 10,
    TargetedTimeInThreeHeartRateZones = 1u << 11,
    TargetedTimeInFiveHeartRateZones = 1u << 12,
    IndoorBikeSimulationParameters = 1u << 13,
    WheelCircumferenceConfiguration = 1u << 14,
    SpinDownControl = 1u << 15,
    TargetedCadenceConfiguration = 1u << 16,
}

/// <summary>Both halves of Fitness Machine Feature (<c>0x2ACC</c>).</summary>
public readonly record struct FitnessMachineFeature(
    FitnessMachineFeatures Features,
    FitnessMachineTargetFeatures TargetFeatures)
{
    /// <summary>True when the machine accepts Set Target Power, i.e. supports ERG mode.</summary>
    public bool SupportsErg => TargetFeatures.HasFlag(FitnessMachineTargetFeatures.PowerTarget);

    /// <summary>True when the machine accepts Set Indoor Bike Simulation Parameters.</summary>
    public bool SupportsSimulation =>
        TargetFeatures.HasFlag(FitnessMachineTargetFeatures.IndoorBikeSimulationParameters);

    /// <summary>True when the machine accepts Set Target Resistance Level.</summary>
    public bool SupportsResistance =>
        TargetFeatures.HasFlag(FitnessMachineTargetFeatures.ResistanceTarget);
}

/// <summary>
/// Fitness Machine Feature parser (<c>0x2ACC</c>, read).
/// <para>FTMS v1.0, §4.3. Two uint32 fields: machine features then target-setting features.</para>
/// <para>
/// This is read <b>before</b> subscribing to anything: it says which control modes exist, so
/// the engine can report "this trainer has no ERG" up front instead of writing a control
/// command and waiting for an Op Code Not Supported response.
/// </para>
/// </summary>
public static class FitnessMachineFeatureParser
{
    public static FitnessMachineFeature Parse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        var features = (FitnessMachineFeatures)reader.ReadUInt32("Fitness Machine Features");
        var targets = (FitnessMachineTargetFeatures)reader.ReadUInt32("Target Setting Features");
        return new FitnessMachineFeature(features, targets);
    }
}

/// <summary>Supported Resistance Level Range (<c>0x2AD6</c>), FTMS v1.0 §4.12.</summary>
/// <param name="Minimum">Minimum resistance level, units of 0.1.</param>
/// <param name="Maximum">Maximum resistance level, units of 0.1.</param>
/// <param name="MinimumIncrement">Smallest step the machine honours, units of 0.1.</param>
public readonly record struct ResistanceLevelRange(double Minimum, double Maximum, double MinimumIncrement)
{
    /// <summary>Clamps a requested level into the range the machine actually accepts.</summary>
    public double Clamp(double level) => Math.Clamp(level, Minimum, Maximum);
}

/// <summary>Supported Power Range (<c>0x2AD8</c>), FTMS v1.0 §4.14. Watts.</summary>
/// <param name="MinimumWatts">Lowest target the machine will hold.</param>
/// <param name="MaximumWatts">Highest target the machine will hold.</param>
/// <param name="MinimumIncrementWatts">Smallest step the machine honours.</param>
public readonly record struct PowerRange(int MinimumWatts, int MaximumWatts, int MinimumIncrementWatts)
{
    /// <summary>Clamps a requested target into the range the machine actually accepts.</summary>
    public int Clamp(int watts) => Math.Clamp(watts, MinimumWatts, MaximumWatts);
}

/// <summary>Parsers for the two "supported range" characteristics.</summary>
public static class SupportedRangeParser
{
    /// <summary>
    /// Supported Resistance Level Range: sint16 minimum, sint16 maximum, uint16 minimum
    /// increment — all in units of 0.1. FTMS v1.0, §4.12.
    /// </summary>
    public static ResistanceLevelRange ParseResistanceRange(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        double min = reader.ReadInt16("Minimum Resistance Level") * 0.1;
        double max = reader.ReadInt16("Maximum Resistance Level") * 0.1;
        double increment = reader.ReadUInt16("Minimum Increment") * 0.1;
        return new ResistanceLevelRange(min, max, increment);
    }

    /// <summary>
    /// Supported Power Range: sint16 minimum, sint16 maximum, uint16 minimum increment —
    /// all in watts. FTMS v1.0, §4.14.
    /// </summary>
    public static PowerRange ParsePowerRange(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        int min = reader.ReadInt16("Minimum Power");
        int max = reader.ReadInt16("Maximum Power");
        int increment = reader.ReadUInt16("Minimum Increment");
        return new PowerRange(min, max, increment);
    }
}
