using RideLink.Model;
using RideLink.Parsing;

namespace RideLink.Ble.Parsers;

/// <summary>Decoded Heart Rate Measurement (<c>0x2A37</c>).</summary>
/// <param name="BeatsPerMinute">Heart rate.</param>
/// <param name="SensorContact">
/// Null when the sensor does not support contact detection. When it does, false means the
/// strap is not reading skin and the BPM should be treated as unreliable.
/// </param>
/// <param name="EnergyExpendedKilojoules">Cumulative energy, when present.</param>
/// <param name="RrIntervals">Beat-to-beat intervals in seconds, oldest first.</param>
public readonly record struct HeartRateMeasurement(
    int BeatsPerMinute,
    bool? SensorContact,
    int? EnergyExpendedKilojoules,
    IReadOnlyList<double> RrIntervals);

/// <summary>
/// Heart Rate Measurement parser.
/// <para>Heart Rate Service v1.0, §3.1; GATT Specification Supplement, "Heart Rate Measurement".</para>
/// <para>
/// Flags (uint8):
/// bit 0 — value format: 0 = uint8 BPM, 1 = uint16 BPM;
/// bit 1 — sensor contact detected;
/// bit 2 — sensor contact feature supported;
/// bit 3 — Energy Expended present (uint16, kJ);
/// bit 4 — RR-Interval values present (one or more uint16, units of 1/1024 s).
/// </para>
/// </summary>
public static class HeartRateMeasurementParser
{
    private const byte FlagValueFormatUInt16 = 1 << 0;
    private const byte FlagSensorContactDetected = 1 << 1;
    private const byte FlagSensorContactSupported = 1 << 2;
    private const byte FlagEnergyExpendedPresent = 1 << 3;
    private const byte FlagRrIntervalPresent = 1 << 4;

    public static HeartRateMeasurement Parse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        byte flags = reader.ReadUInt8("HR flags");

        int bpm = (flags & FlagValueFormatUInt16) != 0
            ? reader.ReadUInt16("HR value (uint16)")
            : reader.ReadUInt8("HR value (uint8)");

        // Contact status is only meaningful when the sensor advertises support for it.
        // Straps that do not support it leave bit 1 clear, which must not be read as
        // "contact lost" — hence the tri-state.
        bool? contact = (flags & FlagSensorContactSupported) != 0
            ? (flags & FlagSensorContactDetected) != 0
            : null;

        int? energy = (flags & FlagEnergyExpendedPresent) != 0
            ? reader.ReadUInt16("Energy Expended")
            : null;

        IReadOnlyList<double> rr = Array.Empty<double>();
        if ((flags & FlagRrIntervalPresent) != 0)
        {
            // The field is "one or more" values filling the rest of the PDU; the count is
            // implied by the remaining length, never transmitted.
            var intervals = new List<double>(reader.Remaining / 2);
            while (reader.Remaining >= 2)
                intervals.Add(Units.RrIntervalToSeconds(reader.ReadUInt16("RR-Interval")));
            rr = intervals;
        }

        return new HeartRateMeasurement(bpm, contact, energy, rr);
    }
}
