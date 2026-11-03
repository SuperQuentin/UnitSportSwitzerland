using RideLink.Capabilities;
using RideLink.Parsing;

namespace RideLink.Ant;

/// <summary>
/// ANT+ data page numbers used by the profiles the engine implements.
/// <para>
/// Every ANT+ broadcast is exactly 8 octets and the first is the page number, so parsing is a
/// switch on octet 0 rather than the flag walk BLE needs.
/// </para>
/// </summary>
public static class AntPages
{
    // --- Fitness Equipment (FE-C) ---------------------------------------------------
    /// <summary>General FE Data. ANT+ Device Profile – Fitness Equipment, §7.2.</summary>
    public const byte GeneralFeData = 0x10;

    /// <summary>Specific Trainer Data. Fitness Equipment, §8.6.</summary>
    public const byte SpecificTrainerData = 0x19;

    /// <summary>Basic Resistance control page. Fitness Equipment, §9.1.</summary>
    public const byte BasicResistance = 0x30;

    /// <summary>Target Power control page — ERG mode. Fitness Equipment, §9.2.</summary>
    public const byte TargetPower = 0x31;

    /// <summary>Wind Resistance control page. Fitness Equipment, §9.3.</summary>
    public const byte WindResistance = 0x32;

    /// <summary>Track Resistance control page — grade, i.e. SIM mode. Fitness Equipment, §9.4.</summary>
    public const byte TrackResistance = 0x33;

    /// <summary>FE Capabilities. Fitness Equipment, §9.5.</summary>
    public const byte FeCapabilities = 0x36;

    /// <summary>Command Status, the FE-C acknowledgement of a control page. Fitness Equipment, §9.6.</summary>
    public const byte CommandStatus = 0x47;

    // --- Bicycle Power ---------------------------------------------------------------
    /// <summary>Standard Power-Only. ANT+ Device Profile – Bicycle Power, §6.2.</summary>
    public const byte StandardPowerOnly = 0x10;

    // --- Common ----------------------------------------------------------------------
    /// <summary>Manufacturer's Identification. ANT+ Common Pages, §5.1.</summary>
    public const byte ManufacturerIdentification = 0x50;

    /// <summary>Product Information. ANT+ Common Pages, §5.2.</summary>
    public const byte ProductInformation = 0x51;

    /// <summary>Battery Status. ANT+ Common Pages, §5.3.</summary>
    public const byte BatteryStatus = 0x52;
}

/// <summary>General FE Data (page 0x10): what every fitness machine reports.</summary>
/// <param name="EquipmentType">Bike, treadmill, rower, elliptic, and so on.</param>
/// <param name="ElapsedTime">Session time; the raw counter is uint8 in 0.25 s and wraps every 64 s.</param>
/// <param name="DistanceMeters">Cumulative distance; uint8 counter in metres, wraps every 256 m.</param>
/// <param name="SpeedMetersPerSecond">Instantaneous speed, null when the device reports invalid.</param>
/// <param name="HeartRateBpm">Heart rate relayed by the machine, null when not present.</param>
public readonly record struct GeneralFeData(
    byte EquipmentType,
    TimeSpan ElapsedTime,
    double DistanceMeters,
    double? SpeedMetersPerSecond,
    int? HeartRateBpm);

/// <summary>Specific Trainer Data (page 0x19): the numbers a trainer adds.</summary>
/// <param name="EventCount">Increments once per power computation; used to detect stalls.</param>
/// <param name="CadenceRpm">Instantaneous cadence, null when the device reports invalid.</param>
/// <param name="AccumulatedPowerWatts">Rolling uint16 sum of instantaneous power.</param>
/// <param name="InstantaneousPowerWatts">Instantaneous power, null when invalid.</param>
/// <param name="TargetPowerLimitsReached">
/// True when the trainer says it cannot reach the ERG target at the current cadence — the
/// signal a UI should use to tell the rider to change gear rather than silently missing target.
/// </param>
public readonly record struct SpecificTrainerData(
    byte EventCount,
    double? CadenceRpm,
    ushort AccumulatedPowerWatts,
    int? InstantaneousPowerWatts,
    bool TargetPowerLimitsReached);

