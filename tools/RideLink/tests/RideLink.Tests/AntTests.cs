using RideLink.Ant;
using RideLink.Capabilities;
using Xunit;

namespace RideLink.Tests;

/// <summary>
/// ANT+ data pages are fixed 8-octet layouts selected by octet 0, so these tests are about
/// field placement, the profile's "invalid" sentinels, and the offset encodings in the FE-C
/// control pages.
/// </summary>
public class AntPageParserTests
{
    [Fact]
    public void Parses_general_fe_data()
    {
        byte[] page =
        [
            AntPages.GeneralFeData,
            0x19,       // equipment type 25 (trainer), high bits masked off
            0x28,       // elapsed 40 * 0.25 s = 10 s
            0x64,       // distance 100 m
            0xE8, 0x03, // speed 1000 * 0.001 = 1.0 m/s
            0x8C,       // heart rate 140
            0x00,
        ];

        var result = AntPageParser.ParseGeneralFeData(page);

        Assert.Equal(25, result.EquipmentType);
        Assert.Equal(TimeSpan.FromSeconds(10), result.ElapsedTime);
        Assert.Equal(100.0, result.DistanceMeters);
        Assert.Equal(1.0, result.SpeedMetersPerSecond!.Value, 6);
        Assert.Equal(140, result.HeartRateBpm);
    }

    [Fact]
    public void Invalid_sentinels_become_null_not_data()
    {
        byte[] page =
        [
            AntPages.GeneralFeData, 0x19, 0x00, 0x00,
            0xFF, 0xFF,   // speed invalid — must not be read as 65.535 m/s
            0xFF,         // heart rate invalid
            0x00,
        ];

        var result = AntPageParser.ParseGeneralFeData(page);

        Assert.Null(result.SpeedMetersPerSecond);
        Assert.Null(result.HeartRateBpm);
    }

    [Fact]
    public void Specific_trainer_power_is_twelve_bits_across_an_octet_boundary()
    {
        // 300 W = 0x12C. Low 8 bits in octet 5, high 4 bits in the low nibble of octet 6.
        byte[] page =
        [
            AntPages.SpecificTrainerData,
            0x05,       // event count
            0x5A,       // cadence 90
            0x10, 0x27, // accumulated power 10000
            0x2C,       // power LSB
            0x01,       // power MSN = 1, trainer status = 0
            0x00,       // flags
        ];

        var result = AntPageParser.ParseSpecificTrainerData(page);

        Assert.Equal(5, result.EventCount);
        Assert.Equal(90.0, result.CadenceRpm!.Value);
        Assert.Equal(10000, result.AccumulatedPowerWatts);
        Assert.Equal(300, result.InstantaneousPowerWatts);
    }

    [Fact]
    public void Specific_trainer_reports_when_it_cannot_hold_the_erg_target()
    {
        byte[] page =
        [
            AntPages.SpecificTrainerData, 0x00, 0x5A, 0x00, 0x00, 0x64, 0x00,
            0x01,   // low bits of the flags field: target power limits reached
        ];

        var result = AntPageParser.ParseSpecificTrainerData(page);

        Assert.True(result.TargetPowerLimitsReached);
    }

    [Fact]
    public void Invalid_trainer_power_becomes_null()
    {
        // 0x0FFF across the split field is the profile's invalid marker.
        byte[] page =
        [
            AntPages.SpecificTrainerData, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x0F, 0x00,
        ];

        var result = AntPageParser.ParseSpecificTrainerData(page);

        Assert.Null(result.InstantaneousPowerWatts);
        Assert.Null(result.CadenceRpm);
    }

    [Fact]
    public void Parses_standard_power_only()
    {
        byte[] page =
        [
            AntPages.StandardPowerOnly,
            0x0A,       // event count
            0xFF,       // pedal power, unused
            0x55,       // cadence 85
            0x88, 0x13, // accumulated power 5000
            0xC8, 0x00, // instantaneous power 200
        ];

        var result = AntPageParser.ParseStandardPowerOnly(page);

        Assert.Equal(10, result.EventCount);
        Assert.Equal(85, result.CadenceRpm);
        Assert.Equal(5000, result.AccumulatedPowerWatts);
        Assert.Equal(200, result.InstantaneousPowerWatts);
    }

