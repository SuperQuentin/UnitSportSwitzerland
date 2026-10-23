using RideLink.Parsing;
using Xunit;

namespace RideLink.Tests;

/// <summary>
/// Rollover behaviour of the cumulative counters.
/// <para>
/// This is the arithmetic that produces wrong cadence in the field, because it only misbehaves
/// after the counters have been running for a while: the crank event time wraps every 64 s at
/// 1/1024 s resolution, so any ride longer than a minute crosses one. Widening the subtraction
/// to <c>int</c> turns a 1-tick delta into a 65 536-tick delta and the cadence collapses to
/// near zero for one sample.
/// </para>
/// </summary>
public class RevolutionRateTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_sample_only_primes_and_produces_no_rate()
    {
        var tracker = new RevolutionRateTracker(1024, revolutions16Bit: true);

        Assert.False(tracker.TryUpdate(100, 1000, T0, out _));
    }

    [Fact]
    public void Computes_cadence_from_two_samples()
    {
        var tracker = new RevolutionRateTracker(1024, revolutions16Bit: true);
        tracker.TryUpdate(100, 0, T0, out _);

        // one revolution in 1024 ticks = 1 second = 60 rpm
        Assert.True(tracker.TryUpdate(101, 1024, T0.AddSeconds(1), out double rpm));
        Assert.Equal(60.0, rpm, 6);
    }

    [Fact]
    public void Handles_event_time_rollover()
    {
        var tracker = new RevolutionRateTracker(1024, revolutions16Bit: true);

        // Prime just below the uint16 ceiling.
        tracker.TryUpdate(100, 65_000, T0, out _);

        // 1024 ticks later the counter has wrapped: 65000 + 1024 = 66024 mod 65536 = 488.
        Assert.True(tracker.TryUpdate(101, 488, T0.AddSeconds(1), out double rpm));
        Assert.Equal(60.0, rpm, 6);
    }

    [Fact]
    public void Handles_revolution_counter_rollover()
    {
        var tracker = new RevolutionRateTracker(1024, revolutions16Bit: true);
        tracker.TryUpdate(65_535, 0, T0, out _);

        // 65535 + 1 wraps to 0. Naive subtraction would give -65535 revolutions.
        Assert.True(tracker.TryUpdate(0, 1024, T0.AddSeconds(1), out double rpm));
        Assert.Equal(60.0, rpm, 6);
    }

    [Fact]
    public void Handles_both_counters_rolling_over_together()
    {
        var tracker = new RevolutionRateTracker(1024, revolutions16Bit: true);
        tracker.TryUpdate(65_534, 65_400, T0, out _);

        // two revolutions across the wrap, 512 ticks = 0.5 s -> 240 rpm
        Assert.True(tracker.TryUpdate(0, 376, T0.AddSeconds(0.5), out double rpm));
        Assert.Equal(240.0, rpm, 6);
    }

    [Fact]
    public void Wheel_counters_are_thirty_two_bit_and_wrap_at_their_own_width()
    {
        var tracker = new RevolutionRateTracker(1024, revolutions16Bit: false);
        tracker.TryUpdate(uint.MaxValue, 0, T0, out _);

        Assert.True(tracker.TryUpdate(0, 1024, T0.AddSeconds(1), out double rpm));
        Assert.Equal(60.0, rpm, 6);
    }

    [Fact]
    public void Holds_the_last_rate_briefly_when_no_new_revolution_arrives()
    {
        var tracker = new RevolutionRateTracker(1024, revolutions16Bit: true);
        tracker.TryUpdate(0, 0, T0, out _);
        tracker.TryUpdate(1, 1024, T0.AddSeconds(1), out double first);

        // Same counters one notification later: the crank simply has not turned yet.
        Assert.True(tracker.TryUpdate(1, 1024, T0.AddSeconds(1.25), out double held));
        Assert.Equal(first, held, 6);
    }

    [Fact]
    public void Decays_to_zero_once_the_rider_has_plainly_stopped()
    {
        var tracker = new RevolutionRateTracker(
            1024, revolutions16Bit: true, zeroAfter: TimeSpan.FromSeconds(3));

        tracker.TryUpdate(0, 0, T0, out _);
        tracker.TryUpdate(1, 1024, T0.AddSeconds(1), out _);

        Assert.True(tracker.TryUpdate(1, 1024, T0.AddSeconds(5), out double rpm));
        Assert.Equal(0.0, rpm);
    }

    [Fact]
    public void Rejects_a_packet_whose_revolutions_advanced_but_clock_did_not()
    {
        var tracker = new RevolutionRateTracker(1024, revolutions16Bit: true);
        tracker.TryUpdate(0, 500, T0, out _);

        // Would be an infinite rate; reporting nothing is better than reporting nonsense.
        Assert.False(tracker.TryUpdate(1, 500, T0.AddSeconds(1), out _));
    }

    [Fact]
    public void Rejects_implausible_rates()
    {
        var tracker = new RevolutionRateTracker(1024, revolutions16Bit: true)
        {
            MaxPlausibleRpm = 300,
        };

        tracker.TryUpdate(0, 0, T0, out _);

        // 100 revolutions in 1024 ticks would be 6000 rpm
        Assert.False(tracker.TryUpdate(100, 1024, T0.AddSeconds(1), out _));
    }

    [Fact]
    public void Reset_forgets_history()
    {
        var tracker = new RevolutionRateTracker(1024, revolutions16Bit: true);
        tracker.TryUpdate(0, 0, T0, out _);
        tracker.Reset();

        Assert.False(tracker.TryUpdate(1, 1024, T0.AddSeconds(1), out _));
    }
}

