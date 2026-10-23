namespace RideLink.Parsing;

/// <summary>
/// Converts the cumulative-count + last-event-time pair used by CSC and Cycling Power into
/// a rate, correctly across counter rollover.
///
/// <para>
/// Both CSC Measurement (<c>0x2A5B</c>) and Cycling Power Measurement (<c>0x2A63</c>) report
/// motion as two free-running counters rather than a rate:
/// a cumulative revolution count and the time of the most recent revolution event. The rate
/// is <c>Δrevolutions / Δtime</c>. GATT Specification Supplement, "CSC Measurement" and
/// "Cycling Power Measurement".
/// </para>
/// <para>
/// Both counters wrap. The event time is always uint16 and wraps every 64 s at 1/1024 s
/// resolution (or every 32 s at 1/2048 s), which happens constantly — a rider coasting for
/// a minute crosses a wrap. Unsigned subtraction at the counter's own width gives the
/// correct delta through a wrap, so the arithmetic here is deliberately done in the narrow
/// type and never in a widened <c>int</c>.
/// </para>
/// </summary>
public sealed class RevolutionRateTracker
{
    private readonly double _ticksPerSecond;
    private readonly bool _revolutions16Bit;
    private readonly TimeSpan _zeroAfter;

    private uint _lastRevolutions;
    private ushort _lastEventTime;
    private bool _primed;
    private DateTimeOffset _lastMovement;
    private double _lastRate;

    /// <param name="eventTimeTicksPerSecond">
    /// Resolution of the last-event-time counter. 1024 for CSC wheel and crank data and for
    /// Cycling Power *crank* data; 2048 for Cycling Power *wheel* data — the two
    /// characteristics genuinely disagree, which is a classic source of 2x speed errors.
    /// GATT Specification Supplement, "Last Wheel Event Time" / "Last Crank Event Time".
    /// </param>
    /// <param name="revolutions16Bit">
    /// True when the cumulative revolution counter is uint16 (crank data in both profiles);
    /// false for the uint32 wheel counters.
    /// </param>
    /// <param name="zeroAfter">
    /// How long without a new revolution event before the rate is reported as zero. Sensors
    /// keep notifying while stopped with an unchanged counter, so without this the last
    /// non-zero cadence would stick forever.
    /// </param>
    public RevolutionRateTracker(
        double eventTimeTicksPerSecond,
        bool revolutions16Bit,
        TimeSpan? zeroAfter = null)
    {
        if (eventTimeTicksPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(eventTimeTicksPerSecond));

        _ticksPerSecond = eventTimeTicksPerSecond;
        _revolutions16Bit = revolutions16Bit;
        _zeroAfter = zeroAfter ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>Largest rate accepted before a sample is rejected as a decode error, rev/min.</summary>
    public double MaxPlausibleRpm { get; init; } = 2200;

    /// <summary>Forgets history, so the next sample only primes the tracker.</summary>
    public void Reset()
    {
        _primed = false;
        _lastRate = 0;
    }

    /// <summary>
    /// Feeds one measurement.
    /// </summary>
    /// <param name="cumulativeRevolutions">Cumulative revolution count straight from the packet.</param>
    /// <param name="lastEventTime">Last event time straight from the packet.</param>
    /// <param name="observedAt">Arrival time, used only for the stopped-detection timeout.</param>
    /// <param name="revolutionsPerMinute">Decoded rate when this returns true.</param>
    /// <returns>
    /// False for the first sample — a rate needs two points — and false when a sample is
    /// rejected as implausible. True otherwise, including when the rate is a genuine zero.
    /// </returns>
    public bool TryUpdate(
        uint cumulativeRevolutions,
        ushort lastEventTime,
        DateTimeOffset observedAt,
        out double revolutionsPerMinute)
    {
        revolutionsPerMinute = 0;

        if (!_primed)
        {
            _lastRevolutions = cumulativeRevolutions;
            _lastEventTime = lastEventTime;
            _lastMovement = observedAt;
            _primed = true;
            return false;
        }

        // Unsigned subtraction at the counter's own width: this is what makes a wrap work.
        uint deltaRevolutions = _revolutions16Bit
            ? (ushort)((ushort)cumulativeRevolutions - (ushort)_lastRevolutions)
            : cumulativeRevolutions - _lastRevolutions;

        ushort deltaTicks = (ushort)(lastEventTime - _lastEventTime);

        _lastRevolutions = cumulativeRevolutions;
        _lastEventTime = lastEventTime;

        if (deltaRevolutions == 0)
        {
            // No new revolution since the last packet. Hold the previous rate briefly — a
            // notification can easily arrive between two crank events at low cadence — then
            // decay to zero once the rider has plainly stopped.
            if (observedAt - _lastMovement >= _zeroAfter)
            {
                _lastRate = 0;
                revolutionsPerMinute = 0;
                return true;
            }

            revolutionsPerMinute = _lastRate;
            return true;
        }

        if (deltaTicks == 0)
        {
            // Revolutions advanced but the event clock did not: the packet is inconsistent.
            // Reporting an infinite rate would be worse than reporting nothing.
            return false;
        }

        double seconds = deltaTicks / _ticksPerSecond;
        double rpm = deltaRevolutions / seconds * 60.0;

        if (double.IsNaN(rpm) || rpm > MaxPlausibleRpm) return false;

        _lastMovement = observedAt;
        _lastRate = rpm;
        revolutionsPerMinute = rpm;
        return true;
    }
}

/// <summary>
/// Wheel-revolution rate converted to ground speed using a wheel circumference.
/// </summary>
public sealed class WheelSpeedTracker
{
    private readonly RevolutionRateTracker _tracker;

