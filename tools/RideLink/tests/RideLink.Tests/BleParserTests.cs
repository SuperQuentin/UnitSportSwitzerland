using RideLink.Ble.Ftms;
using RideLink.Ble.Parsers;
using RideLink.Parsing;
using RideLink.Simulation;
using Xunit;

namespace RideLink.Tests;

/// <summary>
/// Byte-level tests for the GATT parsers.
/// <para>
/// Every fixture is written out as literal octets in the order the spec puts them, so a
/// failure points at a concrete field rather than at "something in the parser". The flag
/// combinations chosen are the ones real devices actually send, plus the ones most likely to
/// shift a field by an octet.
/// </para>
/// </summary>
public class HeartRateMeasurementTests
{
    [Fact]
    public void Parses_eight_bit_bpm_with_no_optional_fields()
    {
        // flags 0x00: uint8 value, no contact support, no energy, no RR
        byte[] payload = [0x00, 0x48];

        var result = HeartRateMeasurementParser.Parse(payload);

        Assert.Equal(72, result.BeatsPerMinute);
        Assert.Null(result.SensorContact);       // unsupported, not "no contact"
        Assert.Empty(result.RrIntervals);
    }

    [Fact]
    public void Parses_sixteen_bit_bpm()
    {
        // flags bit 0 set: uint16 value, little-endian 0x0134 = 308
        byte[] payload = [0x01, 0x34, 0x01];

        var result = HeartRateMeasurementParser.Parse(payload);

        Assert.Equal(308, result.BeatsPerMinute);
    }

    [Theory]
    // supported + detected -> true; supported + not detected -> false; unsupported -> null
    [InlineData(0x06, true)]
    [InlineData(0x04, false)]
    public void Reports_sensor_contact_only_when_supported(byte flags, bool expected)
    {
        var result = HeartRateMeasurementParser.Parse([flags, 0x50]);
        Assert.Equal(expected, result.SensorContact);
    }

    [Fact]
    public void Unsupported_contact_bit_is_not_read_as_contact_lost()
    {
        // bit 1 set but bit 2 clear: the spec says contact status is meaningless here
        var result = HeartRateMeasurementParser.Parse([0x02, 0x50]);
        Assert.Null(result.SensorContact);
    }

    [Fact]
    public void Parses_energy_expended_before_rr_intervals()
    {
        // flags 0x18: energy present (bit 3) and RR present (bit 4).
        // Field order is energy first, then RR — reversing them silently corrupts both.
        byte[] payload =
        [
            0x18,
            0x50,               // bpm 80
            0xE8, 0x03,         // energy 1000 kJ
            0x00, 0x04,         // RR 1024 ticks = 1.000 s
            0x00, 0x02,         // RR  512 ticks = 0.500 s
        ];

        var result = HeartRateMeasurementParser.Parse(payload);

        Assert.Equal(80, result.BeatsPerMinute);
        Assert.Equal(1000, result.EnergyExpendedKilojoules);
        Assert.Equal(2, result.RrIntervals.Count);
        Assert.Equal(1.0, result.RrIntervals[0], 6);
        Assert.Equal(0.5, result.RrIntervals[1], 6);
    }

    [Fact]
    public void Truncated_payload_throws_with_the_raw_bytes_attached()
    {
        // flags claim a uint16 value but only one octet follows
        var ex = Assert.Throws<RideLinkParseException>(
            () => HeartRateMeasurementParser.Parse([0x01, 0x34]));

        Assert.Equal(new byte[] { 0x01, 0x34 }, ex.Payload);
    }

    [Fact]
    public void Round_trips_through_the_encoder()
    {
        byte[] encoded = GattEncoders.HeartRateMeasurement(
            142, sensorContact: true, rrIntervalsSeconds: [0.42]);

        var result = HeartRateMeasurementParser.Parse(encoded);

        Assert.Equal(142, result.BeatsPerMinute);
        Assert.True(result.SensorContact);
        Assert.Single(result.RrIntervals);
        Assert.Equal(0.42, result.RrIntervals[0], 2);
    }
}

