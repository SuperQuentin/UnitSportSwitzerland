using RideLink.Model;
using RideLink.Parsing;

namespace RideLink.Ble.Parsers;

/// <summary>
/// Treadmill Data parser (<c>0x2ACD</c>).
/// <para>FTMS v1.0, §4.4; GATT Specification Supplement, "Treadmill Data".</para>
/// <para>
/// Flags (uint16). As with Indoor Bike Data, bit 0 is "More Data" and Instantaneous Speed is
/// present when it is <b>clear</b>.
/// </para>
/// <list type="bullet">
/// <item>bit 0 — More Data. 0 ⇒ Instantaneous Speed present (uint16, 0.01 km/h)</item>
/// <item>bit 1 — Average Speed present (uint16, 0.01 km/h)</item>
/// <item>bit 2 — Total Distance present (uint24, metres)</item>
/// <item>bit 3 — Inclination and Ramp Angle Setting present (sint16 0.1 %, sint16 0.1 °)</item>
/// <item>bit 4 — Elevation Gain present (uint16 positive 0.1 m, uint16 negative 0.1 m)</item>
/// <item>bit 5 — Instantaneous Pace present (uint8, 0.1 km/min)</item>
/// <item>bit 6 — Average Pace present (uint8, 0.1 km/min)</item>
/// <item>bit 7 — Expended Energy present (uint16 kcal, uint16 kcal/h, uint8 kcal/min)</item>
/// <item>bit 8 — Heart Rate present (uint8, bpm)</item>
/// <item>bit 9 — Metabolic Equivalent present (uint8, 0.1)</item>
/// <item>bit 10 — Elapsed Time present (uint16, seconds)</item>
/// <item>bit 11 — Remaining Time present (uint16, seconds)</item>
/// <item>bit 12 — Force on Belt and Power Output present (sint16 newtons, sint16 watts)</item>
/// </list>
/// </summary>
public static class TreadmillDataParser
{
    private const ushort FlagMoreData = 1 << 0;
    private const ushort FlagAverageSpeed = 1 << 1;
    private const ushort FlagTotalDistance = 1 << 2;
    private const ushort FlagInclinationAndRampAngle = 1 << 3;
    private const ushort FlagElevationGain = 1 << 4;
    private const ushort FlagInstantaneousPace = 1 << 5;
    private const ushort FlagAveragePace = 1 << 6;
    private const ushort FlagExpendedEnergy = 1 << 7;
    private const ushort FlagHeartRate = 1 << 8;
    private const ushort FlagMetabolicEquivalent = 1 << 9;
    private const ushort FlagElapsedTime = 1 << 10;
    private const ushort FlagRemainingTime = 1 << 11;
    private const ushort FlagForceOnBeltAndPower = 1 << 12;

    public static FitnessMachineData Parse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        ushort flags = reader.ReadUInt16("Treadmill Data flags");

        double? speed = null;
        if ((flags & FlagMoreData) == 0)
            speed = Units.FtmsSpeedToMs(reader.ReadUInt16("Instantaneous Speed"));

        double? averageSpeed = (flags & FlagAverageSpeed) != 0
            ? Units.FtmsSpeedToMs(reader.ReadUInt16("Average Speed"))
            : null;

        double? totalDistance = (flags & FlagTotalDistance) != 0
            ? reader.ReadUInt24("Total Distance")
            : null;

        double? inclination = null, rampAngle = null;
        if ((flags & FlagInclinationAndRampAngle) != 0)
        {
            inclination = Units.FtmsInclinationToPercent(reader.ReadInt16("Inclination"));
            rampAngle = reader.ReadInt16("Ramp Angle Setting") * 0.1;
        }

        double? positiveGain = null, negativeGain = null;
        if ((flags & FlagElevationGain) != 0)
        {
            positiveGain = reader.ReadUInt16("Positive Elevation Gain") * 0.1;
            negativeGain = reader.ReadUInt16("Negative Elevation Gain") * 0.1;
        }

        double? pace = (flags & FlagInstantaneousPace) != 0
            ? Units.TreadmillPaceToMs(reader.ReadUInt8("Instantaneous Pace"))
            : null;

        double? averagePace = (flags & FlagAveragePace) != 0
            ? Units.TreadmillPaceToMs(reader.ReadUInt8("Average Pace"))
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

        double? force = null;
        int? power = null;
        if ((flags & FlagForceOnBeltAndPower) != 0)
        {
            force = reader.ReadInt16("Force on Belt");
            power = reader.ReadInt16("Power Output");
        }

        return new FitnessMachineData
        {
            SpeedMetersPerSecond = speed,
            AverageSpeedMetersPerSecond = averageSpeed,
            TotalDistanceMeters = totalDistance,
            InclinationPercent = inclination,
            RampAngleDegrees = rampAngle,
            PositiveElevationGainMeters = positiveGain,
            NegativeElevationGainMeters = negativeGain,
            PaceMetersPerSecond = pace,
            AveragePaceMetersPerSecond = averagePace,
            TotalEnergyKilocalories = totalEnergy,
            EnergyPerHourKilocalories = energyPerHour,
            EnergyPerMinuteKilocalories = energyPerMinute,
            HeartRateBpm = heartRate,
            MetabolicEquivalent = met,
            ElapsedTime = elapsed,
            RemainingTime = remaining,
            ForceOnBeltNewtons = force,
            InstantaneousPowerWatts = power,
        };
    }
}