    /// <param name="eventTimeTicksPerSecond">1024 for CSC, 2048 for Cycling Power.</param>
    /// <param name="wheelCircumferenceMeters">
    /// Rollout of the wheel. 2.096 m is the common 700x23c default; a wrong value scales
    /// speed and distance linearly, so it is configurable per device.
    /// </param>
    public WheelSpeedTracker(double eventTimeTicksPerSecond, double wheelCircumferenceMeters = 2.096)
    {
        if (wheelCircumferenceMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(wheelCircumferenceMeters));

        WheelCircumferenceMeters = wheelCircumferenceMeters;
        // Wheel counters are uint32 in both CSC and Cycling Power.
        _tracker = new RevolutionRateTracker(eventTimeTicksPerSecond, revolutions16Bit: false)
        {
            MaxPlausibleRpm = 3000,   // ~105 m/s on a 2.1 m wheel: comfortably absurd
        };
    }

    public double WheelCircumferenceMeters { get; set; }

    /// <summary>Cumulative distance since the tracker was created, metres.</summary>
    public double TotalDistanceMeters { get; private set; }

    public void Reset()
    {
        _tracker.Reset();
        TotalDistanceMeters = 0;
    }

    /// <summary>Feeds one measurement, producing speed in metres per second.</summary>
    public bool TryUpdate(
        uint cumulativeWheelRevolutions,
        ushort lastWheelEventTime,
        DateTimeOffset observedAt,
        out double metersPerSecond)
    {
        metersPerSecond = 0;
        if (!_tracker.TryUpdate(cumulativeWheelRevolutions, lastWheelEventTime, observedAt, out double rpm))
            return false;

        metersPerSecond = rpm / 60.0 * WheelCircumferenceMeters;
        return true;
    }

    /// <summary>
    /// Distance derived from the cumulative wheel counter rather than integrated from speed,
    /// so it stays exact across dropped notifications.
    /// </summary>
    public double DistanceFromRevolutions(uint deltaRevolutions) =>
        TotalDistanceMeters += deltaRevolutions * WheelCircumferenceMeters;
}