    [Fact]
    public void Heart_rate_comes_from_the_last_four_octets_whatever_the_page()
    {
        byte[] page = [0x04, 0x11, 0x22, 0x33, 0x00, 0x04, 0x2A, 0x8E];

        var result = AntPageParser.ParseHeartRate(page);

        Assert.Equal(1024, result.HeartBeatEventTime);
        Assert.Equal(42, result.HeartBeatCount);
        Assert.Equal(142, result.ComputedHeartRateBpm);
    }

    [Fact]
    public void Parses_combined_speed_and_cadence()
    {
        byte[] page = [0x00, 0x04, 0x64, 0x00, 0x00, 0x08, 0xC8, 0x00];

        var result = AntPageParser.ParseSpeedAndCadence(page);

        Assert.Equal(1024, result.CadenceEventTime);
        Assert.Equal(100, result.CumulativeCadenceRevolutions);
        Assert.Equal(2048, result.SpeedEventTime);
        Assert.Equal(200, result.CumulativeSpeedRevolutions);
    }
}

public class FecControlPageTests
{
    [Fact]
    public void Target_power_uses_quarter_watt_units()
    {
        byte[] page = FecControlPages.TargetPower(250);

        Assert.Equal(AntPages.TargetPower, page[0]);
        Assert.Equal(8, page.Length);
        // 250 W / 0.25 = 1000 = 0x03E8
        Assert.Equal(0xE8, page[6]);
        Assert.Equal(0x03, page[7]);
        Assert.Equal(250, FecControlPages.DecodeTargetPower(page));
    }

    [Theory]
    // Grade is uint16 in 0.01% units offset by -200.00%, so flat is 20000 and not 0.
    [InlineData(0.0, 20000)]
    [InlineData(0.05, 20500)]
    [InlineData(-0.05, 19500)]
    public void Track_resistance_grade_carries_the_two_hundred_percent_offset(
        double fraction, int expectedRaw)
    {
        byte[] page = FecControlPages.TrackResistance(fraction);

        Assert.Equal(AntPages.TrackResistance, page[0]);
        int raw = page[5] | (page[6] << 8);
        Assert.Equal(expectedRaw, raw);
        Assert.Equal(fraction, FecControlPages.DecodeTrackResistanceGrade(page), 6);
    }

    [Fact]
    public void Track_resistance_encodes_crr_in_five_times_ten_to_the_minus_five_units()
    {
        byte[] page = FecControlPages.TrackResistance(0, rollingResistanceCoefficient: 0.004);

        Assert.Equal(80, page[7]);   // 0.004 / 0.00005
    }

    [Fact]
    public void Basic_resistance_uses_half_percent_units()
    {
        byte[] page = FecControlPages.BasicResistance(50);

        Assert.Equal(AntPages.BasicResistance, page[0]);
        Assert.Equal(100, page[7]);
    }

    [Fact]
    public void Wind_resistance_offsets_wind_speed_by_one_hundred_and_twenty_seven()
    {
        byte[] page = FecControlPages.WindResistance(
            windResistanceCoefficient: 0.51, windSpeedKilometersPerHour: 0);

        Assert.Equal(51, page[5]);
        Assert.Equal(127, page[6]);   // 0 km/h with the -127 offset
    }
}

/// <summary>
/// End-to-end over the simulated ANT host: pages in produce capability samples out, and
/// control calls produce the right pages on the wire.
/// </summary>
public class FitnessEquipmentCapabilityTests
{
    private static async Task<(FitnessEquipmentCapability Capability, SimulatedAntChannel Channel)>
        OpenAsync()
    {
        var host = new SimulatedAntChannelHost();
        var channel = (SimulatedAntChannel)await host.OpenChannelAsync(AntProfiles.FitnessEquipment);
        return (new FitnessEquipmentCapability(channel, "Sim FE-C"), channel);
    }

