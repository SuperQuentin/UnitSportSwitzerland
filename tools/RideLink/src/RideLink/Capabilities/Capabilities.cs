using RideLink.Model;

namespace RideLink.Capabilities;

/// <summary>
/// Base of every capability. The consumer subscribes to capabilities, never to a device: one
/// physical box may implement several of these, and several boxes may each implement one.
/// </summary>
public interface ICapability
{
    /// <summary>Which device and stream this capability speaks for.</summary>
    SourceId Source { get; }

    /// <summary>Display name of the owning device, for pickers and logs.</summary>
    string DeviceName { get; }
}

/// <summary>Reports mechanical power.</summary>
public interface IPowerSource : ICapability
{
    IObservable<PowerSample> Power { get; }

    /// <summary>
    /// True when the watts are measured by a strain gauge rather than estimated from a
    /// resistance curve. The router prefers measured power when both are available.
    /// </summary>
    bool IsDirectForceMeasurement { get; }
}

/// <summary>Reports pedal, stroke or step cadence.</summary>
public interface ICadenceSource : ICapability
{
    IObservable<CadenceSample> Cadence { get; }
}

/// <summary>Reports heart rate.</summary>
public interface IHeartRateSource : ICapability
{
    IObservable<HeartRateSample> HeartRate { get; }

    /// <summary>
    /// True for a device worn on the body (chest strap, armband). A machine relaying a heart
    /// rate it received from elsewhere sets this false and loses to the strap in the router.
    /// </summary>
    bool IsWornSensor { get; }
}

/// <summary>Reports speed.</summary>
public interface ISpeedSource : ICapability
{
    IObservable<SpeedSample> Speed { get; }
}

/// <summary>Reports cumulative distance.</summary>
public interface IDistanceSource : ICapability
{
    IObservable<DistanceSample> Distance { get; }
}

/// <summary>Reports its own battery level.</summary>
public interface IBatterySource : ICapability
{
    IObservable<BatterySample> Battery { get; }
}

/// <summary>
/// A device whose resistance can be commanded — a smart trainer over FTMS or ANT+ FE-C.
/// <para>
/// The three modes are mapped onto identically by both radios, so a consumer never learns
/// which one is underneath.
/// </para>
/// </summary>
public interface IControllableResistance : ICapability
{
    /// <summary>What this trainer will actually accept, read from the device, not assumed.</summary>
    TrainerControlCapabilities Capabilities { get; }

    /// <summary>The mode the last successful command put it in.</summary>
    ControlMode Mode { get; }

    /// <summary>Mode transitions, including a forced drop to <see cref="ControlMode.None"/>
    /// when the device revokes control.</summary>
    IObservable<ControlMode> ModeChanges { get; }

    /// <summary>
    /// Takes control of the machine. Runs the handshake the transport requires — for FTMS,
    /// Request Control then Reset. Safe to call again; it is re-run automatically after the
    /// machine reports that control permission was lost.
    /// </summary>
    Task<ControlResult> RequestControlAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// SIM mode: the trainer computes resistance from a simulated road. This is "feel the hill".
    /// </summary>
    Task<ControlResult> SetSimulationAsync(
        SimulationParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// ERG mode: the trainer holds the target regardless of cadence or gear.
    /// Fails with <see cref="ControlFailure.NotSupported"/> on trainers that do not implement it.
    /// </summary>
    Task<ControlResult> SetTargetPowerAsync(
        int watts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raw resistance level, for trainers with no power control at all.
    /// </summary>
    Task<ControlResult> SetResistanceLevelAsync(
        double level, CancellationToken cancellationToken = default);

    /// <summary>Returns the machine to its default state and drops out of any target mode.</summary>
    Task<ControlResult> ResetAsync(CancellationToken cancellationToken = default);
}
