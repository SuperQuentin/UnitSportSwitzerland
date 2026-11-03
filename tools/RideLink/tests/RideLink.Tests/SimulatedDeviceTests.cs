using RideLink.Ble.Parsers;
using RideLink.Parsing;
using RideLink.Simulation;
using RideLink.Transport;
using Xunit;

namespace RideLink.Tests;

/// <summary>
/// The simulated devices are only useful if their wire output is indistinguishable from
/// hardware. These tests decode what they emit with the production parsers and check the
/// numbers come back out.
/// </summary>
public class SimulatedDeviceTests
{
    /// <summary>Drives a device's tick loop and collects what it notified on one endpoint.</summary>
    private static List<byte[]> Run(
        SimulatedDevice device, EndpointId endpoint, int ticks, TimeSpan interval)
    {
        var captured = new List<byte[]>();
        var elapsed = TimeSpan.Zero;

        for (int i = 0; i < ticks; i++)
        {
            elapsed += interval;
            device.Tick(elapsed, interval, (id, payload) =>
            {
                if (id == endpoint) captured.Add(payload);
            });
        }

        return captured;
    }

    [Fact]
    public void Power_meter_crank_counters_decode_back_to_the_riders_cadence()
    {
        // Regression: the crank event time must be the instant the last WHOLE revolution
        // completed. Advancing it once per notification instead makes the decoded cadence
        // come out as the notification rate in rpm — 240 rpm at a 4 Hz update — which looks
        // plausible enough to ship.
        const double cadence = 88;
        var interval = TimeSpan.FromMilliseconds(250);

        var meter = new SimulatedPowerMeter(profile: new SteadyRideProfile(200, cadence, 140));
        var endpoint = EndpointId.Ble(
            RideLink.Ble.BleUuids.CyclingPowerService, RideLink.Ble.BleUuids.CyclingPowerMeasurement);

        var packets = Run(meter, endpoint, ticks: 40, interval);

        var tracker = new RevolutionRateTracker(
            CyclingPowerMeasurementParser.CrankEventTimeTicksPerSecond, revolutions16Bit: true);

        var at = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var decoded = new List<double>();

        foreach (byte[] packet in packets)
        {
            at += interval;
            var measurement = CyclingPowerMeasurementParser.Parse(packet);

            Assert.NotNull(measurement.CumulativeCrankRevolutions);
            Assert.NotNull(measurement.LastCrankEventTime);

            if (tracker.TryUpdate(
                    measurement.CumulativeCrankRevolutions!.Value,
                    measurement.LastCrankEventTime!.Value,
                    at,
                    out double rpm))
            {
                decoded.Add(rpm);
            }
        }

        // Ignore the settling samples; the steady-state value is what matters.
        var settled = decoded.Skip(8).ToList();
        Assert.NotEmpty(settled);

        double average = settled.Average();
        Assert.InRange(average, cadence - 6, cadence + 6);

        // And nothing should ever spike anywhere near the notification-rate artefact.
        Assert.All(settled, rpm => Assert.InRange(rpm, 40, 140));
    }

    [Fact]
    public void Trainer_indoor_bike_data_decodes_back_to_what_it_was_given()
    {
        var trainer = new SimulatedTrainer(profile: new SteadyRideProfile(250, 95, 155));
        var endpoint = EndpointId.Ble(
            RideLink.Ble.BleUuids.FitnessMachineService, RideLink.Ble.BleUuids.IndoorBikeData);

        var packets = Run(trainer, endpoint, ticks: 8, TimeSpan.FromMilliseconds(250));

        Assert.NotEmpty(packets);
        var data = IndoorBikeDataParser.Parse(packets[^1]);

        Assert.InRange(data.InstantaneousPowerWatts!.Value, 240, 260);
        Assert.InRange(data.CadenceRpm!.Value, 90, 100);
        Assert.InRange(data.HeartRateBpm!.Value, 150, 160);
        Assert.True(data.SpeedMetersPerSecond > 0);
        Assert.True(data.TotalDistanceMeters >= 0);
    }

    [Fact]
    public void Heart_rate_strap_emits_an_rr_interval_consistent_with_its_own_bpm()
    {
        var strap = new SimulatedHeartRateStrap(profile: new SteadyRideProfile(200, 90, 150));
        var endpoint = EndpointId.Ble(
            RideLink.Ble.BleUuids.HeartRateService, RideLink.Ble.BleUuids.HeartRateMeasurement);

        var packets = Run(strap, endpoint, ticks: 4, TimeSpan.FromSeconds(1));

        var measurement = HeartRateMeasurementParser.Parse(packets[^1]);

        Assert.InRange(measurement.BeatsPerMinute, 145, 155);
        Assert.True(measurement.SensorContact);

        double rr = Assert.Single(measurement.RrIntervals);
        Assert.Equal(60.0 / measurement.BeatsPerMinute, rr, 2);
    }