public class CscMeasurementTests
{
    [Fact]
    public void Parses_wheel_only()
    {
        // flags 0x01: wheel data only. uint32 revs then uint16 event time.
        byte[] payload = [0x01, 0x10, 0x27, 0x00, 0x00, 0x00, 0x04];

        var result = CscMeasurementParser.Parse(payload);

        Assert.Equal(10000u, result.CumulativeWheelRevolutions);
        Assert.Equal((ushort?)1024, result.LastWheelEventTime);
        Assert.Null(result.CumulativeCrankRevolutions);
    }

    [Fact]
    public void Parses_crank_only()
    {
        // flags 0x02: crank data only. uint16 revs then uint16 event time.
        byte[] payload = [0x02, 0x64, 0x00, 0x00, 0x08];

        var result = CscMeasurementParser.Parse(payload);

        Assert.Null(result.CumulativeWheelRevolutions);
        Assert.Equal((ushort?)100, result.CumulativeCrankRevolutions);
        Assert.Equal((ushort?)2048, result.LastCrankEventTime);
    }

    [Fact]
    public void Parses_both_blocks_in_wheel_then_crank_order()
    {
        byte[] payload =
        [
            0x03,
            0x01, 0x00, 0x00, 0x00,   // wheel revs 1
            0x00, 0x04,               // wheel event time 1024
            0x02, 0x00,               // crank revs 2
            0x00, 0x08,               // crank event time 2048
        ];

        var result = CscMeasurementParser.Parse(payload);

        Assert.Equal(1u, result.CumulativeWheelRevolutions);
        Assert.Equal((ushort?)1024, result.LastWheelEventTime);
        Assert.Equal((ushort?)2, result.CumulativeCrankRevolutions);
        Assert.Equal((ushort?)2048, result.LastCrankEventTime);
    }
}

public class CyclingPowerMeasurementTests
{
    [Fact]
    public void Parses_power_only()
    {
        // flags 0x0000, power 200 W
        byte[] payload = [0x00, 0x00, 0xC8, 0x00];

        var result = CyclingPowerMeasurementParser.Parse(payload);

        Assert.Equal(200, result.InstantaneousPowerWatts);
        Assert.Null(result.CumulativeCrankRevolutions);
    }

    [Fact]
    public void Instantaneous_power_is_signed()
    {
        // -5 W as sint16 little-endian: a coasting trainer can legitimately report this
        byte[] payload = [0x00, 0x00, 0xFB, 0xFF];

        var result = CyclingPowerMeasurementParser.Parse(payload);

        Assert.Equal(-5, result.InstantaneousPowerWatts);
    }

    [Fact]
    public void Walks_past_unsurfaced_optional_fields_to_reach_accumulated_energy()
    {
        // Every optional field before Accumulated Energy is present. If any of the skips is
        // the wrong width, the energy value lands on the wrong octets.
        ushort flags =
            (1 << 0)    // pedal power balance (uint8)
            | (1 << 2)  // accumulated torque (uint16)
            | (1 << 6)  // extreme force magnitudes (4 octets)
            | (1 << 7)  // extreme torque magnitudes (4 octets)
            | (1 << 8)  // extreme angles (3 octets)
            | (1 << 9)  // top dead spot (uint16)
            | (1 << 10) // bottom dead spot (uint16)
            | (1 << 11);// accumulated energy (uint16)

        var payload = new List<byte>
        {
            (byte)(flags & 0xFF), (byte)(flags >> 8),
            0x2C, 0x01,             // power 300 W
            0x64,                   // balance 100 -> 50.0 %
            0x40, 0x00,             // torque 64 -> 2.0 N·m
            0, 0, 0, 0,             // extreme force
            0, 0, 0, 0,             // extreme torque
            0, 0, 0,                // extreme angles
            0, 0,                   // top dead spot
            0, 0,                   // bottom dead spot
            0x2A, 0x00,             // accumulated energy 42 kJ
        };

        var result = CyclingPowerMeasurementParser.Parse(payload.ToArray());

        Assert.Equal(300, result.InstantaneousPowerWatts);
        Assert.Equal(50.0, result.PedalPowerBalancePercent);
        Assert.Equal(2.0, result.AccumulatedTorqueNewtonMeters);
        Assert.Equal(42, result.AccumulatedEnergyKilojoules);
    }