/// <summary>Standard Power-Only (Bicycle Power page 0x10).</summary>
public readonly record struct BicyclePowerData(
    byte EventCount,
    int? CadenceRpm,
    ushort AccumulatedPowerWatts,
    int InstantaneousPowerWatts);

/// <summary>Heart rate from any HRM data page — the last four octets are common to all of them.</summary>
/// <param name="HeartBeatEventTime">Time of the last beat, 1/1024 s, uint16 rollover.</param>
/// <param name="HeartBeatCount">Beat counter, uint8 rollover.</param>
/// <param name="ComputedHeartRateBpm">The sensor's own averaged rate; zero means not yet valid.</param>
public readonly record struct AntHeartRateData(
    ushort HeartBeatEventTime, byte HeartBeatCount, int ComputedHeartRateBpm);

/// <summary>Combined Bike Speed and Cadence page 0 (device type 121).</summary>
public readonly record struct AntSpeedCadenceData(
    ushort CadenceEventTime,
    ushort CumulativeCadenceRevolutions,
    ushort SpeedEventTime,
    ushort CumulativeSpeedRevolutions);

/// <summary>
/// Parsers for ANT+ broadcast pages.
/// <para>
/// ANT+ has no flags field: every page is a fixed 8-octet layout selected by the page number,
/// which makes these far simpler than their BLE equivalents. What it does have is a set of
/// "invalid" sentinel values that must be mapped to null rather than reported as data —
/// 0xFF for cadence, 0xFFFF for power — and several counters that roll over.
/// </para>
/// </summary>
public static class AntPageParser
{
    /// <summary>Page number of a broadcast, or null when the payload is not 8 octets.</summary>
    public static byte? PageNumber(ReadOnlySpan<byte> payload) =>
        payload.Length >= 1 ? payload[0] : null;

    /// <summary>
    /// General FE Data, page 0x10. Fitness Equipment, §7.2.
    /// <para>
    /// Octets: 0 page, 1 equipment type, 2 elapsed time (0.25 s), 3 distance (m),
    /// 4-5 speed (0.001 m/s), 6 heart rate, 7 capabilities and FE state.
    /// </para>
    /// </summary>
    public static GeneralFeData ParseGeneralFeData(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        reader.Skip(1, "page number");

        byte equipmentType = (byte)(reader.ReadUInt8("equipment type") & 0x1F);
        double elapsedSeconds = reader.ReadUInt8("elapsed time") * 0.25;
        double distance = reader.ReadUInt8("distance travelled");
        ushort rawSpeed = reader.ReadUInt16("speed");
        byte heartRate = reader.ReadUInt8("heart rate");

        return new GeneralFeData(
            equipmentType,
            TimeSpan.FromSeconds(elapsedSeconds),
            distance,
            // 0xFFFF is the profile's "invalid" sentinel, not 65.535 m/s.
            rawSpeed == 0xFFFF ? null : rawSpeed * 0.001,
            heartRate == 0xFF ? null : heartRate);
    }

    /// <summary>
    /// Specific Trainer Data, page 0x19. Fitness Equipment, §8.6.
    /// <para>
    /// Octets: 0 page, 1 update event count, 2 instantaneous cadence, 3-4 accumulated power,
    /// 5 and low nibble of 6 instantaneous power (12 bits), high nibble of 6 trainer status,
    /// 7 flags and FE state.
    /// </para>
    /// <para>
    /// Instantaneous power is 12 bits split across an octet boundary, which is the detail most
    /// often got wrong: the low 8 bits are octet 5 and the high 4 bits are the low nibble of
    /// octet 6.
    /// </para>
    /// </summary>
    public static SpecificTrainerData ParseSpecificTrainerData(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        reader.Skip(1, "page number");

        byte eventCount = reader.ReadUInt8("update event count");
        byte cadence = reader.ReadUInt8("instantaneous cadence");
        ushort accumulated = reader.ReadUInt16("accumulated power");
        byte powerLsb = reader.ReadUInt8("instantaneous power LSB");
        byte mixed = reader.ReadUInt8("power MSN / trainer status");
        byte flags = reader.ReadUInt8("flags and FE state");

        int rawPower = ((mixed & 0x0F) << 8) | powerLsb;

        return new SpecificTrainerData(
            eventCount,
            cadence == 0xFF ? null : cadence,
            accumulated,
            rawPower == 0x0FFF ? null : rawPower,
            // Bit 0 of the low nibble of the flags field: target power limits reached.
            TargetPowerLimitsReached: (flags & 0x03) != 0);
    }

