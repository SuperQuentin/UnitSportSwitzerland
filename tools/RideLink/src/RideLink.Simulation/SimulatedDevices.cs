using RideLink.Ble;
using RideLink.Ble.Ftms;
using RideLink.Ble.Parsers;
using RideLink.Capabilities;
using RideLink.Parsing;
using RideLink.Transport;

namespace RideLink.Simulation;

/// <summary>
/// A virtual GATT peripheral. Subclasses decide which endpoints exist, what a read returns,
/// how a write is answered, and what to notify on each tick.
/// </summary>
public abstract class SimulatedDevice
{
    protected SimulatedDevice(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    /// <summary>Service identities the device "advertises", used by scan filters.</summary>
    public abstract IReadOnlyList<string> AdvertisedServices { get; }

    /// <summary>Everything the device exposes, returned verbatim by discovery.</summary>
    public abstract IReadOnlyList<EndpointDescriptor> Endpoints { get; }

    /// <summary>Simulated signal strength.</summary>
    public int Rssi { get; set; } = -55;

    /// <summary>Answers a read. Throws for endpoints that are not readable.</summary>
    public virtual byte[] Read(EndpointId endpoint) =>
        throw new InvalidOperationException($"{Name} has no readable {endpoint}");

    /// <summary>
    /// Handles a write. <paramref name="indicate"/> delivers the response the real device
    /// would have indicated back on the same characteristic.
    /// </summary>
    public virtual void Write(EndpointId endpoint, byte[] value, Action<EndpointId, byte[]> indicate) { }

    /// <summary>
    /// Produces this interval's notifications. Called on a background timer by the connection.
    /// </summary>
    public abstract void Tick(TimeSpan elapsed, TimeSpan delta, Action<EndpointId, byte[]> notify);
}

/// <summary>
/// A virtual smart trainer speaking FTMS Indoor Bike Data with a working Control Point.
/// <para>
/// It implements the full handshake — commands are refused with Control Not Permitted until
/// Request Control succeeds — and honours SIM, ERG and Resistance, so the control path is
/// exercised end to end with no hardware. <see cref="SupportsErg"/> can be turned off to
/// reproduce a trainer that only does SIM, which is the case the engine has to report cleanly.
/// </para>
/// </summary>
public sealed class SimulatedTrainer : SimulatedDevice
{
    private static readonly EndpointId IndoorBike =
        EndpointId.Ble(BleUuids.FitnessMachineService, BleUuids.IndoorBikeData);
    private static readonly EndpointId Feature =
        EndpointId.Ble(BleUuids.FitnessMachineService, BleUuids.FitnessMachineFeature);
    private static readonly EndpointId ControlPoint =
        EndpointId.Ble(BleUuids.FitnessMachineService, BleUuids.FitnessMachineControlPoint);
    private static readonly EndpointId Status =
        EndpointId.Ble(BleUuids.FitnessMachineService, BleUuids.FitnessMachineStatus);
    private static readonly EndpointId PowerRangeEndpoint =
        EndpointId.Ble(BleUuids.FitnessMachineService, BleUuids.SupportedPowerRange);
    private static readonly EndpointId ResistanceRangeEndpoint =
        EndpointId.Ble(BleUuids.FitnessMachineService, BleUuids.SupportedResistanceLevelRange);
    private static readonly EndpointId BatteryLevel =
        EndpointId.Ble(BleUuids.BatteryService, BleUuids.BatteryLevel);
    private static readonly EndpointId Manufacturer =
        EndpointId.Ble(BleUuids.DeviceInformationService, BleUuids.ManufacturerNameString);
    private static readonly EndpointId Model =
        EndpointId.Ble(BleUuids.DeviceInformationService, BleUuids.ModelNumberString);

    private readonly IRideProfile _profile;
    private readonly object _gate = new();

