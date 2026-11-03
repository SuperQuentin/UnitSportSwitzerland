using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RideLink.Capabilities;
using RideLink.Model;

namespace RideLink.Ant;

/// <summary>
/// An ANT+ FE-C trainer, exposed through exactly the same capability interfaces as a BLE FTMS
/// trainer.
/// <para>
/// FE-C has no equivalent of the FTMS Request Control handshake: control pages are simply sent
/// as acknowledged messages and the trainer replies with Command Status (0x47). The mapping
/// onto SIM / ERG / Resistance is otherwise one-to-one — Track Resistance is SIM, Target Power
/// is ERG, Basic Resistance is Resistance.
/// </para>
/// </summary>
public sealed class FitnessEquipmentCapability
    : IPowerSource, ICadenceSource, ISpeedSource, IDistanceSource, IHeartRateSource,
      IControllableResistance, IAsyncDisposable
{
    private readonly IAntChannel _channel;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly IDisposable _subscription;

    private readonly Subject<PowerSample> _power = new();
    private readonly Subject<CadenceSample> _cadence = new();
    private readonly Subject<SpeedSample> _speed = new();
    private readonly Subject<DistanceSample> _distance = new();
    private readonly Subject<HeartRateSample> _heartRate = new();
    private readonly Subject<ControlMode> _modeChanges = new();
    private readonly object _gate = new();

    private ControlMode _mode = ControlMode.None;
    private byte? _lastDistanceCounter;
    private double _totalDistanceMeters;
    private bool _targetPowerLimitsReached;

    public FitnessEquipmentCapability(
        IAntChannel channel,
        string deviceName,
        TimeProvider? clock = null,
        ILogger? logger = null)
    {
        _channel = channel;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;

        DeviceName = deviceName;
        Source = new SourceId(
            channel.DeviceNumber.ToString(), TransportKind.AntPlus, Channels.AntFitnessEquipment);

        _subscription = channel.Broadcasts.Subscribe(OnBroadcast);
    }

    public SourceId Source { get; }
    public string DeviceName { get; }

    public IObservable<PowerSample> Power => _power;
    public IObservable<CadenceSample> Cadence => _cadence;
    public IObservable<SpeedSample> Speed => _speed;
    public IObservable<DistanceSample> Distance => _distance;
    public IObservable<HeartRateSample> HeartRate => _heartRate;
    public IObservable<ControlMode> ModeChanges => _modeChanges;

    /// <summary>False: a trainer estimates watts rather than measuring force.</summary>
    public bool IsDirectForceMeasurement => false;

    /// <summary>False: any heart rate here was relayed to the trainer from another sensor.</summary>
    public bool IsWornSensor => false;

    /// <summary>
    /// FE-C trainers implement all three control modes by definition of the profile, so unlike
    /// FTMS there is no feature bitmap to read. Ranges are not advertised either; the clamps
    /// fall back to the engine's sanity limits.
    /// </summary>
    public TrainerControlCapabilities Capabilities { get; init; } =
        new(SupportsSimulation: true, SupportsErg: true, SupportsResistance: true,
            PowerRange: null, ResistanceRange: null);

    public ControlMode Mode { get { lock (_gate) return _mode; } }

    /// <summary>
    /// True when the trainer last reported it cannot hold the ERG target at the current
    /// cadence — the cue for a UI to tell the rider to change gear.
    /// </summary>
    public bool TargetPowerLimitsReached { get { lock (_gate) return _targetPowerLimitsReached; } }

    private void OnBroadcast(AntBroadcast broadcast)
    {
        try
        {
            var payload = broadcast.Payload.AsSpan();
            byte? page = AntPageParser.PageNumber(payload);
            var at = broadcast.ReceivedAt;

            switch (page)
            {
                case AntPages.GeneralFeData:
                {
                    var data = AntPageParser.ParseGeneralFeData(payload);

                    if (data.SpeedMetersPerSecond is { } speed)
                        _speed.OnNext(new SpeedSample(speed, at, Source));

                    if (data.HeartRateBpm is { } bpm)
                        _heartRate.OnNext(new HeartRateSample(bpm, null, null, null, at, Source));

                    // Distance is a uint8 counter of metres that wraps every 256 m, so the
                    // total has to be accumulated from deltas rather than read directly.
                    byte counter = (byte)data.DistanceMeters;
                    if (_lastDistanceCounter is { } previous)
                        _totalDistanceMeters += (byte)(counter - previous);
                    _lastDistanceCounter = counter;

                    _distance.OnNext(new DistanceSample(_totalDistanceMeters, at, Source));
                    break;
                }

                case AntPages.SpecificTrainerData:
                {
                    var data = AntPageParser.ParseSpecificTrainerData(payload);

                    if (data.InstantaneousPowerWatts is { } watts)
                        _power.OnNext(new PowerSample(watts, at, Source));

                    if (data.CadenceRpm is { } rpm)
                        _cadence.OnNext(new CadenceSample(rpm, at, Source));

                    lock (_gate) _targetPowerLimitsReached = data.TargetPowerLimitsReached;
                    break;
                }

                case AntPages.CommandStatus:
                    _logger.LogDebug("FE-C command status {Hex}",
                        Convert.ToHexString(broadcast.Payload));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unparseable FE-C page {Hex}",
                Convert.ToHexString(broadcast.Payload));
        }
    }

    /// <summary>FE-C needs no handshake; control pages are accepted immediately.</summary>
    public Task<ControlResult> RequestControlAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ControlResult.Ok(message: "FE-C requires no control handshake"));

    public Task<ControlResult> SetSimulationAsync(
        SimulationParameters parameters, CancellationToken cancellationToken = default)
    {
        // Wind is a separate page from grade in FE-C; sending both keeps SIM equivalent to the
        // single FTMS Set Indoor Bike Simulation Parameters command.
        return SendAsync(
            ControlMode.Simulation,
            FecControlPages.TrackResistance(
                parameters.GradeFraction, parameters.RollingResistanceCoefficient),
            parameters.GradePercent,
            cancellationToken,
            follower: FecControlPages.WindResistance(
                parameters.WindResistanceCoefficient,
                parameters.WindSpeedMetersPerSecond * 3.6));
    }

    public Task<ControlResult> SetTargetPowerAsync(
        int watts, CancellationToken cancellationToken = default)
    {
        Capabilities.TryClampPower(watts, out int applied);
        return SendAsync(
            ControlMode.Erg, FecControlPages.TargetPower(applied), applied, cancellationToken);
    }

    public Task<ControlResult> SetResistanceLevelAsync(
        double level, CancellationToken cancellationToken = default)
    {
        Capabilities.TryClampResistance(level, out double applied);
        return SendAsync(
            ControlMode.Resistance, FecControlPages.BasicResistance(applied), applied,
            cancellationToken);
    }

    /// <summary>
    /// FE-C has no reset op-code; returning to a flat track with no wind is the equivalent of
    /// handing the trainer back to the rider.
    /// </summary>
    public async Task<ControlResult> ResetAsync(CancellationToken cancellationToken = default)
    {
        var result = await SetSimulationAsync(SimulationParameters.Flat, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success) SetMode(ControlMode.None);
        return result;
    }

    private async Task<ControlResult> SendAsync(
        ControlMode mode,
        byte[] page,
        double appliedValue,
        CancellationToken cancellationToken,
        byte[]? follower = null)
    {
        bool acknowledged;
        try
        {
            acknowledged = await _channel
                .SendAcknowledgedAsync(page, cancellationToken).ConfigureAwait(false);

            if (acknowledged && follower is not null)
                await _channel.SendAcknowledgedAsync(follower, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ControlResult.Fail(ControlFailure.NotConnected, $"FE-C send failed: {ex.Message}");
        }

        if (!acknowledged)
            return ControlResult.Fail(
                ControlFailure.Timeout, $"Trainer did not acknowledge page 0x{page[0]:X2}");

        SetMode(mode);
        return ControlResult.Ok(appliedValue);
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

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        await _channel.DisposeAsync().ConfigureAwait(false);

        _power.Dispose();
        _cadence.Dispose();
        _speed.Dispose();
        _distance.Dispose();
        _heartRate.Dispose();
        _modeChanges.Dispose();
    }
}