    /// <summary>
    /// Standard Power-Only, Bicycle Power page 0x10. Bicycle Power, §6.2.
    /// <para>
    /// Octets: 0 page, 1 event count, 2 pedal power, 3 instantaneous cadence,
    /// 4-5 accumulated power, 6-7 instantaneous power.
    /// </para>
    /// </summary>
    public static BicyclePowerData ParseStandardPowerOnly(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        reader.Skip(1, "page number");

        byte eventCount = reader.ReadUInt8("event count");
        reader.Skip(1, "pedal power");
        byte cadence = reader.ReadUInt8("instantaneous cadence");
        ushort accumulated = reader.ReadUInt16("accumulated power");
        ushort instantaneous = reader.ReadUInt16("instantaneous power");

        return new BicyclePowerData(
            eventCount, cadence == 0xFF ? null : cadence, accumulated, instantaneous);
    }

    /// <summary>
    /// Heart rate. The last four octets are identical across every HRM data page, so the page
    /// number only matters for the optional extra fields we do not use.
    /// ANT+ Device Profile – Heart Rate Monitor, §5.3.
    /// </summary>
    public static AntHeartRateData ParseHeartRate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
            throw new RideLinkParseException("HRM page must be 8 octets", payload, payload.Length);

        var reader = new SpanReader(payload);
        reader.Skip(4, "page-specific octets");

        ushort eventTime = reader.ReadUInt16("heart beat event time");
        byte beatCount = reader.ReadUInt8("heart beat count");
        byte computed = reader.ReadUInt8("computed heart rate");

        return new AntHeartRateData(eventTime, beatCount, computed);
    }

    /// <summary>
    /// Combined Bike Speed and Cadence, page 0.
    /// ANT+ Device Profile – Bicycle Speed and Cadence, §5.3.
    /// <para>
    /// Octets: 0-1 cadence event time (1/1024 s), 2-3 cumulative cadence revolutions,
    /// 4-5 speed event time (1/1024 s), 6-7 cumulative speed revolutions.
    /// </para>
    /// <para>
    /// Note this profile has no page number: the combined sensor sends this layout every time,
    /// which is why it is parsed from octet 0.
    /// </para>
    /// </summary>
    public static AntSpeedCadenceData ParseSpeedAndCadence(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        return new AntSpeedCadenceData(
            reader.ReadUInt16("cadence event time"),
            reader.ReadUInt16("cumulative cadence revolutions"),
            reader.ReadUInt16("speed event time"),
            reader.ReadUInt16("cumulative speed revolutions"));
    }
}

/// <summary>
/// Builds FE-C control pages.
/// <para>
/// These map onto exactly the same SIM / ERG / Resistance abstraction as BLE FTMS, so a
/// consumer setting a grade never learns which radio carried it. All are sent as acknowledged
/// messages, and the trainer replies with a Command Status page (0x47).
/// </para>
/// </summary>
public static class FecControlPages
{
    /// <summary>
    /// Basic Resistance (0x30): total resistance as a percentage in units of 0.5%.
    /// Fitness Equipment, §9.1.
    /// </summary>
    public static byte[] BasicResistance(double percent)
    {
        byte raw = (byte)Math.Clamp(Math.Round(percent / 0.5), 0, 200);
        return [AntPages.BasicResistance, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, raw];
    }

    /// <summary>
    /// Target Power (0x31) — ERG mode: uint16 in units of 0.25 W.
    /// Fitness Equipment, §9.2.
    /// </summary>
    public static byte[] TargetPower(int watts)
    {
        ushort raw = (ushort)Math.Clamp(Math.Round(watts / 0.25), 0, ushort.MaxValue);
        return
        [
            AntPages.TargetPower, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            (byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF),
        ];
    }