    private bool _hasControl;
    private ControlMode _mode = ControlMode.None;
    private int _targetWatts;
    private double _gradeFraction;
    private double _resistanceLevel;
    private double _currentWatts;
    private double _distanceMeters;
    private double _speedMetersPerSecond;

    public SimulatedTrainer(
        string id = "sim-trainer",
        string name = "RideLink Sim Trainer",
        IRideProfile? profile = null)
        : base(id, name)
    {
        _profile = profile ?? new SteadyRideProfile();
    }

    /// <summary>Turn off to simulate a trainer with no ERG mode.</summary>
    public bool SupportsErg { get; init; } = true;

    /// <summary>Turn off to simulate a trainer with no SIM mode.</summary>
    public bool SupportsSimulation { get; init; } = true;

    /// <summary>Turn off to simulate a trainer with no raw resistance control.</summary>
    public bool SupportsResistance { get; init; } = true;

    /// <summary>Advertised ERG range, also used to clamp.</summary>
    public PowerRange PowerRange { get; init; } = new(30, 2000, 1);

    /// <summary>Advertised resistance range.</summary>
    public ResistanceLevelRange ResistanceRange { get; init; } = new(0, 100, 0.5);

    /// <summary>Combined rider and bike mass used by the speed model, kg.</summary>
    public double TotalMassKg { get; init; } = 85;

    /// <summary>Current control mode, for assertions in tests and for the demo's display.</summary>
    public ControlMode Mode { get { lock (_gate) return _mode; } }

    /// <summary>Last grade written by the host, as a fraction.</summary>
    public double GradeFraction { get { lock (_gate) return _gradeFraction; } }

    /// <summary>Last ERG target written by the host, watts.</summary>
    public int TargetWatts { get { lock (_gate) return _targetWatts; } }

    public override IReadOnlyList<string> AdvertisedServices =>
        [EndpointId.Ble(BleUuids.FitnessMachineService, 0).Service];

    public override IReadOnlyList<EndpointDescriptor> Endpoints =>
    [
        new(IndoorBike, EndpointProperties.Notify),
        new(Feature, EndpointProperties.Read),
        new(PowerRangeEndpoint, EndpointProperties.Read),
        new(ResistanceRangeEndpoint, EndpointProperties.Read),
        // The Control Point is Write plus Indicate: the indication is the transaction result.
        new(ControlPoint, EndpointProperties.Write | EndpointProperties.Indicate),
        new(Status, EndpointProperties.Notify),
        new(BatteryLevel, EndpointProperties.Read | EndpointProperties.Notify),
        new(Manufacturer, EndpointProperties.Read),
        new(Model, EndpointProperties.Read),
    ];

    public override byte[] Read(EndpointId endpoint)
    {
        if (endpoint == Feature)
        {
            uint features =
                (uint)(FitnessMachineFeatures.Cadence
                     | FitnessMachineFeatures.TotalDistance
                     | FitnessMachineFeatures.PowerMeasurement
                     | FitnessMachineFeatures.ResistanceLevel
                     | FitnessMachineFeatures.HeartRateMeasurement
                     | FitnessMachineFeatures.ElapsedTime);

            uint targets = 0;
            if (SupportsErg) targets |= (uint)FitnessMachineTargetFeatures.PowerTarget;
            if (SupportsSimulation)
                targets |= (uint)FitnessMachineTargetFeatures.IndoorBikeSimulationParameters;
            if (SupportsResistance) targets |= (uint)FitnessMachineTargetFeatures.ResistanceTarget;

            return GattEncoders.FitnessMachineFeature(features, targets);
        }

        if (endpoint == PowerRangeEndpoint)
            return GattEncoders.SupportedPowerRange(
                PowerRange.MinimumWatts, PowerRange.MaximumWatts, PowerRange.MinimumIncrementWatts);

        if (endpoint == ResistanceRangeEndpoint)
            return GattEncoders.SupportedResistanceLevelRange(
                ResistanceRange.Minimum, ResistanceRange.Maximum, ResistanceRange.MinimumIncrement);

        if (endpoint == BatteryLevel) return [78];
        if (endpoint == Manufacturer) return "RideLink"u8.ToArray();
        if (endpoint == Model) return "Sim Trainer 1"u8.ToArray();

        return base.Read(endpoint);
    }