/// <summary>An ANT+ heart rate monitor.</summary>
public sealed class AntHeartRateCapability : IHeartRateSource, IAsyncDisposable
{
    private readonly IAntChannel _channel;
    private readonly IDisposable _subscription;
    private readonly Subject<HeartRateSample> _heartRate = new();

    public AntHeartRateCapability(IAntChannel channel, string deviceName)
    {
        _channel = channel;
        DeviceName = deviceName;
        Source = new SourceId(
            channel.DeviceNumber.ToString(), TransportKind.AntPlus, Channels.AntHeartRate);

        _subscription = channel.Broadcasts.Subscribe(OnBroadcast);
    }

    public SourceId Source { get; }
    public string DeviceName { get; }
    public IObservable<HeartRateSample> HeartRate => _heartRate;

    /// <summary>True: an ANT+ HRM is a worn strap, so it outranks a machine's relayed rate.</summary>
    public bool IsWornSensor => true;

    private void OnBroadcast(AntBroadcast broadcast)
    {
        var data = AntPageParser.ParseHeartRate(broadcast.Payload);
        if (data.ComputedHeartRateBpm <= 0) return;   // sensor has not settled yet

        _heartRate.OnNext(new HeartRateSample(
            data.ComputedHeartRateBpm, null, null, null, broadcast.ReceivedAt, Source));
    }

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        await _channel.DisposeAsync().ConfigureAwait(false);
        _heartRate.Dispose();
    }
}