    /// <summary>
    /// Track Resistance (0x33) — SIM mode.
    /// <para>
    /// Grade is uint16 in units of 0.01% with a -200.00% offset, so a 0% grade is transmitted
    /// as 20000, not 0. Rolling resistance coefficient is uint8 in units of 5×10⁻⁵.
    /// Fitness Equipment, §9.4.
    /// </para>
    /// </summary>
    /// <param name="gradeFraction">Grade as a fraction: 0.05 is a 5% climb.</param>
    /// <param name="rollingResistanceCoefficient">Crr; 0.004 is typical tarmac.</param>
    public static byte[] TrackResistance(double gradeFraction, double rollingResistanceCoefficient = 0.004)
    {
        double percent = gradeFraction * 100.0;
        ushort rawGrade = (ushort)Math.Clamp(Math.Round((percent + 200.0) / 0.01), 0, 0xFFFE);
        byte rawCrr = (byte)Math.Clamp(Math.Round(rollingResistanceCoefficient / 0.00005), 0, 254);

        return
        [
            AntPages.TrackResistance, 0xFF, 0xFF, 0xFF, 0xFF,
            (byte)(rawGrade & 0xFF), (byte)((rawGrade >> 8) & 0xFF),
            rawCrr,
        ];
    }

    /// <summary>
    /// Wind Resistance (0x32).
    /// <para>
    /// Coefficient is uint8 in units of 0.01 kg/m; wind speed is uint8 in km/h with a
    /// -127 km/h offset; drafting factor is uint8 in units of 0.01. Fitness Equipment, §9.3.
    /// </para>
    /// </summary>
    public static byte[] WindResistance(
        double windResistanceCoefficient = 0.51,
        double windSpeedKilometersPerHour = 0,
        double draftingFactor = 1.0)
    {
        byte rawCoefficient = (byte)Math.Clamp(Math.Round(windResistanceCoefficient / 0.01), 0, 254);
        byte rawWind = (byte)Math.Clamp(Math.Round(windSpeedKilometersPerHour + 127), 0, 254);
        byte rawDrafting = (byte)Math.Clamp(Math.Round(draftingFactor / 0.01), 0, 100);

        return
        [
            AntPages.WindResistance, 0xFF, 0xFF, 0xFF, 0xFF,
            rawCoefficient, rawWind, rawDrafting,
        ];
    }

    /// <summary>
    /// Decodes the grade back out of a Track Resistance page. Used by tests and by the
    /// simulated trainer, and the inverse of the offset encoding above.
    /// </summary>
    public static double DecodeTrackResistanceGrade(ReadOnlySpan<byte> page)
    {
        if (page.Length < 8 || page[0] != AntPages.TrackResistance)
            throw new RideLinkParseException("Not a Track Resistance page", page, 0);

        ushort raw = (ushort)(page[5] | (page[6] << 8));
        return (raw * 0.01 - 200.0) / 100.0;
    }

    /// <summary>Decodes the watts back out of a Target Power page.</summary>
    public static int DecodeTargetPower(ReadOnlySpan<byte> page)
    {
        if (page.Length < 8 || page[0] != AntPages.TargetPower)
            throw new RideLinkParseException("Not a Target Power page", page, 0);

        ushort raw = (ushort)(page[6] | (page[7] << 8));
        return (int)Math.Round(raw * 0.25);
    }

    /// <summary>Builds the control page for a mode, or null when the mode has no FE-C page.</summary>
    public static byte[]? ForMode(ControlMode mode, SimulationParameters simulation, int targetWatts,
        double resistancePercent) => mode switch
    {
        ControlMode.Simulation => TrackResistance(
            simulation.GradeFraction, simulation.RollingResistanceCoefficient),
        ControlMode.Erg => TargetPower(targetWatts),
        ControlMode.Resistance => BasicResistance(resistancePercent),
        _ => null,
    };
}
