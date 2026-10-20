using System.Reactive.Subjects;
using RideLink.Ble.Parsers;
using RideLink.Capabilities;
using RideLink.Model;
using RideLink.Parsing;

namespace RideLink.Ble;

/// <summary>
/// Shared plumbing for a capability that publishes measurements: identity plus the subjects
/// the interfaces expose. Publishing is inline; the router is what marshals to the host thread.
/// </summary>
public abstract class BleCapabilityBase : ICapability, IDisposable
{
    private readonly Subject<PowerSample> _power = new();
    private readonly Subject<CadenceSample> _cadence = new();
    private readonly Subject<SpeedSample> _speed = new();
    private readonly Subject<DistanceSample> _distance = new();
    private readonly Subject<HeartRateSample> _heartRate = new();

    protected BleCapabilityBase(SourceId source, string deviceName)
    {
        Source = source;
        DeviceName = deviceName;
    }

    public SourceId Source { get; }
    public string DeviceName { get; }

    /// <summary>Time source, injectable so tests are deterministic.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    protected IObservable<PowerSample> PowerStream => _power;
    protected IObservable<CadenceSample> CadenceStream => _cadence;
    protected IObservable<SpeedSample> SpeedStream => _speed;
    protected IObservable<DistanceSample> DistanceStream => _distance;
    protected IObservable<HeartRateSample> HeartRateStream => _heartRate;

    protected void PublishPower(int watts, DateTimeOffset at) =>
        _power.OnNext(new PowerSample(watts, at, Source));

    protected void PublishCadence(double rpm, DateTimeOffset at) =>
        _cadence.OnNext(new CadenceSample(rpm, at, Source));

    protected void PublishSpeed(double metersPerSecond, DateTimeOffset at) =>
        _speed.OnNext(new SpeedSample(metersPerSecond, at, Source));

    protected void PublishDistance(double meters, DateTimeOffset at) =>
        _distance.OnNext(new DistanceSample(meters, at, Source));

    protected void PublishHeartRate(HeartRateSample sample) => _heartRate.OnNext(sample);

    public virtual void Dispose()
    {
        _power.Dispose();
        _cadence.Dispose();
        _speed.Dispose();
        _distance.Dispose();
        _heartRate.Dispose();
    }
}

/// <summary>Heart Rate Service capability. A worn sensor, so it outranks a machine's relay.</summary>
public sealed class HeartRateCapability : BleCapabilityBase, IHeartRateSource
{
    public HeartRateCapability(SourceId source, string deviceName) : base(source, deviceName) { }

    public IObservable<HeartRateSample> HeartRate => HeartRateStream;

    public bool IsWornSensor => true;

    /// <summary>Feeds one Heart Rate Measurement value.</summary>
    public void Ingest(ReadOnlySpan<byte> payload, DateTimeOffset at)
    {
        var m = HeartRateMeasurementParser.Parse(payload);
        PublishHeartRate(new HeartRateSample(
            m.BeatsPerMinute,
            m.SensorContact,
            m.RrIntervals.Count == 0 ? null : m.RrIntervals,
            m.EnergyExpendedKilojoules,
            at,
            Source));
    }
}

/// <summary>
/// Cycling Speed and Cadence capability. Rates are derived from the cumulative counters by
/// <see cref="RevolutionRateTracker"/>, which is where rollover is handled.
/// </summary>
public sealed class CscCapability : BleCapabilityBase, ICadenceSource, ISpeedSource, IDistanceSource
{
    private readonly RevolutionRateTracker _crank =
        new(CscMeasurementParser.EventTimeTicksPerSecond, revolutions16Bit: true);

    private readonly WheelSpeedTracker _wheel;
    private uint? _lastWheelRevolutions;
    private double _distanceMeters;

    public CscCapability(SourceId source, string deviceName, double wheelCircumferenceMeters = 2.096)
        : base(source, deviceName)
    {
        _wheel = new WheelSpeedTracker(
            CscMeasurementParser.EventTimeTicksPerSecond, wheelCircumferenceMeters);
    }

    public IObservable<CadenceSample> Cadence => CadenceStream;
    public IObservable<SpeedSample> Speed => SpeedStream;
    public IObservable<DistanceSample> Distance => DistanceStream;

    /// <summary>Rollout used to turn wheel revolutions into distance and speed.</summary>
    public double WheelCircumferenceMeters
    {
        get => _wheel.WheelCircumferenceMeters;
        set => _wheel.WheelCircumferenceMeters = value;
    }

