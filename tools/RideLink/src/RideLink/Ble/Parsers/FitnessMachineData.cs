namespace RideLink.Ble.Parsers;

/// <summary>
/// Union of everything the four FTMS measurement characteristics can report, normalized to
/// engine units (m/s, metres, rpm, watts, bpm, percent).
/// <para>
/// Indoor Bike, Treadmill, Rower and Cross Trainer share most of their optional fields, so
/// one result type lets the device layer treat any fitness machine identically. A field is
/// null when that machine did not report it in this packet.
/// </para>
/// </summary>
public sealed record FitnessMachineData
{
    /// <summary>Instantaneous speed, m/s.</summary>
    public double? SpeedMetersPerSecond { get; init; }

    /// <summary>Average speed for the session, m/s.</summary>
    public double? AverageSpeedMetersPerSecond { get; init; }

    /// <summary>Instantaneous cadence: pedal rpm, or steps/min on a cross trainer.</summary>
    public double? CadenceRpm { get; init; }

    /// <summary>Average cadence.</summary>
    public double? AverageCadenceRpm { get; init; }

    /// <summary>Cumulative distance for the session, metres.</summary>
    public double? TotalDistanceMeters { get; init; }

    /// <summary>
    /// Resistance level as reported by the machine.
    /// <para>
    /// The FTMS spec is internally inconsistent here: Supported Resistance Level Range
    /// (<c>0x2AD6</c>) defines a resolution of 0.1 while the per-machine data characteristics
    /// use a plain unitless level, and vendors differ. The parsers apply the per-characteristic
    /// resolution and <c>DeviceQuirks</c> is the place to correct a device that disagrees.
    /// </para>
    /// </summary>
    public double? ResistanceLevel { get; init; }

    /// <summary>Instantaneous power, watts. Signed — coasting can report negative.</summary>
    public int? InstantaneousPowerWatts { get; init; }

    /// <summary>Average power for the session, watts.</summary>
    public int? AveragePowerWatts { get; init; }

    /// <summary>Heart rate as relayed by the machine, bpm.</summary>
    public int? HeartRateBpm { get; init; }

    /// <summary>Total expended energy, kilocalories.</summary>
    public int? TotalEnergyKilocalories { get; init; }

    /// <summary>Energy expenditure rate, kilocalories per hour.</summary>
    public int? EnergyPerHourKilocalories { get; init; }

    /// <summary>Energy expenditure rate, kilocalories per minute.</summary>
    public int? EnergyPerMinuteKilocalories { get; init; }

    /// <summary>Metabolic equivalent of task.</summary>
    public double? MetabolicEquivalent { get; init; }

    /// <summary>Elapsed session time.</summary>
    public TimeSpan? ElapsedTime { get; init; }

    /// <summary>Remaining session time.</summary>
    public TimeSpan? RemainingTime { get; init; }

    /// <summary>Inclination, percent (not a fraction).</summary>
    public double? InclinationPercent { get; init; }

    /// <summary>Ramp angle setting, degrees.</summary>
    public double? RampAngleDegrees { get; init; }

    /// <summary>Cumulative positive elevation gain, metres.</summary>
    public double? PositiveElevationGainMeters { get; init; }

    /// <summary>Cumulative negative elevation gain, metres.</summary>
    public double? NegativeElevationGainMeters { get; init; }

    /// <summary>Instantaneous pace expressed as speed, m/s.</summary>
    public double? PaceMetersPerSecond { get; init; }

    /// <summary>Average pace expressed as speed, m/s.</summary>
    public double? AveragePaceMetersPerSecond { get; init; }

    /// <summary>Rowing stroke rate, strokes per minute.</summary>
    public double? StrokeRatePerMinute { get; init; }

    /// <summary>Average rowing stroke rate, strokes per minute.</summary>
    public double? AverageStrokeRatePerMinute { get; init; }

    /// <summary>Cumulative rowing stroke count.</summary>
    public int? StrokeCount { get; init; }

    /// <summary>Cross trainer step rate, steps per minute.</summary>
    public int? StepsPerMinute { get; init; }

    /// <summary>Cross trainer average step rate, steps per minute.</summary>
    public int? AverageStepRate { get; init; }

    /// <summary>Cross trainer cumulative stride count.</summary>
    public double? StrideCount { get; init; }

    /// <summary>Treadmill belt force, newtons.</summary>
    public double? ForceOnBeltNewtons { get; init; }

    /// <summary>Cross trainer movement direction: true forward, false backward.</summary>
    public bool? MovingForward { get; init; }
}