    [Fact]
    public void Wheel_and_crank_event_times_use_different_resolutions()
    {
        // This is the trap: wheel event time is 1/2048 s here, crank is 1/1024 s.
        Assert.Equal(2048.0, CyclingPowerMeasurementParser.WheelEventTimeTicksPerSecond);
        Assert.Equal(1024.0, CyclingPowerMeasurementParser.CrankEventTimeTicksPerSecond);
        Assert.Equal(1024.0, CscMeasurementParser.EventTimeTicksPerSecond);
    }
}

public class IndoorBikeDataTests
{
    [Fact]
    public void More_data_bit_clear_means_speed_is_present()
    {
        // flags 0x0000: bit 0 clear, so Instantaneous Speed follows. 2500 * 0.01 km/h = 25 km/h
        byte[] payload = [0x00, 0x00, 0xC4, 0x09];

        var result = IndoorBikeDataParser.Parse(payload);

        Assert.NotNull(result.SpeedMetersPerSecond);
        Assert.Equal(25.0 / 3.6, result.SpeedMetersPerSecond!.Value, 4);
    }

    [Fact]
    public void More_data_bit_set_means_speed_is_absent()
    {
        // flags 0x0001 with cadence also absent: nothing but flags
        var result = IndoorBikeDataParser.Parse([0x01, 0x00]);
        Assert.Null(result.SpeedMetersPerSecond);
    }

    [Fact]
    public void Applies_every_scaling_factor()
    {
        // speed present (bit 0 clear), cadence (bit 2), distance (bit 4), power (bit 6),
        // heart rate (bit 9)
        ushort flags = (1 << 2) | (1 << 4) | (1 << 6) | (1 << 9);

        byte[] payload =
        [
            (byte)(flags & 0xFF), (byte)(flags >> 8),
            0xC4, 0x09,             // speed 2500 -> 25.00 km/h
            0xB4, 0x00,             // cadence 180 -> 90.0 rpm (0.5 rpm units)
            0xE8, 0x03, 0x00,       // distance 1000 m (uint24)
            0xF4, 0x01,             // power 500 W
            0x8C,                   // heart rate 140
        ];

        var result = IndoorBikeDataParser.Parse(payload);

        Assert.Equal(25.0 / 3.6, result.SpeedMetersPerSecond!.Value, 4);
        Assert.Equal(90.0, result.CadenceRpm!.Value, 4);
        Assert.Equal(1000.0, result.TotalDistanceMeters!.Value);
        Assert.Equal(500, result.InstantaneousPowerWatts);
        Assert.Equal(140, result.HeartRateBpm);
    }

    [Fact]
    public void Round_trips_through_the_encoder()
    {
        byte[] encoded = GattEncoders.IndoorBikeData(
            speedMetersPerSecond: 10.0,
            cadenceRpm: 85.5,
            totalDistanceMeters: 4321,
            instantaneousPowerWatts: 244,
            heartRateBpm: 151);

        var result = IndoorBikeDataParser.Parse(encoded);

        Assert.Equal(10.0, result.SpeedMetersPerSecond!.Value, 2);
        Assert.Equal(85.5, result.CadenceRpm!.Value, 2);
        Assert.Equal(4321, result.TotalDistanceMeters!.Value);
        Assert.Equal(244, result.InstantaneousPowerWatts);
        Assert.Equal(151, result.HeartRateBpm);
    }

    [Fact]
    public void Expended_energy_sentinel_becomes_null()
    {
        // bit 8 set, all three energy fields at their "not available" sentinels
        ushort flags = (1 << 0) | (1 << 8);   // speed absent, energy present
        byte[] payload =
        [
            (byte)(flags & 0xFF), (byte)(flags >> 8),
            0xFF, 0xFF,     // total energy   0xFFFF
            0xFF, 0xFF,     // per hour       0xFFFF
            0xFF,           // per minute     0xFF
        ];

        var result = IndoorBikeDataParser.Parse(payload);

        Assert.Null(result.TotalEnergyKilocalories);
        Assert.Null(result.EnergyPerHourKilocalories);
        Assert.Null(result.EnergyPerMinuteKilocalories);
    }
}

public class CrossTrainerDataTests
{
    [Fact]
    public void Flags_field_is_twenty_four_bits_wide()
    {
        // A 16-bit read here would consume only two of the three flag octets and shift
        // everything after it by one, which is exactly the bug this test exists to catch.
        // flags 0x000000 -> speed present.
        byte[] payload = [0x00, 0x00, 0x00, 0xC4, 0x09];

        var result = CrossTrainerDataParser.Parse(payload);

        Assert.Equal(25.0 / 3.6, result.SpeedMetersPerSecond!.Value, 4);
    }

