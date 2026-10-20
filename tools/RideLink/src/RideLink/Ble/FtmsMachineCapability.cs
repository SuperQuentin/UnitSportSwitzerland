using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RideLink.Ble.Ftms;
using RideLink.Ble.Parsers;
using RideLink.Capabilities;
using RideLink.Model;

namespace RideLink.Ble;

/// <summary>Which FTMS measurement characteristic a machine notifies on.</summary>
public enum FitnessMachineKind
{
    IndoorBike,
    Treadmill,
    Rower,
    CrossTrainer,
}

/// <summary>
/// A Fitness Machine: every quantity it reports, plus its resistance control.
/// <para>
/// One object implements up to five capability interfaces because that is what the hardware
/// is — a trainer genuinely is a power source and a cadence source and a controllable
/// resistance. The router splits it back apart by quantity.
/// </para>
/// </summary>
public sealed class FtmsMachineCapability
    : BleCapabilityBase, IPowerSource, ICadenceSource, ISpeedSource, IDistanceSource,
      IHeartRateSource, IControllableResistance
{
    private readonly FtmsControlSession? _control;
    private readonly ILogger _logger;
    private readonly Subject<ControlMode> _modeChanges = new();
    private readonly Subject<FitnessMachineData> _raw = new();
    private readonly object _gate = new();
    private ControlMode _mode = ControlMode.None;

    public FtmsMachineCapability(
        SourceId source,
        string deviceName,
        FitnessMachineKind kind,
        TrainerControlCapabilities capabilities,
        FtmsControlSession? control,
        ILogger? logger = null)
        : base(source, deviceName)
    {
        Kind = kind;
        Capabilities = capabilities;
        _control = control;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Which machine type this is.</summary>
    public FitnessMachineKind Kind { get; }

    public IObservable<PowerSample> Power => PowerStream;
    public IObservable<CadenceSample> Cadence => CadenceStream;
    public IObservable<SpeedSample> Speed => SpeedStream;
    public IObservable<DistanceSample> Distance => DistanceStream;
    public IObservable<HeartRateSample> HeartRate => HeartRateStream;

    /// <summary>Every decoded field, for consumers that want more than the routed quantities.</summary>
    public IObservable<FitnessMachineData> Data => _raw;

    /// <summary>
    /// False: a trainer estimates watts from a resistance curve rather than measuring force,
    /// so a real power meter outranks it in the router.
    /// </summary>
    public bool IsDirectForceMeasurement => false;

    /// <summary>
    /// False: any heart rate here was relayed to the machine from some other sensor, so a
    /// chest strap connected directly outranks it.
    /// </summary>
    public bool IsWornSensor => false;

    public TrainerControlCapabilities Capabilities { get; }

    public ControlMode Mode
    {
        get { lock (_gate) return _mode; }
    }

    public IObservable<ControlMode> ModeChanges => _modeChanges;

    /// <summary>Feeds one measurement value from whichever characteristic this machine uses.</summary>
    public void Ingest(ReadOnlySpan<byte> payload, DateTimeOffset at)
    {
        var data = Kind switch
        {
            FitnessMachineKind.IndoorBike => IndoorBikeDataParser.Parse(payload),
            FitnessMachineKind.Treadmill => TreadmillDataParser.Parse(payload),
            FitnessMachineKind.Rower => RowerDataParser.Parse(payload),
            FitnessMachineKind.CrossTrainer => CrossTrainerDataParser.Parse(payload),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
        };

        Publish(data, at);
    }

    /// <summary>Publishes an already-decoded set of fields. Used by the simulated backend.</summary>
    public void Publish(FitnessMachineData data, DateTimeOffset at)
    {
        if (data.InstantaneousPowerWatts is { } watts) PublishPower(watts, at);
        if (data.CadenceRpm is { } rpm) PublishCadence(rpm, at);
        if (data.SpeedMetersPerSecond is { } speed) PublishSpeed(speed, at);
        if (data.TotalDistanceMeters is { } distance) PublishDistance(distance, at);

        if (data.HeartRateBpm is { } bpm and > 0)
            PublishHeartRate(new HeartRateSample(bpm, null, null, null, at, Source));

        _raw.OnNext(data);
    }

    /// <summary>
    /// Handles a Fitness Machine Status notification. Its main job is noticing
    /// <see cref="FitnessMachineStatusOpCode.ControlPermissionLost"/>, which no failed write
    /// would otherwise reveal until the next command.
    /// </summary>
    public void IngestStatus(ReadOnlySpan<byte> payload)
    {
        var status = FitnessMachineStatusParser.Parse(payload);
        _logger.LogDebug("FTMS status {OpCode} from {Device}", status.OpCode, DeviceName);

        if (status.OpCode == FitnessMachineStatusOpCode.ControlPermissionLost)
        {
            _control?.NotifyControlLost();
            SetMode(ControlMode.None);
        }
    }

    public async Task<ControlResult> RequestControlAsync(CancellationToken cancellationToken = default)
    {
        if (_control is null)
            return ControlResult.Fail(
                ControlFailure.NotSupported, "This machine exposes no Fitness Machine Control Point");

        return await _control.EnsureControlAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlResult> SetSimulationAsync(
        SimulationParameters parameters, CancellationToken cancellationToken = default)
    {
        if (_control is null || !Capabilities.SupportsSimulation)
            return ControlResult.Fail(
                ControlFailure.NotSupported,
                "This machine does not advertise Indoor Bike Simulation Parameters (SIM mode)");

        var handshake = await _control.EnsureControlAsync(cancellationToken).ConfigureAwait(false);
        if (!handshake.Success) return handshake;

        byte[] payload = FtmsControlPoint.SetIndoorBikeSimulationParameters(
            parameters.GradeFraction,
            parameters.WindSpeedMetersPerSecond,
            parameters.RollingResistanceCoefficient,
            parameters.WindResistanceCoefficient);

        var result = await _control.ExecuteAsync(
            FtmsOpCode.SetIndoorBikeSimulationParameters, payload, cancellationToken,
            parameters.GradePercent).ConfigureAwait(false);

        if (result.Success) SetMode(ControlMode.Simulation);
        return result;
    }

    public async Task<ControlResult> SetTargetPowerAsync(
        int watts, CancellationToken cancellationToken = default)
    {
        if (_control is null || !Capabilities.SupportsErg)
            return ControlResult.Fail(
                ControlFailure.NotSupported,
                "This machine does not advertise Set Target Power, so it has no ERG mode");

        // Clamp before writing: the spec's supported range exists precisely so a host never
        // asks hardware for something it said it cannot do.
        bool inRange = Capabilities.TryClampPower(watts, out int applied);

        var handshake = await _control.EnsureControlAsync(cancellationToken).ConfigureAwait(false);
        if (!handshake.Success) return handshake;

        var result = await _control.ExecuteAsync(
            FtmsOpCode.SetTargetPower, FtmsControlPoint.SetTargetPower(applied),
            cancellationToken, applied).ConfigureAwait(false);

        if (result.Success)
        {
            SetMode(ControlMode.Erg);
            if (!inRange)
                return ControlResult.Ok(applied,
                    $"Requested {watts} W clamped to {applied} W by the machine's supported range");
        }

        return result;
    }

    public async Task<ControlResult> SetResistanceLevelAsync(
        double level, CancellationToken cancellationToken = default)
    {
        if (_control is null || !Capabilities.SupportsResistance)
            return ControlResult.Fail(
                ControlFailure.NotSupported, "This machine does not advertise Set Target Resistance Level");

        bool inRange = Capabilities.TryClampResistance(level, out double applied);

        var handshake = await _control.EnsureControlAsync(cancellationToken).ConfigureAwait(false);
        if (!handshake.Success) return handshake;

        var result = await _control.ExecuteAsync(
            FtmsOpCode.SetTargetResistanceLevel, FtmsControlPoint.SetTargetResistanceLevel(applied),
            cancellationToken, applied).ConfigureAwait(false);

        if (result.Success)
        {
            SetMode(ControlMode.Resistance);
            if (!inRange)
                return ControlResult.Ok(applied,
                    $"Requested level {level} clamped to {applied}");
        }

        return result;
    }

    public async Task<ControlResult> ResetAsync(CancellationToken cancellationToken = default)
    {
        if (_control is null)
            return ControlResult.Fail(ControlFailure.NotSupported, "No control point");

        var result = await _control.ExecuteAsync(
            FtmsOpCode.Reset, FtmsControlPoint.Reset(), cancellationToken).ConfigureAwait(false);

        if (result.Success) SetMode(ControlMode.None);
        return result;
    }

    private void SetMode(ControlMode mode)
    {
        lock (_gate)
        {
            if (_mode == mode) return;
            _mode = mode;
        }
        _modeChanges.OnNext(mode);
    }

    public override void Dispose()
    {
        _modeChanges.Dispose();
        _raw.Dispose();
        base.Dispose();
    }
}
