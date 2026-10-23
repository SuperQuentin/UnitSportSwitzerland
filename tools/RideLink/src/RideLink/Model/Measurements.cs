namespace RideLink.Model;

/// <summary>
/// Stable identity of the thing that produced a measurement.
/// <para>
/// This is deliberately not the transport address: a single physical device can expose
/// several capabilities (a smart trainer reports power *and* cadence *and* speed), and the
/// router needs to tell "the power from the trainer" apart from "the power from the pedals"
/// while still knowing both came from the same box.
/// </para>
/// </summary>
/// <param name="DeviceId">Transport-scoped device identity, e.g. a BLE address or ANT device number.</param>
/// <param name="Transport">Which radio produced it — useful when the same trainer is visible on both.</param>
/// <param name="Channel">
/// Which stream within the device, e.g. <c>"ftms.indoor-bike"</c> or <c>"cps.measurement"</c>.
/// Lets the router prefer a dedicated power meter's watts over a trainer's estimate.
/// </param>
public readonly record struct SourceId(string DeviceId, TransportKind Transport, string Channel)
{
    public override string ToString() => $"{Transport}:{DeviceId}/{Channel}";
}

/// <summary>Radio a measurement arrived over.</summary>
public enum TransportKind
{
    Simulated = 0,
    BluetoothLowEnergy = 1,
    AntPlus = 2,
}

/// <summary>Common shape of every normalized sample.</summary>
public interface IMeasurement
{
    /// <summary>When the engine observed the value (UTC). Device event counters are used for
    /// rate maths; this timestamp is for ordering, logging and staleness only.</summary>
    DateTimeOffset Timestamp { get; }

    SourceId Source { get; }
}

/// <summary>Instantaneous mechanical power, watts.</summary>
public readonly record struct PowerSample(int Watts, DateTimeOffset Timestamp, SourceId Source)
    : IMeasurement;

/// <summary>Pedal or stroke cadence, revolutions per minute.</summary>
public readonly record struct CadenceSample(double Rpm, DateTimeOffset Timestamp, SourceId Source)
    : IMeasurement;

/// <summary>
/// Heart rate in beats per minute, with the extras the HR Measurement characteristic can carry.
/// </summary>
/// <param name="Bpm">Beats per minute.</param>
/// <param name="SensorContact">
/// Null when the sensor does not support contact detection, otherwise whether skin contact is
/// currently detected. A strap reporting <c>false</c> is usually reporting nonsense BPM.
/// </param>
/// <param name="RrIntervals">Beat-to-beat intervals in seconds, oldest first, when present.</param>
/// <param name="EnergyExpendedKilojoules">Cumulative energy, when the sensor reports it.</param>
/// <param name="Timestamp">When the engine observed the value (UTC).</param>
/// <param name="Source">Which device and stream produced it.</param>
public readonly record struct HeartRateSample(
    int Bpm,
    bool? SensorContact,
    IReadOnlyList<double>? RrIntervals,
    int? EnergyExpendedKilojoules,
    DateTimeOffset Timestamp,
    SourceId Source) : IMeasurement;

/// <summary>Ground or virtual speed. Metres per second is the engine's internal unit throughout.</summary>
public readonly record struct SpeedSample(double MetersPerSecond, DateTimeOffset Timestamp, SourceId Source)
    : IMeasurement
{
    public double KilometersPerHour => MetersPerSecond * 3.6;
}

/// <summary>Cumulative distance for the session, metres.</summary>
public readonly record struct DistanceSample(double Meters, DateTimeOffset Timestamp, SourceId Source)
    : IMeasurement;

/// <summary>Battery level, percent 0-100 (Battery Service, <c>0x2A19</c>).</summary>
public readonly record struct BatterySample(int Percent, DateTimeOffset Timestamp, SourceId Source)
    : IMeasurement;

/// <summary>Stride length and running cadence from a footpod (RSC).</summary>
public readonly record struct StrideSample(
    double? StrideLengthMeters,
    bool? IsRunning,
    DateTimeOffset Timestamp,
    SourceId Source) : IMeasurement;