    [Fact]
    public void Movement_direction_bit_reports_backward()
    {
        // bit 0 set (no speed), bit 15 set (backward)
        uint flags = (1u << 0) | (1u << 15);
        byte[] payload = [(byte)(flags & 0xFF), (byte)((flags >> 8) & 0xFF), (byte)(flags >> 16)];

        var result = CrossTrainerDataParser.Parse(payload);

        Assert.False(result.MovingForward);
    }
}

public class RowerDataTests
{
    [Fact]
    public void More_data_bit_clear_yields_both_stroke_rate_and_count()
    {
        // Unlike the other machines, this bit gates a two-field group.
        byte[] payload = [0x00, 0x00, 0x38, 0x2A, 0x00];   // stroke rate 56 -> 28.0, count 42

        var result = RowerDataParser.Parse(payload);

        Assert.Equal(28.0, result.StrokeRatePerMinute!.Value);
        Assert.Equal(42, result.StrokeCount);
        // Stroke rate is surfaced as cadence so a rower satisfies ICadenceSource.
        Assert.Equal(28.0, result.CadenceRpm!.Value);
    }

    [Fact]
    public void Pace_in_seconds_per_500m_becomes_speed()
    {
        // bit 0 set (no stroke group), bit 3 set (instantaneous pace). 125 s / 500 m = 4 m/s
        ushort flags = (1 << 0) | (1 << 3);
        byte[] payload = [(byte)(flags & 0xFF), (byte)(flags >> 8), 0x7D, 0x00];

        var result = RowerDataParser.Parse(payload);

        Assert.Equal(4.0, result.PaceMetersPerSecond!.Value, 6);
        Assert.Equal(4.0, result.SpeedMetersPerSecond!.Value, 6);
    }
}

public class TreadmillDataTests
{
    [Fact]
    public void Parses_inclination_and_ramp_angle_as_a_pair()
    {
        // bit 0 set (no speed), bit 3 set (inclination + ramp angle)
        ushort flags = (1 << 0) | (1 << 3);
        byte[] payload =
        [
            (byte)(flags & 0xFF), (byte)(flags >> 8),
            0x32, 0x00,     // inclination 50 -> 5.0 %
            0xF6, 0xFF,     // ramp angle -10 -> -1.0 degrees
        ];

        var result = TreadmillDataParser.Parse(payload);

        Assert.Equal(5.0, result.InclinationPercent!.Value, 6);
        Assert.Equal(-1.0, result.RampAngleDegrees!.Value, 6);
    }
}

public class RscMeasurementTests
{
    [Fact]
    public void Parses_mandatory_speed_and_cadence()
    {
        // flags 0x00, speed 1024 -> 4 m/s (1/256 units), cadence 180
        byte[] payload = [0x00, 0x00, 0x04, 0xB4];

        var result = RscMeasurementParser.Parse(payload);

        Assert.Equal(4.0, result.SpeedMetersPerSecond, 6);
        Assert.Equal(180, result.CadenceStepsPerMinute);
        Assert.False(result.IsRunning);
    }

    [Fact]
    public void Parses_stride_length_distance_and_running_flag()
    {
        // flags 0x07: stride length, total distance, running
        byte[] payload =
        [
            0x07,
            0x00, 0x04,                 // speed 4 m/s
            0xB4,                       // cadence 180
            0x78, 0x00,                 // stride length 120 cm -> 1.20 m
            0x10, 0x27, 0x00, 0x00,     // total distance 10000 decimetres -> 1000 m
        ];

        var result = RscMeasurementParser.Parse(payload);

        Assert.Equal(1.20, result.StrideLengthMeters!.Value, 6);
        Assert.Equal(1000.0, result.TotalDistanceMeters!.Value, 6);
        Assert.True(result.IsRunning);
    }
}

