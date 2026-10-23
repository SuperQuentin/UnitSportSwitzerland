namespace RideLink.Capabilities;

/// <summary>
/// Conventional <see cref="Model.SourceId.Channel"/> values.
/// <para>
/// The router ranks sources by which stream they came from, not by brand, so these strings
/// are the vocabulary the priority policy reasons over. A new device profile picks the
/// channel that describes what it is — a crank-based cadence reading from a power meter uses
/// <see cref="CyclingPower"/> whatever the manufacturer — and it inherits sensible priority
/// with no policy change.
/// </para>
/// </summary>
public static class Channels
{
    // --- BLE -------------------------------------------------------------------------
    public const string FtmsIndoorBike = "ftms.indoor-bike";
    public const string FtmsTreadmill = "ftms.treadmill";
    public const string FtmsRower = "ftms.rower";
    public const string FtmsCrossTrainer = "ftms.cross-trainer";
    public const string FtmsControl = "ftms.control";

    /// <summary>Cycling Power Measurement: a dedicated power meter, or a trainer's power service.</summary>
    public const string CyclingPower = "cps.measurement";

    /// <summary>CSC Measurement: a dedicated speed and/or cadence sensor.</summary>
    public const string SpeedCadence = "csc.measurement";

    /// <summary>Heart Rate Measurement from a worn sensor.</summary>
    public const string HeartRate = "hrs.measurement";

    /// <summary>RSC Measurement: a footpod or treadmill.</summary>
    public const string RunningSpeedCadence = "rsc.measurement";

    public const string Battery = "bas.level";

    // --- ANT+ ------------------------------------------------------------------------
    public const string AntFitnessEquipment = "ant.fe-c";
    public const string AntBicyclePower = "ant.power";
    public const string AntSpeedCadence = "ant.speed-cadence";
    public const string AntHeartRate = "ant.hrm";

    // --- Simulation --------------------------------------------------------------------
    public const string Simulated = "sim";
}
