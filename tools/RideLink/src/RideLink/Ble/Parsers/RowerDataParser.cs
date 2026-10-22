using RideLink.Model;
using RideLink.Parsing;

namespace RideLink.Ble.Parsers;

/// <summary>
/// Rower Data parser (<c>0x2AD1</c>).
/// <para>FTMS v1.0, §4.7; GATT Specification Supplement, "Rower Data".</para>
/// <para>
/// Flags (uint16). Bit 0 is again "More Data" — when clear, Stroke Rate <b>and</b> Stroke
/// Count are both present, which is a two-field group rather than the single field the other
/// machines gate on this bit.
/// </para>
/// <list type="bullet">
/// <item>bit 0 — More Data. 0 ⇒ Stroke Rate (uint8, 0.5 /min) and Stroke Count (uint16) present</item>
/// <item>bit 1 — Average Stroke Rate present (uint8, 0.5 /min)</item>
/// <item>bit 2 — Total Distance present (uint24, metres)</item>
/// <item>bit 3 — Instantaneous Pace present (uint16, seconds per 500 m)</item>
/// <item>bit 4 — Average Pace present (uint16, seconds per 500 m)</item>
/// <item>bit 5 — Instantaneous Power present (sint16, watts)</item>
/// <item>bit 6 — Average Power present (sint16, watts)</item>
/// <item>bit 7 — Resistance Level present (sint16, unitless)</item>
/// <item>bit 8 — Expended Energy present (uint16 kcal, uint16 kcal/h, uint8 kcal/min)</item>
/// <item>bit 9 — Heart Rate present (uint8, bpm)</item>
/// <item>bit 10 — Metabolic Equivalent present (uint8, 0.1)</item>
/// <item>bit 11 — Elapsed Time present (uint16, seconds)</item>
/// <item>bit 12 — Remaining Time present (uint16, seconds)</item>
/// </list>
/// </summary>
public static class RowerDataParser
{
    private const ushort FlagMoreData = 1 << 0;
    private const ushort FlagAverageStrokeRate = 1 << 1;
    private const ushort FlagTotalDistance = 1 << 2;
    private const ushort FlagInstantaneousPace = 1 << 3;
    private const ushort FlagAveragePace = 1 << 4;
    private const ushort FlagInstantaneousPower = 1 << 5;
    private const ushort FlagAveragePower = 1 << 6;
    private const ushort FlagResistanceLevel = 1 << 7;
    private const ushort FlagExpendedEnergy = 1 << 8;
    private const ushort FlagHeartRate = 1 << 9;
    private const ushort FlagMetabolicEquivalent = 1 << 10;
    private const ushort FlagElapsedTime = 1 << 11;
    private const ushort FlagRemainingTime = 1 << 12;

    public static FitnessMachineData Parse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        ushort flags = reader.ReadUInt16("Rower Data flags");

        double? strokeRate = null;
        int? strokeCount = null;
        if ((flags & FlagMoreData) == 0)
        {
            strokeRate = reader.ReadUInt8("Stroke Rate") * 0.5;
            strokeCount = reader.ReadUInt16("Stroke Count");
        }

        double? averageStrokeRate = (flags & FlagAverageStrokeRate) != 0
            ? reader.ReadUInt8("Average Stroke Rate") * 0.5
            : null;

        double? totalDistance = (flags & FlagTotalDistance) != 0
            ? reader.ReadUInt24("Total Distance")
            : null;

        double? pace = (flags & FlagInstantaneousPace) != 0
            ? Units.RowerPaceToMs(reader.ReadUInt16("Instantaneous Pace"))
            : null;

        double? averagePace = (flags & FlagAveragePace) != 0
            ? Units.RowerPaceToMs(reader.ReadUInt16("Average Pace"))
            : null;

        int? power = (flags & FlagInstantaneousPower) != 0
            ? reader.ReadInt16("Instantaneous Power")
            : null;

        int? averagePower = (flags & FlagAveragePower) != 0
            ? reader.ReadInt16("Average Power")
            : null;

        double? resistance = (flags & FlagResistanceLevel) != 0
            ? reader.ReadInt16("Resistance Level")
            : null;

        int? totalEnergy = null, energyPerHour = null, energyPerMinute = null;
        if ((flags & FlagExpendedEnergy) != 0)
            FtmsFields.ReadExpendedEnergy(ref reader, out totalEnergy, out energyPerHour, out energyPerMinute);

        int? heartRate = (flags & FlagHeartRate) != 0 ? reader.ReadUInt8("Heart Rate") : null;

        double? met = (flags & FlagMetabolicEquivalent) != 0
            ? reader.ReadUInt8("Metabolic Equivalent") * 0.1
            : null;

        TimeSpan? elapsed = (flags & FlagElapsedTime) != 0
            ? TimeSpan.FromSeconds(reader.ReadUInt16("Elapsed Time"))
            : null;

        TimeSpan? remaining = (flags & FlagRemainingTime) != 0
            ? TimeSpan.FromSeconds(reader.ReadUInt16("Remaining Time"))
            : null;

        return new FitnessMachineData
        {
            // A rower's "cadence" is its stroke rate; surfacing it on both keeps a rower
            // usable by anything that subscribes to ICadenceSource.
            StrokeRatePerMinute = strokeRate,
            CadenceRpm = strokeRate,
            AverageStrokeRatePerMinute = averageStrokeRate,
            StrokeCount = strokeCount,
            TotalDistanceMeters = totalDistance,
            PaceMetersPerSecond = pace,
            SpeedMetersPerSecond = pace,
            AveragePaceMetersPerSecond = averagePace,
            InstantaneousPowerWatts = power,
            AveragePowerWatts = averagePower,
            ResistanceLevel = resistance,
            TotalEnergyKilocalories = totalEnergy,
            EnergyPerHourKilocalories = energyPerHour,
            EnergyPerMinuteKilocalories = energyPerMinute,
            HeartRateBpm = heartRate,
            MetabolicEquivalent = met,
            ElapsedTime = elapsed,
            RemainingTime = remaining,
        };
    }
}
