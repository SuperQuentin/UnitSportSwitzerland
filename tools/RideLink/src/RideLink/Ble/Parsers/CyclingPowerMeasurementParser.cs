using RideLink.Parsing;

namespace RideLink.Ble.Parsers;

/// <summary>Decoded Cycling Power Measurement (<c>0x2A63</c>).</summary>
public readonly record struct CyclingPowerMeasurement(
    int InstantaneousPowerWatts,
    double? PedalPowerBalancePercent,
    bool PedalPowerBalanceIsUnknownReference,
    double? AccumulatedTorqueNewtonMeters,
    uint? CumulativeWheelRevolutions,
    ushort? LastWheelEventTime,
    ushort? CumulativeCrankRevolutions,
    ushort? LastCrankEventTime,
    int? AccumulatedEnergyKilojoules);

/// <summary>
/// Cycling Power Measurement parser.
/// <para>Cycling Power Service v1.1, §3.1; GATT Specification Supplement, "Cycling Power Measurement".</para>
/// <para>
/// Layout: Flags (uint16), Instantaneous Power (sint16, watts), then optional fields in
/// ascending flag-bit order:
/// </para>
/// <list type="bullet">
/// <item>bit 0 — Pedal Power Balance present (uint8, units of 0.5%)</item>
/// <item>bit 1 — Pedal Power Balance Reference (0 = unknown, 1 = left)</item>
/// <item>bit 2 — Accumulated Torque present (uint16, units of 1/32 N·m)</item>
/// <item>bit 3 — Accumulated Torque Source (0 = wheel, 1 = crank)</item>
/// <item>bit 4 — Wheel Revolution Data present (uint32 revolutions, uint16 event time @ 1/2048 s)</item>
/// <item>bit 5 — Crank Revolution Data present (uint16 revolutions, uint16 event time @ 1/1024 s)</item>
/// <item>bit 6 — Extreme Force Magnitudes present (2 × sint16, newtons)</item>
/// <item>bit 7 — Extreme Torque Magnitudes present (2 × sint16, 1/32 N·m)</item>
/// <item>bit 8 — Extreme Angles present (3 octets holding two 12-bit values)</item>
/// <item>bit 9 — Top Dead Spot Angle present (uint16, degrees)</item>
/// <item>bit 10 — Bottom Dead Spot Angle present (uint16, degrees)</item>
/// <item>bit 11 — Accumulated Energy present (uint16, kJ)</item>
/// <item>bit 12 — Offset Compensation Indicator</item>
/// </list>
/// <para>
/// The wheel event time here is 1/2048 s, *not* the 1/1024 s used by CSC. Using the CSC
/// resolution against a power meter's wheel data yields exactly half the true speed.
/// </para>
/// </summary>
public static class CyclingPowerMeasurementParser
{
    private const ushort FlagPedalPowerBalance = 1 << 0;
    private const ushort FlagPedalPowerBalanceReference = 1 << 1;
    private const ushort FlagAccumulatedTorque = 1 << 2;
    private const ushort FlagAccumulatedTorqueSource = 1 << 3;
    private const ushort FlagWheelRevolutionData = 1 << 4;
    private const ushort FlagCrankRevolutionData = 1 << 5;
    private const ushort FlagExtremeForceMagnitudes = 1 << 6;
    private const ushort FlagExtremeTorqueMagnitudes = 1 << 7;
    private const ushort FlagExtremeAngles = 1 << 8;
    private const ushort FlagTopDeadSpotAngle = 1 << 9;
    private const ushort FlagBottomDeadSpotAngle = 1 << 10;
    private const ushort FlagAccumulatedEnergy = 1 << 11;

    /// <summary>Resolution of Last Wheel Event Time in this characteristic.</summary>
    public const double WheelEventTimeTicksPerSecond = 2048.0;

    /// <summary>Resolution of Last Crank Event Time in this characteristic.</summary>
    public const double CrankEventTimeTicksPerSecond = 1024.0;

    public static CyclingPowerMeasurement Parse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        ushort flags = reader.ReadUInt16("CPM flags");

        // Instantaneous Power is mandatory and sits immediately after the flags. It is
        // signed: a trainer coasting can legitimately report a small negative value.
        int watts = reader.ReadInt16("Instantaneous Power");

        double? balance = null;
        if ((flags & FlagPedalPowerBalance) != 0)
            balance = reader.ReadUInt8("Pedal Power Balance") * 0.5;

        bool balanceUnknownReference = (flags & FlagPedalPowerBalanceReference) == 0;

        double? torque = null;
        if ((flags & FlagAccumulatedTorque) != 0)
            torque = reader.ReadUInt16("Accumulated Torque") / 32.0;

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

        // The remaining optional fields are not surfaced, but they must still be walked in
        // order so that any field after them lands on the right offset.
        if ((flags & FlagExtremeForceMagnitudes) != 0)
            reader.Skip(4, "Extreme Force Magnitudes");

        if ((flags & FlagExtremeTorqueMagnitudes) != 0)
            reader.Skip(4, "Extreme Torque Magnitudes");

        if ((flags & FlagExtremeAngles) != 0)
            reader.Skip(3, "Extreme Angles");   // two 12-bit values packed into 3 octets

        if ((flags & FlagTopDeadSpotAngle) != 0)
            reader.Skip(2, "Top Dead Spot Angle");

        if ((flags & FlagBottomDeadSpotAngle) != 0)
            reader.Skip(2, "Bottom Dead Spot Angle");

        int? energy = null;
        if ((flags & FlagAccumulatedEnergy) != 0)
            energy = reader.ReadUInt16("Accumulated Energy");

        return new CyclingPowerMeasurement(
            watts,
            balance,
            balanceUnknownReference,
            torque,
            wheelRevolutions,
            wheelEventTime,
            crankRevolutions,
            crankEventTime,
            energy);
    }
}