    public void Ingest(ReadOnlySpan<byte> payload, DateTimeOffset at)
    {
        var m = CscMeasurementParser.Parse(payload);

        if (m.CumulativeCrankRevolutions is { } crankRevs && m.LastCrankEventTime is { } crankTime
            && _crank.TryUpdate(crankRevs, crankTime, at, out double rpm))
        {
            PublishCadence(rpm, at);
        }

        if (m.CumulativeWheelRevolutions is { } wheelRevs && m.LastWheelEventTime is { } wheelTime)
        {
            if (_wheel.TryUpdate(wheelRevs, wheelTime, at, out double ms))
                PublishSpeed(ms, at);

            // Distance comes from the counter difference rather than integrating speed, so a
            // dropped notification loses no distance.
            if (_lastWheelRevolutions is { } previous)
            {
                uint delta = wheelRevs - previous;   // uint32 wrap handled by the subtraction
                _distanceMeters += delta * _wheel.WheelCircumferenceMeters;
                PublishDistance(_distanceMeters, at);
            }
            _lastWheelRevolutions = wheelRevs;
        }
    }
}

/// <summary>
/// Cycling Power Service capability: watts plus whatever crank or wheel data the meter adds.
/// </summary>
public sealed class CyclingPowerCapability
    : BleCapabilityBase, IPowerSource, ICadenceSource, ISpeedSource
{
    private readonly RevolutionRateTracker _crank = new(
        CyclingPowerMeasurementParser.CrankEventTimeTicksPerSecond, revolutions16Bit: true);

    private readonly WheelSpeedTracker _wheel = new(
        CyclingPowerMeasurementParser.WheelEventTimeTicksPerSecond);

    public CyclingPowerCapability(SourceId source, string deviceName, bool isDirectForce = true)
        : base(source, deviceName)
    {
        IsDirectForceMeasurement = isDirectForce;
    }

    public IObservable<PowerSample> Power => PowerStream;
    public IObservable<CadenceSample> Cadence => CadenceStream;
    public IObservable<SpeedSample> Speed => SpeedStream;

    /// <summary>
    /// True for a real power meter. A trainer that also exposes CPS as a courtesy should set
    /// this false so the router still prefers a pedal-based meter.
    /// </summary>
    public bool IsDirectForceMeasurement { get; }

    /// <summary>Wheel rollout, when the meter reports wheel revolutions.</summary>
    public double WheelCircumferenceMeters
    {
        get => _wheel.WheelCircumferenceMeters;
        set => _wheel.WheelCircumferenceMeters = value;
    }

    public void Ingest(ReadOnlySpan<byte> payload, DateTimeOffset at)
    {
        var m = CyclingPowerMeasurementParser.Parse(payload);
        PublishPower(m.InstantaneousPowerWatts, at);

        if (m.CumulativeCrankRevolutions is { } revs && m.LastCrankEventTime is { } time
            && _crank.TryUpdate(revs, time, at, out double rpm))
        {
            PublishCadence(rpm, at);
        }

        if (m.CumulativeWheelRevolutions is { } wheelRevs && m.LastWheelEventTime is { } wheelTime
            && _wheel.TryUpdate(wheelRevs, wheelTime, at, out double ms))
        {
            PublishSpeed(ms, at);
        }
    }
}

/// <summary>Running Speed and Cadence capability: footpod or treadmill.</summary>
public sealed class RscCapability
    : BleCapabilityBase, ISpeedSource, ICadenceSource, IDistanceSource
{
    private readonly Subject<StrideSample> _stride = new();

    public RscCapability(SourceId source, string deviceName) : base(source, deviceName) { }

    public IObservable<SpeedSample> Speed => SpeedStream;
    public IObservable<CadenceSample> Cadence => CadenceStream;
    public IObservable<DistanceSample> Distance => DistanceStream;

    /// <summary>Stride length and walking/running state, when the pod reports them.</summary>
    public IObservable<StrideSample> Stride => _stride;

    public void Ingest(ReadOnlySpan<byte> payload, DateTimeOffset at)
    {
        var m = RscMeasurementParser.Parse(payload);
        PublishSpeed(m.SpeedMetersPerSecond, at);
        PublishCadence(m.CadenceStepsPerMinute, at);

        if (m.TotalDistanceMeters is { } distance) PublishDistance(distance, at);

        _stride.OnNext(new StrideSample(m.StrideLengthMeters, m.IsRunning, at, Source));
    }

    public override void Dispose()
    {
        _stride.Dispose();
        base.Dispose();
    }
}

/// <summary>Battery Service capability.</summary>
public sealed class BatteryCapability : BleCapabilityBase, IBatterySource
{
    private readonly Subject<BatterySample> _battery = new();

    public BatteryCapability(SourceId source, string deviceName) : base(source, deviceName) { }

    public IObservable<BatterySample> Battery => _battery;

    /// <summary>Most recent level, for a device list that wants it without subscribing.</summary>
    public int? LastPercent { get; private set; }

    /// <summary>Battery Level is a single uint8 percentage. BAS v1.0, §3.1.</summary>
    public void Ingest(ReadOnlySpan<byte> payload, DateTimeOffset at)
    {
        if (payload.Length < 1) return;
        int percent = payload[0];
        LastPercent = percent;
        _battery.OnNext(new BatterySample(percent, at, Source));
    }

    public override void Dispose()
    {
        _battery.Dispose();
        base.Dispose();
    }
}