    public override void Write(EndpointId endpoint, byte[] value, Action<EndpointId, byte[]> indicate)
    {
        if (endpoint != ControlPoint || value.Length == 0) return;

        var opCode = (FtmsOpCode)value[0];
        var result = Apply(opCode, value);
        indicate(ControlPoint, GattEncoders.ControlPointResponse((byte)opCode, (byte)result));
    }

    private FtmsResultCode Apply(FtmsOpCode opCode, byte[] value)
    {
        lock (_gate)
        {
            switch (opCode)
            {
                case FtmsOpCode.RequestControl:
                    _hasControl = true;
                    return FtmsResultCode.Success;

                case FtmsOpCode.Reset:
                    if (!_hasControl) return FtmsResultCode.ControlNotPermitted;
                    _mode = ControlMode.None;
                    _targetWatts = 0;
                    _gradeFraction = 0;
                    _resistanceLevel = 0;
                    return FtmsResultCode.Success;

                case FtmsOpCode.StartOrResume:
                case FtmsOpCode.StopOrPause:
                    return _hasControl ? FtmsResultCode.Success : FtmsResultCode.ControlNotPermitted;

                case FtmsOpCode.SetTargetPower:
                {
                    if (!SupportsErg) return FtmsResultCode.OpCodeNotSupported;
                    if (!_hasControl) return FtmsResultCode.ControlNotPermitted;
                    if (value.Length < 3) return FtmsResultCode.InvalidParameter;

                    short watts = (short)(value[1] | (value[2] << 8));
                    if (watts < PowerRange.MinimumWatts || watts > PowerRange.MaximumWatts)
                        return FtmsResultCode.InvalidParameter;

                    _targetWatts = watts;
                    _mode = ControlMode.Erg;
                    return FtmsResultCode.Success;
                }

                case FtmsOpCode.SetIndoorBikeSimulationParameters:
                {
                    if (!SupportsSimulation) return FtmsResultCode.OpCodeNotSupported;
                    if (!_hasControl) return FtmsResultCode.ControlNotPermitted;
                    if (value.Length < 7) return FtmsResultCode.InvalidParameter;

                    // Grade is sint16 in hundredths of a percent (FTMS v1.0, §4.16.2.18).
                    short rawGrade = (short)(value[3] | (value[4] << 8));
                    _gradeFraction = rawGrade * 0.01 / 100.0;
                    _mode = ControlMode.Simulation;
                    return FtmsResultCode.Success;
                }

                case FtmsOpCode.SetTargetResistanceLevel:
                {
                    if (!SupportsResistance) return FtmsResultCode.OpCodeNotSupported;
                    if (!_hasControl) return FtmsResultCode.ControlNotPermitted;
                    if (value.Length < 2) return FtmsResultCode.InvalidParameter;

                    double level = value[1] * 0.1;
                    if (level < ResistanceRange.Minimum || level > ResistanceRange.Maximum)
                        return FtmsResultCode.InvalidParameter;

                    _resistanceLevel = level;
                    _mode = ControlMode.Resistance;
                    return FtmsResultCode.Success;
                }

                default:
                    return FtmsResultCode.OpCodeNotSupported;
            }
        }
    }