public class FitnessMachineFeatureTests
{
    [Fact]
    public void Decodes_both_words_and_derives_the_control_modes()
    {
        uint features = (uint)(FitnessMachineFeatures.Cadence | FitnessMachineFeatures.PowerMeasurement);
        uint targets = (uint)(FitnessMachineTargetFeatures.PowerTarget
                              | FitnessMachineTargetFeatures.IndoorBikeSimulationParameters);

        var result = FitnessMachineFeatureParser.Parse(
            GattEncoders.FitnessMachineFeature(features, targets));

        Assert.True(result.Features.HasFlag(FitnessMachineFeatures.Cadence));
        Assert.True(result.SupportsErg);
        Assert.True(result.SupportsSimulation);
        Assert.False(result.SupportsResistance);
    }

    [Fact]
    public void Power_range_clamps_targets()
    {
        var range = SupportedRangeParser.ParsePowerRange(
            GattEncoders.SupportedPowerRange(50, 800, 1));

        Assert.Equal(50, range.MinimumWatts);
        Assert.Equal(800, range.MaximumWatts);
        Assert.Equal(800, range.Clamp(1200));
        Assert.Equal(50, range.Clamp(10));
    }

    [Fact]
    public void Resistance_range_uses_tenth_units()
    {
        var range = SupportedRangeParser.ParseResistanceRange(
            GattEncoders.SupportedResistanceLevelRange(0, 100, 0.5));

        Assert.Equal(0, range.Minimum, 6);
        Assert.Equal(100, range.Maximum, 6);
        Assert.Equal(0.5, range.MinimumIncrement, 6);
    }
}

public class FtmsControlPointTests
{
    [Fact]
    public void Set_target_power_is_little_endian_signed()
    {
        byte[] packet = FtmsControlPoint.SetTargetPower(250);

        Assert.Equal([(byte)FtmsOpCode.SetTargetPower, 0xFA, 0x00], packet);
    }

    [Theory]
    // Grade is transmitted in hundredths of a percent, so a 0.05 fraction is 500.
    [InlineData(0.05, 500)]
    [InlineData(-0.02, -200)]
    [InlineData(0.0, 0)]
    public void Simulation_grade_uses_hundredths_of_a_percent(double fraction, short expectedRaw)
    {
        byte[] packet = FtmsControlPoint.SetIndoorBikeSimulationParameters(fraction);

        Assert.Equal((byte)FtmsOpCode.SetIndoorBikeSimulationParameters, packet[0]);
        short grade = (short)(packet[3] | (packet[4] << 8));
        Assert.Equal(expectedRaw, grade);
    }

    [Fact]
    public void Simulation_encodes_wind_crr_and_cw_at_their_own_scales()
    {
        byte[] packet = FtmsControlPoint.SetIndoorBikeSimulationParameters(
            gradeFraction: 0,
            windSpeedMetersPerSecond: 2.5,      // 0.001 m/s units -> 2500
            rollingResistanceCoefficient: 0.004, // 0.0001 units    -> 40
            windResistanceCoefficient: 0.51);    // 0.01 kg/m units -> 51

        short wind = (short)(packet[1] | (packet[2] << 8));
        Assert.Equal(2500, wind);
        Assert.Equal(40, packet[5]);
        Assert.Equal(51, packet[6]);
    }

    [Fact]
    public void Parses_a_success_response()
    {
        var response = FtmsControlPoint.ParseResponse(
            GattEncoders.ControlPointResponse((byte)FtmsOpCode.SetTargetPower, 0x01));

        Assert.Equal(FtmsOpCode.SetTargetPower, response.RequestOpCode);
        Assert.Equal(FtmsResultCode.Success, response.Result);
        Assert.True(response.IsSuccess);
    }

    [Fact]
    public void Rejects_a_value_that_is_not_a_response_code()
    {
        Assert.Throws<RideLinkParseException>(
            () => FtmsControlPoint.ParseResponse([0x05, 0x00, 0x01]));
    }

    [Fact]
    public void Maps_every_failure_result_code()
    {
        foreach (var code in new[]
                 {
                     FtmsResultCode.OpCodeNotSupported,
                     FtmsResultCode.InvalidParameter,
                     FtmsResultCode.OperationFailed,
                     FtmsResultCode.ControlNotPermitted,
                 })
        {
            var response = FtmsControlPoint.ParseResponse(
                GattEncoders.ControlPointResponse((byte)FtmsOpCode.SetTargetPower, (byte)code));

            Assert.False(response.IsSuccess);
            Assert.Equal(code, response.Result);
        }
    }
}
