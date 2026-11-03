using System.Reactive.Subjects;
using RideLink.Capabilities;
using RideLink.Model;
using Xunit;

namespace RideLink.Tests;

/// <summary>A capability whose streams the test drives directly.</summary>
internal sealed class FakeSource(SourceId source, string deviceName)
    : ICapability, IPowerSource, ICadenceSource, IHeartRateSource, ISpeedSource
{
    private readonly Subject<PowerSample> _power = new();
    private readonly Subject<CadenceSample> _cadence = new();
    private readonly Subject<HeartRateSample> _heartRate = new();
    private readonly Subject<SpeedSample> _speed = new();

    public SourceId Source { get; } = source;
    public string DeviceName { get; } = deviceName;

    public IObservable<PowerSample> Power => _power;
    public IObservable<CadenceSample> Cadence => _cadence;
    public IObservable<HeartRateSample> HeartRate => _heartRate;
    public IObservable<SpeedSample> Speed => _speed;

    public bool IsDirectForceMeasurement { get; init; }
    public bool IsWornSensor { get; init; }

    public void EmitPower(int watts, DateTimeOffset at) =>
        _power.OnNext(new PowerSample(watts, at, Source));

    public void EmitCadence(double rpm, DateTimeOffset at) =>
        _cadence.OnNext(new CadenceSample(rpm, at, Source));

    public void EmitHeartRate(int bpm, DateTimeOffset at) =>
        _heartRate.OnNext(new HeartRateSample(bpm, null, null, null, at, Source));

    public void EmitSpeed(double ms, DateTimeOffset at) =>
        _speed.OnNext(new SpeedSample(ms, at, Source));
}