    public override void Tick(TimeSpan elapsed, TimeSpan delta, Action<EndpointId, byte[]> notify)
    {
        var rider = _profile.Sample(elapsed);

        double watts;
        double grade;
        lock (_gate)
        {
            watts = _mode switch
            {
                // ERG servos to the target; the ramp keeps it from stepping instantly, which
                // is what makes the demo visibly "catch up" to a new target.
                ControlMode.Erg => Approach(_currentWatts, _targetWatts, delta, ratePerSecond: 120),

                // Raw resistance makes the same pedalling effort harder.
                ControlMode.Resistance => rider.PowerWatts * (1 + _resistanceLevel * 0.03),

                // In SIM the rider's effort is their own; the hill changes their speed, not
                // their power. That is the physically honest mapping and the one that makes a
                // game's grade feel right.
                _ => rider.PowerWatts,
            };

            _currentWatts = watts;
            grade = _gradeFraction;
        }

        _speedMetersPerSecond = SolveSpeed(watts, grade, TotalMassKg);
        _distanceMeters += _speedMetersPerSecond * delta.TotalSeconds;

        notify(IndoorBike, GattEncoders.IndoorBikeData(
            speedMetersPerSecond: _speedMetersPerSecond,
            cadenceRpm: rider.CadenceRpm,
            totalDistanceMeters: _distanceMeters,
            instantaneousPowerWatts: (int)Math.Round(watts),
            heartRateBpm: rider.HeartRateBpm,
            elapsed: elapsed));
    }

    private static double Approach(double current, double target, TimeSpan delta, double ratePerSecond)
    {
        double step = ratePerSecond * delta.TotalSeconds;
        double diff = target - current;
        return Math.Abs(diff) <= step ? target : current + Math.Sign(diff) * step;
    }