public class WheelSpeedTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Converts_wheel_revolutions_to_metres_per_second()
    {
        var tracker = new WheelSpeedTracker(1024, wheelCircumferenceMeters: 2.0);
        tracker.TryUpdate(0, 0, T0, out _);

        // one revolution per second on a 2 m wheel = 2 m/s
        Assert.True(tracker.TryUpdate(1, 1024, T0.AddSeconds(1), out double ms));
        Assert.Equal(2.0, ms, 6);
    }

    [Fact]
    public void Cycling_power_wheel_data_at_2048_ticks_gives_the_same_speed()
    {
        // The same physical speed encoded at the Cycling Power resolution: using 1024 here
        // would report double.
        var tracker = new WheelSpeedTracker(2048, wheelCircumferenceMeters: 2.0);
        tracker.TryUpdate(0, 0, T0, out _);

        Assert.True(tracker.TryUpdate(1, 2048, T0.AddSeconds(1), out double ms));
        Assert.Equal(2.0, ms, 6);
    }

    [Fact]
    public void Wheel_circumference_scales_speed_linearly()
    {
        var tracker = new WheelSpeedTracker(1024, wheelCircumferenceMeters: 2.096);
        tracker.TryUpdate(0, 0, T0, out _);
        tracker.TryUpdate(10, 1024, T0.AddSeconds(1), out double ms);

        Assert.Equal(20.96, ms, 4);
    }
}

public class SpanReaderTests
{
    [Fact]
    public void Reads_little_endian_widths()
    {
        var reader = new SpanReader([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);

        Assert.Equal(0x01, reader.ReadUInt8());
        Assert.Equal(0x0302, reader.ReadUInt16());
        Assert.Equal(0x060504u, reader.ReadUInt24());
        Assert.Equal(1, reader.Remaining);
    }

    [Fact]
    public void Reads_signed_values()
    {
        var reader = new SpanReader([0xFF, 0xFF]);
        Assert.Equal(-1, reader.ReadInt16());
    }

    [Fact]
    public void Underrun_reports_the_field_name_and_the_payload()
    {
        var ex = Assert.Throws<RideLinkParseException>(() =>
        {
            var reader = new SpanReader([0x01]);
            reader.ReadUInt32("Cumulative Wheel Revolutions");
        });

        Assert.Contains("Cumulative Wheel Revolutions", ex.Message);
        Assert.Contains("01", ex.Message);
    }
}