/// <summary>A clock the test moves by hand, so staleness is deterministic.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The router's job is to answer "which watts?" when several devices all claim to know.
/// </summary>
public class CapabilityRouterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static FakeSource PowerMeter(string id = "pm") =>
        new(new SourceId(id, TransportKind.BluetoothLowEnergy, Channels.CyclingPower), "Power Meter")
        { IsDirectForceMeasurement = true };

    private static FakeSource Trainer(string id = "trainer") =>
        new(new SourceId(id, TransportKind.BluetoothLowEnergy, Channels.FtmsIndoorBike), "Trainer");

    private static FakeSource ChestStrap(string id = "strap") =>
        new(new SourceId(id, TransportKind.BluetoothLowEnergy, Channels.HeartRate), "Chest Strap")
        { IsWornSensor = true };

    private static FakeSource CadenceSensor(string id = "csc") =>
        new(new SourceId(id, TransportKind.BluetoothLowEnergy, Channels.SpeedCadence), "Cadence Sensor");

    [Fact]
    public void Single_source_is_forwarded()
    {
        var clock = new ManualClock(T0);
        using var router = new CapabilityRouter(clock: clock);

        var trainer = Trainer();
        router.Register(trainer);

        var seen = new List<int>();
        using var _ = router.Power.Subscribe(s => seen.Add(s.Watts));

        trainer.EmitPower(200, clock.GetUtcNow());

        Assert.Equal([200], seen);
    }

    [Fact]
    public void Power_meter_wins_over_trainer_estimate()
    {
        var clock = new ManualClock(T0);
        using var router = new CapabilityRouter(clock: clock);

        var trainer = Trainer();
        var meter = PowerMeter();
        router.Register(trainer);
        router.Register(meter);

        var seen = new List<int>();
        using var _ = router.Power.Subscribe(s => seen.Add(s.Watts));

        trainer.EmitPower(200, clock.GetUtcNow());   // wins: only live source so far
        meter.EmitPower(210, clock.GetUtcNow());     // wins: higher rank
        trainer.EmitPower(205, clock.GetUtcNow());   // dropped: meter outranks it

        Assert.Equal([200, 210], seen);
    }

    [Fact]
    public void Falls_back_when_the_preferred_source_goes_stale()
    {
        var clock = new ManualClock(T0);
        using var router = new CapabilityRouter(clock: clock);

        var trainer = Trainer();
        var meter = PowerMeter();
        router.Register(trainer);
        router.Register(meter);

        var seen = new List<int>();
        using var _ = router.Power.Subscribe(s => seen.Add(s.Watts));

        meter.EmitPower(250, clock.GetUtcNow());
        trainer.EmitPower(240, clock.GetUtcNow());   // dropped, meter is live

        // The meter's battery dies. Past the staleness window the trainer takes over — and
        // notice nothing but the trainer's own next sample triggers the handover.
        clock.Advance(TimeSpan.FromSeconds(10));
        trainer.EmitPower(230, clock.GetUtcNow());

        Assert.Equal([250, 230], seen);
    }

    [Fact]
    public void Recovers_the_preferred_source_when_it_comes_back()
    {
        var clock = new ManualClock(T0);
        using var router = new CapabilityRouter(clock: clock);

        var trainer = Trainer();
        var meter = PowerMeter();
        router.Register(trainer);
        router.Register(meter);

        var seen = new List<int>();
        using var _ = router.Power.Subscribe(s => seen.Add(s.Watts));

        meter.EmitPower(250, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(10));
        trainer.EmitPower(230, clock.GetUtcNow());

        meter.EmitPower(260, clock.GetUtcNow());     // back, and immediately preferred again
        trainer.EmitPower(235, clock.GetUtcNow());   // dropped again

        Assert.Equal([250, 230, 260], seen);
    }

    [Fact]
    public void Chest_strap_wins_over_a_machine_relayed_heart_rate()
    {
        var clock = new ManualClock(T0);
        using var router = new CapabilityRouter(clock: clock);

        var trainer = Trainer();
        var strap = ChestStrap();
        router.Register(trainer);
        router.Register(strap);

        var seen = new List<int>();
        using var _ = router.HeartRate.Subscribe(s => seen.Add(s.Bpm));

        trainer.EmitHeartRate(120, clock.GetUtcNow());
        strap.EmitHeartRate(145, clock.GetUtcNow());
        trainer.EmitHeartRate(121, clock.GetUtcNow());

        Assert.Equal([120, 145], seen);
    }

    [Fact]
    public void Dedicated_cadence_sensor_wins_over_power_meter_and_trainer()
    {
        var clock = new ManualClock(T0);
        using var router = new CapabilityRouter(clock: clock);

        var trainer = Trainer();
        var meter = PowerMeter();
        var sensor = CadenceSensor();
        router.Register(trainer);
        router.Register(meter);
        router.Register(sensor);

        var seen = new List<double>();
        using var _ = router.Cadence.Subscribe(s => seen.Add(s.Rpm));

        trainer.EmitCadence(80, clock.GetUtcNow());
        meter.EmitCadence(85, clock.GetUtcNow());    // beats the trainer
        sensor.EmitCadence(90, clock.GetUtcNow());   // beats both
        meter.EmitCadence(86, clock.GetUtcNow());    // dropped
        trainer.EmitCadence(81, clock.GetUtcNow());  // dropped

        Assert.Equal([80, 85, 90], seen);
    }

    [Fact]
    public void Trainer_speed_wins_over_a_wheel_sensor_indoors()
    {
        var clock = new ManualClock(T0);
        using var router = new CapabilityRouter(clock: clock);

        var trainer = Trainer();
        var wheel = CadenceSensor();
        router.Register(wheel);
        router.Register(trainer);

        var seen = new List<double>();
        using var _ = router.Speed.Subscribe(s => seen.Add(s.MetersPerSecond));

        wheel.EmitSpeed(8.0, clock.GetUtcNow());
        trainer.EmitSpeed(9.0, clock.GetUtcNow());
        wheel.EmitSpeed(8.1, clock.GetUtcNow());

        Assert.Equal([8.0, 9.0], seen);
    }

    [Fact]
    public void Reports_which_device_serves_each_quantity()
    {
        var clock = new ManualClock(T0);
        using var router = new CapabilityRouter(clock: clock);

        var trainer = Trainer();
        var meter = PowerMeter();
        router.Register(trainer);
        router.Register(meter);

        var changes = new List<ActiveSourceChanged>();
        using var _ = router.ActiveSourceChanges.Subscribe(changes.Add);

        trainer.EmitPower(200, clock.GetUtcNow());
        meter.EmitPower(210, clock.GetUtcNow());

        Assert.Equal(2, changes.Count);
        Assert.Equal("Power", changes[0].Quantity);
        Assert.Equal("Trainer", changes[0].DeviceName);
        Assert.Null(changes[0].Previous);
        Assert.Equal("Power Meter", changes[1].DeviceName);
        Assert.Equal(trainer.Source, changes[1].Previous);
    }

    [Fact]
    public void Unregistering_removes_a_source_from_the_contest()
    {
        var clock = new ManualClock(T0);
        using var router = new CapabilityRouter(clock: clock);

        var trainer = Trainer();
        var meter = PowerMeter();
        router.Register(trainer);
        var meterRegistration = router.Register(meter);

        var seen = new List<int>();
        using var _ = router.Power.Subscribe(s => seen.Add(s.Watts));

        meter.EmitPower(250, clock.GetUtcNow());
        trainer.EmitPower(240, clock.GetUtcNow());   // dropped

        meterRegistration.Dispose();
        trainer.EmitPower(238, clock.GetUtcNow());   // now the only source

        Assert.Equal([250, 238], seen);
    }

    [Fact]
    public void Identical_sensors_do_not_ping_pong()
    {
        var clock = new ManualClock(T0);
        using var router = new CapabilityRouter(clock: clock);

        // Two power meters of the same rank: whoever wins first should keep winning rather
        // than the stream alternating between them on every sample.
        var first = PowerMeter("pm-1");
        var second = PowerMeter("pm-2");
        router.Register(first);
        router.Register(second);

        var seen = new List<int>();
        using var _ = router.Power.Subscribe(s => seen.Add(s.Watts));

        first.EmitPower(200, clock.GetUtcNow());
        second.EmitPower(300, clock.GetUtcNow());
        first.EmitPower(201, clock.GetUtcNow());
        second.EmitPower(301, clock.GetUtcNow());

        Assert.Equal([200, 201], seen);
    }

    [Fact]
    public void A_custom_policy_can_invert_the_defaults()
    {
        var clock = new ManualClock(T0);
        using var router = new CapabilityRouter(policy: new TrainerFirstPolicy(), clock: clock);

        var trainer = Trainer();
        var meter = PowerMeter();
        router.Register(meter);
        router.Register(trainer);

        var seen = new List<int>();
        using var _ = router.Power.Subscribe(s => seen.Add(s.Watts));

        meter.EmitPower(250, clock.GetUtcNow());
        trainer.EmitPower(240, clock.GetUtcNow());
        meter.EmitPower(251, clock.GetUtcNow());     // now dropped

        Assert.Equal([250, 240], seen);
    }

    private sealed class TrainerFirstPolicy : SourcePriorityPolicy
    {
        public override int RankPower(IPowerSource source) =>
            source.Source.Channel == Channels.FtmsIndoorBike ? 1000 : 1;
    }
}
