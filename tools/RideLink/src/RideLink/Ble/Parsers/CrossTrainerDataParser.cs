using RideLink.Model;
using RideLink.Parsing;

namespace RideLink.Ble.Parsers;

/// <summary>
/// Cross Trainer Data parser (<c>0x2ACE</c>).
/// <para>FTMS v1.0, §4.5; GATT Specification Supplement, "Cross Trainer Data".</para>
/// <para>
/// This characteristic is the odd one out: its flags field is <b>24 bits</b>, not 16. Reading
/// it as a uint16 leaves one octet unconsumed and shifts every field after it. Bit 0 is
/// "More Data" with the same inverted sense as the other machines.
/// </para>
/// <list type="bullet">
/// <item>bit 0 — More Data. 0 ⇒ Instantaneous Speed present (uint16, 0.01 km/h)</item>
/// <item>bit 1 — Average Speed present (uint16, 0.01 km/h)</item>
/// <item>bit 2 — Total Distance present (uint24, metres)</item>
/// <item>bit 3 — Step Count present (uint16 steps/min, uint16 average step rate)</item>
/// <item>bit 4 — Stride Count present (uint16, 0.1)</item>
/// <item>bit 5 — Elevation Gain present (uint16 positive 0.1 m, uint16 negative 0.1 m)</item>
/// <item>bit 6 — Inclination and Ramp Angle Setting present (sint16 0.1 %, sint16 0.1 °)</item>
/// <item>bit 7 — Resistance Level present (sint16, 0.1)</item>
/// <item>bit 8 — Instantaneous Power present (sint16, watts)</item>
/// <item>bit 9 — Average Power present (sint16, watts)</item>
/// <item>bit 10 — Expended Energy present (uint16 kcal, uint16 kcal/h, uint8 kcal/min)</item>
/// <item>bit 11 — Heart Rate present (uint8, bpm)</item>
/// <item>bit 12 — Metabolic Equivalent present (uint8, 0.1)</item>
/// <item>bit 13 — Elapsed Time present (uint16, seconds)</item>
/// <item>bit 14 — Remaining Time present (uint16, seconds)</item>
/// <item>bit 15 — Movement Direction: 0 forward, 1 backward</item>
/// </list>
/// </summary>
public static class CrossTrainerDataParser
{
    private const uint FlagMoreData = 1u << 0;
    private const uint FlagAverageSpeed = 1u << 1;
    private const uint FlagTotalDistance = 1u << 2;
    private const uint FlagStepCount = 1u << 3;
    private const uint FlagStrideCount = 1u << 4;
    private const uint FlagElevationGain = 1u << 5;
    private const uint FlagInclinationAndRampAngle = 1u << 6;
    private const uint FlagResistanceLevel = 1u << 7;
    private const uint FlagInstantaneousPower = 1u << 8;
    private const uint FlagAveragePower = 1u << 9;
    private const uint FlagExpendedEnergy = 1u << 10;
    private const uint FlagHeartRate = 1u << 11;
    private const uint FlagMetabolicEquivalent = 1u << 12;
    private const uint FlagElapsedTime = 1u << 13;
    private const uint FlagRemainingTime = 1u << 14;
    private const uint FlagMovementDirectionBackward = 1u << 15;

    public static FitnessMachineData Parse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        uint flags = reader.ReadUInt24("Cross Trainer Data flags");   // 24-bit, see remarks

        double? speed = null;
        if ((flags & FlagMoreData) == 0)
            speed = Units.FtmsSpeedToMs(reader.ReadUInt16("Instantaneous Speed"));

        double? averageSpeed = (flags & FlagAverageSpeed) != 0
            ? Units.FtmsSpeedToMs(reader.ReadUInt16("Average Speed"))
            : null;

        double? totalDistance = (flags & FlagTotalDistance) != 0
            ? reader.ReadUInt24("Total Distance")
            : null;

        int? stepsPerMinute = null, averageStepRate = null;
        if ((flags & FlagStepCount) != 0)
        {
            stepsPerMinute = reader.ReadUInt16("Step Per Minute");
            averageStepRate = reader.ReadUInt16("Average Step Rate");
        }

        double? strideCount = (flags & FlagStrideCount) != 0
            ? reader.ReadUInt16("Stride Count") * 0.1
            : null;

        double? positiveGain = null, negativeGain = null;
        if ((flags & FlagElevationGain) != 0)
        {
            positiveGain = reader.ReadUInt16("Positive Elevation Gain") * 0.1;
            negativeGain = reader.ReadUInt16("Negative Elevation Gain") * 0.1;
        }

        double? inclination = null, rampAngle = null;
        if ((flags & FlagInclinationAndRampAngle) != 0)
        {
            inclination = Units.FtmsInclinationToPercent(reader.ReadInt16("Inclination"));
            rampAngle = reader.ReadInt16("Ramp Angle Setting") * 0.1;
        }

        // Cross Trainer resistance carries a 0.1 resolution, unlike Indoor Bike and Rower.
        double? resistance = (flags & FlagResistanceLevel) != 0
            ? Units.FtmsResistanceToLevel(reader.ReadInt16("Resistance Level"))
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
            SpeedMetersPerSecond = speed,
            AverageSpeedMetersPerSecond = averageSpeed,
            TotalDistanceMeters = totalDistance,
            StepsPerMinute = stepsPerMinute,
            AverageStepRate = averageStepRate,
            // A cross trainer's step rate is the closest thing it has to cadence.
            CadenceRpm = stepsPerMinute,
            StrideCount = strideCount,
            PositiveElevationGainMeters = positiveGain,
            NegativeElevationGainMeters = negativeGain,
            InclinationPercent = inclination,
            RampAngleDegrees = rampAngle,
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
            MovingForward = (flags & FlagMovementDirectionBackward) == 0,
        };
    }
}
