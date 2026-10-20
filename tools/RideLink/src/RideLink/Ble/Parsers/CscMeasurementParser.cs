using RideLink.Parsing;

namespace RideLink.Ble.Parsers;

/// <summary>Decoded CSC Measurement (<c>0x2A5B</c>). Raw counters; rates come from the trackers.</summary>
public readonly record struct CscMeasurement(
    uint? CumulativeWheelRevolutions,
    ushort? LastWheelEventTime,
    ushort? CumulativeCrankRevolutions,
    ushort? LastCrankEventTime);

/// <summary>
/// Cycling Speed and Cadence Measurement parser.
/// <para>CSCS v1.0, §3.1; GATT Specification Supplement, "CSC Measurement".</para>
/// <para>
/// Flags (uint8):
/// bit 0 — Wheel Revolution Data present: Cumulative Wheel Revolutions (uint32) then
///         Last Wheel Event Time (uint16, units of 1/1024 s);
/// bit 1 — Crank Revolution Data present: Cumulative Crank Revolutions (uint16) then
///         Last Crank Event Time (uint16, units of 1/1024 s).
/// </para>
/// <para>
/// Note the widths differ between the two blocks — wheel revolutions are 32-bit, crank
/// revolutions 16-bit — and that both event times here use 1/1024 s, unlike Cycling Power
/// Measurement whose *wheel* event time is 1/2048 s.
/// </para>
/// </summary>
public static class CscMeasurementParser
{
    private const byte FlagWheelRevolutionData = 1 << 0;
    private const byte FlagCrankRevolutionData = 1 << 1;

    /// <summary>Resolution of both event-time counters in this characteristic.</summary>
    public const double EventTimeTicksPerSecond = 1024.0;

    public static CscMeasurement Parse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        byte flags = reader.ReadUInt8("CSC flags");

        uint? wheelRevolutions = null;
        ushort? wheelEventTime = null;
        if ((flags & FlagWheelRevolutionData) != 0)
        {
            wheelRevolutions = reader.ReadUInt32("Cumulative Wheel Revolutions");
            wheelEventTime = reader.ReadUInt16("Last Wheel Event Time");
        }

        ushort? crankRevolutions = null;
        ushort? crankEventTime = null;
        if ((flags & FlagCrankRevolutionData) != 0)
        {
            crankRevolutions = reader.ReadUInt16("Cumulative Crank Revolutions");
            crankEventTime = reader.ReadUInt16("Last Crank Event Time");
        }

        return new CscMeasurement(wheelRevolutions, wheelEventTime, crankRevolutions, crankEventTime);
    }
}