    [Fact]
    public void Trainer_refuses_targets_until_request_control_has_run()
    {
        // The simulated trainer enforces the FTMS handshake, which is what makes the
        // FtmsControlSession tests meaningful rather than self-fulfilling.
        var trainer = new SimulatedTrainer();
        var controlPoint = EndpointId.Ble(
            RideLink.Ble.BleUuids.FitnessMachineService,
            RideLink.Ble.BleUuids.FitnessMachineControlPoint);

        var responses = new List<byte[]>();
        void Capture(EndpointId _, byte[] payload) => responses.Add(payload);

        trainer.Write(controlPoint, RideLink.Ble.Ftms.FtmsControlPoint.SetTargetPower(200), Capture);
        var refused = RideLink.Ble.Ftms.FtmsControlPoint.ParseResponse(responses[^1]);
        Assert.Equal(RideLink.Ble.Ftms.FtmsResultCode.ControlNotPermitted, refused.Result);

        trainer.Write(controlPoint, RideLink.Ble.Ftms.FtmsControlPoint.RequestControl(), Capture);
        trainer.Write(controlPoint, RideLink.Ble.Ftms.FtmsControlPoint.SetTargetPower(200), Capture);
        var accepted = RideLink.Ble.Ftms.FtmsControlPoint.ParseResponse(responses[^1]);
        Assert.Equal(RideLink.Ble.Ftms.FtmsResultCode.Success, accepted.Result);
    }

    [Fact]
    public void Trainer_rejects_a_target_outside_its_advertised_power_range()
    {
        var trainer = new SimulatedTrainer
        {
            PowerRange = new PowerRange(50, 400, 1),
        };
        var controlPoint = EndpointId.Ble(
            RideLink.Ble.BleUuids.FitnessMachineService,
            RideLink.Ble.BleUuids.FitnessMachineControlPoint);

        var responses = new List<byte[]>();
        void Capture(EndpointId _, byte[] payload) => responses.Add(payload);

        trainer.Write(controlPoint, RideLink.Ble.Ftms.FtmsControlPoint.RequestControl(), Capture);
        trainer.Write(controlPoint, RideLink.Ble.Ftms.FtmsControlPoint.SetTargetPower(1500), Capture);

        var response = RideLink.Ble.Ftms.FtmsControlPoint.ParseResponse(responses[^1]);
        Assert.Equal(RideLink.Ble.Ftms.FtmsResultCode.InvalidParameter, response.Result);
    }

    [Fact]
    public void Trainer_without_erg_answers_op_code_not_supported()
    {
        var trainer = new SimulatedTrainer { SupportsErg = false };
        var controlPoint = EndpointId.Ble(
            RideLink.Ble.BleUuids.FitnessMachineService,
            RideLink.Ble.BleUuids.FitnessMachineControlPoint);

        var responses = new List<byte[]>();
        void Capture(EndpointId _, byte[] payload) => responses.Add(payload);

        trainer.Write(controlPoint, RideLink.Ble.Ftms.FtmsControlPoint.RequestControl(), Capture);
        trainer.Write(controlPoint, RideLink.Ble.Ftms.FtmsControlPoint.SetTargetPower(200), Capture);

        var response = RideLink.Ble.Ftms.FtmsControlPoint.ParseResponse(responses[^1]);
        Assert.Equal(RideLink.Ble.Ftms.FtmsResultCode.OpCodeNotSupported, response.Result);

        // ...and its Feature characteristic said so up front, which is how the engine knows
        // before writing anything.
        var feature = FitnessMachineFeatureParser.Parse(trainer.Read(EndpointId.Ble(
            RideLink.Ble.BleUuids.FitnessMachineService,
            RideLink.Ble.BleUuids.FitnessMachineFeature)));

        Assert.False(feature.SupportsErg);
        Assert.True(feature.SupportsSimulation);
    }

    [Fact]
    public void Simulated_grade_decodes_to_the_fraction_that_was_sent()
    {
        var trainer = new SimulatedTrainer();
        var controlPoint = EndpointId.Ble(
            RideLink.Ble.BleUuids.FitnessMachineService,
            RideLink.Ble.BleUuids.FitnessMachineControlPoint);

        void Ignore(EndpointId _, byte[] __) { }

        trainer.Write(controlPoint, RideLink.Ble.Ftms.FtmsControlPoint.RequestControl(), Ignore);
        trainer.Write(
            controlPoint,
            RideLink.Ble.Ftms.FtmsControlPoint.SetIndoorBikeSimulationParameters(0.075),
            Ignore);

        Assert.Equal(0.075, trainer.GradeFraction, 6);
    }
}