    [Fact]
    public async Task Broadcast_pages_become_capability_samples()
    {
        var (capability, channel) = await OpenAsync();
        await using var _ = capability;

        int? watts = null;
        double? cadence = null;
        using var powerSub = capability.Power.Subscribe(s => watts = s.Watts);
        using var cadenceSub = capability.Cadence.Subscribe(s => cadence = s.Rpm);

        channel.Broadcast(
        [
            AntPages.SpecificTrainerData, 0x01, 0x5A, 0x00, 0x00, 0x2C, 0x01, 0x00,
        ]);

        Assert.Equal(300, watts);
        Assert.Equal(90.0, cadence);
    }

    [Fact]
    public async Task Distance_accumulates_across_the_uint8_counter_wrap()
    {
        var (capability, channel) = await OpenAsync();
        await using var _ = capability;

        double total = 0;
        using var sub = capability.Distance.Subscribe(s => total = s.Meters);

        // The FE-C distance field is a byte of metres and wraps every 256 m.
        channel.Broadcast(GeneralFe(distance: 250));
        channel.Broadcast(GeneralFe(distance: 10));    // wrapped: +16 m, not -240

        Assert.Equal(16.0, total, 6);

        static byte[] GeneralFe(byte distance) =>
            [AntPages.GeneralFeData, 0x19, 0x00, distance, 0xFF, 0xFF, 0xFF, 0x00];
    }

    [Fact]
    public async Task Sim_mode_sends_a_track_resistance_page_with_the_right_grade()
    {
        var (capability, channel) = await OpenAsync();
        await using var _ = capability;

        var result = await capability.SetSimulationAsync(new SimulationParameters(0.075));

        Assert.True(result.Success);
        Assert.Equal(ControlMode.Simulation, capability.Mode);

        byte[] track = channel.SentPages[0];
        Assert.Equal(AntPages.TrackResistance, track[0]);
        Assert.Equal(0.075, FecControlPages.DecodeTrackResistanceGrade(track), 6);

        // SIM also pushes wind, so FE-C matches the single FTMS simulation command.
        Assert.Equal(AntPages.WindResistance, channel.SentPages[1][0]);
    }

    [Fact]
    public async Task Erg_mode_sends_a_target_power_page()
    {
        var (capability, channel) = await OpenAsync();
        await using var _ = capability;

        var result = await capability.SetTargetPowerAsync(220);

        Assert.True(result.Success);
        Assert.Equal(ControlMode.Erg, capability.Mode);
        Assert.Equal(220, FecControlPages.DecodeTargetPower(channel.LastSentPage!));
    }

    [Fact]
    public async Task An_unacknowledged_page_is_reported_as_a_timeout()
    {
        var (capability, channel) = await OpenAsync();
        await using var _ = capability;

        channel.AcknowledgeSends = false;
        var result = await capability.SetTargetPowerAsync(200);

        Assert.False(result.Success);
        Assert.Equal(ControlFailure.Timeout, result.Failure);
        Assert.Equal(ControlMode.None, capability.Mode);
    }

    [Fact]
    public void Every_profile_uses_the_shared_ant_plus_rf_frequency()
    {
        Assert.All(AntProfiles.All, p => Assert.Equal(57, p.RadioFrequency));

        Assert.Equal(17, AntProfiles.FitnessEquipment.DeviceType);
        Assert.Equal(120, AntProfiles.HeartRate.DeviceType);
        Assert.Equal(11, AntProfiles.BicyclePower.DeviceType);
        Assert.Equal(121, AntProfiles.BikeSpeedAndCadence.DeviceType);
    }

    [Fact]
    public async Task Pairing_by_device_number_overrides_the_wildcard_search()
    {
        var host = new SimulatedAntChannelHost { PairedDeviceNumber = 999 };

        var wildcard = await host.OpenChannelAsync(AntProfiles.FitnessEquipment);
        Assert.Equal(999, wildcard.DeviceNumber);

        var paired = await host.OpenChannelAsync(AntProfiles.FitnessEquipment.PairedTo(4242));
        Assert.Equal(4242, paired.DeviceNumber);

        await host.DisposeAsync();
    }
}
