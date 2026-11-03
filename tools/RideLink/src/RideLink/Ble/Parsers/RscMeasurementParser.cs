using RideLink.Model;
using RideLink.Parsing;

namespace RideLink.Ble.Parsers;

/// <summary>Decoded RSC Measurement (<c>0x2A53</c>), normalized to engine units.</summary>
/// <param name="SpeedMetersPerSecond">Instantaneous speed.</param>
/// <param name="CadenceStepsPerMinute">Instantaneous cadence, steps (not strides) per minute.</param>
/// <param name="StrideLengthMeters">Instantaneous stride length, when present.</param>
/// <param name="TotalDistanceMeters">Cumulative distance, when present.</param>
/// <param name="IsRunning">True running, false walking. Devices without the bit report walking.</param>
public readonly record struct RscMeasurement(
    double SpeedMetersPerSecond,
    int CadenceStepsPerMinute,
    double? StrideLengthMeters,
    double? TotalDistanceMeters,
    bool IsRunning);

/// <summary>
/// Running Speed and Cadence Measurement parser.
/// <para>RSCS v1.0, §3.1; GATT Specification Supplement, "RSC Measurement".</para>
/// <para>
/// Flags (uint8):
/// bit 0 — Instantaneous Stride Length present (uint16, units of 0.01 m);
/// bit 1 — Total Distance present (uint32, units of 0.1 m);
/// bit 2 — Walking or Running Status (0 = walking, 1 = running).
/// </para>
/// <para>
/// Instantaneous Speed (uint16, units of 1/256 m/s) and Instantaneous Cadence (uint8,
/// 1/minute) are mandatory and always follow the flags in that order.
/// </para>
/// </summary>
public static class RscMeasurementParser
{
    private const byte FlagStrideLengthPresent = 1 << 0;
    private const byte FlagTotalDistancePresent = 1 << 1;
    private const byte FlagRunning = 1 << 2;

    public static RscMeasurement Parse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        byte flags = reader.ReadUInt8("RSC flags");

        double speed = Units.RscSpeedToMs(reader.ReadUInt16("Instantaneous Speed"));
        int cadence = reader.ReadUInt8("Instantaneous Cadence");

        double? strideLength = (flags & FlagStrideLengthPresent) != 0
            ? Units.RscStrideLengthToMeters(reader.ReadUInt16("Instantaneous Stride Length"))
            : null;

        double? totalDistance = (flags & FlagTotalDistancePresent) != 0
            ? Units.RscDistanceToMeters(reader.ReadUInt32("Total Distance"))
            : null;

        return new RscMeasurement(
            speed, cadence, strideLength, totalDistance, (flags & FlagRunning) != 0);
    }
}