    /// <summary>
    /// Solves the steady-state cycling power equation for speed:
    /// <c>P = v · (Crr·m·g + m·g·grade + ½·ρ·CdA·v²)</c>.
    /// <para>
    /// Newton's method on a monotonic cubic, so it converges in a handful of iterations. This
    /// is what makes a grade change visibly slow the rider down in the demo rather than just
    /// changing a number.
    /// </para>
    /// </summary>
    private static double SolveSpeed(double watts, double gradeFraction, double massKg)
    {
        if (watts <= 0) return 0;

        const double g = 9.81, crr = 0.004, rho = 1.225, cda = 0.32;
        double rolling = crr * massKg * g;
        double gravity = massKg * g * gradeFraction;
        double drag = 0.5 * rho * cda;

        double v = 8.0;   // a sane starting guess, m/s
        for (int i = 0; i < 40; i++)
        {
            double f = v * (rolling + gravity + drag * v * v) - watts;
            double df = rolling + gravity + 3 * drag * v * v;
            if (Math.Abs(df) < 1e-9) break;

            double next = v - f / df;
            if (double.IsNaN(next) || double.IsInfinity(next)) break;

            next = Math.Clamp(next, 0, 40);
            if (Math.Abs(next - v) < 1e-6) { v = next; break; }
            v = next;
        }

        return Math.Max(0, v);
    }
}

/// <summary>A virtual chest strap speaking the Heart Rate Service, with RR intervals.</summary>
public sealed class SimulatedHeartRateStrap(
    string id = "sim-hrm",
    string name = "RideLink Sim HRM",
    IRideProfile? profile = null)
    : SimulatedDevice(id, name)
{
    private static readonly EndpointId Measurement =
        EndpointId.Ble(BleUuids.HeartRateService, BleUuids.HeartRateMeasurement);
    private static readonly EndpointId BatteryLevel =
        EndpointId.Ble(BleUuids.BatteryService, BleUuids.BatteryLevel);

    private readonly IRideProfile _profile = profile ?? new SteadyRideProfile();

    public override IReadOnlyList<string> AdvertisedServices =>
        [EndpointId.Ble(BleUuids.HeartRateService, 0).Service];

    public override IReadOnlyList<EndpointDescriptor> Endpoints =>
    [
        new(Measurement, EndpointProperties.Notify),
        new(BatteryLevel, EndpointProperties.Read),
    ];

    public override byte[] Read(EndpointId endpoint) =>
        endpoint == BatteryLevel ? [92] : base.Read(endpoint);

    public override void Tick(TimeSpan elapsed, TimeSpan delta, Action<EndpointId, byte[]> notify)
    {
        int bpm = _profile.Sample(elapsed).HeartRateBpm;
        if (bpm <= 0) return;

        // One RR interval consistent with the reported rate, so RR parsing has real input.
        double rr = 60.0 / bpm;
        notify(Measurement, GattEncoders.HeartRateMeasurement(bpm, sensorContact: true, [rr]));
    }
}

/// <summary>
/// A virtual crank-based power meter speaking the Cycling Power Service.
/// <para>
/// It advances the cumulative crank counters exactly as hardware does, including letting them
/// wrap, so the engine's rollover handling is exercised on every long simulated ride rather
/// than only in a unit test.
/// </para>
/// </summary>
public sealed class SimulatedPowerMeter(
    string id = "sim-power",
    string name = "RideLink Sim Power Meter",
    IRideProfile? profile = null)
    : SimulatedDevice(id, name)
{
    private static readonly EndpointId Measurement =
        EndpointId.Ble(BleUuids.CyclingPowerService, BleUuids.CyclingPowerMeasurement);
    private static readonly EndpointId BatteryLevel =
        EndpointId.Ble(BleUuids.BatteryService, BleUuids.BatteryLevel);

    private readonly IRideProfile _profile = profile ?? new SteadyRideProfile();

    private double _exactRevolutions;
    private double _sessionSeconds;
    private long _completedRevolutions;
    private double _lastEventSeconds;

    public override IReadOnlyList<string> AdvertisedServices =>
        [EndpointId.Ble(BleUuids.CyclingPowerService, 0).Service];

    public override IReadOnlyList<EndpointDescriptor> Endpoints =>
    [
        new(Measurement, EndpointProperties.Notify),
        new(BatteryLevel, EndpointProperties.Read),
    ];

    public override byte[] Read(EndpointId endpoint) =>
        endpoint == BatteryLevel ? [64] : base.Read(endpoint);

    public override void Tick(TimeSpan elapsed, TimeSpan delta, Action<EndpointId, byte[]> notify)
    {
        var rider = _profile.Sample(elapsed);
        AdvanceCrank(rider.CadenceRpm, delta);

        notify(Measurement, GattEncoders.CyclingPowerMeasurement(
            rider.PowerWatts,
            cumulativeCrankRevolutions: (ushort)(_completedRevolutions & 0xFFFF),
            lastCrankEventTime: (ushort)((long)Math.Round(_lastEventSeconds * 1024.0) & 0xFFFF)));
    }

    /// <summary>
    /// Advances the crank counters the way hardware does.
    /// <para>
    /// Last Crank Event Time is the instant the most recent <b>whole</b> revolution completed,
    /// not the time the packet was assembled. Advancing it every notification instead — which
    /// is the obvious-looking shortcut — makes the pair inconsistent: a notification where the
    /// revolution counter happens to tick over reports one revolution across one notification
    /// interval, and the decoded cadence comes out as the notification rate in rpm rather than
    /// the rider's. At 4 Hz that reads as a very believable 240 rpm.
    /// </para>
    /// </summary>
    private void AdvanceCrank(double cadenceRpm, TimeSpan delta)
    {
        double seconds = delta.TotalSeconds;
        double revolutionsThisTick = Math.Max(0, cadenceRpm) / 60.0 * seconds;

        double before = _exactRevolutions;
        _exactRevolutions += revolutionsThisTick;
        _sessionSeconds += seconds;

        long whole = (long)_exactRevolutions;
        if (whole <= _completedRevolutions || revolutionsThisTick <= 0) return;

        // Interpolate back to the moment that revolution actually completed.
        double fraction = (whole - before) / revolutionsThisTick;
        _lastEventSeconds = _sessionSeconds - seconds + Math.Clamp(fraction, 0, 1) * seconds;
        _completedRevolutions = whole;
    }
}
