using RideLink.Model;
using RideLink.Parsing;

namespace RideLink.Ble.Parsers;

/// <summary>
/// Indoor Bike Data parser (<c>0x2AD2</c>) — the characteristic a smart trainer notifies on.
/// <para>FTMS v1.0, §4.9; GATT Specification Supplement, "Indoor Bike Data".</para>
/// <para>
/// Flags (uint16). Note bit 0 is inverted relative to every other bit in the field: it is
/// "More Data", and Instantaneous Speed is present when it is <b>clear</b>. Treating it like
/// the other "present" bits is the single most common FTMS parsing bug, and it shifts every
/// subsequent field by two octets.
/// </para>
/// <list type="bullet">
/// <item>bit 0 — More Data. 0 ⇒ Instantaneous Speed present (uint16, 0.01 km/h)</item>
/// <item>bit 1 — Average Speed present (uint16, 0.01 km/h)</item>
/// <item>bit 2 — Instantaneous Cadence present (uint16, 0.5 rpm)</item>
/// <item>bit 3 — Average Cadence present (uint16, 0.5 rpm)</item>
/// <item>bit 4 — Total Distance present (uint24, metres)</item>
/// <item>bit 5 — Resistance Level present (sint16, unitless)</item>
/// <item>bit 6 — Instantaneous Power present (sint16, watts)</item>
/// <item>bit 7 — Average Power present (sint16, watts)</item>
/// <item>bit 8 — Expended Energy present (uint16 kcal, uint16 kcal/h, uint8 kcal/min)</item>
/// <item>bit 9 — Heart Rate present (uint8, bpm)</item>
/// <item>bit 10 — Metabolic Equivalent present (uint8, 0.1)</item>
/// <item>bit 11 — Elapsed Time present (uint16, seconds)</item>
/// <item>bit 12 — Remaining Time present (uint16, seconds)</item>
/// </list>
/// </summary>
public static class IndoorBikeDataParser
{
    private const ushort FlagMoreData = 1 << 0;
    private const ushort FlagAverageSpeed = 1 << 1;
    private const ushort FlagInstantaneousCadence = 1 << 2;
    private const ushort FlagAverageCadence = 1 << 3;
    private const ushort FlagTotalDistance = 1 << 4;
    private const ushort FlagResistanceLevel = 1 << 5;
    private const ushort FlagInstantaneousPower = 1 << 6;
    private const ushort FlagAveragePower = 1 << 7;
    private const ushort FlagExpendedEnergy = 1 << 8;
    private const ushort FlagHeartRate = 1 << 9;
    private const ushort FlagMetabolicEquivalent = 1 << 10;
    private const ushort FlagElapsedTime = 1 << 11;
    private const ushort FlagRemainingTime = 1 << 12;

    public static FitnessMachineData Parse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        ushort flags = reader.ReadUInt16("Indoor Bike Data flags");

        double? speed = null;
        if ((flags & FlagMoreData) == 0)   // inverted: see remarks
            speed = Units.FtmsSpeedToMs(reader.ReadUInt16("Instantaneous Speed"));

        double? averageSpeed = (flags & FlagAverageSpeed) != 0
            ? Units.FtmsSpeedToMs(reader.ReadUInt16("Average Speed"))
            : null;

        double? cadence = (flags & FlagInstantaneousCadence) != 0
            ? Units.FtmsCadenceToRpm(reader.ReadUInt16("Instantaneous Cadence"))
            : null;

        double? averageCadence = (flags & FlagAverageCadence) != 0
            ? Units.FtmsCadenceToRpm(reader.ReadUInt16("Average Cadence"))
            : null;

        double? totalDistance = (flags & FlagTotalDistance) != 0
            ? reader.ReadUInt24("Total Distance")
            : null;

        double? resistance = (flags & FlagResistanceLevel) != 0
            ? reader.ReadInt16("Resistance Level")
            : null;

        int? power = (flags & FlagInstantaneousPower) != 0
            ? reader.ReadInt16("Instantaneous Power")
            : null;

        int? averagePower = (flags & FlagAveragePower) != 0
            ? reader.ReadInt16("Average Power")
            : null;

        int? totalEnergy = null, energyPerHour = null, energyPerMinute = null;
        if ((flags & FlagExpendedEnergy) != 0)
            FtmsFields.ReadExpendedEnergy(ref reader, out totalEnergy, out energyPerHour, out energyPerMinute);

        int? heartRate = (flags & FlagHeartRate) != 0
            ? reader.ReadUInt8("Heart Rate")
            : null;

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
            SpeedMetersPerSecond = speed,
            AverageSpeedMetersPerSecond = averageSpeed,
            CadenceRpm = cadence,
            AverageCadenceRpm = averageCadence,
            TotalDistanceMeters = totalDistance,
            ResistanceLevel = resistance,
            InstantaneousPowerWatts = power,
            AveragePowerWatts = averagePower,
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

/// <summary>Field groups shared by more than one FTMS measurement characteristic.</summary>
internal static class FtmsFields
{
    /// <summary>
    /// Expended Energy: Total Energy (uint16, kcal), Energy Per Hour (uint16, kcal),
    /// Energy Per Minute (uint8, kcal). FTMS v1.0 — identical in all four data characteristics.
    /// <para>
    /// 0xFFFF (or 0xFF) is the spec's "data not available" sentinel and is mapped to null
    /// rather than surfaced as a 65535 kcal reading.
    /// </para>
    /// </summary>
    public static void ReadExpendedEnergy(
        ref SpanReader reader, out int? total, out int? perHour, out int? perMinute)
    {
        ushort rawTotal = reader.ReadUInt16("Total Energy");
        ushort rawPerHour = reader.ReadUInt16("Energy Per Hour");
        byte rawPerMinute = reader.ReadUInt8("Energy Per Minute");

        total = rawTotal == 0xFFFF ? null : rawTotal;
        perHour = rawPerHour == 0xFFFF ? null : rawPerHour;
        perMinute = rawPerMinute == 0xFF ? null : rawPerMinute;
    }
}
